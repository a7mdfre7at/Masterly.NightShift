// This file is part of Hangfire. Copyright © 2021 Hangfire OÜ.
//
// Hangfire is free software: you can redistribute it and/or modify
// it under the terms of the GNU Lesser General Public License as
// published by the Free Software Foundation, either version 3
// of the License, or any later version.
//
// Hangfire is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU Lesser General Public License for more details.
//
// You should have received a copy of the GNU Lesser General Public
// License along with Hangfire. If not, see <http://www.gnu.org/licenses/>.

using System.Collections.Concurrent;
using System.Text;
using Masterly.NightShift.Logging;
using Masterly.NightShift.Server;

namespace Masterly.NightShift.SqlServer;

internal sealed class SqlServerHeartbeatProcess([NotNull] SqlServerStorage storage) : IBackgroundProcess
{
    // Three parameters per job, well below the 2100 a command may have
    private const int MaxJobsPerCommand = 500;

    // A keep-alive seeks each row it renews and skips locked rows, so it is done in milliseconds. One still running
    // after this is blocked (by a table lock, or on a connection that stopped answering): it gives up, and the next
    // pass, a second later, tries again, instead of holding its jobs, and the commits of their workers, for the
    // whole command timeout.
    private const int KeepAliveTimeoutSeconds = 5;

    // The batch sizes a command is padded to, so that a few query plans serve every pass
    private static readonly int[] BatchSizes = [1, 10, 50, 100, MaxJobsPerCommand];

    private readonly ILog _logger = LogProvider.GetLogger(typeof(SqlServerHeartbeatProcess));

    private readonly ConcurrentDictionary<SqlServerTimeoutJob, object> _items =
        new();

    internal int TrackedCount => _items.Count;

    public void Track(SqlServerTimeoutJob item)
    {
        _items.TryAdd(item, null!); // Used as a set: the value is ignored
    }

    public void Untrack(SqlServerTimeoutJob item)
    {
        _items.TryRemove(item, out _);
    }

    public void Execute(BackgroundProcessContext context)
    {
        Execute(context.ShutdownToken);
    }

    public void Execute(CancellationToken cancellationToken)
    {
        KeepAlive(_items.Keys);

        cancellationToken.Wait(TimeSpan.FromSeconds(1));
    }

    /// <summary>
    /// Renews the rows of the jobs whose keep-alive is due, with one command per pass (per 500 jobs). The command
    /// skips locked rows (<c>readpast</c>), and a job whose own lock is held is left out, so a job that can't be
    /// renewed now is tried again on the next pass instead of holding up the others until their rows time out
    /// and other workers fetch them while they still run. The connection is opened before any job's lock is
    /// taken, and a batch holds the locks of its jobs only while its commands run, so a worker that commits the
    /// removal of its job waits for a keep-alive at most that long, never while the keep-alive waits for a pooled
    /// connection.
    /// </summary>
    internal void KeepAlive([NotNull] IEnumerable<SqlServerTimeoutJob> jobs)
    {
        long now = TimestampHelper.GetTimestamp();
        List<SqlServerTimeoutJob> due = [.. jobs.Where(job => job.IsKeepAliveDue(now))];

        if (due.Count == 0)
        {
            return;
        }

        try
        {
            storage.UseConnection(null, static (_, connection, context) =>
            {
                for (int index = 0; index < context.Due.Count; index += MaxJobsPerCommand)
                {
                    List<SqlServerTimeoutJob> batch = context.Due.GetRange(index, Math.Min(MaxJobsPerCommand, context.Due.Count - index));
                    context.Process.KeepAlive(connection, batch, context.Now);
                }

                return true;
            }, (Process: this, Due: due, Now: now));
        }
        catch (Exception ex) when (ex.IsCatchableExceptionType())
        {
            // The jobs are still due: the next pass tries again
            _logger.DebugException($"Unable to execute keep-alive query for {due.Count} fetched job(s)", ex);
        }
    }

    private void KeepAlive(DbConnection connection, List<SqlServerTimeoutJob> batch, long now)
    {
        List<KeyValuePair<SqlServerTimeoutJob, DateTime>> started = [];

        try
        {
            foreach (SqlServerTimeoutJob job in batch)
            {
                if (job.TryBeginKeepAlive(now, out DateTime fetchedAt))
                {
                    started.Add(new KeyValuePair<SqlServerTimeoutJob, DateTime>(job, fetchedAt));
                }
            }

            if (started.Count > 0)
            {
                KeepAliveStarted(connection, started, now);
            }
        }
        finally
        {
            foreach (KeyValuePair<SqlServerTimeoutJob, DateTime> job in started)
            {
                job.Key.EndKeepAlive();
            }
        }
    }

    private void KeepAliveStarted(DbConnection connection, List<KeyValuePair<SqlServerTimeoutJob, DateTime>> jobs, long now)
    {
        Dictionary<long, DateTime> renewed = Renew(connection, jobs);
        List<KeyValuePair<SqlServerTimeoutJob, DateTime>> left = [];

        foreach (KeyValuePair<SqlServerTimeoutJob, DateTime> job in jobs)
        {
            if (renewed.TryGetValue(job.Key.Id, out DateTime fetchedAt))
            {
                job.Key.KeepAliveRenewed(now, fetchedAt);
            }
            else
            {
                left.Add(job);
            }
        }

        if (left.Count == 0)
        {
            return;
        }

        // A row that wasn't renewed was fetched again by another worker, or it is locked, or it is gone. Only
        // the first is certain from what is committed; the others are tried again on the next pass.
        Dictionary<long, DateTime?> committed = ReadCommitted(connection, left);
        foreach (KeyValuePair<SqlServerTimeoutJob, DateTime> job in left)
        {
            if (committed.TryGetValue(job.Key.Id, out DateTime? fetchedAt) && fetchedAt != job.Value)
            {
                job.Key.KeepAliveFoundTakenOver(now);
            }
        }
    }

    private Dictionary<long, DateTime> Renew(DbConnection connection, List<KeyValuePair<SqlServerTimeoutJob, DateTime>> jobs)
    {
        using DbCommand command = CreateRenewCommand(storage, connection, jobs);
        Dictionary<long, DateTime> renewed = [];
        using DbDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            renewed[Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture)] = reader.GetDateTime(1);
        }

        return renewed;
    }

    // What is committed, read from the rows nobody holds a lock on
    private Dictionary<long, DateTime?> ReadCommitted(DbConnection connection, List<KeyValuePair<SqlServerTimeoutJob, DateTime>> jobs)
    {
        using DbCommand command = CreateReadCommand(storage, connection, jobs);
        Dictionary<long, DateTime?> committed = [];
        using DbDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            committed[Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture)] =
                reader.IsDBNull(1) ? null : reader.GetDateTime(1);
        }

        return committed;
    }

    /// <summary>
    /// The update that renews <paramref name="jobs"/>, each matched by the fetch time it was renewed or fetched
    /// with, padded to one of the <see cref="BatchSizes"/>.
    /// </summary>
    internal static DbCommand CreateRenewCommand(
        SqlServerStorage storage,
        DbConnection connection,
        List<KeyValuePair<SqlServerTimeoutJob, DateTime>> jobs)
    {
        DbCommand command = connection.Create(string.Empty, timeout: GetCommandTimeout(storage));
        StringBuilder text = new("set nocount on;set transaction isolation level read committed;\n");
        text.Append("update JQ set FetchedAt = getutcdate() output INSERTED.Id, INSERTED.FetchedAt from (values ");

        int count = GetPaddedCount(jobs.Count);
        for (int i = 0; i < count; i++)
        {
            text.Append(CultureInfo.InvariantCulture, $"{(i > 0 ? ", " : string.Empty)}(@queue{i}, @id{i}, @fetchedAt{i})");
            AddJob(command, i, i < jobs.Count ? jobs[i].Key : null);
            command.AddParameter($"@fetchedAt{i}", i < jobs.Count ? jobs[i].Value : null, DbType.DateTime);
        }

        text.Append(") as T (Queue, Id, FetchedAt) inner loop join ");
        text.Append(storage.GetQueryFromTemplate(static schemaName => $"[{schemaName}].JobQueue"));
        text.Append(" JQ with (forceseek, readpast, rowlock) on JQ.Queue = T.Queue and JQ.Id = T.Id where JQ.FetchedAt = T.FetchedAt;");
        command.CommandText = text.ToString();
        return command;
    }

    /// <summary>The select of the committed rows of <paramref name="jobs"/>, padded as the renewal is.</summary>
    internal static DbCommand CreateReadCommand(
        SqlServerStorage storage,
        DbConnection connection,
        List<KeyValuePair<SqlServerTimeoutJob, DateTime>> jobs)
    {
        DbCommand command = connection.Create(string.Empty, timeout: GetCommandTimeout(storage));
        StringBuilder text = new("set nocount on;set transaction isolation level read committed;\n");
        text.Append("select JQ.Id, JQ.FetchedAt from (values ");

        int count = GetPaddedCount(jobs.Count);
        for (int i = 0; i < count; i++)
        {
            text.Append(CultureInfo.InvariantCulture, $"{(i > 0 ? ", " : string.Empty)}(@queue{i}, @id{i})");
            AddJob(command, i, i < jobs.Count ? jobs[i].Key : null);
        }

        text.Append(") as T (Queue, Id) inner loop join ");
        text.Append(storage.GetQueryFromTemplate(static schemaName => $"[{schemaName}].JobQueue"));
        text.Append(" JQ with (forceseek, readcommittedlock, readpast) on JQ.Queue = T.Queue and JQ.Id = T.Id;");
        command.CommandText = text.ToString();
        return command;
    }

    // A padding row (no job) holds nulls, which match no row. The queue's size is the column's, so the
    // parameter is declared the same whatever the name's length.
    private static void AddJob(DbCommand command, int index, SqlServerTimeoutJob? job)
    {
        command
            .AddParameter($"@queue{index}", job?.Queue, DbType.String, SqlServerLimits.QueueNameLength)
            .AddParameter($"@id{index}", job?.Id, DbType.Int64);
    }

    private static int GetPaddedCount(int count)
    {
        return BatchSizes.First(size => size >= count);
    }

    private static int GetCommandTimeout(SqlServerStorage storage)
    {
        return storage.CommandTimeout is { } configured and > 0
            ? Math.Min(configured, KeepAliveTimeoutSeconds)
            : KeepAliveTimeoutSeconds;
    }
}
