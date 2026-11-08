extern alias ReferencedDapper;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using Castle.DynamicProxy;
using Masterly.NightShift.Common;
using Masterly.NightShift.States;
using Masterly.NightShift.Storage;
using ReferencedDapper::Dapper;
using Xunit;

namespace Masterly.NightShift.SqlServer.Tests
{
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
            var storage = CreateStorage();
            var client = new BackgroundJobClient(storage);
            FanInJobs.Reset();

            // Two parents per child, run by many workers at once: the last two parents of a child often
            // finish together, which used to leave the child waiting forever
            const int pairs = 150;
            for (var i = 0; i < pairs; i++)
            {
                var first = client.Enqueue(() => FanInJobs.Parent());
                var second = client.Enqueue(() => FanInJobs.Parent());
                client.ContinueJobWithAll(new[] { first, second }, () => FanInJobs.Child(), JobContinuationOptions.OnAnyFinishedState);
            }

            const int batches = 10;
            for (var i = 0; i < batches; i++)
            {
                var batchId = BatchJob.StartNew(client, storage, batch =>
                {
                    for (var j = 0; j < 20; j++)
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
            var storage = CreateStorage();
            var client = new BackgroundJobClient(storage);

            var batchId = BatchJob.StartNew(client, storage, batch =>
            {
                batch.Enqueue(() => FanInJobs.Parent());
                batch.Enqueue(() => FanInJobs.Parent());
            });
            BatchJob.ContinueWith(client, storage, batchId, Job.FromExpression(() => FanInJobs.BatchDone()));

            using (var connection = ConnectionUtils.CreateConnection(microsoftDataSqlClient: true))
            {
                var rows = connection.Query<(string Key, DateTime? ExpireAt)>(
                    "select [Key], ExpireAt from [HangFire].[Set] where [Key] like 'batch:%' " +
                    "union all select [Key], ExpireAt from [HangFire].[Hash] where [Key] like 'batch:%'").ToList();

                Assert.Contains(rows, x => x.Key == BatchJob.GetJobsKey(batchId));
                Assert.Contains(rows, x => x.Key == BatchJob.GetContinuationsKey(batchId));
                Assert.Contains(rows, x => x.Key == BatchJob.GetHashKey(batchId));
                Assert.All(rows, x => Assert.NotNull(x.ExpireAt));
            }
        }

        [Fact, CleanDatabase]
        public void BatchJob_StartNew_RemovesExpiredBatchesFromTheIndex()
        {
            var storage = CreateStorage();
            var client = new BackgroundJobClient(storage);

            using (var connection = storage.GetConnection())
            using (var transaction = connection.CreateWriteTransaction())
            {
                transaction.AddToSet(BatchJob.IndexKey, "expired", JobHelper.ToTimestamp(DateTime.UtcNow - BatchJob.ExpireIn - TimeSpan.FromHours(1)));
                transaction.AddToSet(BatchJob.IndexKey, "recent", JobHelper.ToTimestamp(DateTime.UtcNow - TimeSpan.FromHours(1)));
                transaction.Commit();
            }

            var batchId = BatchJob.StartNew(client, storage, batch => batch.Enqueue(() => FanInJobs.Parent()));

            using (var connection = storage.GetConnection())
            {
                var index = connection.GetAllItemsFromSet(BatchJob.IndexKey);
                Assert.Equal(new[] { "recent", batchId }.OrderBy(x => x), index.OrderBy(x => x));
            }
        }

        [Fact, CleanDatabase]
        public void JobTags_AcceptsTheLongestTag_AndTagAttributeShortensLongerOnes()
        {
            var storage = CreateStorage();
            var client = new BackgroundJobClient(storage);
            var longest = new string('t', JobTags.MaxLength);

            var tagged = client.Enqueue(() => FanInJobs.Tagged(new string('v', 300)));
            var untagged = client.Enqueue(() => FanInJobs.Tagged(null));

            using (var connection = storage.GetConnection())
            {
                JobTags.Add(connection, untagged, new[] { longest });

                var tag = Assert.Single(JobTags.GetTags(connection, tagged));
                Assert.Equal(JobTags.MaxLength, tag.Length);
                Assert.Equal(new[] { tagged }, JobTags.GetJobIds(connection, tag, 0, 10));
                Assert.Equal(new[] { longest }, JobTags.GetTags(connection, untagged));
                Assert.Equal(1, JobTags.Count(connection, longest));
            }
        }

        [Fact, CleanDatabase]
        public void FanInChild_Starts_WhenTheParentsCommitGoesThrough_ButItsReplyIsLost()
        {
            var storage = CreateStorage();
            var client = new BackgroundJobClient(storage);
            var parent = client.Create(() => FanInJobs.Parent(), new EnqueuedState());
            var child = client.ContinueJobWithAll(new[] { parent }, () => FanInJobs.Child(), JobContinuationOptions.OnAnyFinishedState);

            var flaky = new LostCommitReplyStorage(storage);
            var flakyClient = new BackgroundJobClient(flaky);

            // Like a worker: change the state from an expected state, and retry once when that throws
            Assert.ThrowsAny<Exception>(() => flakyClient.ChangeState(parent, new SucceededState(null, 0, 0), EnqueuedState.StateName));
            Assert.False(flakyClient.ChangeState(parent, new SucceededState(null, 0, 0), EnqueuedState.StateName));

            using (var connection = storage.GetConnection())
            {
                Assert.Equal(SucceededState.StateName, connection.GetStateData(parent).Name);
                Assert.Equal(EnqueuedState.StateName, connection.GetStateData(child).Name);
            }
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
                var storage = CreateStorage();
                var client = new BackgroundJobClient(storage);
                string tagged = null;

                var batchId = BatchJob.StartNew(client, storage, batch => tagged = batch.Enqueue(() => FanInJobs.Tagged("Contoso")));
                var continuation = BatchJob.ContinueWith(client, storage, batchId, Job.FromExpression(() => FanInJobs.BatchDone()));

                using (var connection = storage.GetConnection())
                {
                    JobTags.Add(connection, tagged, new[] { "billing" });

                    Assert.Equal(new[] { "billing", "contoso" }, JobTags.GetTags(connection, tagged));
                    Assert.Equal(new[] { tagged }, JobTags.GetJobIds(connection, "billing", 0, 10));
                    Assert.Equal(new[] { tagged }, JobTags.GetJobIds(connection, "contoso", 0, 10));

                    var batch = BatchJob.Get(connection, batchId);
                    Assert.Equal(1, batch.Total);
                    Assert.Equal(new[] { continuation }, batch.Continuations);
                    Assert.Equal(new[] { tagged }, BatchJob.GetJobIds(connection, batchId));
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
            using (var connection = ConnectionUtils.CreateConnection(microsoftDataSqlClient: true))
            {
                return connection.ExecuteScalar<int>("select count(*) from [HangFire].[Job] where StateName = @state", new { state });
            }
        }
    }

    /// <summary>
    /// A storage whose next commit goes through but then throws, as when the reply of the database is lost.
    /// </summary>
    internal sealed class LostCommitReplyStorage : JobStorage
    {
        private static readonly ProxyGenerator Generator = new ProxyGenerator();
        private readonly JobStorage _inner;
        private int _commitsToFail = 1;

        public LostCommitReplyStorage(JobStorage inner)
        {
            _inner = inner;
        }

        public override bool LinearizableReads => _inner.LinearizableReads;

        public override bool HasFeature(string featureId) => false;

        public override IMonitoringApi GetMonitoringApi() => _inner.GetMonitoringApi();

        public override IEnumerable<IStateHandler> GetStateHandlers() => _inner.GetStateHandlers();

        public override IStorageConnection GetConnection()
        {
            return Generator.CreateInterfaceProxyWithTarget(_inner.GetConnection(), new ConnectionInterceptor(this));
        }

        private bool ShouldFailCommit() => Interlocked.Exchange(ref _commitsToFail, 0) == 1;

        private sealed class ConnectionInterceptor : IInterceptor
        {
            private readonly LostCommitReplyStorage _storage;

            public ConnectionInterceptor(LostCommitReplyStorage storage) => _storage = storage;

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

        private sealed class TransactionInterceptor : IInterceptor
        {
            private readonly LostCommitReplyStorage _storage;

            public TransactionInterceptor(LostCommitReplyStorage storage) => _storage = storage;

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
    internal sealed class KnownTypesResolver : IJsonTypeInfoResolver
    {
        private readonly DefaultJsonTypeInfoResolver _inner = new DefaultJsonTypeInfoResolver();
        private readonly HashSet<Type> _types;

        public KnownTypesResolver(params Type[] types)
        {
            _types = new HashSet<Type>(types);
        }

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
}
