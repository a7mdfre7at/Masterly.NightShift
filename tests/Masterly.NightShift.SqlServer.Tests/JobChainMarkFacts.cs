extern alias ReferencedDapper;

using System.Collections.Concurrent;
using System.Data.Common;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using Masterly.NightShift.Client;
using Microsoft.Data.SqlClient;
using ReferencedDapper::Dapper;

namespace Masterly.NightShift.SqlServer.Tests;

/// <summary>
/// The mark of a recorded step (the <c>ChainRecorded</c> job parameter) is committed with the record that lists
/// the step: no step is ever listed without its mark, a record that fails to commit leaves no mark, and a step
/// whose job is gone doesn't keep the chain from being recorded.
/// </summary>
[SuppressMessage("Usage", "xUnit1013:Public method should be marked as test", Justification = "Job methods must be public")]
public class JobChainMarkFacts
{
    // What a recorded step is marked with: how long its record is kept at least, in seconds
    private static string RecordedMark => ((long)JobChain.RecordRetention.TotalSeconds).ToString(CultureInfo.InvariantCulture);

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    // How long a record is watched for listing its steps while one of their marks can't be written
    private static readonly TimeSpan Watched = TimeSpan.FromSeconds(2);

    // The entry of a record's map of jobs that says the chain is being created: the lease of its client
    private const string LeaseKey = "changing";

    [Theory, CleanDatabase]
    [InlineData(3, 1)]
    // Too many commands for one round trip: the record goes through SqlClient's transaction and a command batch
    [InlineData(80, 40)]
    public async Task Chain_ListsItsSteps_OnlyOnceEveryOneOfThemIsMarked(int steps, int blockedStep)
    {
        SqlServerStorage storage = CreateStorage();
        CreatedSteps created = new();
        GlobalJobFilters.Filters.Add(created);

        using DbConnection blocker = ConnectionUtils.CreateConnection();
        using TransactionBegunLater blocking = new(blocker);
        using ManualResetEventSlim blocked = new(false);
        Task<JobChainResult> creation = null;

        try
        {
            // Once the last step is created, and before the chain is recorded, another session holds the row of
            // one step's mark: nobody can write that mark until the session lets go
            created.OnCreated = () =>
            {
                if (created.Ids.Count < steps)
                {
                    return;
                }

                blocker.Execute(
                    $"insert into [{Constants.DefaultSchema}].JobParameter (JobId, Name, Value) values (@id, @name, null)",
                    new { id = long.Parse(created.Ids[blockedStep]), name = JobChains.ChainRecordedParameter },
                    blocking.Begin());
                blocked.Set();
            };

            creation = Task.Run(() => Chain(steps).Enqueue(new BackgroundJobClient(storage)), TestContext.Current.CancellationToken);

            await Task.WhenAny(creation, Task.Run(() => blocked.Wait(Timeout), TestContext.Current.CancellationToken));
            Assert.True(blocked.IsSet, creation.IsFaulted ? creation.Exception.ToString() : "The steps weren't created in time.");
            string chainId = Parameter(storage, created.Ids[0], JobChains.ChainIdParameter).Trim('"');

            // While the mark can't be written, the record lists no step: a record that listed them now would
            // list steps without their mark
            Stopwatch stopwatch = Stopwatch.StartNew();
            while (stopwatch.Elapsed < Watched)
            {
                AssertEveryListedStepIsMarked(storage, chainId);
                await Task.Delay(50, TestContext.Current.CancellationToken);
            }

            Assert.Empty(ListedSteps(storage, chainId));
            Assert.False(creation.IsCompleted, "The chain was created although one of its steps couldn't be marked.");

            // The other session lets go, without having written the mark
            blocking.Rollback();
            JobChainResult result = await creation.WaitAsync(Timeout, TestContext.Current.CancellationToken);

            Assert.Equal(steps, ListedSteps(storage, chainId).Count);
            Assert.Equal(chainId, result.ChainId);
            AssertEveryListedStepIsMarked(storage, chainId);
        }
        finally
        {
            GlobalJobFilters.Filters.Remove(created);

            // A failed test must not leave the creation waiting for the other session
            blocking.RollBackIfOpen();

            if (creation != null)
            {
                // Without its result: only a Task, not a Task<T>, can be awaited with SuppressThrowing
                Task finishing = creation;
                await finishing.WaitAsync(Timeout, CancellationToken.None).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
            }
        }
    }

    [Theory, CleanDatabase]
    [InlineData(3)]
    // Too many commands for one round trip
    [InlineData(80)]
    public void Chain_IsRecorded_WithTheMarksOfTheStepsThatExist_WhenTheJobOfAStepIsGone(int steps)
    {
        SqlServerStorage storage = CreateStorage();
        CreatedSteps created = new();
        GlobalJobFilters.Filters.Add(created);

        try
        {
            // Once the last step is created, and before the chain is recorded, the job of that step is gone:
            // purged, or deleted from the table by hand
            created.OnCreated = () =>
            {
                if (created.Ids.Count < steps)
                {
                    return;
                }

                using DbConnection sql = ConnectionUtils.CreateConnection();
                sql.Execute($"delete from [{Constants.DefaultSchema}].Job where Id = @id", new { id = long.Parse(created.Ids[steps - 1]) });
            };

            JobChainResult chain = Chain(steps).Enqueue(new BackgroundJobClient(storage));

            // The chain is recorded and listed, with every step it was created with
            long gone = long.Parse(created.Ids[steps - 1]);
            List<long> listed = ListedSteps(storage, chain.ChainId);
            Assert.Equal(steps, listed.Count);
            Assert.Contains(gone, listed);

            using IStorageConnection connection = storage.GetConnection();
            Assert.Contains(chain.ChainId, connection.GetAllItemsFromSet(JobChains.SetKey));

            // Every step that exists is marked, and nothing is written for the one that is gone
            using DbConnection reader = ConnectionUtils.CreateConnection();
            List<long> marked = [.. reader.Query<long>(
                $"select JobId from [{Constants.DefaultSchema}].JobParameter where Name = @name and Value = @mark order by JobId",
                new { name = JobChains.ChainRecordedParameter, mark = RecordedMark })];

            Assert.Equal(listed.Where(id => id != gone).Order(), marked);
            Assert.Equal(0, reader.ExecuteScalar<int>($"select count(*) from [{Constants.DefaultSchema}].Job where Id = @id", new { id = gone }));
        }
        finally
        {
            GlobalJobFilters.Filters.Remove(created);
        }
    }

    [Theory, CleanDatabase]
    // One round trip
    [InlineData(3, true)]
    // SqlClient's transaction and a command batch
    [InlineData(80, true)]
    // SqlClient's transaction, command after command
    [InlineData(3, false)]
    public void Record_WhoseCommitFails_LeavesNeitherTheRecordNorAMark(int steps, bool useBatching)
    {
        SqlServerStorage storage = CreateStorage(x => x.CommandBatchMaxTimeout = useBatching ? TimeSpan.FromMinutes(5) : null);
        const string chainId = "failing-chain";
        string[] jobIds = [.. Enumerable.Range(0, steps).Select(number => CreateJob(storage, number))];

        using (IStorageConnection connection = storage.GetConnection())
        using (JobStorageTransaction transaction = (JobStorageTransaction)connection.CreateWriteTransaction())
        {
            // What recording a chain writes: the record, the list of chains and the mark of every step
            transaction.SetRangeInHash(JobChains.GetKey(chainId), new Dictionary<string, string>
            {
                ["Definition"] = "{}",
                ["Jobs"] = JsonSerializer.Serialize(jobIds.Select(static (id, number) => (id, number)).ToDictionary(static x => $"{x.number}.0", static x => x.id)),
            });
            transaction.AddToSet(JobChains.SetKey, chainId, 1);

            foreach (string jobId in jobIds)
            {
                transaction.SetJobParameter(jobId, JobChains.ChainRecordedParameter, "true");
            }

            // A state for a job that doesn't exist breaks the foreign key, after the marks are written
            transaction.SetJobState("987654321", new EnqueuedState());
            transaction.ExpireHash(JobChains.GetKey(chainId), TimeSpan.FromDays(30));

            Assert.Throws<SqlException>(transaction.Commit);
        }

        using DbConnection sql = ConnectionUtils.CreateConnection();
        Assert.Equal(0, sql.ExecuteScalar<int>($"select count(*) from [{Constants.DefaultSchema}].Hash where [Key] = @key", new { key = JobChains.GetKey(chainId) }));
        Assert.Equal(0, sql.ExecuteScalar<int>($"select count(*) from [{Constants.DefaultSchema}].[Set] where [Key] = @key", new { key = JobChains.SetKey }));
        Assert.Equal(0, sql.ExecuteScalar<int>($"select count(*) from [{Constants.DefaultSchema}].JobParameter where Name = @name", new { name = JobChains.ChainRecordedParameter }));
    }

    public static void Step(int number)
    {
    }

    /// <summary>
    /// A chain of that many steps: a first step and the others after it, side by side, as a chain has fewer
    /// levels than these tests have steps.
    /// </summary>
    private static JobChain Chain(int steps)
    {
        return JobChain.StartWith(() => Step(0)).ThenMany(branches =>
        {
            for (int i = 1; i < steps; i++)
            {
                int number = i;
                branches.Branch(() => Step(number));
            }
        });
    }

    /// <summary>The jobs a chain's record lists as its steps, read from the table.</summary>
    private static List<long> ListedSteps(SqlServerStorage storage, string chainId)
    {
        using DbConnection sql = ConnectionUtils.CreateConnection();
        string jobs = sql.ExecuteScalar<string>(
            $"select Value from [{Constants.DefaultSchema}].Hash where [Key] = @key and Field = N'Jobs'",
            new { key = JobChains.GetKey(chainId) });

        if (jobs == null)
        {
            return [];
        }

        // The record of a chain that is being created carries the lease of its client, which is no step
        return [.. JsonSerializer.Deserialize<Dictionary<string, string>>(jobs)
            .Where(static x => x.Key != LeaseKey)
            .Select(static x => long.Parse(x.Value))];
    }

    /// <summary>
    /// Reads the record, then the marks: a mark that is written but not committed doesn't count (its row is
    /// skipped, not waited for).
    /// </summary>
    private static void AssertEveryListedStepIsMarked(SqlServerStorage storage, string chainId)
    {
        List<long> listed = ListedSteps(storage, chainId);
        if (listed.Count == 0)
        {
            return;
        }

        using DbConnection sql = ConnectionUtils.CreateConnection();
        HashSet<long> marked = [.. sql.Query<long>(
            $"select JobId from [{Constants.DefaultSchema}].JobParameter with (readpast) where Name = @name and Value = @mark",
            new { name = JobChains.ChainRecordedParameter, mark = RecordedMark })];

        Assert.DoesNotContain(listed, id => !marked.Contains(id));
    }

    private static string CreateJob(SqlServerStorage storage, int number)
    {
        using IStorageConnection connection = storage.GetConnection();
        return connection.CreateExpiredJob(
            Job.FromExpression(() => Step(number)),
            new Dictionary<string, string>(),
            DateTime.UtcNow,
            TimeSpan.FromHours(1));
    }

    private static string Parameter(SqlServerStorage storage, string jobId, string name)
    {
        using IStorageConnection connection = storage.GetConnection();
        return connection.GetJobParameter(jobId, name);
    }

    private static SqlServerStorage CreateStorage(Action<SqlServerStorageOptions> configure = null)
    {
        SqlServerStorageOptions options = new() { PrepareSchemaIfNecessary = false };
        configure?.Invoke(options);
        return new SqlServerStorage(ConnectionUtils.GetConnectionString(), options);
    }

    /// <summary>Tells the test which job each step of a chain became, as the steps are created.</summary>
    private sealed class CreatedSteps : IClientFilter
    {
        public ConcurrentDictionary<int, string> Ids { get; } = new();
        public Action OnCreated { get; set; }

        public void OnCreating(CreatingContext context)
        {
        }

        void IClientFilter.OnCreated(CreatedContext context)
        {
            if (context.Job.Type == typeof(JobChainMarkFacts) && context.BackgroundJob != null)
            {
                int number = (int)context.Job.Args[0];
                Ids[number] = context.BackgroundJob.Id;
                OnCreated?.Invoke();
            }
        }
    }

    /// <summary>
    /// A transaction another thread begins on the connection while the test runs; rolled back, if still open, and
    /// disposed with this object.
    /// </summary>
    private sealed class TransactionBegunLater(DbConnection connection) : IDisposable
    {
        private DbTransaction? _transaction;

        public DbTransaction Begin()
        {
            _transaction = connection.BeginTransaction();
            return _transaction;
        }

        public void Rollback()
        {
            DbTransaction transaction = _transaction ?? throw new InvalidOperationException("No transaction was begun.");
            transaction.Rollback();
        }

        public void RollBackIfOpen()
        {
            if (_transaction?.Connection != null)
            {
                _transaction.Rollback();
            }
        }

        public void Dispose()
        {
            RollBackIfOpen();
            _transaction?.Dispose();
        }
    }
}
