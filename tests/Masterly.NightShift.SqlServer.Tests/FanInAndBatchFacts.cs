extern alias ReferencedDapper;

using System.Data.Common;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Castle.DynamicProxy;
using ReferencedDapper::Dapper;
using IInvocation = Castle.DynamicProxy.IInvocation;

namespace Masterly.NightShift.SqlServer.Tests;

/// <summary>
/// End-to-end checks of fan-in continuations, batches and tags against a real SQL Server: many parents
/// finishing at the same moment on a real server, and what the bookkeeping leaves in the tables.
/// </summary>
public class FanInAndBatchFacts
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(90);

    [Fact, CleanDatabase]
    public void FanInAndBatchContinuations_AllStart_WhenTheLastParentsFinishAtTheSameTime()
    {
        SqlServerStorage storage = CreateStorage();
        BackgroundJobClient client = new(storage);
        FanInJobs.Reset();

        // Two parents per child, run by many workers at once: the last two parents of a child often
        // finish together, which used to leave the child waiting forever
        const int pairs = 150;
        for (int i = 0; i < pairs; i++)
        {
            string? first = client.Enqueue(() => FanInJobs.Parent());
            string? second = client.Enqueue(() => FanInJobs.Parent());
            client.ContinueJobWithAll([first, second], () => FanInJobs.Child(), JobContinuationOptions.OnAnyFinishedState);
        }

        const int batches = 10;
        for (int i = 0; i < batches; i++)
        {
            string batchId = BatchJob.StartNew(client, storage, batch =>
            {
                for (int j = 0; j < 20; j++)
                {
                    batch.Enqueue(() => FanInJobs.Parent());
                }
            });

            BatchJob.ContinueWith(client, storage, batchId, Job.FromExpression(() => FanInJobs.BatchDone()));
        }

        using (new BackgroundJobServer(new BackgroundJobServerOptions { WorkerCount = 20 }, storage))
        {
            SpinWait.SpinUntil(() => FanInJobs.ChildRuns == pairs && FanInJobs.BatchRuns == batches, Timeout);
        }

        Assert.Equal(pairs, FanInJobs.ChildRuns);
        Assert.Equal(batches, FanInJobs.BatchRuns);
        Assert.Equal(0, CountJobs(AwaitingState.StateName));
    }

    [Fact, CleanDatabase]
    public void BatchJob_ExpiresEveryRowOfItsBookkeeping()
    {
        SqlServerStorage storage = CreateStorage();
        BackgroundJobClient client = new(storage);

        string batchId = BatchJob.StartNew(client, storage, batch =>
        {
            batch.Enqueue(() => FanInJobs.Parent());
            batch.Enqueue(() => FanInJobs.Parent());
        });
        BatchJob.ContinueWith(client, storage, batchId, Job.FromExpression(() => FanInJobs.BatchDone()));

        using DbConnection connection = ConnectionUtils.CreateConnection();
        List<(string Key, DateTime? ExpireAt)> rows = [.. connection.Query<(string Key, DateTime? ExpireAt)>(
            "select [Key], ExpireAt from [HangFire].[Set] where [Key] like 'batch:%' " +
            "union all select [Key], ExpireAt from [HangFire].[Hash] where [Key] like 'batch:%'")];

        Assert.Contains(rows, x => x.Key == BatchJob.GetJobsKey(batchId));
        Assert.Contains(rows, x => x.Key == BatchJob.GetContinuationsKey(batchId));
        Assert.Contains(rows, x => x.Key == BatchJob.GetHashKey(batchId));
        Assert.All(rows, x => Assert.NotNull(x.ExpireAt));
    }

    [Fact, CleanDatabase]
    public void BatchJob_StartNew_RemovesExpiredBatchesFromTheIndex()
    {
        SqlServerStorage storage = CreateStorage();
        BackgroundJobClient client = new(storage);

        using (IStorageConnection connection = storage.GetConnection())
        using (IWriteOnlyTransaction transaction = connection.CreateWriteTransaction())
        {
            transaction.AddToSet(BatchJob.IndexKey, "expired", JobHelper.ToTimestamp(DateTime.UtcNow - BatchJob.ExpireIn - TimeSpan.FromHours(1)));
            transaction.AddToSet(BatchJob.IndexKey, "recent", JobHelper.ToTimestamp(DateTime.UtcNow - TimeSpan.FromHours(1)));
            transaction.Commit();
        }

        string batchId = BatchJob.StartNew(client, storage, batch => batch.Enqueue(() => FanInJobs.Parent()));

        using (IStorageConnection connection = storage.GetConnection())
        {
            HashSet<string> index = connection.GetAllItemsFromSet(BatchJob.IndexKey);
            Assert.Equal(new[] { "recent", batchId }.OrderBy(x => x), index.OrderBy(x => x));
        }
    }

    [Fact, CleanDatabase]
    public void JobTags_AcceptsTheLongestTag_AndTagAttributeShortensLongerOnes()
    {
        SqlServerStorage storage = CreateStorage();
        BackgroundJobClient client = new(storage);
        string longest = new('t', JobTags.MaxLength);

        string? tagged = client.Enqueue(() => FanInJobs.Tagged(new string('v', 300)));
        string? untagged = client.Enqueue(() => FanInJobs.Tagged(null));

        using IStorageConnection connection = storage.GetConnection();
        JobTags.Add(connection, untagged, [longest]);

        string tag = Assert.Single(JobTags.GetTags(connection, tagged));
        Assert.Equal(JobTags.MaxLength, tag.Length);
        Assert.Equal([tagged], JobTags.GetJobIds(connection, tag, 0, 10));
        Assert.Equal([longest], JobTags.GetTags(connection, untagged));
        Assert.Equal(1, JobTags.Count(connection, longest));
    }

    [Fact, CleanDatabase]
    public void FanInChild_Starts_WhenTheParentsCommitGoesThrough_ButItsReplyIsLost()
    {
        SqlServerStorage storage = CreateStorage();
        BackgroundJobClient client = new(storage);
        string? parent = client.Create(() => FanInJobs.Parent(), new EnqueuedState());
        string? child = client.ContinueJobWithAll([parent], () => FanInJobs.Child(), JobContinuationOptions.OnAnyFinishedState);

        LostCommitReplyStorage flaky = new(storage);
        BackgroundJobClient flakyClient = new(flaky);

        // Like a worker: change the state from an expected state, and retry once when that throws
        Assert.ThrowsAny<Exception>(() => flakyClient.ChangeState(parent, new SucceededState(null, 0, 0), EnqueuedState.StateName));
        Assert.False(flakyClient.ChangeState(parent, new SucceededState(null, 0, 0), EnqueuedState.StateName));

        using IStorageConnection connection = storage.GetConnection();
        Assert.Equal(SucceededState.StateName, connection.GetStateData(parent).Name);
        Assert.Equal(EnqueuedState.StateName, connection.GetStateData(child).Name);
    }

    [Fact, CleanDatabase, CleanSerializerSettings]
    public void TagsAndBatches_Work_WhenTheApplicationsOptionsDoNotKnowStringArrays()
    {
        GlobalConfiguration.Configuration.SetDataCompatibilityLevel(CompatibilityLevel.Version_180);
        GlobalConfiguration.Configuration.UseSerializerSettings(new JsonSerializerOptions
        {
            // Like a source-generated context that knows the application's own types only
            TypeInfoResolver = new KnownTypesResolver(typeof(string), typeof(int))
        });

        try
        {
            SqlServerStorage storage = CreateStorage();
            BackgroundJobClient client = new(storage);
            string tagged = null;

            string batchId = BatchJob.StartNew(client, storage, batch => tagged = batch.Enqueue(() => FanInJobs.Tagged("Contoso")));
            string? continuation = BatchJob.ContinueWith(client, storage, batchId, Job.FromExpression(() => FanInJobs.BatchDone()));

            using (IStorageConnection connection = storage.GetConnection())
            {
                JobTags.Add(connection, tagged, ["billing"]);

                Assert.Equal(["billing", "contoso"], JobTags.GetTags(connection, tagged));
                Assert.Equal([tagged], JobTags.GetJobIds(connection, "billing", 0, 10));
                Assert.Equal([tagged], JobTags.GetJobIds(connection, "contoso", 0, 10));

                JobBatch? batch = BatchJob.Get(connection, batchId);
                Assert.Equal(1, batch.Total);
                Assert.Equal([continuation], batch.Continuations);
                Assert.Equal([tagged], BatchJob.GetJobIds(connection, batchId));
            }
        }
        finally
        {
            GlobalConfiguration.Configuration.SetDataCompatibilityLevel(CompatibilityLevel.Version_110);
        }
    }

    private static SqlServerStorage CreateStorage()
    {
        return new SqlServerStorage(
            ConnectionUtils.GetConnectionString(),
            new SqlServerStorageOptions { PrepareSchemaIfNecessary = false, QueuePollInterval = TimeSpan.FromMilliseconds(50) });
    }

    private static int CountJobs(string state)
    {
        using DbConnection connection = ConnectionUtils.CreateConnection();
        return connection.ExecuteScalar<int>("select count(*) from [HangFire].[Job] where StateName = @state", new { state });
    }
}

/// <summary>
/// A storage whose next commit goes through but then throws, as when the reply of the database is lost.
/// </summary>
internal sealed class LostCommitReplyStorage(JobStorage inner) : JobStorage
{
    private static readonly ProxyGenerator Generator = new();
    private readonly JobStorage _inner = inner;
    private int _commitsToFail = 1;

    public override bool LinearizableReads => _inner.LinearizableReads;

    public override bool HasFeature(string featureId) => false;

    public override IMonitoringApi GetMonitoringApi() => _inner.GetMonitoringApi();

    public override IEnumerable<IStateHandler> GetStateHandlers() => _inner.GetStateHandlers();

    public override IStorageConnection GetConnection()
    {
        return Generator.CreateInterfaceProxyWithTarget(_inner.GetConnection(), new ConnectionInterceptor(this));
    }

    private bool ShouldFailCommit() => Interlocked.Exchange(ref _commitsToFail, 0) == 1;

    private sealed class ConnectionInterceptor(LostCommitReplyStorage storage) : IInterceptor
    {
        private readonly LostCommitReplyStorage _storage = storage;

        public void Intercept(IInvocation invocation)
        {
            invocation.Proceed();

            if (invocation.Method.Name == nameof(IStorageConnection.CreateWriteTransaction))
            {
                invocation.ReturnValue = Generator.CreateInterfaceProxyWithTarget(
                    (IWriteOnlyTransaction)invocation.ReturnValue, new TransactionInterceptor(_storage));
            }
        }
    }

    private sealed class TransactionInterceptor(LostCommitReplyStorage storage) : IInterceptor
    {
        private readonly LostCommitReplyStorage _storage = storage;

        public void Intercept(IInvocation invocation)
        {
            invocation.Proceed();

            if (invocation.Method.Name == nameof(IWriteOnlyTransaction.Commit) && _storage.ShouldFailCommit())
            {
                throw new TimeoutException("The commit went through, but its reply was lost.");
            }
        }
    }
}

/// <summary>A resolver that knows only the given types, like a source-generated context.</summary>
internal sealed class KnownTypesResolver(params Type[] types) : IJsonTypeInfoResolver
{
    private readonly DefaultJsonTypeInfoResolver _inner = new();
    private readonly HashSet<Type> _types = [.. types];

    public JsonTypeInfo GetTypeInfo(Type type, JsonSerializerOptions options)
    {
        return _types.Contains(type) ? _inner.GetTypeInfo(type, options) : null;
    }
}

public static class FanInJobs
{
    private static int _childRuns;
    private static int _batchRuns;

    public static int ChildRuns => Volatile.Read(ref _childRuns);
    public static int BatchRuns => Volatile.Read(ref _batchRuns);

    public static void Reset()
    {
        Interlocked.Exchange(ref _childRuns, 0);
        Interlocked.Exchange(ref _batchRuns, 0);
    }

    public static void Parent()
    {
    }

    public static void Child() => Interlocked.Increment(ref _childRuns);

    public static void BatchDone() => Interlocked.Increment(ref _batchRuns);

    [Tag("{0}")]
    public static void Tagged(string value)
    {
    }
}
