extern alias ReferencedDapper;

using System;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using ReferencedDapper::Dapper;
using Masterly.NightShift.Common;
using Masterly.NightShift.Server;
using Masterly.NightShift.States;
using Masterly.NightShift.Storage;
using Moq;
using Xunit;

namespace Masterly.NightShift.SqlServer.Tests
{
    /// <summary>
    /// The concurrency filters against real SQL Server tables: keys built from long resources must fit the
    /// <c>nvarchar(100)</c> key columns, and the sets they keep must expire.
    /// </summary>
    public class ConcurrencyFilterFacts
    {
        // Long enough for "circuit:partner-{value}:failures" and "throttle:tenant-{value}" to exceed 100 characters
        private static readonly string LongValue = new string('v', 95);

        [Fact, CleanDatabase]
        public void Throttle_TakesAndReleasesASlot_ForALongResource_AndTheSetExpires()
        {
            var storage = CreateStorage();

            using (var connection = storage.GetConnection())
            {
                var context = CreatePerformContext(storage, connection, Job.FromExpression(() => Jobs.ForTenant(LongValue)));
                var throttle = new ThrottleAttribute("tenant-{0}") { StaleTimeoutMinutes = 30 };
                var performing = new PerformingContext(context);

                throttle.OnPerforming(performing);

                Assert.False(performing.Canceled);
                var key = Assert.Single(QuerySetKeys("throttle:tenant-%"));
                Assert.True(key.Length <= 100);
                Assert.Contains("~", key);
                Assert.Single(connection.GetAllItemsFromSet(key));
                Assert.All(QuerySetExpirations(key), expireAt => Assert.True(expireAt > DateTime.UtcNow.AddMinutes(25)));

                throttle.OnPerformed(new PerformedContext(context, null, false, null));

                Assert.Empty(connection.GetAllItemsFromSet(key));
            }
        }

        [Fact, CleanDatabase]
        public void RateLimit_RecordsAStart_ForALongResource_AndTheSetExpiresWithTheWindow()
        {
            var storage = CreateStorage();

            using (var connection = storage.GetConnection())
            {
                var context = CreatePerformContext(storage, connection, Job.FromExpression(() => Jobs.ForTenant(LongValue)));
                var performing = new PerformingContext(context);

                new RateLimitAttribute("tenant-{0}", 5, 600).OnPerforming(performing);

                Assert.False(performing.Canceled);
                var key = Assert.Single(QuerySetKeys("ratelimit:tenant-%"));
                Assert.True(key.Length <= 100);
                Assert.All(QuerySetExpirations(key), expireAt => Assert.True(expireAt > DateTime.UtcNow.AddMinutes(9) && expireAt < DateTime.UtcNow.AddMinutes(12)));
            }
        }

        [Fact, CleanDatabase]
        public void CircuitBreaker_RecordsTheFailure_AndOpens_ForALongResource()
        {
            var storage = CreateStorage();
            var client = new BackgroundJobClient(storage);
            var jobId = client.Create(Job.FromExpression(() => Jobs.CallPartner(LongValue)), new EnqueuedState());

            // A failure that doesn't fit the storage used to break the transition, and the job lost its retries
            Assert.True(client.ChangeState(jobId, new FailedState(new TimeoutException()), EnqueuedState.StateName));

            using (var connection = storage.GetConnection())
            {
                Assert.Equal(FailedState.StateName, connection.GetStateData(jobId).Name);

                var resource = Assert.Single(connection.GetAllItemsFromSet(CircuitBreakerAttribute.CircuitsSetKey));
                Assert.StartsWith("partner-vvv", resource, StringComparison.Ordinal);

                var circuit = CircuitBreakerAttribute.ReadCircuit(connection, resource);
                Assert.Equal(CircuitBreakerAttribute.OpenState, circuit.State);
                Assert.Equal(1, circuit.Failures);
                Assert.NotNull(circuit.OpenUntil);
                Assert.All(QuerySetExpirations($"circuit:{resource}:failures"), expireAt => Assert.NotNull(expireAt));
            }
        }

        [Fact, CleanDatabase]
        public void RateLimit_CountsStartsByScore_AndRemovesTheOnesThatLeftTheWindow()
        {
            var storage = CreateStorage();
            var now = JobHelper.ToTimestamp(DateTime.UtcNow);
            const string key = "ratelimit:sms:60";

            using (var connection = storage.GetConnection())
            {
                using (var transaction = connection.CreateWriteTransaction())
                {
                    transaction.AddToSet(key, $"{now - 120}:old", now - 120);
                    transaction.AddToSet(key, $"{now - 30}:a", now - 30);
                    transaction.AddToSet(key, $"{now - 10}:b", now - 10);
                    transaction.Commit();
                }

                var job = Job.FromExpression(() => Jobs.ForTenant("x"));
                var refused = new PerformingContext(CreatePerformContext(storage, connection, job));
                new RateLimitAttribute("sms", 2, 60) { Action = ConcurrencyAction.Skip }.OnPerforming(refused);

                Assert.True(refused.Canceled);
                Assert.Equal(new[] { $"{now - 10}:b", $"{now - 30}:a" }, connection.GetAllItemsFromSet(key).OrderByDescending(x => x, StringComparer.Ordinal));

                var allowed = new PerformingContext(CreatePerformContext(storage, connection, job));
                new RateLimitAttribute("sms", 3, 60) { Action = ConcurrencyAction.Skip }.OnPerforming(allowed);

                Assert.False(allowed.Canceled);
                Assert.Equal(3, connection.GetAllItemsFromSet(key).Count);
            }
        }

        [Fact, CleanDatabase]
        public void PreventDuplicates_RefusesTheSecondCopy_ForALongTemplatedFingerprint()
        {
            var storage = CreateStorage();
            var client = new BackgroundJobClient(storage);

            var first = client.Create(Job.FromExpression(() => Jobs.Import(LongValue)), new EnqueuedState());
            var second = client.Create(Job.FromExpression(() => Jobs.Import(LongValue)), new EnqueuedState());

            Assert.NotNull(first);
            Assert.Null(second);
        }

        [Fact, CleanDatabase]
        public void Throttle_ReclaimsTheLeakedSlotOfASucceededJob_ButNotTheSlotOfAProcessingOne()
        {
            var storage = CreateStorage();
            var client = new BackgroundJobClient(storage);
            var job = Job.FromExpression(() => Jobs.ForTenant("x"));
            var now = JobHelper.ToTimestamp(DateTime.UtcNow);
            const string key = "throttle:gpu";

            var succeeded = client.Create(job, new SucceededState(null, 1, 1));
            var processing = client.Create(job, CreateProcessingState("worker"));
            var waiting = client.Create(job, CreateProcessingState("worker-2"));

            using (var connection = storage.GetConnection())
            {
                // The succeeded job's release failed, so its slot stayed in the set
                using (var transaction = connection.CreateWriteTransaction())
                {
                    transaction.AddToSet(key, $"{now - 60}:{succeeded}", now - 60);
                    transaction.AddToSet(key, $"{now - 30}:{processing}", now - 30);
                    transaction.Commit();
                }

                var throttle = new ThrottleAttribute("gpu") { MaxConcurrency = 2, Action = ConcurrencyAction.Skip };
                var performing = new PerformingContext(CreatePerformContext(storage, connection, job, waiting));

                throttle.OnPerforming(performing);

                Assert.False(performing.Canceled);
                Assert.Equal(
                    new[] { processing, waiting }.OrderBy(x => x, StringComparer.Ordinal),
                    connection.GetAllItemsFromSet(key).Select(x => x.Substring(x.IndexOf(':') + 1)).OrderBy(x => x, StringComparer.Ordinal));

                // Now both slots belong to Processing jobs: a third job is refused
                var refused = new PerformingContext(CreatePerformContext(storage, connection, job, "999999"));
                throttle.OnPerforming(refused);

                Assert.True(refused.Canceled);
                Assert.Equal(2, connection.GetAllItemsFromSet(key).Count);
            }
        }

        // The constructor is internal: workers create the state
        private static ProcessingState CreateProcessingState(string workerId)
        {
            var ctor = typeof(ProcessingState).GetConstructors(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).Single();
            return (ProcessingState)ctor.Invoke(new object[] { "server", workerId });
        }

        private static SqlServerStorage CreateStorage()
        {
            return new SqlServerStorage(ConnectionUtils.GetConnectionString(), new SqlServerStorageOptions { PrepareSchemaIfNecessary = false });
        }

        private static PerformContext CreatePerformContext(JobStorage storage, IStorageConnection connection, Job job, string jobId = "1")
        {
            var token = new Mock<IJobCancellationToken>();
            token.Setup(x => x.ShutdownToken).Returns(CancellationToken.None);

            return new PerformContext(storage, connection, new BackgroundJob(jobId, job, DateTime.UtcNow), token.Object);
        }

        private static string[] QuerySetKeys(string pattern)
        {
            using (var connection = ConnectionUtils.CreateConnection(microsoftDataSqlClient: true))
            {
                return connection.Query<string>("select distinct [Key] from [HangFire].[Set] where [Key] like @pattern", new { pattern }).ToArray();
            }
        }

        private static DateTime?[] QuerySetExpirations(string key)
        {
            using (var connection = ConnectionUtils.CreateConnection(microsoftDataSqlClient: true))
            {
                return connection.Query<DateTime?>("select ExpireAt from [HangFire].[Set] where [Key] = @key", new { key }).ToArray();
            }
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
}
