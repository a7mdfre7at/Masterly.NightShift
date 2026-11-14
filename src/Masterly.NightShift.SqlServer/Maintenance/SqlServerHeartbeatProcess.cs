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
    /// and other workers fetch them while they still run.
    /// </summary>
    internal void KeepAlive([NotNull] IEnumerable<SqlServerTimeoutJob> jobs)
    {
        long now = TimestampHelper.GetTimestamp();
        List<KeyValuePair<SqlServerTimeoutJob, DateTime>> started = [];

        try
        {
            foreach (SqlServerTimeoutJob job in jobs)
            {
                if (job.TryBeginKeepAlive(now, out DateTime fetchedAt))
                {
                    started.Add(new KeyValuePair<SqlServerTimeoutJob, DateTime>(job, fetchedAt));
                }
            }

            for (int index = 0; index < started.Count; index += MaxJobsPerCommand)
            {
                KeepAlive(started.GetRange(index, Math.Min(MaxJobsPerCommand, started.Count - index)), now);
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

    private void KeepAlive(List<KeyValuePair<SqlServerTimeoutJob, DateTime>> jobs, long now)
    {
        try
        {
            Dictionary<long, DateTime> renewed = Renew(jobs);
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
            Dictionary<long, DateTime?> committed = ReadCommitted(left);
            foreach (KeyValuePair<SqlServerTimeoutJob, DateTime> job in left)
            {
                if (committed.TryGetValue(job.Key.Id, out DateTime? fetchedAt) && fetchedAt != job.Value)
                {
                    job.Key.KeepAliveFoundTakenOver(now);
                }
            }
        }
        catch (Exception ex) when (ex.IsCatchableExceptionType())
        {
            _logger.DebugException($"Unable to execute keep-alive query for {jobs.Count} fetched job(s)", ex);
        }
    }

    private Dictionary<long, DateTime> Renew(List<KeyValuePair<SqlServerTimeoutJob, DateTime>> jobs)
    {
        return storage.UseConnection(null, static (storage, connection, jobs) =>
        {
            using DbCommand command = connection.Create(string.Empty, timeout: storage.CommandTimeout);
            StringBuilder text = new("set nocount on;set transaction isolation level read committed;\n");
            text.Append("update JQ set FetchedAt = getutcdate() output INSERTED.Id, INSERTED.FetchedAt from (values ");

            for (int i = 0; i < jobs.Count; i++)
            {
                text.Append(CultureInfo.InvariantCulture, $"{(i > 0 ? ", " : string.Empty)}(@queue{i}, @id{i}, @fetchedAt{i})");
                command
                    .AddParameter($"@queue{i}", jobs[i].Key.Queue, DbType.String)
                    .AddParameter($"@id{i}", jobs[i].Key.Id, DbType.Int64)
                    .AddParameter($"@fetchedAt{i}", jobs[i].Value, DbType.DateTime);
            }

            text.Append(") as T (Queue, Id, FetchedAt) inner loop join ");
            text.Append(storage.GetQueryFromTemplate(static schemaName => $"[{schemaName}].JobQueue"));
            text.Append(" JQ with (forceseek, readpast, rowlock) on JQ.Queue = T.Queue and JQ.Id = T.Id where JQ.FetchedAt = T.FetchedAt;");
            command.CommandText = text.ToString();

            Dictionary<long, DateTime> renewed = [];
            using DbDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                renewed[Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture)] = reader.GetDateTime(1);
            }

            return renewed;
        }, jobs);
    }

    // What is committed, read from the rows nobody holds a lock on
    private Dictionary<long, DateTime?> ReadCommitted(List<KeyValuePair<SqlServerTimeoutJob, DateTime>> jobs)
    {
        return storage.UseConnection(null, static (storage, connection, jobs) =>
        {
            using DbCommand command = connection.Create(string.Empty, timeout: storage.CommandTimeout);
            StringBuilder text = new("set nocount on;set transaction isolation level read committed;\n");
            text.Append("select JQ.Id, JQ.FetchedAt from (values ");

            for (int i = 0; i < jobs.Count; i++)
            {
                text.Append(CultureInfo.InvariantCulture, $"{(i > 0 ? ", " : string.Empty)}(@queue{i}, @id{i})");
                command
                    .AddParameter($"@queue{i}", jobs[i].Key.Queue, DbType.String)
                    .AddParameter($"@id{i}", jobs[i].Key.Id, DbType.Int64);
            }

            text.Append(") as T (Queue, Id) inner loop join ");
            text.Append(storage.GetQueryFromTemplate(static schemaName => $"[{schemaName}].JobQueue"));
            text.Append(" JQ with (forceseek, readcommittedlock, readpast) on JQ.Queue = T.Queue and JQ.Id = T.Id;");
            command.CommandText = text.ToString();

            Dictionary<long, DateTime?> committed = [];
            using DbDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                committed[Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture)] =
                    reader.IsDBNull(1) ? null : reader.GetDateTime(1);
            }

            return committed;
        }, jobs);
    }
}
