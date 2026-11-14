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
                ExpireJob(expire);
                break;

            case AddJobStateOperation add:
                AddJobState(add);
                break;

            case AddToQueueOperation queue:
                context.Add(new JobQueueRow { Queue = queue.Queue, JobId = queue.JobId });
                break;

            case RemoveFromQueueOperation removal:
                if (!immediate)
                {
                    throw new NotSupportedException("Fetched jobs are acknowledged by the storage, not in an application's context.");
                }

                EfFetchedJob fetched = (EfFetchedJob)removal.FetchedJob; // This storage only fetches its own jobs
                Flush();
                context.Set<JobQueueRow>().Where(x => x.Id == fetched.EntryId && x.FetchToken == fetched.Token).ExecuteDelete();
                break;

            case SetJobParameterOperation parameter:
                if (!JobExists(parameter.JobId))
                {
                    break;
                }

                JobParameterRow? existing = Revive(context.Set<JobParameterRow>().Find(parameter.JobId, parameter.Name));
                if (existing != null)
                {
                    existing.Value = parameter.Value;
                }
                else
                {
                    context.Add(new JobParameterRow { JobId = parameter.JobId, Name = parameter.Name, Value = parameter.Value });
                }

                break;

            case CounterOperation counter:
                context.Add(new CounterRow { Key = counter.Key, Value = counter.Value, ExpireAt = counter.ExpireAt });
                break;

            case AddToSetOperation add:
                AddToSet(add.Key, add.Value, add.Score, keepScore: false);
                break;

            case AddRangeToSetOperation range:
                foreach (string value in range.Values)
                {
                    AddToSet(range.Key, value, 0.0, keepScore: true);
                }

                break;

            case RemoveFromSetOperation remove:
                if (Revive(context.Set<SetRow>().Find(remove.Key, remove.Value)) is { } removed)
                {
                    context.Remove(removed);
                }

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
                    if (row != null)
                    {
                        row.Value = field.Value;
                    }
                    else
                    {
                        context.Add(new HashRow { Key = hash.Key, Field = field.Key, Value = field.Value, ExpireAt = HashExpireAt(hash.Key) });
                    }
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

    /// <summary>
    /// Saves what is tracked so far, when applying at once, and forgets the saved rows: a statement that follows
    /// may delete or change them in the database, and a later write of the transaction must find them as they
    /// are there, not as the context saved them.
    /// </summary>
    public void Flush()
    {
        if (immediate)
        {
            context.SaveChanges();
            context.ChangeTracker.Clear();
        }
    }

    private void ExpireJob(ExpireJobOperation expire)
    {
        DateTime? expireAt = expire.ExpireAt;

        // Applied at once, the job row isn't loaded (it carries the job's arguments and state data): only the
        // column changes. Jobs are never added through this writer, so no tracked change of the row is pending.
        if (immediate && context.Set<JobRow>().Local.FindEntry(expire.JobId) == null)
        {
            context.Set<JobRow>().Where(x => x.Id == expire.JobId).ExecuteUpdate(s => s.SetProperty(x => x.ExpireAt, expireAt));
        }
        else if (context.Set<JobRow>().Find(expire.JobId) is { } expiring)
        {
            expiring.ExpireAt = expireAt;
        }
    }

    private void AddJobState(AddJobStateOperation add)
    {
        string? reason = Truncate(add.State.Reason);

        if (immediate && context.Set<JobRow>().Local.FindEntry(add.JobId) == null)
        {
            // The current state is written by one statement, without loading the row; the history row is saved
            // with the transaction's other rows, and only for a job that exists
            bool exists = add.SetCurrent
                ? context.Set<JobRow>().Where(x => x.Id == add.JobId).ExecuteUpdate(s => s
                    .SetProperty(x => x.StateName, add.State.Name)
                    .SetProperty(x => x.StateReason, reason)
                    .SetProperty(x => x.StateData, add.State.Data)
                    .SetProperty(x => x.StateChangedAt, add.State.CreatedAt)) > 0
                : context.Set<JobRow>().Any(x => x.Id == add.JobId);

            if (exists)
            {
                AddStateRow(add, reason);
            }

            return;
        }

        if (context.Set<JobRow>().Find(add.JobId) is not { } job)
        {
            return;
        }

        AddStateRow(add, reason);
        if (add.SetCurrent)
        {
            job.StateName = add.State.Name;
            job.StateReason = reason;
            job.StateData = add.State.Data;
            job.StateChangedAt = add.State.CreatedAt;
        }
    }

    private void AddStateRow(AddJobStateOperation add, string? reason)
    {
        context.Add(new StateRow { JobId = add.JobId, Name = add.State.Name, Reason = reason, CreatedAt = add.State.CreatedAt, Data = add.State.Data });
    }

    private bool JobExists(long jobId)
    {
        return context.Set<JobRow>().Local.FindEntry(jobId) != null || context.Set<JobRow>().Any(x => x.Id == jobId);
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
        foreach (TRow row in context.Set<TRow>().Local.Where(matches).ToList())
        {
            context.Remove(row);
        }

        foreach (TRow row in context.Set<TRow>().Where(predicate).ToList())
        {
            context.Remove(row);
        }
    }

    /// <summary>Keeps the items from position <c>KeepStartingFrom</c> to <c>KeepEndingAt</c>, newest first, and removes the others.</summary>
    private void TrimList(TrimListOperation trim)
    {
        if (immediate)
        {
            Flush();
            TrimSavedList(trim);
            return;
        }

        // Rows added in this context have no id yet: they are newer than every saved row, the last added first.
        // Saved rows are only identified (by id), never loaded, and removed through a stand-in of their key.
        List<ListRow> added = [.. context.ChangeTracker.Entries<ListRow>()
            .Where(x => x.State == EntityState.Added && x.Entity.Key == trim.Key)
            .Select(static x => x.Entity)
            .Reverse()];
        List<long> saved = [.. context.Set<ListRow>()
            .Where(x => x.Key == trim.Key)
            .OrderByDescending(static x => x.Id)
            .Select(static x => x.Id)
            .ToList()
            .Where(id => context.Set<ListRow>().Local.FindEntry(id)?.State != EntityState.Deleted)];

        for (int i = 0; i < added.Count + saved.Count; i++)
        {
            if (i >= trim.KeepStartingFrom && i <= trim.KeepEndingAt)
            {
                continue;
            }

            if (i < added.Count)
            {
                context.Remove(added[i]);
            }
            else
            {
                long id = saved[i - added.Count];
                ListRow row = context.Set<ListRow>().Local.FindEntry(id)?.Entity ?? context.Attach(new ListRow { Id = id, Key = trim.Key }).Entity;
                context.Remove(row);
            }
        }
    }

    /// <summary>
    /// Trims a saved list with at most three statements: the kept items are those between two ids, so the
    /// others are deleted by comparing ids, whatever the length of the list.
    /// </summary>
    private void TrimSavedList(TrimListOperation trim)
    {
        IQueryable<ListRow> list = context.Set<ListRow>().Where(x => x.Key == trim.Key);
        int keepFrom = Math.Max(0, trim.KeepStartingFrom);

        // The ids are read before anything is deleted. Items another transaction inserts meanwhile are newer than
        // the newest one read, so they are never deleted.
        long? newest = IdAt(list, 0);
        if (newest == null)
        {
            return;
        }

        // Nothing is kept from a range that is empty or starts past the end; everything from the newest kept item
        // on is kept when the range ends past the end
        long? newestKept = trim.KeepEndingAt < keepFrom ? null : keepFrom == 0 ? newest : IdAt(list, keepFrom);
        if (newestKept == null)
        {
            list.Where(x => x.Id <= newest).ExecuteDelete();
            return;
        }

        long? oldestKept = IdAt(list, trim.KeepEndingAt);
        if (newestKept != newest)
        {
            list.Where(x => x.Id > newestKept && x.Id <= newest).ExecuteDelete();
        }

        if (oldestKept != null)
        {
            list.Where(x => x.Id < oldestKept).ExecuteDelete();
        }
    }

    // The id of the item at a position, newest first
    private static long? IdAt(IQueryable<ListRow> list, int position)
    {
        return list.OrderByDescending(static x => x.Id).Select(static x => (long?)x.Id).Skip(position).FirstOrDefault();
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
                foreach (SetRow row in Tracked<SetRow>(x => x.Key == expire.Key))
                {
                    row.ExpireAt = expireAt;
                }

                break;
            case KeyKind.List:
                foreach (ListRow row in Tracked<ListRow>(x => x.Key == expire.Key))
                {
                    row.ExpireAt = expireAt;
                }

                break;
            default:
                foreach (HashRow row in Tracked<HashRow>(x => x.Key == expire.Key))
                {
                    row.ExpireAt = expireAt;
                }

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
