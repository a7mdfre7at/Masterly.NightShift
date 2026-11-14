// This file is part of Hangfire. Copyright © 2013-2014 Hangfire OÜ.
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
using System.Text.RegularExpressions;
using Masterly.NightShift.States;

// ReSharper disable RedundantAnonymousTypePropertyName

namespace Masterly.NightShift.SqlServer;

internal sealed class SqlServerWriteOnlyTransaction : JobStorageTransaction
{
    private readonly Queue<Action<DbConnection, DbTransaction>> _queueCommandQueue = new();
    private readonly HashSet<string> _queuesToSignal = [];

    private readonly SqlServerStorage _storage;
    private readonly SqlServerConnection _connection;

    // Job parameters are written before everything else, in the order of their job and name. Outside a
    // transaction, SqlServerConnection.SetJobParameter locks the parameter's row and then reads the job's row
    // (the foreign key of a new parameter). A transaction that locked the job's row first (a state, an expiration)
    // and the parameter's row second would deadlock with it.
    private readonly SortedDictionary<(long JobId, string Name), List<Func<DbConnection, DbCommand>>> _parameterCommands = new(ParameterOrder);
    private readonly SortedDictionary<long, List<Func<DbConnection, DbCommand>>> _jobCommands = [];
    private readonly SortedDictionary<string, List<Func<DbConnection, DbCommand>>> _counterCommands = [];
    private readonly SortedDictionary<string, List<Func<DbConnection, DbCommand>>> _hashCommands = [];
    private readonly SortedDictionary<string, List<Func<DbConnection, DbCommand>>> _listCommands = [];
    private readonly SortedDictionary<string, List<Func<DbConnection, DbCommand>>> _setCommands = [];
    private readonly SortedDictionary<string, List<Func<DbConnection, DbCommand>>> _queueCommands = [];
    private readonly List<Tuple<DbCommand, DbParameter, string>> _lockCommands = [];

    private readonly List<SqlServerConnection.DisposableLock> _acquiredLocks = [];

    private readonly SortedSet<string> _lockedResources = [];

    // The fetched jobs it removes from their queues, in the order of their row
    private readonly SortedDictionary<long, SqlServerTimeoutJob> _removedJobs = [];

    // A transaction that only moves jobs to Processing doesn't have to wait for the disk: if it is lost, the
    // jobs are fetched and run again, as after any failure before their final state is stored. A job parameter
    // is not written again that way, so a transaction that writes one waits, as the write outside a transaction does
    private bool _movesToProcessing;
    private bool _hasOtherStateOrQueueChanges;
    private bool _writesJobParameters;

    public SqlServerWriteOnlyTransaction([NotNull] SqlServerConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        _connection = connection;
        _storage = connection.Storage;
    }

    public bool Committed { get; private set; }

    /// <summary>
    /// Whether the commit may return before the transaction is on the disk, where the database allows it
    /// (<see cref="SqlServerStorage.UseDelayedDurability"/>): only when all it does is move jobs to Processing.
    /// </summary>
    internal bool CommitsWithoutWaitingForTheDisk => _movesToProcessing && !_hasOtherStateOrQueueChanges && !_writesJobParameters;

    public override void Commit()
    {
        // The delete of a job this removes from its queue matches the FetchedAt read when its command is built:
        // no keep-alive may change it before the commit is done (SqlServerTimeoutJob.EnterCommit)
        List<SqlServerTimeoutJob> heldJobs = [];

        try
        {
            foreach (SqlServerTimeoutJob job in _removedJobs.Values)
            {
                job.EnterCommit();
                heldJobs.Add(job);
            }

            CommitAndReleaseLocks();
        }
        finally
        {
            foreach (SqlServerTimeoutJob job in heldJobs)
            {
                job.ExitCommit();
            }
        }

        TrySignalListeningWorkers();
    }

    private void CommitAndReleaseLocks()
    {
        try
        {
            if (CanCommitTogether())
            {
                // Callers that commit at the same time share a round trip and a commit; a caller on its own
                // goes straight on
                _storage.Commits.Write(this, static transaction => transaction.CommitAlone());
            }
            else
            {
                CommitAlone();
            }

            Committed = true;
        }
        finally
        {
            foreach (SqlServerConnection.DisposableLock acquiredLock in _acquiredLocks)
            {
                acquiredLock.Dispose();
            }
        }
    }

    private void CommitAlone()
    {
        if (!TryCommitInOneRoundTrip())
        {
            CommitWithApiTransaction();
        }
    }

    // Small transactions that take no locks, such as the first state of a new job. A transaction with locks
    // stays on its own: merged with others it would hold them longer and in an order nobody chose. A job
    // parameter takes no such lock, only its row, like a state: a transaction that writes one can be merged.
    private bool CanCommitTogether()
    {
        if (_storage.Options.MaxConcurrentWriters <= 0 ||
            !_storage.CommandBatchMaxTimeout.HasValue ||
            _connection.DedicatedConnection != null ||
            _queueCommandQueue.Count > 0 ||
            _lockedResources.Count > 0 ||
            _lockCommands.Count > 0 ||
            _acquiredLocks.Count > 0 ||
            CommitsWithoutWaitingForTheDisk)
        {
            return false;
        }

        int commandCount = Count(_parameterCommands) + Count(_jobCommands) + Count(_counterCommands) + Count(_hashCommands) +
            Count(_listCommands) + Count(_setCommands) + Count(_queueCommands);

        return commandCount is > 0 and <= MaxCommandsToCommitTogether;
    }

    /// <summary>
    /// Commits several transactions as one: one command, one round trip, one commit. All of them are stored or
    /// none is; when it throws, each transaction is committed on its own by its caller.
    /// </summary>
    internal static void CommitTogether(SqlServerStorage storage, IReadOnlyList<SqlServerWriteOnlyTransaction> transactions)
    {
        storage.UseConnection(null, static (storage, connection, transactions) =>
        {
            List<DbCommand> commands = [];

            try
            {
                // The job parameters of every transaction first, in one order, as in a transaction on its own
                SortedDictionary<(long JobId, string Name), List<Func<DbConnection, DbCommand>>> parameterCommands = new(ParameterOrder);
                foreach (SqlServerWriteOnlyTransaction transaction in transactions)
                {
                    foreach (KeyValuePair<(long JobId, string Name), List<Func<DbConnection, DbCommand>>> pair in transaction._parameterCommands)
                    {
                        foreach (Func<DbConnection, DbCommand> command in pair.Value)
                        {
                            AddCommand(parameterCommands, pair.Key, command);
                        }
                    }
                }

                CreateCommands(parameterCommands, connection, commands);

                foreach (SqlServerWriteOnlyTransaction transaction in transactions)
                {
                    CreateCommands(transaction._jobCommands, connection, commands);
                    CreateCommands(transaction._counterCommands, connection, commands);
                    CreateCommands(transaction._hashCommands, connection, commands);
                    CreateCommands(transaction._listCommands, connection, commands);
                    CreateCommands(transaction._setCommands, connection, commands);
                    CreateCommands(transaction._queueCommands, connection, commands);
                }

                int parameterCount = 0;
                foreach (DbCommand command in commands)
                {
                    parameterCount += command.Parameters.Count;
                }

                if (parameterCount > MaxParametersInOneCommand)
                {
                    throw new InvalidOperationException("The transactions have too many parameters to be committed together.");
                }

                using DbCommand merged = connection.Create(string.Empty);
                // At read committed whatever the pooled session was left at: see TryCommitInOneRoundTrip
                StringBuilder text = new("set xact_abort on;set nocount on;set transaction isolation level read committed;begin tran;\n");

                for (int i = 0; i < commands.Count; i++)
                {
                    AppendRenamed(text, merged, commands[i], i);
                }

                text.Append("commit tran;");
                merged.CommandText = text.ToString();

                int? timeout = storage.CommandTimeout;
                if (timeout is > 0)
                {
                    long total = (long)timeout.Value * Math.Max(1, commands.Count);
                    merged.CommandTimeout = (int)Math.Min(total, storage.CommandBatchMaxTimeout ?? timeout.Value);
                }
                else if (timeout.HasValue)
                {
                    merged.CommandTimeout = timeout.Value;
                }

                try
                {
                    merged.ExecuteNonQuery();
                }
                catch (Exception ex) when (ex.IsCatchableExceptionType())
                {
                    TryRollback(storage, connection);
                    throw;
                }

                return true;
            }
            finally
            {
                foreach (DbCommand command in commands)
                {
                    command.Dispose();
                }
            }
        }, transactions);
    }

    // The transaction as SqlClient offers it: one round trip to begin it, at least one for the commands and
    // one to commit. Used for what the single command below can't carry.
    private void CommitWithApiTransaction()
    {
        _storage.UseTransaction(_connection.DedicatedConnection, static (storage, connection, transaction, ctx) =>
        {
            using SqlCommandBatch commandBatch = new(connection, transaction, preferBatching: storage.CommandBatchMaxTimeout.HasValue);
            commandBatch.Append(connection.Create("set xact_abort on;set nocount on;"));

            foreach (string lockedResource in ctx._lockedResources)
            {
                commandBatch.Append(connection
                    .Create("exec sp_getapplock @Resource=@resource, @LockMode=N'Exclusive'")
                    .AddParameter("@resource", SqlServerLimits.GetLockResource(lockedResource), DbType.String, size: SqlServerLimits.LockResourceLength));
            }

            AppendBatch(ctx._parameterCommands, commandBatch);
            AppendBatch(ctx._jobCommands, commandBatch);
            AppendBatch(ctx._counterCommands, commandBatch);
            AppendBatch(ctx._hashCommands, commandBatch);
            AppendBatch(ctx._listCommands, commandBatch);
            AppendBatch(ctx._setCommands, commandBatch);
            AppendBatch(ctx._queueCommands, commandBatch);

            foreach (Tuple<DbCommand, DbParameter, string> command in ctx._lockCommands)
            {
                commandBatch.Append(command.Item1);
            }

            commandBatch.CommandTimeout = storage.CommandTimeout;
            commandBatch.CommandBatchMaxTimeout = storage.CommandBatchMaxTimeout;

            commandBatch.ExecuteNonQuery();
            foreach (SqlServerConnection.DisposableLock acquiredLock in ctx._acquiredLocks)
            {
                acquiredLock.TryReportReleased();
            }

            foreach (Tuple<DbCommand, DbParameter, string> lockCommand in ctx._lockCommands)
            {
                int? releaseResult = lockCommand.Item2.GetParameterValue<int?>();
                if (releaseResult.HasValue && releaseResult.Value < 0)
                {
                    throw new SqlServerDistributedLockException($"Could not release a lock on the resource '{lockCommand.Item3}': Server returned the '{releaseResult}' error.");
                }
            }

            foreach (Action<DbConnection, DbTransaction> queueCommand in ctx._queueCommandQueue)
            {
                queueCommand(connection, transaction);
            }
        }, this);
    }

    /// <summary>
    /// Sends the whole transaction as one command: <c>begin tran</c>, every command with its parameters renamed
    /// so they don't clash, and <c>commit tran</c>. One round trip instead of three or more. Returns
    /// <c>false</c>, having done nothing, when the transaction doesn't fit: batching is turned off, a custom
    /// queue wants the transaction object, or there are too many commands or parameters for one command.
    /// </summary>
    private bool TryCommitInOneRoundTrip()
    {
        if (!_storage.CommandBatchMaxTimeout.HasValue || _queueCommandQueue.Count > 0)
        {
            return false;
        }

        // A large transaction, such as the states of a group of jobs, is cheaper as a batch of commands
        int commandCount = _lockedResources.Count + _lockCommands.Count +
            Count(_parameterCommands) + Count(_jobCommands) + Count(_counterCommands) + Count(_hashCommands) +
            Count(_listCommands) + Count(_setCommands) + Count(_queueCommands);

        if (commandCount > MaxCommandsInOneCommand)
        {
            return false;
        }

        return _storage.UseConnection(_connection.DedicatedConnection, static (storage, connection, ctx) =>
        {
            List<DbCommand> commands = [];
            List<KeyValuePair<DbParameter, string>> releaseResults = [];

            try
            {
                foreach (string lockedResource in ctx._lockedResources)
                {
                    commands.Add(connection
                        .Create("exec sp_getapplock @Resource=@resource, @LockMode=N'Exclusive'")
                        .AddParameter("@resource", SqlServerLimits.GetLockResource(lockedResource), DbType.String, size: SqlServerLimits.LockResourceLength));
                }

                CreateCommands(ctx._parameterCommands, connection, commands);
                CreateCommands(ctx._jobCommands, connection, commands);
                CreateCommands(ctx._counterCommands, connection, commands);
                CreateCommands(ctx._hashCommands, connection, commands);
                CreateCommands(ctx._listCommands, connection, commands);
                CreateCommands(ctx._setCommands, connection, commands);
                CreateCommands(ctx._queueCommands, connection, commands);

                int parameterCount = ctx._lockCommands.Count * 3;
                foreach (DbCommand command in commands)
                {
                    parameterCount += command.Parameters.Count;
                }

                if (parameterCount > MaxParametersInOneCommand)
                {
                    return false;
                }

                if (commands.Count == 0 && ctx._lockCommands.Count == 0)
                {
                    // Nothing to write
                    return true;
                }

                using DbCommand merged = connection.Create(string.Empty);
                StringBuilder text = new();

                // At read committed, as SqlClient's transaction (CommitWithApiTransaction) begins it. A pooled session
                // keeps the isolation level its last user set, and the application may share the pool: under
                // repeatable read, the check that a parameter's job exists would keep a shared lock on the job's row
                // until the commit, and two transactions that write a job's parameter and its state would deadlock
                text.Append("set xact_abort on;set nocount on;set transaction isolation level read committed;begin tran;\n");

                for (int i = 0; i < commands.Count; i++)
                {
                    AppendRenamed(text, merged, commands[i], i);
                }

                // The locks this transaction took on its connection are released by it, as its last commands
                for (int i = 0; i < ctx._lockCommands.Count; i++)
                {
                    Tuple<DbCommand, DbParameter, string> lockCommand = ctx._lockCommands[i];
                    string suffix = i.ToString(CultureInfo.InvariantCulture);

                    text.Append(CultureInfo.InvariantCulture, $"exec @lockResult{suffix} = sp_releaseapplock @Resource = @lockResource{suffix}, @LockOwner = @lockOwner{suffix};\n");

                    merged.AddParameter("@lockResource" + suffix, lockCommand.Item1.Parameters["@Resource"].Value, DbType.String, size: SqlServerLimits.LockResourceLength);
                    merged.AddParameter("@lockOwner" + suffix, lockCommand.Item1.Parameters["@LockOwner"].Value, DbType.String, size: 32);

                    DbParameter result = merged.CreateParameter();
                    result.ParameterName = "@lockResult" + suffix;
                    result.DbType = DbType.Int32;
                    result.Direction = ParameterDirection.Output;
                    result.Value = DBNull.Value;
                    merged.Parameters.Add(result);

                    releaseResults.Add(new KeyValuePair<DbParameter, string>(result, lockCommand.Item3));
                }

                text.Append(storage.UseDelayedDurability && ctx.CommitsWithoutWaitingForTheDisk
                    ? "commit tran with (delayed_durability = on);"
                    : "commit tran;");
                merged.CommandText = text.ToString();

                int? timeout = storage.CommandTimeout;
                if (timeout is > 0)
                {
                    // As for a batch: the time of every command, up to the limit for a batch
                    long total = (long)timeout.Value * Math.Max(1, commands.Count);
                    merged.CommandTimeout = (int)Math.Min(total, storage.CommandBatchMaxTimeout!.Value); // Checked at the top
                }
                else if (timeout.HasValue)
                {
                    merged.CommandTimeout = timeout.Value;
                }

                try
                {
                    merged.ExecuteNonQuery();
                }
                catch (Exception ex) when (ex.IsCatchableExceptionType())
                {
                    // XACT_ABORT rolled the transaction back for most errors. Make sure, so no lock outlives
                    // this call on a pooled connection.
                    TryRollback(storage, connection);
                    throw;
                }

                foreach (SqlServerConnection.DisposableLock acquiredLock in ctx._acquiredLocks)
                {
                    acquiredLock.TryReportReleased();
                }

                foreach (KeyValuePair<DbParameter, string> release in releaseResults)
                {
                    int? releaseResult = release.Key.GetParameterValue<int?>();
                    if (releaseResult.HasValue && releaseResult.Value < 0)
                    {
                        throw new SqlServerDistributedLockException($"Could not release a lock on the resource '{release.Value}': Server returned the '{releaseResult}' error.");
                    }
                }

                return true;
            }
            finally
            {
                foreach (DbCommand command in commands)
                {
                    command.Dispose();
                }
            }
        }, this);
    }

    // SQL Server allows 2100 parameters in a command
    private const int MaxParametersInOneCommand = 2000;
    private const int MaxCommandsInOneCommand = 64;
    private const int MaxCommandsToCommitTogether = 12;

    private static int Count<TKey>(SortedDictionary<TKey, List<Func<DbConnection, DbCommand>>> collection)
        where TKey : notnull
    {
        int count = 0;
        foreach (KeyValuePair<TKey, List<Func<DbConnection, DbCommand>>> pair in collection)
        {
            count += pair.Value.Count;
        }

        return count;
    }

    private static readonly ConcurrentDictionary<(string Text, int Index), string> RenamedTexts = new();

    // By job, then by name. Names that differ only in case share an entry, so that on a database that takes them
    // for one name (a case-insensitive collation) their writes stay in the order they were made.
    private static readonly Comparer<(long JobId, string Name)> ParameterOrder = Comparer<(long JobId, string Name)>.Create(
        static (x, y) => x.JobId != y.JobId
            ? x.JobId.CompareTo(y.JobId)
            : StringComparer.OrdinalIgnoreCase.Compare(x.Name, y.Name));

    private static void CreateCommands<TKey>(
        SortedDictionary<TKey, List<Func<DbConnection, DbCommand>>> collection,
        DbConnection connection,
        List<DbCommand> commands)
        where TKey : notnull
    {
        foreach (KeyValuePair<TKey, List<Func<DbConnection, DbCommand>>> pair in collection)
        {
            foreach (Func<DbConnection, DbCommand> command in pair.Value)
            {
                commands.Add(command(connection));
            }
        }
    }

    // Adds a command's text and parameters to the merged command, with "_{index}" added to every parameter
    // name, so that the same name in two commands stays two parameters
    private static void AppendRenamed(StringBuilder text, DbCommand merged, DbCommand command, int index)
    {
        string suffix = "_" + index.ToString(CultureInfo.InvariantCulture);

        if (command.Parameters.Count == 0)
        {
            text.Append(command.CommandText);
        }
        else
        {
            string[] names = new string[command.Parameters.Count];
            for (int i = 0; i < names.Length; i++)
            {
                names[i] = command.Parameters[i].ParameterName;
            }

            text.Append(RenamedTexts.GetOrAdd((command.CommandText, index), static (key, parameterNames) =>
            {
                string renamed = key.Text;
                string keySuffix = "_" + key.Index.ToString(CultureInfo.InvariantCulture);

                foreach (string name in parameterNames)
                {
                    // The whole name only, in its exact case: "@key" but not "@key1", "@@rowcount" or "@Resource="
                    renamed = Regex.Replace(
                        renamed,
                        $@"(?<![\w@#$]){Regex.Escape(name)}(?![\w@#$])",
                        name + keySuffix,
                        RegexOptions.CultureInvariant);
                }

                return renamed;
            }, names));

            DbParameter[] parameters = new DbParameter[command.Parameters.Count];
            command.Parameters.CopyTo(parameters, 0);
            command.Parameters.Clear();

            foreach (DbParameter parameter in parameters)
            {
                parameter.ParameterName += suffix;
                merged.Parameters.Add(parameter);
            }
        }

        text.Append(";\n");
    }

    private static void TryRollback(SqlServerStorage storage, DbConnection connection)
    {
        try
        {
            using DbCommand rollback = connection.Create("if @@trancount > 0 rollback tran;", timeout: storage.CommandTimeout);
            rollback.ExecuteNonQuery();
        }
        catch (Exception ex) when (ex.IsCatchableExceptionType())
        {
            // The connection is broken, and SQL Server rolls back what it had open
        }
    }

    public override void Dispose()
    {
        foreach (SqlServerConnection.DisposableLock acquiredLock in _acquiredLocks)
        {
            acquiredLock.Dispose();
        }

        base.Dispose();
    }

    public override void AcquireDistributedLock(string resource, TimeSpan timeout)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resource);

        SqlServerConnection.DisposableLock disposableLock = _connection.AcquireLock($"{_storage.SchemaName}:{resource}", timeout);
        if (disposableLock.OwnLock)
        {
            DbCommand command = SqlServerDistributedLock.CreateReleaseCommand(
                _connection.DedicatedConnection!, // A lock owned here holds a dedicated connection
                disposableLock.Resource,
                out DbParameter? resultParameter);

            _lockCommands.Add(Tuple.Create(command, resultParameter, disposableLock.Resource));
        }

        _acquiredLocks.Add(disposableLock);
    }

    public override void ExpireJob(string jobId, TimeSpan expireIn)
    {
        ArgumentNullException.ThrowIfNull(jobId);

        string query = _storage.GetQueryFromTemplate(static schemaName =>
$@"update J set ExpireAt = @expireAt from [{schemaName}].Job J with (forceseek) where Id = @id;");

        if (!SqlServerConnection.TryParseJobId(jobId, out long longId))
        {
            return;
        }

        AddCommand(_jobCommands, longId, batch => batch.Create(query)
            .AddParameter("@expireAt", SqlServerLimits.GetExpireAt(expireIn), DbType.DateTime)
            .AddParameter("@id", longId, DbType.Int64));
    }

    public override void PersistJob(string jobId)
    {
        ArgumentNullException.ThrowIfNull(jobId);

        string query = _storage.GetQueryFromTemplate(static schemaName =>
$@"update J set ExpireAt = NULL from [{schemaName}].Job J with (forceseek) where Id = @id;");

        if (!SqlServerConnection.TryParseJobId(jobId, out long longId))
        {
            return;
        }

        AddCommand(_jobCommands, longId, batch => batch.Create(query)
            .AddParameter("@id", longId, DbType.Int64));
    }

    public override void SetJobParameter(string jobId, string name, string? value)
    {
        ArgumentNullException.ThrowIfNull(jobId);
        ArgumentNullException.ThrowIfNull(name);
        SqlServerLimits.EnsureJobParameterNameFits(name);

        // The upsert of SqlServerConnection.SetJobParameter: an update, an insert when there was nothing to update,
        // and the update again when another session inserted the parameter in between. What differs is what a
        // transaction needs. XACT_ABORT is off while it runs, or the duplicate key would end the transaction, and
        // on again after it, for the commands that follow in the same command. Any other error is thrown again
        // with THROW, which ends the command: after RAISERROR the commands that follow, and the commit, would run.
        // And no variable is declared, as a command may hold this text several times.
        //
        // The parameter of a job that doesn't exist (any more) is not written, and the rest of the transaction is:
        // the job expired or was purged, which is no reason to lose a state change or a chain's record. So the
        // insert looks for the job, a read of the row the foreign key reads anyway, and a job deleted between that
        // read and the insert (the foreign key's error, 547) is skipped like one that was gone before.
        string query = _storage.GetQueryFromTemplate(static schemaName => $"""
            set xact_abort off;
            begin try
              update [{schemaName}].JobParameter set Value = @value where JobId = @jobId and Name = @name;
              if @@ROWCOUNT = 0
                insert into [{schemaName}].JobParameter (JobId, Name, Value)
                select @jobId, @name, @value
                where exists (select * from [{schemaName}].Job with (forceseek) where Id = @jobId);
            end try
            begin catch
              if ERROR_NUMBER() not in (2601, 2627, 547)
              begin
                set xact_abort on;
                throw;
              end
              if ERROR_NUMBER() <> 547
                update [{schemaName}].JobParameter set Value = @value where JobId = @jobId and Name = @name;
            end catch
            set xact_abort on;
            """);

        if (!SqlServerConnection.TryParseJobId(jobId, out long longId))
        {
            return;
        }

        _writesJobParameters = true;

        AddCommand(_parameterCommands, (longId, name), batch => batch.Create(query)
            .AddParameter("@jobId", longId, DbType.Int64)
            .AddParameter("@name", name, DbType.String, size: SqlServerLimits.JobParameterNameLength)
            .AddParameter("@value", (object?)value ?? DBNull.Value, DbType.String, size: -1));
    }

    public override void SetJobState(string jobId, IState state)
    {
        ArgumentNullException.ThrowIfNull(jobId);
        ArgumentNullException.ThrowIfNull(state);
        SqlServerLimits.EnsureStateNameFits(state.Name);

        string query = _storage.GetQueryFromTemplate(static schemaName =>
            $"""
            insert into [{schemaName}].State (JobId, Name, Reason, CreatedAt, Data)
            values (@jobId, @name, @reason, @createdAt, @data);
            update [{schemaName}].Job set StateId = SCOPE_IDENTITY(), StateName = @name where Id = @jobId;
            """);

        if (!SqlServerConnection.TryParseJobId(jobId, out long longId))
        {
            return;
        }

        if (state.Name == ProcessingState.StateName)
        {
            _movesToProcessing = true;
        }
        else
        {
            _hasOtherStateOrQueueChanges = true;
        }

        AddCommand(_jobCommands, longId, batch => batch.Create(query)
            .AddParameter("@jobId", longId, DbType.Int64)
            .AddParameter("@name", state.Name, DbType.String, size: 20)
            .AddParameter("@reason", (object?)state.Reason?[..Math.Min(99, state.Reason.Length)] ?? DBNull.Value, DbType.String, size: 100)
            .AddParameter("@createdAt", JobClock.UtcNow, DbType.DateTime)
            .AddParameter("@data", (object?)SerializationHelper.Serialize(state.SerializeData()) ?? DBNull.Value, DbType.String, size: -1));
    }

    public override void AddJobState(string jobId, IState state)
    {
        ArgumentNullException.ThrowIfNull(jobId);
        ArgumentNullException.ThrowIfNull(state);
        SqlServerLimits.EnsureStateNameFits(state.Name);

        string query = _storage.GetQueryFromTemplate(static schemaName =>
            $"""
            insert into [{schemaName}].State (JobId, Name, Reason, CreatedAt, Data)
            values (@jobId, @name, @reason, @createdAt, @data)
            """);

        if (!SqlServerConnection.TryParseJobId(jobId, out long longId))
        {
            return;
        }

        AddCommand(_jobCommands, longId, batch => batch.Create(query)
            .AddParameter("@jobId", longId, DbType.Int64)
            .AddParameter("@name", state.Name, DbType.String, size: 20)
            .AddParameter("@reason", (object?)state.Reason?[..Math.Min(99, state.Reason.Length)] ?? DBNull.Value, DbType.String, size: 100)
            .AddParameter("@createdAt", JobClock.UtcNow, DbType.DateTime)
            .AddParameter("@data", (object?)SerializationHelper.Serialize(state.SerializeData()) ?? DBNull.Value, DbType.String, size: -1));
    }

    public override void AddToQueue(string queue, string jobId)
    {
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(jobId);

        IPersistentJobQueueProvider provider = _storage.QueueProviders.GetProvider(queue);
        IPersistentJobQueue persistentQueue = provider.GetJobQueue();
        _hasOtherStateOrQueueChanges = true;

        if (persistentQueue.GetType() == typeof(SqlServerJobQueue))
        {
            SqlServerLimits.EnsureQueueNameFits(queue);
            if (!SqlServerConnection.TryParseJobId(jobId, out long longId))
            {
                return;
            }

            string query = _storage.GetQueryFromTemplate(static schemaName =>
$@"insert into [{schemaName}].JobQueue (JobId, Queue) values (@jobId, @queue)");

            AddCommand(_queueCommands, queue, batch => batch.Create(query)
                .AddParameter("@jobId", longId, DbType.Int64)
                .AddParameter("@queue", queue, DbType.String));

            _queuesToSignal.Add(queue);
        }
        else
        {
            _queueCommandQueue.Enqueue((connection, transaction) => persistentQueue.Enqueue(
                connection,
                transaction,
                queue,
                jobId));
        }
    }

    public override void IncrementCounter(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        SqlServerLimits.EnsureKeyFits(key);

        string query = _storage.GetQueryFromTemplate(static schemaName =>
$@"insert into [{schemaName}].Counter ([Key], [Value]) values (@key, @value)");

        AddCommand(_counterCommands, key, batch => batch.Create(query)
            .AddParameter("@key", key, DbType.String)
            .AddParameter("@value", value: +1, DbType.Int32));
    }

    public override void IncrementCounter(string key, TimeSpan expireIn)
    {
        ArgumentNullException.ThrowIfNull(key);
        SqlServerLimits.EnsureKeyFits(key);

        string query = _storage.GetQueryFromTemplate(static schemaName =>
$@"insert into [{schemaName}].Counter ([Key], [Value], [ExpireAt]) values (@key, @value, @expireAt)");

        AddCommand(_counterCommands, key, batch => batch.Create(query)
            .AddParameter("@key", key, DbType.String)
            .AddParameter("@value", value: +1, DbType.Int32)
            .AddParameter("@expireAt", SqlServerLimits.GetExpireAt(expireIn), DbType.DateTime));
    }

    public override void DecrementCounter(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        SqlServerLimits.EnsureKeyFits(key);

        string query = _storage.GetQueryFromTemplate(static schemaName =>
$@"insert into [{schemaName}].Counter ([Key], [Value]) values (@key, @value)");

        AddCommand(_counterCommands, key, batch => batch.Create(query)
            .AddParameter("@key", key, DbType.String)
            .AddParameter("@value", value: -1, DbType.Int32));
    }

    public override void DecrementCounter(string key, TimeSpan expireIn)
    {
        ArgumentNullException.ThrowIfNull(key);
        SqlServerLimits.EnsureKeyFits(key);

        string query = _storage.GetQueryFromTemplate(static schemaName =>
$@"insert into [{schemaName}].Counter ([Key], [Value], [ExpireAt]) values (@key, @value, @expireAt)");

        AddCommand(_counterCommands, key, batch => batch.Create(query)
            .AddParameter("@key", key, DbType.String)
            .AddParameter("@value", value: -1, DbType.Int32)
            .AddParameter("@expireAt", SqlServerLimits.GetExpireAt(expireIn), DbType.DateTime));
    }

    public override void AddToSet(string key, string value)
    {
        AddToSet(key, value, 0.0);
    }

    public override void AddToSet(string key, string value, double score)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(value);
        SqlServerLimits.EnsureKeyFits(key);
        SqlServerLimits.EnsureSetValueFits(key, value);

        string query = _storage.GetQueryFromTemplate(_storage.UseIgnoreDupKeyOption
            ? static schemaName => $"""
                insert into [{schemaName}].[Set] ([Key], Value, Score) values (@key, @value, @score);
                if @@ROWCOUNT = 0 update [{schemaName}].[Set] set Score = @score where [Key] = @key and Value = @value;
                """

            : static schemaName => $"""
                ;merge [{schemaName}].[Set] with (xlock) as Target
                using (VALUES (@key, @value, @score)) as Source ([Key], Value, Score)
                on Target.[Key] = Source.[Key] and Target.Value = Source.Value
                when matched then update set Score = Source.Score
                when not matched then insert ([Key], Value, Score) values (Source.[Key], Source.Value, Source.Score);
                """);

        AcquireSetLock(key);
        AddCommand(_setCommands, key, batch => batch.Create(query)
            .AddParameter("@key", key, DbType.String)
            .AddParameter("@value", value, DbType.String, 256)
            .AddParameter("@score", score, DbType.Double, size: 53));
    }

    public override void RemoveFromSet(string key, string value)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(value);

        // The value parameter is sized to the column, so a longer value would be cut and match another one
        SqlServerLimits.EnsureSetValueFits(key, value);

        string query = _storage.GetQueryFromTemplate(static schemaName =>
$@"delete S from [{schemaName}].[Set] S with (forceseek) where [Key] = @key and Value = @value");

        AcquireSetLock(key);
        AddCommand(_setCommands, key, batch => batch.Create(query)
            .AddParameter("@key", key, DbType.String)
            .AddParameter("@value", value, DbType.String, size: 256));
    }

    public override void InsertToList(string key, string value)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(value);
        SqlServerLimits.EnsureKeyFits(key);

        // The new row takes the expiration of the list, so an item added to a list that expires expires with
        // it, as ExpireList only updates the rows that exist at that moment. ExpireList and PersistList set every
        // row alike and new rows copy it, so the newest row tells the list's expiration: reading only that row
        // (one seek on [Key], [Id]) keeps the cost and the locks independent of the list's size, where reading
        // every row escalated to an exclusive lock on the whole table for long lists. Its xlock still
        // serializes concurrent inserts to the same list
        string query = _storage.GetQueryFromTemplate(static schemaName => $"""
            insert into [{schemaName}].List ([Key], Value, ExpireAt)
            select @key, @value, (
                select top (1) ExpireAt from [{schemaName}].List with (xlock, forceseek)
                where [Key] = @key
                order by Id desc);
            """);

        AcquireListLock(key);
        AddCommand(_listCommands, key, batch => batch.Create(query)
            .AddParameter("@key", key, DbType.String)
            .AddParameter("@value", value, DbType.String, size: -1));
    }

    public override void RemoveFromList(string key, string value)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(value);

        string query = _storage.GetQueryFromTemplate(static schemaName =>
$@"delete L from [{schemaName}].List L with (forceseek) where [Key] = @key and Value = @value");

        AcquireListLock(key);
        AddCommand(_listCommands, key, batch => batch.Create(query)
            .AddParameter("@key", key, DbType.String)
            .AddParameter("@value", value, DbType.String, size: -1));
    }

    public override void TrimList(string key, int keepStartingFrom, int keepEndingAt)
    {
        ArgumentNullException.ThrowIfNull(key);

        string query = _storage.GetQueryFromTemplate(static schemaName =>
            $"""
            ;with cte as (
                select row_number() over (order by Id desc) as row_num
                from [{schemaName}].List with (xlock, forceseek)
                where [Key] = @key)
            delete from cte where row_num not between @start and @end
            """);

        AcquireListLock(key);

        AddCommand(_listCommands, key, batch => batch.Create(query)
            .AddParameter("@key", key, DbType.String)
            .AddParameter("@start", keepStartingFrom + 1, DbType.Int32)
            .AddParameter("@end", keepEndingAt + 1, DbType.Int32));
    }

    public override void SetRangeInHash(string key, IEnumerable<KeyValuePair<string, string>> keyValuePairs)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(keyValuePairs);
        SqlServerLimits.EnsureKeyFits(key);

        // Checked before any command is queued, so a bad field doesn't leave half of the fields behind
        List<KeyValuePair<string, string>> pairs = [.. keyValuePairs];
        foreach (KeyValuePair<string, string> pair in pairs)
        {
            SqlServerLimits.EnsureHashFieldFits(key, pair.Key);
        }

        string query = _storage.GetQueryFromTemplate(_storage.UseIgnoreDupKeyOption
            ? static schemaName => $"""
                insert into [{schemaName}].Hash ([Key], Field, Value) values (@key, @field, @value);
                if @@ROWCOUNT = 0 update [{schemaName}].Hash set Value = @value where [Key] = @key and Field = @field;
                """

            : static schemaName => $"""
                ;merge [{schemaName}].Hash with (xlock) as Target
                using (VALUES (@key, @field, @value)) as Source ([Key], Field, Value)
                on Target.[Key] = Source.[Key] and Target.Field = Source.Field
                when matched then update set Value = Source.Value
                when not matched then insert ([Key], Field, Value) values (Source.[Key], Source.Field, Source.Value);
                """);

        AcquireHashLock(key);

        foreach (KeyValuePair<string, string> pair in pairs)
        {
            AddCommand(_hashCommands, key, batch => batch.Create(query)
                .AddParameter("@key", key, DbType.String)
                .AddParameter("@field", pair.Key, DbType.String, size: 100)
                .AddParameter("@value", (object)pair.Value ?? DBNull.Value, DbType.String, size: -1));
        }
    }

    public override void RemoveHash(string key)
    {
        ArgumentNullException.ThrowIfNull(key);

        string query = _storage.GetQueryFromTemplate(static schemaName =>
$@"delete H from [{schemaName}].Hash H with (forceseek) where [Key] = @key");

        AcquireHashLock(key);
        AddCommand(_hashCommands, key, batch => batch.Create(query)
            .AddParameter("@key", key, DbType.String));
    }

    public override void AddRangeToSet(string key, IList<string> items)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(items);
        SqlServerLimits.EnsureKeyFits(key);

        foreach (string item in items)
        {
            if (item == null)
            {
                throw new ArgumentException("The items can't contain null.", nameof(items));
            }

            SqlServerLimits.EnsureSetValueFits(key, item, nameof(items));
        }

        string query = _storage.GetQueryFromTemplate(static schemaName =>
$@"insert into [{schemaName}].[Set] ([Key], Value, Score) values (@key, @value, 0.0)");

        AcquireSetLock(key);

        foreach (string item in items)
        {
            AddCommand(_setCommands, key, batch => batch.Create(query)
                .AddParameter("@key", key, DbType.String)
                .AddParameter("@value", item, DbType.String, size: 256));
        }
    }

    public override void RemoveSet(string key)
    {
        ArgumentNullException.ThrowIfNull(key);

        string query = _storage.GetQueryFromTemplate(static schemaName =>
$@"delete S from [{schemaName}].[Set] S with (forceseek) where [Key] = @key");

        AcquireSetLock(key);
        AddCommand(_setCommands, key, batch => batch.Create(query).AddParameter("@key", key, DbType.String));
    }

    public override void ExpireHash(string key, TimeSpan expireIn)
    {
        ArgumentNullException.ThrowIfNull(key);

        string query = _storage.GetQueryFromTemplate(static schemaName => $"""
            update [{schemaName}].[Hash] set ExpireAt = @expireAt where [Key] = @key
            """);

        AcquireHashLock(key);
        AddCommand(_hashCommands, key, batch => batch.Create(query)
            .AddParameter("@key", key, DbType.String)
            .AddParameter("@expireAt", SqlServerLimits.GetExpireAt(expireIn), DbType.DateTime));
    }

    public override void ExpireSet(string key, TimeSpan expireIn)
    {
        ArgumentNullException.ThrowIfNull(key);

        string query = _storage.GetQueryFromTemplate(static schemaName => $"""
            update [{schemaName}].[Set] set ExpireAt = @expireAt where [Key] = @key
            """);

        AcquireSetLock(key);
        AddCommand(_setCommands, key, batch => batch.Create(query)
            .AddParameter("@key", key, DbType.String)
            .AddParameter("@expireAt", SqlServerLimits.GetExpireAt(expireIn), DbType.DateTime));
    }

    public override void ExpireList(string key, TimeSpan expireIn)
    {
        ArgumentNullException.ThrowIfNull(key);

        string query = _storage.GetQueryFromTemplate(static schemaName => $"""
            update [{schemaName}].[List] set ExpireAt = @expireAt where [Key] = @key
            """);

        AcquireListLock(key);
        AddCommand(_listCommands, key, batch => batch.Create(query)
            .AddParameter("@key", key, DbType.String)
            .AddParameter("@expireAt", SqlServerLimits.GetExpireAt(expireIn), DbType.DateTime));
    }

    public override void PersistHash(string key)
    {
        ArgumentNullException.ThrowIfNull(key);

        string query = _storage.GetQueryFromTemplate(static schemaName => $"""
            update [{schemaName}].Hash set ExpireAt = null where [Key] = @key
            """);

        AcquireHashLock(key);
        AddCommand(_hashCommands, key, batch => batch.Create(query).AddParameter("@key", key, DbType.String));
    }

    public override void PersistSet(string key)
    {
        ArgumentNullException.ThrowIfNull(key);

        string query = _storage.GetQueryFromTemplate(static schemaName => $"""
            update [{schemaName}].[Set] set ExpireAt = null where [Key] = @key
            """);

        AcquireSetLock(key);
        AddCommand(_setCommands, key, batch => batch.Create(query).AddParameter("@key", key, DbType.String));
    }

    public override void PersistList(string key)
    {
        ArgumentNullException.ThrowIfNull(key);

        string query = _storage.GetQueryFromTemplate(static schemaName => $"""
            update [{schemaName}].[List] set ExpireAt = null where [Key] = @key
            """);

        AcquireListLock(key);
        AddCommand(_listCommands, key, batch => batch.Create(query).AddParameter("@key", key, DbType.String));
    }

    public override void RemoveFromQueue(IFetchedJob fetchedJob)
    {
        ArgumentNullException.ThrowIfNull(fetchedJob);

        _hasOtherStateOrQueueChanges = true;

        if (fetchedJob is SqlServerTimeoutJob timeoutJob)
        {
            string query = _storage.GetQueryFromTemplate(static schemaName =>
$@"delete JQ from [{schemaName}].JobQueue JQ with (forceseek, rowlock) where Queue = @queue and Id = @id and FetchedAt = @fetchedAt");

            AddCommand(_queueCommands, timeoutJob.Queue, batch => batch.Create(query)
                .AddParameter("@queue", timeoutJob.Queue, DbType.String)
                .AddParameter("@id", timeoutJob.Id, DbType.Int64)
                .AddParameter("@fetchedAt", timeoutJob.FetchedAt, DbType.DateTime));

            timeoutJob.SetTransaction(this);
            _removedJobs[timeoutJob.Id] = timeoutJob;
        }
        else
        {
            throw new NotSupportedException(
                "Only '" + nameof(SqlServerTimeoutJob) + "' type supports transactional acknowledge, '" + fetchedJob.GetType().Name + "' given.");
        }
    }

    private static void AppendBatch<TKey>(
        SortedDictionary<TKey, List<Func<DbConnection, DbCommand>>> collection,
        SqlCommandBatch batch)
        where TKey : notnull
    {
        foreach (KeyValuePair<TKey, List<Func<DbConnection, DbCommand>>> pair in collection)
        {
            foreach (Func<DbConnection, DbCommand> command in pair.Value)
            {
                DbCommand dbCommand = command(batch.Connection);
                batch.Append(dbCommand);
            }
        }
    }

    private static void AddCommand<TKey>(
        SortedDictionary<TKey, List<Func<DbConnection, DbCommand>>> collection,
        TKey key,
        Func<DbConnection, DbCommand> command)
        where TKey : notnull
    {
        if (!collection.TryGetValue(key, out List<Func<DbConnection, DbCommand>>? commands))
        {
            commands = [];
            collection.Add(key, commands);
        }

        commands.Add(command);
    }

    private void AcquireListLock(string key)
    {
        AcquireLock(_storage.DisableGlobalLocks ? $"List:{key}" : "List");
    }

    private void AcquireSetLock(string key)
    {
        AcquireLock(_storage.DisableGlobalLocks ? $"Set:{key}" : "Set");
    }

    private void AcquireHashLock(string key)
    {
        AcquireLock(_storage.DisableGlobalLocks ? $"Hash:{key}" : "Hash");
    }

    private void AcquireLock(string resource)
    {
        if (!_storage.DisableGlobalLocks || _storage.Options.UseFineGrainedLocks)
        {
            _lockedResources.Add($"{_storage.SchemaName}:{resource}:Lock");
        }
    }

    private void TrySignalListeningWorkers()
    {
        foreach (string queue in _queuesToSignal)
        {
            if (SqlServerJobQueue.NewItemInQueueEvents.TryGetValue(Tuple.Create(_storage, queue), out AutoResetEvent? signal))
            {
                signal.Set();
            }
        }
    }
}