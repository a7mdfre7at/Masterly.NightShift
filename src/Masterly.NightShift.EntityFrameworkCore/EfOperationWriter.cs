using System.Linq.Expressions;
using Masterly.NightShift.Storage.Shared;
using Microsoft.EntityFrameworkCore;

namespace Masterly.NightShift.EntityFrameworkCore;

/// <summary>
/// Turns a transaction's operations into changes of a context. Applied at once (<c>immediate</c>), it saves as
/// it goes inside the storage's database transaction and deletes or updates many rows with one statement;
/// staged in an application's context, it only changes tracked rows, which the application's
/// <c>SaveChanges</c> commits or discards with its own changes.
/// </summary>
internal sealed class EfOperationWriter(DbContext context, bool immediate)
{
    public void Write(StorageOperation operation)
    {
        switch (operation)
        {
            case ExpireJobOperation expire:
                if (context.Set<JobRow>().Find(expire.JobId) is { } expiring) expiring.ExpireAt = expire.ExpireAt;
                break;

            case AddJobStateOperation add:
                if (context.Set<JobRow>().Find(add.JobId) is { } job)
                {
                    context.Add(new StateRow { JobId = job.Id, Name = add.State.Name, Reason = Truncate(add.State.Reason), CreatedAt = add.State.CreatedAt, Data = add.State.Data });
                    if (add.SetCurrent)
                    {
                        job.StateName = add.State.Name;
                        job.StateReason = Truncate(add.State.Reason);
                        job.StateData = add.State.Data;
                        job.StateChangedAt = add.State.CreatedAt;
                    }
                }

                break;

            case AddToQueueOperation queue:
                context.Add(new JobQueueRow { Queue = queue.Queue, JobId = queue.JobId });
                break;

            case RemoveFromQueueOperation removal:
                if (!immediate) throw new NotSupportedException("Fetched jobs are acknowledged by the storage, not in an application's context.");
                EfFetchedJob fetched = (EfFetchedJob)removal.FetchedJob; // This storage only fetches its own jobs
                Flush();
                context.Set<JobQueueRow>().Where(x => x.Id == fetched.EntryId && x.FetchToken == fetched.Token).ExecuteDelete();
                break;

            case SetJobParameterOperation parameter:
                if (context.Set<JobRow>().Find(parameter.JobId) == null) break;
                JobParameterRow? existing = Revive(context.Set<JobParameterRow>().Find(parameter.JobId, parameter.Name));
                if (existing != null) existing.Value = parameter.Value;
                else context.Add(new JobParameterRow { JobId = parameter.JobId, Name = parameter.Name, Value = parameter.Value });
                break;

            case CounterOperation counter:
                context.Add(new CounterRow { Key = counter.Key, Value = counter.Value, ExpireAt = counter.ExpireAt });
                break;

            case AddToSetOperation add:
                AddToSet(add.Key, add.Value, add.Score, keepScore: false);
                break;

            case AddRangeToSetOperation range:
                foreach (string value in range.Values) AddToSet(range.Key, value, 0.0, keepScore: true);
                break;

            case RemoveFromSetOperation remove:
                if (Revive(context.Set<SetRow>().Find(remove.Key, remove.Value)) is { } removed) context.Remove(removed);
                break;

            case RemoveSetOperation removeSet:
                RemoveWhere<SetRow>(x => x.Key == removeSet.Key);
                break;

            case InsertToListOperation insert:
                // A new item expires with the list, as on SQL Server
                DateTime? listExpireAt = context.Set<ListRow>().Local.FirstOrDefault(x => x.Key == insert.Key && x.ExpireAt != null)?.ExpireAt
                    ?? context.Set<ListRow>().Where(x => x.Key == insert.Key && x.ExpireAt != null).Select(static x => x.ExpireAt).FirstOrDefault();
                context.Add(new ListRow { Key = insert.Key, Value = insert.Value, ExpireAt = listExpireAt });
                break;

            case RemoveFromListOperation removeFromList:
                RemoveWhere<ListRow>(x => x.Key == removeFromList.Key && x.Value == removeFromList.Value);
                break;

            case TrimListOperation trim:
                TrimList(trim);
                break;

            case SetRangeInHashOperation hash:
                foreach (KeyValuePair<string, string> field in hash.Fields)
                {
                    HashRow? row = Revive(context.Set<HashRow>().Find(hash.Key, field.Key));
                    if (row != null) row.Value = field.Value;
                    else context.Add(new HashRow { Key = hash.Key, Field = field.Key, Value = field.Value, ExpireAt = HashExpireAt(hash.Key) });
                }

                break;

            case RemoveHashOperation removeHash:
                RemoveWhere<HashRow>(x => x.Key == removeHash.Key);
                break;

            case ExpireKeyOperation expireKey:
                ExpireKey(expireKey);
                break;

            default:
                throw new NotSupportedException($"Unknown storage operation {operation.GetType().Name}.");
        }
    }

    /// <summary>Saves what is tracked so far, when applying at once.</summary>
    public void Flush()
    {
        if (immediate) context.SaveChanges();
    }

    private void AddToSet(string key, string value, double score, bool keepScore)
    {
        SetRow? row = Revive(context.Set<SetRow>().Find(key, value));
        if (row == null)
        {
            context.Add(new SetRow { Key = key, Value = value, Score = score });
        }
        else if (!keepScore)
        {
            row.Score = score;
        }
    }

    private DateTime? HashExpireAt(string key)
    {
        return context.Set<HashRow>().Local.FirstOrDefault(x => x.Key == key && x.ExpireAt != null)?.ExpireAt
            ?? context.Set<HashRow>().Where(x => x.Key == key && x.ExpireAt != null).Select(static x => x.ExpireAt).FirstOrDefault();
    }

    private void RemoveWhere<TRow>(Expression<Func<TRow, bool>> predicate)
        where TRow : class
    {
        if (immediate)
        {
            Flush();
            context.Set<TRow>().Where(predicate).ExecuteDelete();
            return;
        }

        Func<TRow, bool> matches = predicate.Compile();
        foreach (TRow row in context.Set<TRow>().Local.Where(matches).ToList()) context.Remove(row);
        foreach (TRow row in context.Set<TRow>().Where(predicate).ToList()) context.Remove(row);
    }

    private void TrimList(TrimListOperation trim)
    {
        List<ListRow> newestFirst;
        if (immediate)
        {
            Flush();
            newestFirst = [.. context.Set<ListRow>().Where(x => x.Key == trim.Key).OrderByDescending(static x => x.Id)];
        }
        else
        {
            // Rows added in this context have no id yet: they are newer than every saved row, the last added first
            List<ListRow> added = [.. context.ChangeTracker.Entries<ListRow>()
                .Where(x => x.State == EntityState.Added && x.Entity.Key == trim.Key)
                .Select(static x => x.Entity)
                .Reverse()];
            newestFirst = [.. added, .. context.Set<ListRow>().Where(x => x.Key == trim.Key).OrderByDescending(static x => x.Id)];
        }

        for (int i = 0; i < newestFirst.Count; i++)
        {
            if (i < trim.KeepStartingFrom || i > trim.KeepEndingAt) context.Remove(newestFirst[i]);
        }
    }

    private void ExpireKey(ExpireKeyOperation expire)
    {
        DateTime? expireAt = expire.ExpireAt;

        switch (expire.Kind)
        {
            case KeyKind.Set when immediate:
                Flush();
                context.Set<SetRow>().Where(x => x.Key == expire.Key).ExecuteUpdate(s => s.SetProperty(x => x.ExpireAt, expireAt));
                break;
            case KeyKind.List when immediate:
                Flush();
                context.Set<ListRow>().Where(x => x.Key == expire.Key).ExecuteUpdate(s => s.SetProperty(x => x.ExpireAt, expireAt));
                break;
            case KeyKind.Hash when immediate:
                Flush();
                context.Set<HashRow>().Where(x => x.Key == expire.Key).ExecuteUpdate(s => s.SetProperty(x => x.ExpireAt, expireAt));
                break;
            case KeyKind.Set:
                foreach (SetRow row in Tracked<SetRow>(x => x.Key == expire.Key)) row.ExpireAt = expireAt;
                break;
            case KeyKind.List:
                foreach (ListRow row in Tracked<ListRow>(x => x.Key == expire.Key)) row.ExpireAt = expireAt;
                break;
            default:
                foreach (HashRow row in Tracked<HashRow>(x => x.Key == expire.Key)) row.ExpireAt = expireAt;
                break;
        }
    }

    /// <summary>The rows matching a condition: those saved, loaded and tracked, and those added in this context.</summary>
    private List<TRow> Tracked<TRow>(Expression<Func<TRow, bool>> predicate)
        where TRow : class
    {
        List<TRow> saved = [.. context.Set<TRow>().Where(predicate)];
        return [.. saved.Union(context.Set<TRow>().Local.Where(predicate.Compile()))];
    }

    /// <summary>A row the context deleted earlier in the transaction is written again instead of added twice.</summary>
    private TRow? Revive<TRow>(TRow? row)
        where TRow : class
    {
        if (row != null && context.Entry(row).State == EntityState.Deleted)
        {
            context.Entry(row).State = EntityState.Modified;
        }

        return row;
    }

    private static string? Truncate(string? reason) => reason?.Length > 100 ? reason[..100] : reason;
}
