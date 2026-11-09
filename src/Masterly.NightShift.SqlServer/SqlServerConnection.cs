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

using Masterly.NightShift.Common;
using Masterly.NightShift.Server;
using Masterly.NightShift.SqlServer.Entities;

// ReSharper disable RedundantAnonymousTypePropertyName

namespace Masterly.NightShift.SqlServer;

internal sealed class SqlServerConnection : JobStorageConnection
{
    private readonly Dictionary<string, HashSet<Guid>> _lockedResources = [];

    public SqlServerConnection([NotNull] SqlServerStorage storage)
    {
        ArgumentNullException.ThrowIfNull(storage);
        Storage = storage;
    }

    public SqlServerStorage Storage { get; }
    public DbConnection DedicatedConnection { get; private set; }

    public override void Dispose()
    {
        DedicatedConnection?.Dispose();
        DedicatedConnection = null;

        base.Dispose();
    }

    public override IWriteOnlyTransaction CreateWriteTransaction()
    {
        return new SqlServerWriteOnlyTransaction(this);
    }

    public override IDisposable AcquireDistributedLock([NotNull] string resource, TimeSpan timeout)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resource);
        return AcquireLock($"{Storage.SchemaName}:{resource}", timeout);
    }

    public override IFetchedJob FetchNextJob(string[] queues, CancellationToken cancellationToken)
    {
        if (queues == null || queues.Length == 0) throw new ArgumentNullException(nameof(queues));

        var providers = queues
            .Select(Storage.QueueProviders.GetProvider)
            .Distinct()
            .ToArray();

        if (providers.Length != 1)
        {
            throw new InvalidOperationException(
                $"Multiple provider instances registered for queues: {string.Join(", ", queues)}. You should choose only one type of persistent queues per server instance.");
        }

        var persistentQueue = providers[0].GetJobQueue();
        return persistentQueue.Dequeue(queues, cancellationToken);
    }

    public override string CreateExpiredJob(
        Job job,
        IDictionary<string, string> parameters,
        DateTime createdAt,
        TimeSpan expireIn)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(parameters);

        foreach (var parameter in parameters)
        {
            SqlServerLimits.EnsureJobParameterNameFits(parameter.Key);
        }

        var queryString = Storage.GetQueryFromTemplate(static schemaName =>
            $"""
            insert into [{schemaName}].Job (InvocationData, Arguments, CreatedAt, ExpireAt)
            output inserted.Id
            values (@invocationData, @arguments, @createdAt, @expireAt)
            """);

        var invocationData = InvocationData.SerializeJob(job);
        var payload = invocationData.SerializePayload(excludeArguments: true);

        Action<DbCommand> queryParameters = cmd => cmd
            .AddParameter("@invocationData", payload, DbType.String, size: -1)
            .AddParameter("@arguments", invocationData.Arguments, DbType.String, size: -1)
            .AddParameter("@createdAt", createdAt, DbType.DateTime)
            .AddParameter("@expireAt", SqlServerLimits.GetExpireAt(createdAt, expireIn), DbType.DateTime);

        Action<DbCommand> additionalParameters = null;

        var parametersArray = parameters.ToArray();

        if (parametersArray.Length <= 4)
        {
            if (parametersArray.Length == 1)
            {
                queryString = Storage.GetQueryFromTemplate(static schemaName => $"""
                    set xact_abort on; set nocount on; declare @jobId bigint;
                    begin tran;
                    insert into [{schemaName}].Job (InvocationData, Arguments, CreatedAt, ExpireAt) values (@invocationData, @arguments, @createdAt, @expireAt);
                    select @jobId = scope_identity(); select @jobId;
                    insert into [{schemaName}].JobParameter (JobId, Name, Value) values (@jobId, @name, @value);
                    commit tran;
                    """);
                additionalParameters = cmd => cmd
                    .AddParameter("@name", parametersArray[0].Key, DbType.String, size: 40)
                    .AddParameter("@value", parametersArray[0].Value, DbType.String, size: -1);
            }
            else if (parametersArray.Length == 2)
            {
                queryString = Storage.GetQueryFromTemplate(static schemaName => $"""
                    set xact_abort on; set nocount on; declare @jobId bigint;
                    begin tran;
                    insert into [{schemaName}].Job (InvocationData, Arguments, CreatedAt, ExpireAt) values (@invocationData, @arguments, @createdAt, @expireAt);
                    select @jobId = scope_identity(); select @jobId;
                    insert into [{schemaName}].JobParameter (JobId, Name, Value) values (@jobId, @name1, @value1), (@jobId, @name2, @value2);
                    commit tran;
                    """);
                additionalParameters = cmd => cmd
                    .AddParameter("@name1", parametersArray[0].Key, DbType.String, size: 40)
                    .AddParameter("@value1", parametersArray[0].Value, DbType.String, size: -1)
                    .AddParameter("@name2", parametersArray[1].Key, DbType.String, size: 40)
                    .AddParameter("@value2", parametersArray[1].Value, DbType.String, size: -1);
            }
            else if (parametersArray.Length == 3)
            {
                queryString = Storage.GetQueryFromTemplate(static schemaName => $"""
                    set xact_abort on; set nocount on; declare @jobId bigint;
                    begin tran;
                    insert into [{schemaName}].Job (InvocationData, Arguments, CreatedAt, ExpireAt) values (@invocationData, @arguments, @createdAt, @expireAt);
                    select @jobId = scope_identity(); select @jobId;
                    insert into [{schemaName}].JobParameter (JobId, Name, Value) values (@jobId, @name1, @value1), (@jobId, @name2, @value2), (@jobId, @name3, @value3);
                    commit tran;
                    """);
                additionalParameters = cmd => cmd
                    .AddParameter("@name1", parametersArray[0].Key, DbType.String, size: 40)
                    .AddParameter("@value1", parametersArray[0].Value, DbType.String, size: -1)
                    .AddParameter("@name2", parametersArray[1].Key, DbType.String, size: 40)
                    .AddParameter("@value2", parametersArray[1].Value, DbType.String, size: -1)
                    .AddParameter("@name3", parametersArray[2].Key, DbType.String, size: 40)
                    .AddParameter("@value3", parametersArray[2].Value, DbType.String, size: -1);
            }
            else if (parametersArray.Length == 4)
            {
                queryString = Storage.GetQueryFromTemplate(static schemaName => $"""
                    set xact_abort on; set nocount on; declare @jobId bigint;
                    begin tran;
                    insert into [{schemaName}].Job (InvocationData, Arguments, CreatedAt, ExpireAt) values (@invocationData, @arguments, @createdAt, @expireAt);
                    select @jobId = scope_identity(); select @jobId;
                    insert into [{schemaName}].JobParameter (JobId, Name, Value) values (@jobId, @name1, @value1), (@jobId, @name2, @value2), (@jobId, @name3, @value3), (@jobId, @name4, @value4);
                    commit tran;
                    """);
                additionalParameters = cmd => cmd
                    .AddParameter("@name1", parametersArray[0].Key, DbType.String, size: 40)
                    .AddParameter("@value1", parametersArray[0].Value, DbType.String, size: -1)
                    .AddParameter("@name2", parametersArray[1].Key, DbType.String, size: 40)
                    .AddParameter("@value2", parametersArray[1].Value, DbType.String, size: -1)
                    .AddParameter("@name3", parametersArray[2].Key, DbType.String, size: 40)
                    .AddParameter("@value3", parametersArray[2].Value, DbType.String, size: -1)
                    .AddParameter("@name4", parametersArray[3].Key, DbType.String, size: 40)
                    .AddParameter("@value4", parametersArray[3].Value, DbType.String, size: -1);
            }

            return Storage.UseConnection(DedicatedConnection, static (storage, connection, ctx) =>
                {
                    using var command = connection.Create(ctx.Key, timeout: storage.CommandTimeout);
                    ctx.Value.Key(command);
                    ctx.Value.Value?.Invoke(command);

                    return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);
                },
                new KeyValuePair<string, KeyValuePair<Action<DbCommand>, Action<DbCommand>>>(
                    queryString,
                    new KeyValuePair<Action<DbCommand>, Action<DbCommand>>(queryParameters, additionalParameters)));
        }

        return Storage.UseTransaction(DedicatedConnection, static (storage, connection, transaction, triple) =>
        {
            using var jobCommand = connection.Create(triple.Item1, timeout: storage.CommandTimeout);
            triple.Item2(jobCommand);

            jobCommand.Transaction = transaction;

            var jobId = Convert.ToInt64(jobCommand.ExecuteScalar(), CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);

            var query = storage.GetQueryFromTemplate(static schemaName =>
$@"insert into [{schemaName}].JobParameter (JobId, Name, Value) values (@jobId, @name, @value)");

            using (var commandBatch = new SqlCommandBatch(connection, transaction, preferBatching: storage.CommandBatchMaxTimeout.HasValue))
            {
                commandBatch.CommandTimeout = storage.CommandTimeout;
                commandBatch.CommandBatchMaxTimeout = storage.CommandBatchMaxTimeout;

                foreach (var parameter in triple.Item3)
                {
                    var command = connection.Create(query)
                        .AddParameter("@jobId", long.Parse(jobId, CultureInfo.InvariantCulture), DbType.Int64)
                        .AddParameter("@name", parameter.Key, DbType.String, size: 40)
                        .AddParameter("@value", (object)parameter.Value ?? DBNull.Value, DbType.String, size: -1);

                    commandBatch.Append(command);
                }

                commandBatch.ExecuteNonQuery();
            }

            return jobId;
        }, ValueTuple.Create(queryString, queryParameters, parametersArray), null);
    }

    public override JobData GetJobData(string id)
    {
        ArgumentNullException.ThrowIfNull(id);

        if (!TryParseJobId(id, out var parsedId))
        {
            return null;
        }

        return Storage.UseConnection(DedicatedConnection, static (storage, connection, parsedId) =>
        {
            var query = storage.GetQueryFromTemplate(static schemaName =>
                $"""
                select InvocationData, StateName, Arguments, CreatedAt from [{schemaName}].Job with (readcommittedlock, forceseek) where Id = @id
                select Name, Value from [{schemaName}].JobParameter with (forceseek) where JobId = @id
                """);

            using var multi = connection.QueryMultiple(query, new { id = parsedId }, commandTimeout: storage.CommandTimeout);
            var jobData = multi.ReadSingleOrDefault();
            if (jobData == null) return null;

            var parameters = new Dictionary<string, string>();

            var jobParameters = multi.Read<JobParameter>().ToArray();
            for (var i = 0; i < jobParameters.Length; i++)
            {
                var jobParameter = jobParameters[i];
                parameters[jobParameter.Name] = jobParameter.Value;
            }

            // TODO: conversion exception could be thrown.
            var invocationData = InvocationData.DeserializePayload(jobData.InvocationData);

            if (!string.IsNullOrEmpty(jobData.Arguments))
            {
                invocationData.Arguments = jobData.Arguments;
            }

            Job job = null;
            JobLoadException loadException = null;

            try
            {
                job = invocationData.DeserializeJob();
            }
            catch (JobLoadException ex)
            {
                loadException = ex;
            }

            return new JobData
            {
                Job = job,
                InvocationData = invocationData,
                State = jobData.StateName,
                CreatedAt = jobData.CreatedAt,
                LoadException = loadException,
                ParametersSnapshot = parameters
            };
        }, parsedId);
    }

    public override StateData GetStateData(string jobId)
    {
        ArgumentNullException.ThrowIfNull(jobId);

        if (!TryParseJobId(jobId, out var parsedId))
        {
            return null;
        }

        return Storage.UseConnection(DedicatedConnection, static (storage, connection, parsedId) =>
        {
            var query = storage.GetQueryFromTemplate(static schemaName =>
                $"""
                select s.Name, s.Reason, s.Data
                from [{schemaName}].State s with (readcommittedlock, forceseek)
                inner join [{schemaName}].Job j with (readcommittedlock, forceseek) on j.StateId = s.Id and j.Id = s.JobId
                where j.Id = @jobId
                """);

            var sqlState = connection.QuerySingleOrDefault<SqlState>(query, new { jobId = parsedId }, commandTimeout: storage.CommandTimeout);
            if (sqlState == null)
            {
                return null;
            }

            var data = new Dictionary<string, string>(
                SerializationHelper.Deserialize<Dictionary<string, string>>(sqlState.Data),
                StringComparer.OrdinalIgnoreCase);

            return new StateData
            {
                Name = sqlState.Name,
                Reason = sqlState.Reason,
                Data = data
            };
        }, parsedId);
    }

    public override void SetJobParameter(string id, string name, string value)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(name);
        SqlServerLimits.EnsureJobParameterNameFits(name);
        if (!TryParseJobId(id, out var parsedId)) return;

        // First updated is required for older schema versions (5 and below), where
        // [IX_HangFire_JobParameter_JobIdAndName] index wasn't declared as unique,
        // to make our query resistant to (no matter what schema we are using):
        // 
        // https://github.com/HangfireIO/Hangfire/issues/1743 (deadlocks)
        // https://github.com/HangfireIO/Hangfire/issues/1741 (duplicate entries)
        // https://github.com/HangfireIO/Hangfire/issues/1693#issuecomment-697976133 (records aren't updated)

        Storage.UseConnection(DedicatedConnection, static (storage, connection, triple) =>
        {
            var query = storage.GetQueryFromTemplate(static schemaName => $"""
                set xact_abort off;
                begin try
                  update [{schemaName}].JobParameter set Value = @value where JobId = @jobId and Name = @name;
                  if @@ROWCOUNT = 0 insert into [{schemaName}].JobParameter (JobId, Name, Value) values (@jobId, @name, @value);
                end try
                begin catch
                  declare @em nvarchar(4000), @es int, @est int;
                  select @em=error_message(),@es=error_severity(),@est=error_state();
                  IF ERROR_NUMBER() not in (2601, 2627) raiserror(@em, @es, @est);
                  update [{schemaName}].JobParameter set Value = @value where JobId = @jobId and Name = @name;
                end catch
                """);

            return connection.Execute(
                query,
                new { jobId = triple.Item1, name = triple.Item2, value = triple.Item3 },
                commandTimeout: storage.CommandTimeout);
        }, ValueTuple.Create(parsedId, name, value));
    }

    public override string GetJobParameter(string id, string name)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(name);

        if (!TryParseJobId(id, out var parsedId))
        {
            return null;
        }

        return Storage.UseConnection(DedicatedConnection, static (storage, connection, pair) => connection.ExecuteScalar<string>(
            storage.GetQueryFromTemplate(static schemaName =>
                $@"select top (1) Value from [{schemaName}].JobParameter with (forceseek) where JobId = @id and Name = @name"),
            new { id = pair.Key, name = pair.Value },
            commandTimeout: storage.CommandTimeout),
            new KeyValuePair<long, string>(parsedId, name));
    }

    public override HashSet<string> GetAllItemsFromSet(string key)
    {
        ArgumentNullException.ThrowIfNull(key);

        return Storage.UseConnection(DedicatedConnection, static (storage, connection, key) =>
        {
            var query = storage.GetQueryFromTemplate(static schemaName =>
$@"select Value from [{schemaName}].[Set] with (forceseek) where [Key] = @key");

            var result = connection.Query<string>(
                query,
                new { key },
                commandTimeout: storage.CommandTimeout);

            return new HashSet<string>(result);
        }, key);
    }

    public override string GetFirstByLowestScoreFromSet(string key, double fromScore, double toScore)
    {
        return GetFirstByLowestScoreFromSet(key, fromScore, toScore, 1).FirstOrDefault();
    }

    public override List<string> GetFirstByLowestScoreFromSet(string key, double fromScore, double toScore, int count)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (count <= 0) throw new ArgumentException("The value must be a positive number", nameof(count));
        if (toScore < fromScore) throw new ArgumentException("The `toScore` value must be higher or equal to the `fromScore` value.", nameof(toScore));

        return Storage.UseConnection(DedicatedConnection, static (storage, connection, pair) =>
        {
            var query = storage.GetQueryFromTemplate(static schemaName =>
$@"select top (@count) Value from [{schemaName}].[Set] with (forceseek) where [Key] = @key and Score between @from and @to order by Score");

            var result = connection.Query<string>(
                query,
                new { count = pair.Value.Item3, key = pair.Key, from = pair.Value.Item1, to = pair.Value.Item2 },
                commandTimeout: storage.CommandTimeout);

            return result.ToList();
        }, new KeyValuePair<string, ValueTuple<double, double, int>>(key, ValueTuple.Create(fromScore, toScore, count)));
    }

    public override void SetRangeInHash(string key, IEnumerable<KeyValuePair<string, string>> keyValuePairs)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(keyValuePairs);
        SqlServerLimits.EnsureKeyFits(key);

        var pairs = new List<KeyValuePair<string, string>>(keyValuePairs);
        foreach (var pair in pairs)
        {
            SqlServerLimits.EnsureHashFieldFits(key, pair.Key);
        }

        Storage.UseTransaction(DedicatedConnection, static (storage, connection, transaction, pair) =>
        {
            var query = storage.GetQueryFromTemplate(static schemaName => $"""
                set xact_abort off;
                begin try
                  insert into [{schemaName}].Hash ([Key], Field, Value) values (@key, @field, @value);
                  if @@ROWCOUNT = 0 update [{schemaName}].Hash set Value = @value where [Key] = @key and Field = @field;
                end try
                begin catch
                  declare @em nvarchar(4000), @es int, @est int;
                  select @em=error_message(),@es=error_severity(),@est=error_state();
                  IF ERROR_NUMBER() not in (2601, 2627) raiserror(@em, @es, @est);
                  update [{schemaName}].Hash set Value = @value where [Key] = @key and Field = @field;
                end catch
                """);

            var lockResourceKey = SqlServerLimits.GetLockResource($"{storage.SchemaName}:Hash:Lock");

            using var commandBatch = new SqlCommandBatch(connection, transaction, preferBatching: storage.CommandBatchMaxTimeout.HasValue);
            if (!storage.DisableGlobalLocks)
            {
                var command = connection
                    .Create("SET XACT_ABORT ON;exec sp_getapplock @Resource=@resource, @LockMode=N'Exclusive', @LockOwner=N'Transaction', @LockTimeout=-1;")
                    .AddParameter("@resource", lockResourceKey, DbType.String, size: 255);
                commandBatch.Append(command);
            }

            foreach (var keyValuePair in pair.Value)
            {
                var command = connection.Create(query)
                    .AddParameter("@key", pair.Key, DbType.String)
                    .AddParameter("@field", keyValuePair.Key, DbType.String, size: 100)
                    .AddParameter("@value", (object)keyValuePair.Value ?? DBNull.Value, DbType.String, size: -1);
                commandBatch.Append(command);
            }

            if (!storage.DisableGlobalLocks)
            {
                var command = connection
                    .Create("exec sp_releaseapplock @Resource=@resource, @LockOwner=N'Transaction';")
                    .AddParameter("@resource", lockResourceKey, DbType.String, size: 255);
                commandBatch.Append(command);
            }

            commandBatch.CommandTimeout = storage.CommandTimeout;
            commandBatch.CommandBatchMaxTimeout = storage.CommandBatchMaxTimeout;

            commandBatch.ExecuteNonQuery();
        }, new KeyValuePair<string, IEnumerable<KeyValuePair<string, string>>>(key, pairs));
    }

    public override Dictionary<string, string> GetAllEntriesFromHash(string key)
    {
        ArgumentNullException.ThrowIfNull(key);

        return Storage.UseConnection(DedicatedConnection, static (storage, connection, key) =>
        {
            var query = storage.GetQueryFromTemplate(static schemaName =>
$@"select Field, Value from [{schemaName}].Hash with (forceseek) where [Key] = @key");

            var result = connection.Query<SqlHash>(
                query,
                new { key },
                commandTimeout: storage.CommandTimeout)
                .ToDictionary(static x => x.Field, static x => x.Value);

            return result.Count != 0 ? result : null;
        }, key);
    }

    public override void AnnounceServer(string serverId, ServerContext context)
    {
        ArgumentNullException.ThrowIfNull(serverId);
        ArgumentNullException.ThrowIfNull(context);

        // Heartbeat and RemoveServer only look the id up: a longer one can't be stored, so they find nothing
        SqlServerLimits.EnsureServerIdFits(serverId);

        var data = new ServerData
        {
            WorkerCount = context.WorkerCount,
            Queues = context.Queues,
            StartedAt = DateTime.UtcNow,
        };

        Storage.UseConnection(DedicatedConnection, static (storage, connection, pair) =>
        {
            var query = storage.GetQueryFromTemplate(static schemaName =>
                $"""
                ;merge [{schemaName}].Server with (holdlock) as Target
                using (VALUES (@id, @data, sysutcdatetime())) as Source (Id, Data, Heartbeat)
                on Target.Id = Source.Id
                when matched then update set Data = Source.Data, LastHeartbeat = Source.Heartbeat
                when not matched then insert (Id, Data, LastHeartbeat) values (Source.Id, Source.Data, Source.Heartbeat);
                """);

            return connection.Execute(
                query,
                new { id = pair.Key, data = pair.Value },
                commandTimeout: storage.CommandTimeout);
        }, new KeyValuePair<string, string>(serverId, SerializationHelper.Serialize(data)));
    }

    public override void RemoveServer(string serverId)
    {
        ArgumentNullException.ThrowIfNull(serverId);

        Storage.UseConnection(DedicatedConnection, static (storage, connection, serverId) =>
        {
            var query = storage.GetQueryFromTemplate(static schemaName =>
$@"delete S from [{schemaName}].Server S with (forceseek) where Id = @id");

            return connection.Execute(
                query,
                new { id = serverId },
                commandTimeout: storage.CommandTimeout);
        }, serverId);
    }

    public override void Heartbeat(string serverId)
    {
        ArgumentNullException.ThrowIfNull(serverId);

        Storage.UseConnection(DedicatedConnection, static (storage, connection, serverId) =>
        {
            var query = storage.GetQueryFromTemplate(static schemaName =>
$@"update [{schemaName}].Server set LastHeartbeat = sysutcdatetime() where Id = @id");

            var affected = connection.Execute(
                query,
                new { id = serverId },
                commandTimeout: storage.CommandTimeout);

            if (affected == 0)
            {
                throw new BackgroundServerGoneException();
            }

            return affected;
        }, serverId);
    }

    public override int RemoveTimedOutServers(TimeSpan timeOut)
    {
        if (timeOut.Duration() != timeOut)
        {
            throw new ArgumentException("The `timeOut` value must be positive.", nameof(timeOut));
        }

        return Storage.UseConnection(DedicatedConnection, static (storage, connection, timeout) => connection.Execute(
            storage.GetQueryFromTemplate(static schemaName =>
                $@"delete s from [{schemaName}].Server s with (readpast, readcommitted) where LastHeartbeat < dateadd(ms, @timeoutMsNeg, sysutcdatetime())"),
            new { timeoutMsNeg = timeout.Negate().TotalMilliseconds },
            commandTimeout: storage.CommandTimeout), timeOut);
    }

    public override long GetSetCount(string key)
    {
        ArgumentNullException.ThrowIfNull(key);

        return Storage.UseConnection(DedicatedConnection, static (storage, connection, key) => connection.ExecuteScalar<long>(
            storage.GetQueryFromTemplate(static schemaName =>
                $@"select count(*) from [{schemaName}].[Set] with (forceseek) where [Key] = @key"),
            new { key },
            commandTimeout: storage.CommandTimeout), key);
    }

    public override long GetSetCount(IEnumerable<string> keys, int limit)
    {
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentOutOfRangeException.ThrowIfNegative(limit);

        return Storage.UseConnection(DedicatedConnection, static (storage, connection, pair) => connection.ExecuteScalar<long>(
            storage.GetQueryFromTemplate(static schemaName =>
                $"""
                select count(*) from (
                  select top(@limit) 1 as N from [{schemaName}].[Set] with (forceseek) where [Key] in @keys
                ) a
                """),
            new { keys = pair.Key, limit = pair.Value },
            commandTimeout: storage.CommandTimeout),
            new KeyValuePair<IEnumerable<string>, int>(keys, limit));
    }

    public override bool GetSetContains(string key, string value)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(value);

        return Storage.UseConnection(DedicatedConnection, static (storage, connection, pair) => connection.ExecuteScalar<int>(
            storage.GetQueryFromTemplate(static schemaName =>
                $@"select count(1) from [{schemaName}].[Set] with (forceseek) where [Key] = @key and [Value] = @value"),
            new { key = pair.Key, value = pair.Value },
            commandTimeout: storage.CommandTimeout) == 1, new KeyValuePair<string, string>(key, value));
    }

    // Ranges of GetRangeFromSet ending past this row may be numbered from the end of the set
    private const int ReadFromEndThreshold = 1000;

    public override List<string> GetRangeFromSet(string key, int startingFrom, int endingAt)
    {
        ArgumentNullException.ThrowIfNull(key);

        return Storage.UseConnection(DedicatedConnection, static (storage, connection, triple) =>
        {
            // Numbering the rows up to a range near the end of a long set reads the whole set (the tag page
            // reads the newest jobs that way), so a range closer to the end is numbered from the end instead.
            // Counting costs a read of the key's index rows, so it is only done for ranges that end far in, and
            // stops at twice the range's end: in a set at least that long, the range is nearer the start, and
            // numbering from the start reads fewer rows than counting further would.
            // Value breaks ties in Score, so both directions number the rows in the same order
            var query = storage.GetQueryFromTemplate(static schemaName =>
                $"""
                set nocount on;
                declare @total bigint = null;
                declare @countLimit bigint = 2 * cast(@endingAt as bigint);
                if @endingAt > {ReadFromEndThreshold}
                begin
                    select @total = count_big(*) from (
                        select top (@countLimit) 1 as N from [{schemaName}].[Set] with (forceseek) where [Key] = @key
                    ) as c;
                    if @total >= @countLimit set @total = null;
                end

                if @total is not null and @total - @startingFrom + 1 < @endingAt
                    select [Value] from (
                        select [Value], row_number() over (order by [Score] desc, [Value] desc) as row_num
                        from [{schemaName}].[Set] with (forceseek)
                        where [Key] = @key
                    ) as s where s.row_num between @total - @endingAt + 1 and @total - @startingFrom + 1
                    order by s.row_num desc;
                else
                    select [Value] from (
                        select [Value], row_number() over (order by [Score] asc, [Value] asc) as row_num
                        from [{schemaName}].[Set] with (forceseek)
                        where [Key] = @key
                    ) as s where s.row_num between @startingFrom and @endingAt
                    order by s.row_num;
                """);

            return connection
                .Query<string>(query, new { key = triple.Item1, startingFrom = triple.Item2 + 1, endingAt = triple.Item3 + 1 },
                    commandTimeout: storage.CommandTimeout)
                .ToList();
        }, ValueTuple.Create(key, startingFrom, endingAt));
    }

    public override TimeSpan GetSetTtl(string key)
    {
        ArgumentNullException.ThrowIfNull(key);

        return Storage.UseConnection(DedicatedConnection, static (storage, connection, key) =>
        {
            var query = storage.GetQueryFromTemplate(static schemaName =>
$@"select min([ExpireAt]) from [{schemaName}].[Set] with (forceseek) where [Key] = @key");

            var result = connection.ExecuteScalar<DateTime?>(query, new { key }, commandTimeout: storage.CommandTimeout);
            if (!result.HasValue) return TimeSpan.FromSeconds(-1);

            return result.Value - DateTime.UtcNow;
        }, key);
    }

    public override long GetCounter(string key)
    {
        ArgumentNullException.ThrowIfNull(key);

        return Storage.UseConnection(DedicatedConnection, static (storage, connection, key) =>
        {
            var query = storage.GetQueryFromTemplate(static schemaName =>
                $"""
                select sum(s.[Value]) from (select sum([Value]) as [Value] from [{schemaName}].Counter with (forceseek)
                where [Key] = @key
                union all
                select [Value] from [{schemaName}].AggregatedCounter with (forceseek)
                where [Key] = @key) as s
                """);

            return connection.ExecuteScalar<long?>(query, new { key },
                commandTimeout: storage.CommandTimeout) ?? 0;
        }, key);
    }

    public override long GetHashCount(string key)
    {
        ArgumentNullException.ThrowIfNull(key);

        return Storage.UseConnection(DedicatedConnection, static (storage, connection, key) =>
        {
            var query = storage.GetQueryFromTemplate(static schemaName =>
$@"select count(*) from [{schemaName}].Hash with (forceseek) where [Key] = @key");

            return connection.ExecuteScalar<long>(query, new { key },
                commandTimeout: storage.CommandTimeout);
        }, key);
    }

    public override TimeSpan GetHashTtl(string key)
    {
        ArgumentNullException.ThrowIfNull(key);

        return Storage.UseConnection(DedicatedConnection, static (storage, connection, key) =>
        {
            var query = storage.GetQueryFromTemplate(static schemaName =>
$@"select min([ExpireAt]) from [{schemaName}].Hash with (forceseek) where [Key] = @key");

            var result = connection.ExecuteScalar<DateTime?>(query, new { key }, commandTimeout: storage.CommandTimeout);
            if (!result.HasValue) return TimeSpan.FromSeconds(-1);

            return result.Value - DateTime.UtcNow;
        }, key);
    }

    public override string GetValueFromHash(string key, string name)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(name);

        return Storage.UseConnection(DedicatedConnection, static (storage, connection, pair) =>
        {
            var query = storage.GetQueryFromTemplate(static schemaName =>
                $"""
                select [Value] from [{schemaName}].Hash with (forceseek)
                where [Key] = @key and [Field] = @field
                """);

            return connection.ExecuteScalar<string>(query, new { key = pair.Key, field = pair.Value },
                commandTimeout: storage.CommandTimeout);
        }, new KeyValuePair<string, string>(key, name));
    }

    public override long GetListCount(string key)
    {
        ArgumentNullException.ThrowIfNull(key);

        return Storage.UseConnection(DedicatedConnection, static (storage, connection, key) =>
        {
            var query = storage.GetQueryFromTemplate(static schemaName =>
                $"""
                select count(*) from [{schemaName}].List with (forceseek)
                where [Key] = @key
                """);

            return connection.ExecuteScalar<long>(query, new { key },
                commandTimeout: storage.CommandTimeout);
        }, key);
    }

    public override TimeSpan GetListTtl(string key)
    {
        ArgumentNullException.ThrowIfNull(key);

        return Storage.UseConnection(DedicatedConnection, static (storage, connection, key) =>
        {
            var query = storage.GetQueryFromTemplate(static schemaName =>
                $"""
                select min([ExpireAt]) from [{schemaName}].List with (forceseek)
                where [Key] = @key
                """);

            var result = connection.ExecuteScalar<DateTime?>(query, new { key }, commandTimeout: storage.CommandTimeout);
            if (!result.HasValue) return TimeSpan.FromSeconds(-1);

            return result.Value - DateTime.UtcNow;
        }, key);
    }

    public override List<string> GetRangeFromList(string key, int startingFrom, int endingAt)
    {
        ArgumentNullException.ThrowIfNull(key);

        return Storage.UseConnection(DedicatedConnection, static (storage, connection, triple) =>
        {
            var query = storage.GetQueryFromTemplate(static schemaName =>
                $"""
                select [Value] from (
                	select [Value], row_number() over (order by [Id] desc) as row_num 
                	from [{schemaName}].List with (forceseek)
                	where [Key] = @key 
                ) as s where s.row_num between @startingFrom and @endingAt
                """);

            return connection
                .Query<string>(query, new { key = triple.Item1, startingFrom = triple.Item2 + 1, endingAt = triple.Item3 + 1 },
                    commandTimeout: storage.CommandTimeout)
                .ToList();
        }, ValueTuple.Create(key, startingFrom, endingAt));
    }

    public override List<string> GetAllItemsFromList(string key)
    {
        ArgumentNullException.ThrowIfNull(key);

        return Storage.UseConnection(DedicatedConnection, static (storage, connection, key) =>
        {
            var query = storage.GetQueryFromTemplate(static schemaName =>
                $"""
                select [Value] from [{schemaName}].List with (forceseek)
                where [Key] = @key
                order by [Id] desc
                """);

            return connection
                .Query<string>(query, new { key }, commandTimeout: storage.CommandTimeout)
                .ToList();
        }, key);
    }

    public override DateTime GetUtcDateTime()
    {
        return Storage.UseConnection(DedicatedConnection, static (_, connection) =>
            DateTime.SpecifyKind(connection.ExecuteScalar<DateTime>("SELECT SYSUTCDATETIME()"), DateTimeKind.Utc));
    }

    /// <summary>
    /// Parses a job id. Ids of this storage are numbers, so an id that isn't one names no job: reads return
    /// <c>null</c> for it and writes do nothing, instead of throwing <see cref="FormatException"/>.
    /// </summary>
    internal static bool TryParseJobId(string jobId, out long id)
    {
        return long.TryParse(jobId, NumberStyles.Integer, CultureInfo.InvariantCulture, out id);
    }

    internal DisposableLock AcquireLock(string resource, TimeSpan timeout)
    {
        DedicatedConnection ??= Storage.CreateAndOpenConnection();

        var lockId = Guid.NewGuid();
        var ownLock = false;

        if (!_lockedResources.TryGetValue(resource, out var lockIds))
        {
            try
            {
                SqlServerDistributedLock.Acquire(DedicatedConnection, resource, timeout);
                ownLock = true;
            }
            catch (Exception ex) when (ex.IsCatchableExceptionType())
            {
                ReleaseLock(resource, lockId, true, false);
                throw;
            }

            _lockedResources.Add(resource, lockIds = []);
        }

        lockIds.Add(lockId);
        return new DisposableLock(this, resource, lockId, ownLock);
    }

    private void ReleaseLock(string resource, Guid lockId, bool onDisposing, bool releasedExternally)
    {
        try
        {
            if (_lockedResources.TryGetValue(resource, out var lockIds))
            {
                if (lockIds.Contains(lockId))
                {
                    if (lockIds.Remove(lockId) &&
                        lockIds.Count == 0 &&
                        _lockedResources.Remove(resource) &&
                        DedicatedConnection.State == ConnectionState.Open)
                    {
                        // Session-scoped application locks are held only when connection
                        // is open. When connection is closed or broken, for example, when
                        // there was an error, application lock is already released by SQL
                        // Server itself, and we shouldn't do anything.
                        if (!releasedExternally)
                        {
                            SqlServerDistributedLock.Release(DedicatedConnection, resource);
                        }
                    }
                }
            }
        }
        catch (Exception ex) when (ex.IsCatchableExceptionType())
        {
            if (!onDisposing)
            {
                throw;
            }
        }
        finally
        {
            if (_lockedResources.Count == 0)
            {
                Storage.ReleaseConnection(DedicatedConnection);
                DedicatedConnection = null;
            }
        }
    }

    internal sealed class DisposableLock(SqlServerConnection connection, string resource, Guid lockId, bool ownLock) : IDisposable
    {
        private bool _disposed;
        private readonly SqlServerConnection _connection = connection;
        private readonly Guid _lockId = lockId;

        public string Resource { get; } = resource;
        public bool OwnLock { get; } = ownLock;
        public bool ReleasedExternally { get; private set; }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _connection.ReleaseLock(Resource, _lockId, true, ReleasedExternally);
        }

        public void TryReportReleased()
        {
            if (OwnLock) ReleasedExternally = true;
        }
    }
}
