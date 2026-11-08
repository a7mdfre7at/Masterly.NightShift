using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using Masterly.NightShift.Common;
using Masterly.NightShift.Server;
using Masterly.NightShift.States;
using Moq;
using Xunit;

namespace Masterly.NightShift.SqlServer.Tests
{
    /// <summary>
    /// End-to-end checks of <see cref="DrainMode"/> through real storage, where hash fields are limited to
    /// 100 characters and held jobs go through the delayed job scheduler.
    /// </summary>
    public class DrainModeFacts
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

        // A Kubernetes pod name can be 63 characters long, so the server ID doesn't fit in a 100-character field
        private const string LongServerId = "orders-api-worker-deployment-7d9f8b6c5d-x2k4p-production-west-europe:1:0f8fad5b-d9cb-469f-a165-70867728950e";

        [Fact, CleanDatabase]
        public void PauseServer_WithALongServerId_IsReadBackWithTheFullId_AndResumeServerClearsIt()
        {
            var storage = CreateStorage();

            try
            {
                using (var connection = storage.GetConnection())
                {
                    DrainMode.PauseServer(connection, LongServerId, "draining the node");

                    var status = DrainMode.Read(connection);
                    Assert.Equal(new[] { LongServerId }, status.PausedServers.Keys);
                    Assert.Equal("draining the node", status.PausedServers[LongServerId]);

                    DrainMode.ResumeServer(connection, LongServerId);

                    Assert.Same(DrainModeStatus.Inactive, DrainMode.Read(connection));
                }
            }
            finally
            {
                DrainMode.InvalidateCache();
            }
        }

        [Fact, CleanDatabase]
        public void PausedQueue_HoldsEveryJobOfTheQueue_AndPutsThemBackInIt_WhenResumed()
        {
            var storage = CreateStorage();
            var holdInterval = DrainMode.HoldInterval;
            QueueJobs.Reset();

            try
            {
                DrainMode.HoldInterval = TimeSpan.FromSeconds(1);

                using (var connection = storage.GetConnection())
                {
                    DrainMode.PauseQueue(connection, "emails", "SMTP outage");
                }

                var client = new BackgroundJobClient(storage);

                // A queue of the job's own, and a queue given only by the Enqueued state
                var ownQueue = client.Enqueue("emails", () => QueueJobs.Run("own"));
                var stateQueue = client.Create(() => QueueJobs.Run("state"), new EnqueuedState("emails"));

                // The server listens to "emails" only: a job sent back to "default" would never run
                using (new BackgroundJobServer(new BackgroundJobServerOptions { WorkerCount = 2, Queues = new[] { "emails" }, SchedulePollingInterval = TimeSpan.FromMilliseconds(500) }, storage))
                {
                    Assert.True(
                        SpinWait.SpinUntil(() => WasHeld(storage, ownQueue) && WasHeld(storage, stateQueue), Timeout),
                        "The jobs weren't held in time.");
                    Assert.Empty(QueueJobs.Runs);

                    using (var connection = storage.GetConnection())
                    {
                        DrainMode.ResumeQueue(connection, "emails");
                    }

                    Assert.True(
                        SpinWait.SpinUntil(() => QueueJobs.Runs.Count == 2, Timeout),
                        "The held jobs didn't run in their queue after the queue was resumed.");
                }

                Assert.Equal(new[] { "own", "state" }, QueueJobs.Runs.Keys.OrderBy(x => x, StringComparer.Ordinal));
            }
            finally
            {
                DrainMode.HoldInterval = holdInterval;
                DrainMode.InvalidateCache();
            }
        }

        [Fact, CleanDatabase]
        public void HeldJob_GoesBackToItsStateQueueOnce_AndALaterRetryGoesWhereItWouldWithoutTheHold()
        {
            var storage = CreateStorage();
            var stateChanger = new BackgroundJobStateChanger();

            try
            {
                var client = new BackgroundJobClient(storage);
                var jobId = client.Create(() => QueueJobs.Run("state"), new EnqueuedState("emails"));

                using (var connection = storage.GetConnection())
                {
                    DrainMode.PauseQueue(connection, "emails");

                    // A worker picks the job up while its queue is paused: it is held instead
                    stateChanger.ChangeState(new StateChangeContext(storage, connection, jobId, CreateProcessingState(), EnqueuedState.StateName));
                    Assert.Equal(ScheduledState.StateName, connection.GetStateData(jobId).Name);
                    Assert.Equal("\"emails\"", connection.GetJobParameter(jobId, DrainModeFilter.HeldQueueParameter));

                    DrainMode.ResumeQueue(connection, "emails");

                    // The delayed job scheduler enqueues it with a new state for the default queue
                    stateChanger.ChangeState(new StateChangeContext(storage, connection, jobId, new EnqueuedState(), ScheduledState.StateName));
                    Assert.Equal("emails", connection.GetStateData(jobId).Data["Queue"]);
                    Assert.Null(connection.GetJobParameter(jobId, DrainModeFilter.HeldQueueParameter));

                    // A retry of a job whose queue came only from its state goes to the default queue, held or not
                    stateChanger.ChangeState(new StateChangeContext(storage, connection, jobId, new ScheduledState(TimeSpan.Zero) { Reason = "Retry attempt 1 of 10" }, EnqueuedState.StateName));
                    stateChanger.ChangeState(new StateChangeContext(storage, connection, jobId, new EnqueuedState(), ScheduledState.StateName));
                    Assert.Equal(EnqueuedState.DefaultQueue, connection.GetStateData(jobId).Data["Queue"]);
                }
            }
            finally
            {
                DrainMode.InvalidateCache();
            }
        }

        [Fact, CleanDatabase]
        public void ThrottleHold_OfAJobWhoseQueueCameFromItsState_PutsItBackInThatQueue()
        {
            var storage = CreateStorage();
            var stateChanger = new BackgroundJobStateChanger();
            var client = new BackgroundJobClient(storage);
            var jobId = client.Create(() => QueueJobs.Throttled(), new EnqueuedState("emails"));
            var otherJobId = client.Create(() => QueueJobs.Run("other"), new EnqueuedState());

            using (var connection = storage.GetConnection())
            {
                // Another job, which is running, holds the only slot
                stateChanger.ChangeState(new StateChangeContext(storage, connection, otherJobId, CreateProcessingState(), EnqueuedState.StateName));

                using (var transaction = connection.CreateWriteTransaction())
                {
                    var now = JobHelper.ToTimestamp(DateTime.UtcNow);
                    transaction.AddToSet("throttle:held-queue-facts", $"{now}:{otherJobId}", now);
                    transaction.Commit();
                }

                // What a worker does: move the job to Processing, then perform it with the same background job
                var processing = new StateChangeContext(storage, connection, jobId, CreateProcessingState(), EnqueuedState.StateName);
                stateChanger.ChangeState(processing);

                var token = new Mock<IJobCancellationToken>();
                token.Setup(x => x.ShutdownToken).Returns(CancellationToken.None);
                var performing = new PerformingContext(new PerformContext(storage, connection, processing.ProcessedJob, token.Object));

                new ThrottleAttribute("held-queue-facts").OnPerforming(performing);

                Assert.True(performing.Canceled);
                Assert.Equal(ScheduledState.StateName, connection.GetStateData(jobId).Name);
                Assert.Equal("\"emails\"", connection.GetJobParameter(jobId, DrainModeFilter.HeldQueueParameter));

                stateChanger.ChangeState(new StateChangeContext(storage, connection, jobId, new EnqueuedState(), ScheduledState.StateName));

                Assert.Equal("emails", connection.GetStateData(jobId).Data["Queue"]);
            }
        }

        [Fact, CleanDatabase]
        public void JobsRequeuedWhileRunning_AreHeldByTheirPausedQueue_AndGoBackToIt()
        {
            // A shutdown interrupted both jobs: they come back from the queue still in the Processing state, which
            // names no queue, so the queue is read from the Enqueued state in their history
            var storage = CreateStorage();
            var stateChanger = new BackgroundJobStateChanger();
            var client = new BackgroundJobClient(storage);
            var stateQueue = client.Create(() => QueueJobs.Run("state"), new EnqueuedState("emails"));
            var attributeQueue = client.Create(() => QueueJobs.QueuedByAttribute(), new EnqueuedState());

            try
            {
                using (var connection = storage.GetConnection())
                {
                    foreach (var jobId in new[] { stateQueue, attributeQueue })
                    {
                        stateChanger.ChangeState(new StateChangeContext(storage, connection, jobId, CreateProcessingState(), EnqueuedState.StateName));
                    }

                    DrainMode.PauseQueue(connection, "emails");

                    foreach (var jobId in new[] { stateQueue, attributeQueue })
                    {
                        // Another worker fetches the requeued job
                        stateChanger.ChangeState(new StateChangeContext(storage, connection, jobId, CreateProcessingState(), ProcessingState.StateName));

                        var state = connection.GetStateData(jobId);
                        Assert.Equal(ScheduledState.StateName, state.Name);
                        Assert.Equal("Held: queue 'emails' is paused", state.Reason);
                        Assert.Equal("\"emails\"", connection.GetJobParameter(jobId, DrainModeFilter.HeldQueueParameter));
                    }

                    DrainMode.ResumeQueue(connection, "emails");

                    foreach (var jobId in new[] { stateQueue, attributeQueue })
                    {
                        stateChanger.ChangeState(new StateChangeContext(storage, connection, jobId, new EnqueuedState(), ScheduledState.StateName));
                        Assert.Equal("emails", connection.GetStateData(jobId).Data["Queue"]);
                    }
                }
            }
            finally
            {
                DrainMode.InvalidateCache();
            }
        }

        // The constructor is internal: workers create the state
        private static ProcessingState CreateProcessingState()
        {
            var ctor = typeof(ProcessingState).GetConstructors(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).Single();
            return (ProcessingState)ctor.Invoke(new object[] { "server", "worker" });
        }

        private static bool WasHeld(JobStorage storage, string jobId)
        {
            var details = storage.GetMonitoringApi().JobDetails(jobId);
            return details.History.Any(x => x.Reason == "Held: queue 'emails' is paused");
        }

        private static SqlServerStorage CreateStorage()
        {
            return new SqlServerStorage(
                ConnectionUtils.GetConnectionString(),
                new SqlServerStorageOptions { PrepareSchemaIfNecessary = false, QueuePollInterval = TimeSpan.FromMilliseconds(200) });
        }

        public static class QueueJobs
        {
            public static ConcurrentDictionary<string, DateTime> Runs { get; } = new ConcurrentDictionary<string, DateTime>();

            public static void Reset() => Runs.Clear();

            public static void Run(string name) => Runs[name] = DateTime.UtcNow;

            [Throttle("held-queue-facts")]
            public static void Throttled()
            {
            }

            [Queue("emails")]
            public static void QueuedByAttribute()
            {
            }
        }
    }
}
