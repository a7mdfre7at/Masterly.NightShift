extern alias ReferencedDapper;

using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Masterly.NightShift.Server;
using ReferencedDapper::Dapper;

namespace Masterly.NightShift.SqlServer.Tests;

/// <summary>
/// The concurrency filters against real SQL Server tables: keys built from long resources must fit the
/// <c>nvarchar(100)</c> key columns, and the sets they keep must expire.
/// </summary>
public class ConcurrencyFilterFacts
{
    // Long enough for "circuit:partner-{value}:failures" and "throttle:tenant-{value}" to exceed 100 characters
    private static readonly string LongValue = new('v', 95);

    [Fact, CleanDatabase]
    public void Throttle_TakesAndReleasesASlot_ForALongResource_AndTheSetExpires()
    {
        SqlServerStorage storage = CreateStorage();

        using IStorageConnection connection = storage.GetConnection();
        PerformContext context = CreatePerformContext(storage, connection, Job.FromExpression(() => Jobs.ForTenant(LongValue)));
        ThrottleAttribute throttle = new("tenant-{0}") { StaleTimeoutMinutes = 30 };
        PerformingContext performing = new(context);

        throttle.OnPerforming(performing);

        Assert.False(performing.Canceled);
        string key = Assert.Single(QuerySetKeys("throttle:tenant-%"));
        Assert.True(key.Length <= 100);
        Assert.Contains("~", key);
        Assert.Single(connection.GetAllItemsFromSet(key));
        Assert.All(QuerySetExpirations(key), expireAt => Assert.True(expireAt > DateTime.UtcNow.AddMinutes(25)));

        throttle.OnPerformed(new PerformedContext(context, null, false, null));

        Assert.Empty(connection.GetAllItemsFromSet(key));
    }

    [Fact, CleanDatabase]
    public void RateLimit_RecordsAStart_ForALongResource_AndTheSetExpiresWithTheWindow()
    {
        SqlServerStorage storage = CreateStorage();

        using IStorageConnection connection = storage.GetConnection();
        PerformContext context = CreatePerformContext(storage, connection, Job.FromExpression(() => Jobs.ForTenant(LongValue)));
        PerformingContext performing = new(context);

        new RateLimitAttribute("tenant-{0}", 5, 600).OnPerforming(performing);

        Assert.False(performing.Canceled);
        string key = Assert.Single(QuerySetKeys("ratelimit:tenant-%"));
        Assert.True(key.Length <= 100);
        Assert.All(QuerySetExpirations(key), expireAt => Assert.True(expireAt > DateTime.UtcNow.AddMinutes(9) && expireAt < DateTime.UtcNow.AddMinutes(12)));
    }

    [Fact, CleanDatabase]
    public void CircuitBreaker_RecordsTheFailure_AndOpens_ForALongResource()
    {
        SqlServerStorage storage = CreateStorage();
        BackgroundJobClient client = new(storage);
        string? jobId = client.Create(Job.FromExpression(() => Jobs.CallPartner(LongValue)), new EnqueuedState());

        // A failure that doesn't fit the storage used to break the transition, and the job lost its retries
        Assert.True(client.ChangeState(jobId, new FailedState(new TimeoutException()), EnqueuedState.StateName));

        using IStorageConnection connection = storage.GetConnection();
        Assert.Equal(FailedState.StateName, connection.GetStateData(jobId).Name);

        string resource = Assert.Single(connection.GetAllItemsFromSet(CircuitBreakerAttribute.CircuitsSetKey));
        Assert.StartsWith("partner-vvv", resource, StringComparison.Ordinal);

        CircuitBreakerAttribute.CircuitState circuit = CircuitBreakerAttribute.ReadCircuit(connection, resource);
        Assert.Equal(CircuitBreakerAttribute.OpenState, circuit.State);
        Assert.Equal(1, circuit.Failures);
        Assert.NotNull(circuit.OpenUntil);
        Assert.All(QuerySetExpirations($"circuit:{resource}:failures"), expireAt => Assert.NotNull(expireAt));
    }

    [Fact, CleanDatabase]
    public void RateLimit_CountsStartsByScore_AndRemovesTheOnesThatLeftTheWindow()
    {
        SqlServerStorage storage = CreateStorage();
        long now = JobHelper.ToTimestamp(DateTime.UtcNow);
        const string key = "ratelimit:sms:60";

        using IStorageConnection connection = storage.GetConnection();
        using (IWriteOnlyTransaction transaction = connection.CreateWriteTransaction())
        {
            transaction.AddToSet(key, $"{now - 120}:old", now - 120);
            transaction.AddToSet(key, $"{now - 30}:a", now - 30);
            transaction.AddToSet(key, $"{now - 10}:b", now - 10);
            transaction.Commit();
        }

        Job job = Job.FromExpression(() => Jobs.ForTenant("x"));
        PerformingContext refused = new(CreatePerformContext(storage, connection, job));
        new RateLimitAttribute("sms", 2, 60) { Action = ConcurrencyAction.Skip }.OnPerforming(refused);

        Assert.True(refused.Canceled);
        Assert.Equal([$"{now - 10}:b", $"{now - 30}:a"], connection.GetAllItemsFromSet(key).OrderByDescending(x => x, StringComparer.Ordinal));

        PerformingContext allowed = new(CreatePerformContext(storage, connection, job));
        new RateLimitAttribute("sms", 3, 60) { Action = ConcurrencyAction.Skip }.OnPerforming(allowed);

        Assert.False(allowed.Canceled);
        Assert.Equal(3, connection.GetAllItemsFromSet(key).Count);
    }

    [Fact, CleanDatabase]
    public void PreventDuplicates_RefusesTheSecondCopy_ForALongTemplatedFingerprint()
    {
        SqlServerStorage storage = CreateStorage();
        BackgroundJobClient client = new(storage);

        string? first = client.Create(Job.FromExpression(() => Jobs.Import(LongValue)), new EnqueuedState());
        string? second = client.Create(Job.FromExpression(() => Jobs.Import(LongValue)), new EnqueuedState());

        Assert.NotNull(first);
        Assert.Null(second);
    }

    [Fact, CleanDatabase]
    public void Throttle_ReclaimsTheLeakedSlotOfASucceededJob_ButNotTheSlotOfAProcessingOne()
    {
        SqlServerStorage storage = CreateStorage();
        BackgroundJobClient client = new(storage);
        Job job = Job.FromExpression(() => Jobs.ForTenant("x"));
        long now = JobHelper.ToTimestamp(DateTime.UtcNow);
        const string key = "throttle:gpu";

        string? succeeded = client.Create(job, new SucceededState(null, 1, 1));
        string? processing = client.Create(job, CreateProcessingState("worker"));
        string? waiting = client.Create(job, CreateProcessingState("worker-2"));

        using IStorageConnection connection = storage.GetConnection();
        // The succeeded job's release failed, so its slot stayed in the set
        using (IWriteOnlyTransaction transaction = connection.CreateWriteTransaction())
        {
            transaction.AddToSet(key, $"{now - 60}:{succeeded}", now - 60);
            transaction.AddToSet(key, $"{now - 30}:{processing}", now - 30);
            transaction.Commit();
        }

        ThrottleAttribute throttle = new("gpu") { MaxConcurrency = 2, Action = ConcurrencyAction.Skip };
        PerformingContext performing = new(CreatePerformContext(storage, connection, job, waiting));

        throttle.OnPerforming(performing);

        Assert.False(performing.Canceled);
        Assert.Equal(
            new[] { processing, waiting }.OrderBy(x => x, StringComparer.Ordinal),
            connection.GetAllItemsFromSet(key).Select(x => x[(x.IndexOf(':') + 1)..]).OrderBy(x => x, StringComparer.Ordinal));

        // Now both slots belong to Processing jobs: a third job is refused
        PerformingContext refused = new(CreatePerformContext(storage, connection, job, "999999"));
        throttle.OnPerforming(refused);

        Assert.True(refused.Canceled);
        Assert.Equal(2, connection.GetAllItemsFromSet(key).Count);
    }

    // The constructor is internal: workers create the state
    private static ProcessingState CreateProcessingState(string workerId)
    {
        ConstructorInfo ctor = typeof(ProcessingState).GetConstructors(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).Single();
        return (ProcessingState)ctor.Invoke(["server", workerId]);
    }

    private static SqlServerStorage CreateStorage()
    {
        return new SqlServerStorage(ConnectionUtils.GetConnectionString(), new SqlServerStorageOptions { PrepareSchemaIfNecessary = false });
    }

    private static PerformContext CreatePerformContext(JobStorage storage, IStorageConnection connection, Job job, string jobId = "1")
    {
        Mock<IJobCancellationToken> token = new();
        token.Setup(x => x.ShutdownToken).Returns(CancellationToken.None);

        return new PerformContext(storage, connection, new BackgroundJob(jobId, job, DateTime.UtcNow), token.Object);
    }

    private static string[] QuerySetKeys(string pattern)
    {
        using DbConnection connection = ConnectionUtils.CreateConnection();
        return [.. connection.Query<string>("select distinct [Key] from [HangFire].[Set] where [Key] like @pattern", new { pattern })];
    }

    private static DateTime?[] QuerySetExpirations(string key)
    {
        using DbConnection connection = ConnectionUtils.CreateConnection();
        return [.. connection.Query<DateTime?>("select ExpireAt from [HangFire].[Set] where [Key] = @key", new { key })];
    }

    [SuppressMessage("Usage", "xUnit1013:Public method should be marked as test")]
    public static class Jobs
    {
        public static void ForTenant(string tenant)
        {
        }

        [CircuitBreaker("partner-{0}", failureThreshold: 1, windowSeconds: 60, openSeconds: 60)]
        [AutomaticRetry(Attempts = 0)]
        public static void CallPartner(string partner)
        {
        }

        [PreventDuplicates("import:{0}")]
        public static void Import(string file)
        {
        }
    }
}
