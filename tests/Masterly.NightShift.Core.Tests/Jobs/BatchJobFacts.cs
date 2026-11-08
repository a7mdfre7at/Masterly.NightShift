using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading.Tasks;
using Masterly.NightShift.Common;
using Masterly.NightShift.States;
using Masterly.NightShift.Storage;
using Moq;
using Xunit;

namespace Masterly.NightShift.Core.Tests
{
    public class BatchJobFacts : IDisposable
    {
        private readonly DateTime _now = new DateTime(2026, 11, 5, 15, 0, 0, DateTimeKind.Utc);
        private readonly Mock<JobStorage> _storage = new Mock<JobStorage>();
        private readonly Mock<JobStorageConnection> _connection = new Mock<JobStorageConnection>();
        private readonly Mock<JobStorageTransaction> _transaction = new Mock<JobStorageTransaction>();
        private readonly Mock<IBackgroundJobClient> _client = new Mock<IBackgroundJobClient>();
        private readonly Dictionary<string, Dictionary<string, string>> _hashes = new Dictionary<string, Dictionary<string, string>>();
        private readonly Dictionary<string, List<string>> _sets = new Dictionary<string, List<string>>();
        private readonly Dictionary<string, long> _counters = new Dictionary<string, long>();
        private readonly List<(Job Job, IState State)> _created = new List<(Job, IState)>();

        public BatchJobFacts()
        {
            BatchJob.Clock = () => _now;

            _storage.Setup(x => x.HasFeature(JobStorageFeatures.ExtendedApi)).Returns(true);
            _storage.Setup(x => x.GetConnection()).Returns(_connection.Object);
            _storage.Setup(x => x.GetReadOnlyConnection()).Returns(_connection.Object);
            _connection.Setup(x => x.CreateWriteTransaction()).Returns(_transaction.Object);
            _connection.Setup(x => x.GetAllEntriesFromHash(It.IsAny<string>())).Returns<string>(key => _hashes.TryGetValue(key, out var hash) ? hash : null);
            _connection.Setup(x => x.GetAllItemsFromSet(It.IsAny<string>())).Returns<string>(key => new HashSet<string>(_sets.TryGetValue(key, out var set) ? set : new List<string>()));
            _connection.Setup(x => x.GetRangeFromSet(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>())).Returns<string, int, int>((key, from, to) => (_sets.TryGetValue(key, out var set) ? set : new List<string>()).Skip(from).Take(to - from + 1).ToList());
            _connection.Setup(x => x.GetSetCount(It.IsAny<string>())).Returns<string>(key => _sets.TryGetValue(key, out var set) ? set.Count : 0);
            _connection.Setup(x => x.GetCounter(It.IsAny<string>())).Returns<string>(key => _counters.TryGetValue(key, out var value) ? value : 0);

            _transaction.Setup(x => x.SetRangeInHash(It.IsAny<string>(), It.IsAny<IEnumerable<KeyValuePair<string, string>>>()))
                .Callback<string, IEnumerable<KeyValuePair<string, string>>>((key, pairs) =>
                {
                    if (!_hashes.TryGetValue(key, out var hash)) _hashes[key] = hash = new Dictionary<string, string>();
                    foreach (var pair in pairs) hash[pair.Key] = pair.Value;
                });
            _transaction.Setup(x => x.AddToSet(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<double>())).Callback<string, string, double>((key, value, _) => Add(key, value));
            _transaction.Setup(x => x.AddToSet(It.IsAny<string>(), It.IsAny<string>())).Callback<string, string>(Add);
            _transaction.Setup(x => x.RemoveFromSet(It.IsAny<string>(), It.IsAny<string>())).Callback<string, string>((key, value) => { if (_sets.TryGetValue(key, out var set)) set.Remove(value); });
            _transaction.Setup(x => x.IncrementCounter(It.IsAny<string>(), It.IsAny<TimeSpan>())).Callback<string, TimeSpan>((key, _) => _counters[key] = (_counters.TryGetValue(key, out var v) ? v : 0) + 1);
            _transaction.Setup(x => x.DecrementCounter(It.IsAny<string>(), It.IsAny<TimeSpan>())).Callback<string, TimeSpan>((key, _) => _counters[key] = (_counters.TryGetValue(key, out var v) ? v : 0) - 1);

            var nextId = 100;
            _client.Setup(x => x.Create(It.IsAny<Job>(), It.IsAny<IState>())).Returns<Job, IState>((job, state) =>
            {
                _created.Add((job, state));
                var id = (nextId++).ToString();
                // What BatchSupportAttribute does on creation, without a real pipeline
                var filter = new BatchSupportAttribute();
                var applyContext = new ApplyStateContextMock { NewStateObject = state };
                applyContext.BackgroundJob.Id = id;
                applyContext.Connection = new Mock<IStorageConnection>();
                filter.OnStateApplied(applyContext.Object, _transaction.Object);
                return id;
            });
        }

        public void Dispose()
        {
            BatchJob.Clock = () => DateTime.UtcNow;
        }

        private void Add(string key, string value)
        {
            if (!_sets.TryGetValue(key, out var set)) _sets[key] = set = new List<string>();
            if (!set.Contains(value)) set.Add(value);
        }

        [SuppressMessage("Usage", "xUnit1013:Public method should be marked as test")]
        public static void Export(string region) { }

        [SuppressMessage("Usage", "xUnit1013:Public method should be marked as test")]
        public static void Merge() { }

        [SuppressMessage("Usage", "xUnit1013:Public method should be marked as test")]
        public static Task MergeAsync() => Task.CompletedTask;

        [SuppressMessage("Usage", "xUnit1013:Public method should be marked as test")]
        [SuppressMessage("Performance", "CA1822:Mark members as static")]
        public void Instance(string region) { }

        [SuppressMessage("Usage", "xUnit1013:Public method should be marked as test")]
        [SuppressMessage("Performance", "CA1822:Mark members as static")]
        public Task InstanceAsync(string region) => Task.CompletedTask;

        private void SetupRealClientStorage()
        {
            _connection
                .Setup(x => x.CreateExpiredJob(It.IsAny<Job>(), It.IsAny<IDictionary<string, string>>(), It.IsAny<DateTime>(), It.IsAny<TimeSpan>()))
                .Returns("900");
            _connection.Setup(x => x.GetJobData(It.IsAny<string>())).Returns(new JobData { CreatedAt = _now });
            JobStorage.Current = _storage.Object;
        }

        [Fact, GlobalLock(Reason = "Uses JobStorage.Current instance")]
        public void StartNew_WithoutClient_CreatesTheBatch_InTheCurrentStorage_AndCountsItsJobs()
        {
            SetupRealClientStorage();
            string jobId = null;

            var batchId = BatchJob.StartNew(batch => jobId = batch.Enqueue(() => Export("eu")), "Through the current storage");

            Assert.Equal("900", jobId);
            Assert.Equal("Through the current storage", _hashes[BatchJob.GetHashKey(batchId)]["Description"]);
            Assert.Contains(batchId, _sets[BatchJob.IndexKey]);
            Assert.Equal(new[] { "900" }, _sets[BatchJob.GetJobsKey(batchId)]);
            Assert.Equal(1, _counters[BatchJob.GetTotalKey(batchId)]);
            Assert.Equal(1, _counters[BatchJob.GetStateKey(batchId, EnqueuedState.StateName)]);
        }

        [Fact, GlobalLock(Reason = "Uses JobStorage.Current instance")]
        public void ContinueWith_Expression_CreatesTheContinuation_InTheCurrentStorage()
        {
            SetupRealClientStorage();
            _hashes[BatchJob.GetHashKey("b1")] = new Dictionary<string, string>();
            Add(BatchJob.GetJobsKey("b1"), "1");

            var id = BatchJob.ContinueWith("b1", () => Merge());

            Assert.Equal("900", id);
            Assert.Equal(new[] { "900" }, _sets[BatchJob.GetContinuationsKey("b1")]);
            _connection.Verify(x => x.CreateExpiredJob(
                It.Is<Job>(job => job.Method.Name == nameof(Merge)),
                It.IsAny<IDictionary<string, string>>(),
                It.IsAny<DateTime>(),
                It.IsAny<TimeSpan>()));
        }

        [Fact, GlobalLock(Reason = "Uses JobStorage.Current instance")]
        public void ContinueWith_GenericExpression_CreatesTheContinuation_InTheCurrentStorage()
        {
            SetupRealClientStorage();
            _hashes[BatchJob.GetHashKey("b1")] = new Dictionary<string, string>();
            Add(BatchJob.GetJobsKey("b1"), "1");

            var id = BatchJob.ContinueWith<BatchJobFacts>("b1", x => x.Instance("eu"), JobContinuationOptions.OnlyOnSucceededState);

            Assert.Equal("900", id);
            _connection.Verify(x => x.CreateExpiredJob(
                It.Is<Job>(job => job.Type == typeof(BatchJobFacts) && job.Method.Name == nameof(Instance)),
                It.IsAny<IDictionary<string, string>>(),
                It.IsAny<DateTime>(),
                It.IsAny<TimeSpan>()));
        }

        [Fact, GlobalLock(Reason = "Uses JobStorage.Current instance")]
        public void ContinueWith_AsyncExpression_CreatesTheContinuation_InTheCurrentStorage()
        {
            SetupRealClientStorage();
            _hashes[BatchJob.GetHashKey("b1")] = new Dictionary<string, string>();
            Add(BatchJob.GetJobsKey("b1"), "1");

            var id = BatchJob.ContinueWith("b1", () => MergeAsync());

            Assert.Equal("900", id);
            _connection.Verify(x => x.CreateExpiredJob(
                It.Is<Job>(job => job.Method.Name == nameof(MergeAsync)),
                It.IsAny<IDictionary<string, string>>(),
                It.IsAny<DateTime>(),
                It.IsAny<TimeSpan>()));
        }

        [Fact, GlobalLock(Reason = "Uses JobStorage.Current instance")]
        public void ContinueWith_Job_Throws_WhenTheBatchDoesNotExist_InTheCurrentStorage()
        {
            SetupRealClientStorage();

            var exception = Assert.Throws<InvalidOperationException>(() => BatchJob.ContinueWith("nope", Job.FromExpression(() => Merge())));

            Assert.Contains("'nope'", exception.Message);
            _connection.Verify(x => x.CreateExpiredJob(It.IsAny<Job>(), It.IsAny<IDictionary<string, string>>(), It.IsAny<DateTime>(), It.IsAny<TimeSpan>()), Times.Never);
        }

        [Fact]
        public void StartNew_ThrowsAnException_WhenArgumentsAreNull()
        {
            Assert.Equal("client", Assert.Throws<ArgumentNullException>(() => BatchJob.StartNew(null, _storage.Object, _ => { })).ParamName);
            Assert.Equal("storage", Assert.Throws<ArgumentNullException>(() => BatchJob.StartNew(_client.Object, null, _ => { })).ParamName);
            Assert.Equal("createJobs", Assert.Throws<ArgumentNullException>(() => BatchJob.StartNew(_client.Object, _storage.Object, null)).ParamName);
        }

        [Fact]
        public void ContinueWith_ThrowsAnException_WhenArgumentsAreNull()
        {
            var job = Job.FromExpression(() => Merge());

            Assert.Equal("client", Assert.Throws<ArgumentNullException>(() => BatchJob.ContinueWith(null, _storage.Object, "b1", job)).ParamName);
            Assert.Equal("storage", Assert.Throws<ArgumentNullException>(() => BatchJob.ContinueWith(_client.Object, null, "b1", job)).ParamName);
            Assert.Equal("batchId", Assert.Throws<ArgumentNullException>(() => BatchJob.ContinueWith(_client.Object, _storage.Object, null, job)).ParamName);
            Assert.Equal("job", Assert.Throws<ArgumentNullException>(() => BatchJob.ContinueWith(_client.Object, _storage.Object, "b1", null)).ParamName);
        }

        [Fact]
        public void ContinueWith_ReturnsNull_WhenCreationIsCanceled()
        {
            _hashes[BatchJob.GetHashKey("b1")] = new Dictionary<string, string>();
            Add(BatchJob.GetJobsKey("b1"), "1");
            _client.Setup(x => x.Create(It.IsAny<Job>(), It.IsAny<AwaitingJobsState>())).Returns((string)null);

            var id = BatchJob.ContinueWith(_client.Object, _storage.Object, "b1", Job.FromExpression(() => Merge()));

            Assert.Null(id);
            Assert.Null(BatchJob.CurrentContinuation);
        }

        [Fact]
        public void Builder_CreatesEveryKindOfJob_ThroughTheClient()
        {
            var state = new Mock<IState>();
            var ids = new List<string>();
            var batchIds = new List<string>();

            BatchJob.StartNew(_client.Object, _storage.Object, batch =>
            {
                batchIds.Add(batch.BatchId);
                ids.Add(batch.Create(Job.FromExpression(() => Merge()), state.Object));
                ids.Add(batch.Enqueue<BatchJobFacts>(x => x.Instance("eu")));
                ids.Add(batch.Enqueue(() => MergeAsync()));
                ids.Add(batch.Enqueue<BatchJobFacts>(x => x.InstanceAsync("us")));
                ids.Add(batch.Schedule<BatchJobFacts>(x => x.Instance("apac"), TimeSpan.FromMinutes(5)));
            });

            Assert.Equal(new[] { "100", "101", "102", "103", "104" }, ids);
            Assert.Equal(12, batchIds.Single().Length);
            Assert.Same(state.Object, _created[0].State);
            Assert.Equal(nameof(Merge), _created[0].Job.Method.Name);
            Assert.IsType<EnqueuedState>(_created[1].State);
            Assert.Equal(nameof(Instance), _created[1].Job.Method.Name);
            Assert.IsType<EnqueuedState>(_created[2].State);
            Assert.Equal(nameof(MergeAsync), _created[2].Job.Method.Name);
            Assert.IsType<EnqueuedState>(_created[3].State);
            Assert.Equal(nameof(InstanceAsync), _created[3].Job.Method.Name);
            Assert.True(Assert.IsType<ScheduledState>(_created[4].State).EnqueueAt > DateTime.UtcNow.AddMinutes(4));
            Assert.Equal(nameof(Instance), _created[4].Job.Method.Name);
        }

        [Fact]
        public void Builder_ReturnsNull_WhenTheClientCancelsCreation()
        {
            _client.Setup(x => x.Create(It.IsAny<Job>(), It.IsAny<IState>())).Returns((string)null);
            string id = "not-set";

            BatchJob.StartNew(_client.Object, _storage.Object, batch => id = batch.Enqueue(() => Export("eu")));

            Assert.Null(id);
        }

        [Fact]
        public void Get_ReportsProcessingAndDeletedCounts()
        {
            _hashes[BatchJob.GetHashKey("b1")] = new Dictionary<string, string>();
            _counters[BatchJob.GetTotalKey("b1")] = 3;
            _counters[BatchJob.GetStateKey("b1", ProcessingState.StateName)] = 1;
            _counters[BatchJob.GetStateKey("b1", DeletedState.StateName)] = 2;

            var batch = BatchJob.Get(_connection.Object, "b1");

            Assert.Equal(1, batch.Processing);
            Assert.Equal(2, batch.Deleted);
            Assert.Equal(1, batch.Pending);
            Assert.Null(batch.Description);
            Assert.Null(batch.CreatedAt);
        }

        [Fact]
        public void Get_OnALegacyConnection_CountsTheJobsSet_AndReadsTheContinuations()
        {
            var connection = new Mock<IStorageConnection>();
            connection.Setup(x => x.GetAllEntriesFromHash(BatchJob.GetHashKey("b1"))).Returns(new Dictionary<string, string> { { "Description", "Legacy" } });
            connection.Setup(x => x.GetAllItemsFromSet(BatchJob.GetJobsKey("b1"))).Returns(new HashSet<string> { "1", "2", "3" });
            connection.Setup(x => x.GetAllItemsFromSet(BatchJob.GetContinuationsKey("b1"))).Returns(new HashSet<string> { "9", "10" });

            var batch = BatchJob.Get(connection.Object, "b1");

            Assert.Equal("Legacy", batch.Description);
            Assert.Equal(3, batch.Total);
            Assert.Empty(batch.Counts);
            Assert.Equal(new[] { "10", "9" }, batch.Continuations);
        }

        [Fact]
        public void Get_OnALegacyConnection_ToleratesMissingSets()
        {
            var connection = new Mock<IStorageConnection>();
            connection.Setup(x => x.GetAllEntriesFromHash(BatchJob.GetHashKey("b1"))).Returns(new Dictionary<string, string>());
            connection.Setup(x => x.GetAllItemsFromSet(It.IsAny<string>())).Returns((HashSet<string>)null);

            var batch = BatchJob.Get(connection.Object, "b1");

            Assert.Equal(0, batch.Total);
            Assert.Empty(batch.Continuations);
            Assert.Equal(0, batch.Progress);
            Assert.False(batch.IsFinished);
        }

        [Fact]
        public void Get_ThrowsAnException_WhenArgumentsAreNull()
        {
            Assert.Equal("connection", Assert.Throws<ArgumentNullException>(() => BatchJob.Get(null, "b1")).ParamName);
            Assert.Equal("batchId", Assert.Throws<ArgumentNullException>(() => BatchJob.Get(_connection.Object, null)).ParamName);
        }

        [Fact]
        public void GetJobIds_ReadsTheJobsSet_InCreationOrder()
        {
            Add(BatchJob.GetJobsKey("b1"), "3");
            Add(BatchJob.GetJobsKey("b1"), "1");

            var ids = BatchJob.GetJobIds(_connection.Object, "b1");

            Assert.Equal(new[] { "3", "1" }, ids);
        }

        [Fact]
        public void GetJobIds_OnALegacyConnection_ReadsTheWholeSet()
        {
            var connection = new Mock<IStorageConnection>();
            connection.Setup(x => x.GetAllItemsFromSet(BatchJob.GetJobsKey("b1"))).Returns(new HashSet<string> { "1", "2" });

            var ids = BatchJob.GetJobIds(connection.Object, "b1");

            Assert.Equal(new[] { "1", "2" }, ids.OrderBy(x => x, StringComparer.Ordinal));
        }

        [Fact]
        public void GetJobIds_OnALegacyConnection_ReturnsNothing_WhenTheSetIsMissing()
        {
            var connection = new Mock<IStorageConnection>();
            connection.Setup(x => x.GetAllItemsFromSet(BatchJob.GetJobsKey("b1"))).Returns((HashSet<string>)null);

            Assert.Empty(BatchJob.GetJobIds(connection.Object, "b1"));
        }

        [Fact]
        public void GetJobIds_ThrowsAnException_WhenArgumentsAreNull()
        {
            Assert.Equal("connection", Assert.Throws<ArgumentNullException>(() => BatchJob.GetJobIds(null, "b1")).ParamName);
            Assert.Equal("batchId", Assert.Throws<ArgumentNullException>(() => BatchJob.GetJobIds(_connection.Object, null)).ParamName);
        }

        [Fact]
        public void Count_UsesTheSetCount_OnAStorageConnection()
        {
            Add(BatchJob.IndexKey, "a");
            Add(BatchJob.IndexKey, "b");

            Assert.Equal(2, BatchJob.Count(_connection.Object));
            _connection.Verify(x => x.GetSetCount(BatchJob.IndexKey));
        }

        [Fact]
        public void Count_OnALegacyConnection_CountsTheIndexMembers()
        {
            var connection = new Mock<IStorageConnection>();
            connection.Setup(x => x.GetAllItemsFromSet(BatchJob.IndexKey)).Returns(new HashSet<string> { "a", "b", "c" });

            Assert.Equal(3, BatchJob.Count(connection.Object));
        }

        [Fact]
        public void Count_OnALegacyConnection_ReturnsZero_WhenTheIndexIsMissing()
        {
            var connection = new Mock<IStorageConnection>();
            connection.Setup(x => x.GetAllItemsFromSet(BatchJob.IndexKey)).Returns((HashSet<string>)null);

            Assert.Equal(0, BatchJob.Count(connection.Object));
        }

        [Fact]
        public void Count_ThrowsAnException_WhenConnectionIsNull()
        {
            var exception = Assert.Throws<ArgumentNullException>(() => BatchJob.Count(null));

            Assert.Equal("connection", exception.ParamName);
        }

        [Fact]
        public void List_OnALegacyConnection_IsNewestFirst_AndPages()
        {
            var connection = new Mock<IStorageConnection>();
            connection.Setup(x => x.GetAllItemsFromSet(BatchJob.IndexKey)).Returns(new HashSet<string> { "a", "b", "c" });
            connection.Setup(x => x.GetAllEntriesFromHash(It.IsAny<string>())).Returns(new Dictionary<string, string>());

            var batches = BatchJob.List(connection.Object, 1, 2);

            Assert.Equal(new[] { "b", "a" }, batches.Select(x => x.Id));
            connection.Verify(x => x.CreateWriteTransaction(), Times.Never);
        }

        [Fact]
        public void List_OnALegacyConnection_ReturnsNothing_WhenTheIndexIsMissing()
        {
            var connection = new Mock<IStorageConnection>();
            connection.Setup(x => x.GetAllItemsFromSet(BatchJob.IndexKey)).Returns((HashSet<string>)null);

            Assert.Empty(BatchJob.List(connection.Object, 0, 10));
        }

        [Fact]
        public void List_ReturnsNothing_PastTheEnd()
        {
            Add(BatchJob.IndexKey, "a");

            Assert.Empty(BatchJob.List(_connection.Object, 1, 10));
            _connection.Verify(x => x.GetRangeFromSet(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>()), Times.Never);
        }

        [Fact]
        public void List_ValidatesItsArguments()
        {
            Assert.Throws<ArgumentNullException>(() => BatchJob.List(null, 0, 1));
            Assert.Throws<ArgumentOutOfRangeException>(() => BatchJob.List(_connection.Object, -1, 1));
            Assert.Throws<ArgumentOutOfRangeException>(() => BatchJob.List(_connection.Object, 0, 0));
        }

        [Fact]
        public void BatchSupport_OnStateApplied_IgnoresStatesThatAreNotTracked()
        {
            var filter = new BatchSupportAttribute();
            var snapshot = new Dictionary<string, string> { { BatchJob.ParameterName, SerializationHelper.Serialize("b1", SerializationOption.User) } };
            var context = new ApplyStateContextMock { OldStateName = "Custom" };
            context.NewState.Setup(x => x.Name).Returns("AnotherCustom");
            context.BackgroundJob.Id = "7";
            context.BackgroundJob.ParametersSnapshot = snapshot;

            filter.OnStateApplied(context.Object, _transaction.Object);

            _transaction.Verify(x => x.IncrementCounter(It.IsAny<string>(), It.IsAny<TimeSpan>()), Times.Never);
            _transaction.Verify(x => x.DecrementCounter(It.IsAny<string>(), It.IsAny<TimeSpan>()), Times.Never);
            _transaction.Verify(x => x.AddToSet(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<double>()), Times.Never);
        }

        [Fact]
        public void BatchSupport_OnStateApplied_ReadsTheBatchFromStorage_WhenThereIsNoSnapshot()
        {
            var filter = new BatchSupportAttribute();
            var context = new ApplyStateContextMock { NewStateObject = new EnqueuedState() };
            context.BackgroundJob.Id = "7";
            context.Connection.Setup(x => x.GetJobParameter("7", BatchJob.ParameterName)).Returns(SerializationHelper.Serialize("b1", SerializationOption.User));

            filter.OnStateApplied(context.Object, _transaction.Object);

            Assert.Equal(new[] { "7" }, _sets[BatchJob.GetJobsKey("b1")]);
            Assert.Equal(1, _counters[BatchJob.GetTotalKey("b1")]);
            Assert.Equal(1, _counters[BatchJob.GetStateKey("b1", EnqueuedState.StateName)]);
        }

        [Fact]
        public void BatchSupport_OnStateApplied_IgnoresTheJob_WhenItsParameterCanNotBeRead()
        {
            var filter = new BatchSupportAttribute();
            var context = new ApplyStateContextMock { NewStateObject = new EnqueuedState() };
            context.BackgroundJob.Id = "7";
            context.Connection.Setup(x => x.GetJobParameter("7", BatchJob.ParameterName)).Throws(new InvalidOperationException("storage is down"));

            filter.OnStateApplied(context.Object, _transaction.Object);

            Assert.Empty(_sets);
            Assert.Empty(_counters);
        }

        [Fact]
        public void BatchSupport_OnStateApplied_ThrowsAnException_WhenArgumentsAreNull()
        {
            var filter = new BatchSupportAttribute();
            var context = new ApplyStateContextMock { NewStateObject = new EnqueuedState() };

            Assert.Equal("context", Assert.Throws<ArgumentNullException>(() => filter.OnStateApplied(null, _transaction.Object)).ParamName);
            Assert.Equal("transaction", Assert.Throws<ArgumentNullException>(() => filter.OnStateApplied(context.Object, null)).ParamName);
        }

        [Fact]
        public void BatchSupport_OnStateUnapplied_ChangesNothing()
        {
            var filter = new BatchSupportAttribute();
            var context = new ApplyStateContextMock { NewStateObject = new EnqueuedState(), OldStateName = ProcessingState.StateName };
            context.BackgroundJob.ParametersSnapshot = new Dictionary<string, string> { { BatchJob.ParameterName, SerializationHelper.Serialize("b1", SerializationOption.User) } };

            filter.OnStateUnapplied(context.Object, _transaction.Object);

            Assert.Empty(_sets);
            Assert.Empty(_counters);
            _transaction.Verify(x => x.Commit(), Times.Never);
        }

        [Fact]
        public void BatchSupport_OnCreated_WritesNothing_EvenForAContinuation()
        {
            // A write after the job exists could fail and make the caller create the job again
            var filter = new BatchSupportAttribute();
            var context = new CreateContextMock();
            context.Connection.Setup(x => x.CreateWriteTransaction()).Returns(_transaction.Object);
            _hashes[BatchJob.GetHashKey("b1")] = new Dictionary<string, string>();
            Add(BatchJob.GetJobsKey("b1"), "1");
            _client.Setup(x => x.Create(It.IsAny<Job>(), It.IsAny<AwaitingJobsState>())).Returns<Job, IState>((_, __) =>
            {
                filter.OnCreated(context.GetCreatedContext("500"));
                return "500";
            });

            filter.OnCreated(context.GetCreatedContext("501"));
            BatchJob.ContinueWith(_client.Object, _storage.Object, "b1", Job.FromExpression(() => Merge()));

            context.Connection.Verify(x => x.CreateWriteTransaction(), Times.Never);
            Assert.False(_sets.ContainsKey(BatchJob.GetContinuationsKey("b1")));
        }

        [Fact]
        public void BatchSupport_ThrowsAnException_WhenContextIsNull()
        {
            var filter = new BatchSupportAttribute();

            Assert.Throws<ArgumentNullException>(() => filter.OnCreating(null));
            Assert.Throws<ArgumentNullException>(() => filter.OnCreated(null));
        }

        [Fact]
        public void StartNew_CreatesTheBatchRecord_AndTagsEveryJob()
        {
            string insideId = null;

            var batchId = BatchJob.StartNew(_client.Object, _storage.Object, batch =>
            {
                insideId = BatchJob.Current;
                batch.Enqueue(() => Export("eu"));
                batch.Enqueue(() => Export("us"));
                batch.Schedule(() => Export("apac"), TimeSpan.FromMinutes(5));
            }, "Regional exports");

            Assert.Equal(12, batchId.Length);
            Assert.Equal(batchId, insideId);
            Assert.Null(BatchJob.Current);
            Assert.Equal("Regional exports", _hashes[BatchJob.GetHashKey(batchId)]["Description"]);
            Assert.Contains(batchId, _sets[BatchJob.IndexKey]);
            Assert.Equal(3, _created.Count);
            Assert.IsType<ScheduledState>(_created[2].State);
            _transaction.Verify(x => x.ExpireHash(BatchJob.GetHashKey(batchId), BatchJob.ExpireIn));
        }

        [Fact]
        public void StartNew_RejectsLongDescriptions_AndRestoresCurrentOnFailure()
        {
            Assert.Throws<ArgumentException>(() => BatchJob.StartNew(_client.Object, _storage.Object, _ => { }, new string('x', 201)));
            Assert.Throws<InvalidOperationException>(() => BatchJob.StartNew(_client.Object, _storage.Object, _ => throw new InvalidOperationException("boom")));
            Assert.Null(BatchJob.Current);
        }

        [Fact]
        public void BatchSupport_OnCreating_SetsTheParameter_OnlyInsideABatch()
        {
            var filter = new BatchSupportAttribute();
            var outside = new CreateContextMock().GetCreatingContext();
            filter.OnCreating(outside);
            Assert.False(outside.Parameters.ContainsKey(BatchJob.ParameterName));

            BatchJob.StartNew(_client.Object, _storage.Object, _ =>
            {
                var inside = new CreateContextMock().GetCreatingContext();
                filter.OnCreating(inside);
                Assert.Equal(BatchJob.Current, inside.GetJobParameter<string>(BatchJob.ParameterName));
            });
        }

        [Fact]
        public void BatchSupport_OnStateApplied_KeepsTheCountersCurrent()
        {
            var filter = new BatchSupportAttribute();
            var snapshot = new Dictionary<string, string> { { BatchJob.ParameterName, SerializationHelper.Serialize("b1", SerializationOption.User) } };

            var created = new ApplyStateContextMock { NewStateObject = new EnqueuedState() };
            created.BackgroundJob.Id = "7";
            created.BackgroundJob.ParametersSnapshot = snapshot;
            filter.OnStateApplied(created.Object, _transaction.Object);

            var processing = new ApplyStateContextMock { NewStateObject = new ProcessingState("srv", "w"), OldStateName = EnqueuedState.StateName };
            processing.BackgroundJob.Id = "7";
            processing.BackgroundJob.ParametersSnapshot = snapshot;
            filter.OnStateApplied(processing.Object, _transaction.Object);

            var succeeded = new ApplyStateContextMock { NewStateObject = new SucceededState(null, 1, 1), OldStateName = ProcessingState.StateName };
            succeeded.BackgroundJob.Id = "7";
            succeeded.BackgroundJob.ParametersSnapshot = snapshot;
            filter.OnStateApplied(succeeded.Object, _transaction.Object);

            Assert.Equal(new[] { "7" }, _sets[BatchJob.GetJobsKey("b1")]);
            Assert.Equal(1, _counters[BatchJob.GetTotalKey("b1")]);
            Assert.Equal(0, _counters[BatchJob.GetStateKey("b1", EnqueuedState.StateName)]);
            Assert.Equal(0, _counters[BatchJob.GetStateKey("b1", ProcessingState.StateName)]);
            Assert.Equal(1, _counters[BatchJob.GetStateKey("b1", SucceededState.StateName)]);
        }

        [Fact, StaticLock]
        public void StateMachine_KeepsTheBatchCounters_WhenTheStateIsChangedWithoutFilters()
        {
            // The worker stores a FailedState without filters when every attempt to store the final state failed
            var context = CreateFailedContext("b1", out var transaction);

            new CoreStateMachine().ApplyState(context.Object);

            transaction.Verify(x => x.DecrementCounter(BatchJob.GetStateKey("b1", ProcessingState.StateName), It.IsAny<TimeSpan>()), Times.Once);
            transaction.Verify(x => x.IncrementCounter(BatchJob.GetStateKey("b1", FailedState.StateName), It.IsAny<TimeSpan>()), Times.Once);
        }

        [Fact, StaticLock]
        public void StateMachine_UpdatesTheBatchCountersOnce_WhenTheFilterRan()
        {
            var context = CreateFailedContext("b1", out var transaction);
            var filters = new JobFilterCollection { new BatchSupportAttribute() };

            new StateMachine(filters, new CoreStateMachine()).ApplyState(context.Object);

            transaction.Verify(x => x.DecrementCounter(BatchJob.GetStateKey("b1", ProcessingState.StateName), It.IsAny<TimeSpan>()), Times.Once);
            transaction.Verify(x => x.IncrementCounter(BatchJob.GetStateKey("b1", FailedState.StateName), It.IsAny<TimeSpan>()), Times.Once);
        }

        [Fact, StaticLock]
        public void StateMachine_LeavesTheBatchCounters_WhenTheFilterIsNotRegistered()
        {
            var context = CreateFailedContext("b1", out var transaction);
            GlobalJobFilters.Filters.Remove(typeof(BatchSupportAttribute));
            try
            {
                new CoreStateMachine().ApplyState(context.Object);
            }
            finally
            {
                GlobalJobFilters.Filters.Add(new BatchSupportAttribute());
            }

            transaction.Verify(x => x.IncrementCounter(It.IsAny<string>(), It.IsAny<TimeSpan>()), Times.Never);
            transaction.Verify(x => x.DecrementCounter(It.IsAny<string>(), It.IsAny<TimeSpan>()), Times.Never);
        }

        private static ApplyStateContextMock CreateFailedContext(string batchId, out Mock<IWriteOnlyTransaction> transaction)
        {
            var context = new ApplyStateContextMock
            {
                NewStateObject = new FailedState(new InvalidOperationException("boom"), "server"),
                OldStateName = ProcessingState.StateName
            };
            context.BackgroundJob.Id = "7";
            context.BackgroundJob.ParametersSnapshot = new Dictionary<string, string>
            {
                { BatchJob.ParameterName, SerializationHelper.Serialize(batchId, SerializationOption.User) }
            };
            context.Storage.Setup(x => x.GetStateHandlers()).Returns(Enumerable.Empty<IStateHandler>());

            transaction = context.Transaction;
            return context;
        }

        [Fact]
        public void BatchSupport_OnStateApplied_IgnoresJobsOutsideBatches()
        {
            var filter = new BatchSupportAttribute();
            var context = new ApplyStateContextMock { NewStateObject = new EnqueuedState() };
            context.BackgroundJob.Id = "9";
            context.BackgroundJob.ParametersSnapshot = new Dictionary<string, string>();

            filter.OnStateApplied(context.Object, _transaction.Object);

            Assert.Empty(_counters);
            Assert.Empty(_sets);
        }

        [Fact]
        public void Get_ReadsCountsAndDerivedState()
        {
            _hashes[BatchJob.GetHashKey("b1")] = new Dictionary<string, string> { { "Description", "Nightly" }, { "CreatedAt", JobHelper.SerializeDateTime(_now) } };
            _counters[BatchJob.GetTotalKey("b1")] = 4;
            _counters[BatchJob.GetStateKey("b1", SucceededState.StateName)] = 2;
            _counters[BatchJob.GetStateKey("b1", FailedState.StateName)] = 1;
            _counters[BatchJob.GetStateKey("b1", ProcessingState.StateName)] = 1;
            Add(BatchJob.GetContinuationsKey("b1"), "77");

            var batch = BatchJob.Get(_connection.Object, "b1");

            Assert.Equal("Nightly", batch.Description);
            Assert.Equal(_now, batch.CreatedAt);
            Assert.Equal(4, batch.Total);
            Assert.Equal(2, batch.Succeeded);
            Assert.Equal(1, batch.Failed);
            Assert.Equal(1, batch.Pending);
            Assert.Equal(50.0, batch.Progress);
            Assert.False(batch.IsFinished);
            Assert.False(batch.IsStuck);
            Assert.Equal(new[] { "77" }, batch.Continuations);

            // The running job finishes: 3 succeeded + 1 failed, nothing pending
            _counters[BatchJob.GetStateKey("b1", ProcessingState.StateName)] = 0;
            _counters[BatchJob.GetStateKey("b1", SucceededState.StateName)] = 3;
            Assert.True(BatchJob.Get(_connection.Object, "b1").IsStuck);

            _counters[BatchJob.GetStateKey("b1", FailedState.StateName)] = 0;
            _counters[BatchJob.GetStateKey("b1", SucceededState.StateName)] = 4;
            Assert.True(BatchJob.Get(_connection.Object, "b1").IsFinished);
        }

        [Fact]
        public void Get_ReturnsNull_ForUnknownBatches()
        {
            Assert.Null(BatchJob.Get(_connection.Object, "nope"));
        }

        [Fact]
        public void List_IsNewestFirst_AndSkipsExpiredBatches_WithoutWriting()
        {
            foreach (var id in new[] { "old", "gone", "new" })
            {
                Add(BatchJob.IndexKey, id);
                if (id != "gone") _hashes[BatchJob.GetHashKey(id)] = new Dictionary<string, string>();
            }

            var batches = BatchJob.List(_connection.Object, 0, 10);

            // Listing serves dashboard GETs, which must not write
            Assert.Equal(new[] { "new", "old" }, batches.Select(b => b.Id));
            Assert.Contains("gone", _sets[BatchJob.IndexKey]);
            _connection.Verify(x => x.CreateWriteTransaction(), Times.Never);
        }

        [Fact]
        public void StartNew_ExpiresTheJobList_OnceItsJobsAreCreated()
        {
            var createdWhenExpired = -1;
            _transaction.Setup(x => x.ExpireSet(It.IsAny<string>(), It.IsAny<TimeSpan>())).Callback(() => createdWhenExpired = _created.Count);

            var batchId = BatchJob.StartNew(_client.Object, _storage.Object, batch =>
            {
                batch.Enqueue(() => Export("eu"));
                batch.Enqueue(() => Export("us"));
            });

            // On SQL Server, expiring a set only reaches the rows it has: expiring it first left the jobs' rows forever
            _transaction.Verify(x => x.ExpireSet(BatchJob.GetJobsKey(batchId), BatchJob.ExpireIn), Times.Once);
            _transaction.Verify(x => x.ExpireSet(BatchJob.GetContinuationsKey(batchId), It.IsAny<TimeSpan>()), Times.Never);
            Assert.Equal(2, createdWhenExpired);
        }

        [Fact]
        public void StartNew_ExpiresTheJobList_EvenWhenTheCallbackFails()
        {
            string batchId = null;

            Assert.Throws<InvalidOperationException>(() => BatchJob.StartNew(_client.Object, _storage.Object, batch =>
            {
                batchId = batch.BatchId;
                batch.Enqueue(() => Export("eu"));
                throw new InvalidOperationException("boom");
            }));

            _transaction.Verify(x => x.ExpireSet(BatchJob.GetJobsKey(batchId), BatchJob.ExpireIn), Times.Once);
        }

        [Fact]
        public void StartNew_ReturnsTheBatch_WhenExpiringTheJobListFails()
        {
            _transaction.Setup(x => x.ExpireSet(It.IsAny<string>(), It.IsAny<TimeSpan>())).Throws(new InvalidOperationException("storage down"));

            var batchId = BatchJob.StartNew(_client.Object, _storage.Object, batch => batch.Enqueue(() => Export("eu")));

            Assert.Equal(12, batchId.Length);
        }

        [Fact]
        public void StartNew_DoesNotExpireAnything_WhenTheStorageHasNoExtendedApi()
        {
            _storage.Setup(x => x.HasFeature(JobStorageFeatures.ExtendedApi)).Returns(false);

            var batchId = BatchJob.StartNew(_client.Object, _storage.Object, batch => batch.Enqueue(() => Export("eu")));

            Assert.Contains(batchId, _sets[BatchJob.IndexKey]);
            _transaction.Verify(x => x.ExpireHash(It.IsAny<string>(), It.IsAny<TimeSpan>()), Times.Never);
            _transaction.Verify(x => x.ExpireSet(It.IsAny<string>(), It.IsAny<TimeSpan>()), Times.Never);
        }

        [Fact]
        public void StartNew_RemovesExpiredBatchesFromTheIndex()
        {
            var expiredBefore = (double)JobHelper.ToTimestamp(_now - BatchJob.ExpireIn);
            _storage.Setup(x => x.HasFeature(JobStorageFeatures.Connection.BatchedGetFirstByLowest)).Returns(true);
            _connection.Setup(x => x.GetFirstByLowestScoreFromSet(BatchJob.IndexKey, 0, expiredBefore, 100)).Returns(new List<string> { "old1", "old2" });
            Add(BatchJob.IndexKey, "old1");
            Add(BatchJob.IndexKey, "old2");
            Add(BatchJob.IndexKey, "recent");

            var batchId = BatchJob.StartNew(_client.Object, _storage.Object, _ => { });

            Assert.Equal(new[] { "recent", batchId }, _sets[BatchJob.IndexKey]);
        }

        [Fact]
        public void StartNew_RemovesTheOldestExpiredBatch_WithoutBatchedReads()
        {
            var expiredBefore = (double)JobHelper.ToTimestamp(_now - BatchJob.ExpireIn);
            _connection.Setup(x => x.GetFirstByLowestScoreFromSet(BatchJob.IndexKey, 0, expiredBefore)).Returns("old1");
            Add(BatchJob.IndexKey, "old1");

            var batchId = BatchJob.StartNew(_client.Object, _storage.Object, _ => { });

            Assert.Equal(new[] { batchId }, _sets[BatchJob.IndexKey]);
        }

        [Fact]
        public void BatchSupport_OnStateApplied_RecordsAContinuation_InTheTransactionOfItsFirstState()
        {
            var filter = new BatchSupportAttribute();
            var context = ContinuationContext("500", oldStateName: null);
            context.Storage.Setup(x => x.HasFeature(JobStorageFeatures.ExtendedApi)).Returns(true);
            var calls = new List<string>();
            _transaction.Setup(x => x.AddToSet(It.IsAny<string>(), It.IsAny<string>())).Callback<string, string>((key, value) => { calls.Add("add"); Add(key, value); });
            _transaction.Setup(x => x.ExpireSet(It.IsAny<string>(), It.IsAny<TimeSpan>())).Callback(() => calls.Add("expire"));

            filter.OnStateApplied(context.Object, _transaction.Object);

            // The filter only adds to the state change's transaction, which its owner commits
            Assert.Equal(new[] { "add", "expire" }, calls);
            Assert.Equal(new[] { "500" }, _sets[BatchJob.GetContinuationsKey("b1")]);
            _transaction.Verify(x => x.ExpireSet(BatchJob.GetContinuationsKey("b1"), BatchJob.ExpireIn), Times.Once);
            _transaction.Verify(x => x.Commit(), Times.Never);
        }

        [Fact]
        public void BatchSupport_OnStateApplied_DoesNotExpireTheContinuationList_WhenTheStorageHasNoExtendedApi()
        {
            var filter = new BatchSupportAttribute();
            var context = ContinuationContext("500", oldStateName: null);

            filter.OnStateApplied(context.Object, _transaction.Object);

            Assert.Equal(new[] { "500" }, _sets[BatchJob.GetContinuationsKey("b1")]);
            _transaction.Verify(x => x.ExpireSet(It.IsAny<string>(), It.IsAny<TimeSpan>()), Times.Never);
        }

        [Fact]
        public void BatchSupport_OnStateApplied_RecordsAContinuation_OnlyWithItsFirstState()
        {
            var filter = new BatchSupportAttribute();
            var context = ContinuationContext("500", oldStateName: AwaitingState.StateName);

            filter.OnStateApplied(context.Object, _transaction.Object);

            Assert.False(_sets.ContainsKey(BatchJob.GetContinuationsKey("b1")));
        }

        [Fact]
        public void BatchSupport_OnStateApplied_ReadsTheContinuationFromStorage_WhenThereIsNoSnapshot()
        {
            var filter = new BatchSupportAttribute();
            var context = new ApplyStateContextMock { NewStateObject = new AwaitingJobsState(new[] { "1" }) };
            context.BackgroundJob.Id = "500";
            context.Connection.Setup(x => x.GetJobParameter("500", BatchJob.ContinuationParameterName)).Returns(SerializationHelper.Serialize("b1", SerializationOption.User));

            filter.OnStateApplied(context.Object, _transaction.Object);

            Assert.Equal(new[] { "500" }, _sets[BatchJob.GetContinuationsKey("b1")]);
        }

        [DataCompatibilityRangeFact, CleanSerializerSettings]
        public void BatchSupport_WritesItsParameters_WhenTheApplicationsOptionsDoNotKnowStrings()
        {
            SerializationHelper.SetUserSerializerSettings(new System.Text.Json.JsonSerializerOptions { TypeInfoResolver = new KnownTypesResolver(typeof(int)) });
            var filter = new BatchSupportAttribute();
            var context = new CreateContextMock();
            IDictionary<string, string> stored = null;
            context.Connection.Setup(x => x.CreateWriteTransaction()).Returns(_transaction.Object);
            context.Connection
                .Setup(x => x.CreateExpiredJob(It.IsAny<Job>(), It.IsAny<IDictionary<string, string>>(), It.IsAny<DateTime>(), It.IsAny<TimeSpan>()))
                .Callback<Job, IDictionary<string, string>, DateTime, TimeSpan>((_, parameters, _, _) => stored = parameters)
                .Returns("500");

            BatchJob.StartNew(_client.Object, _storage.Object, _ =>
            {
                filter.OnCreating(context.GetCreatingContext());
                new Masterly.NightShift.Client.CoreBackgroundJobFactory(new Mock<IStateMachine>().Object).Create(context.Object);
            });

            Assert.Equal(12, BatchJob.ParseParameter(stored[BatchJob.ParameterName]).Length);
            Assert.StartsWith("\"", stored[BatchJob.ParameterName]);
        }

        [Fact, CleanSerializerSettings]
        public void ParseParameter_ReadsValuesWrittenWithTheApplicationsOptions()
        {
            // Stored before with a converter of the application that the internal options don't have
            SerializationHelper.SetUserSerializerSettings(new System.Text.Json.JsonSerializerOptions { Converters = { new WrappedStringConverter() } });

            Assert.Equal("b1", BatchJob.ParseParameter("{\"v\":\"b1\"}"));
            Assert.Equal("b2", BatchJob.ParseParameter("\"b2\""));
            Assert.Null(BatchJob.ParseParameter(null));
            Assert.Null(BatchJob.ParseParameter(""));
        }

        private ApplyStateContextMock ContinuationContext(string jobId, string oldStateName)
        {
            var context = new ApplyStateContextMock { NewStateObject = new AwaitingJobsState(new[] { "1" }), OldStateName = oldStateName };
            context.BackgroundJob.Id = jobId;
            context.BackgroundJob.ParametersSnapshot = new Dictionary<string, string>
            {
                { BatchJob.ContinuationParameterName, SerializationHelper.SerializeInternal("b1") }
            };

            return context;
        }

        private sealed class WrappedStringConverter : System.Text.Json.Serialization.JsonConverter<string>
        {
            public override string Read(ref System.Text.Json.Utf8JsonReader reader, Type typeToConvert, System.Text.Json.JsonSerializerOptions options)
            {
                using var document = System.Text.Json.JsonDocument.ParseValue(ref reader);
                return document.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object
                    ? document.RootElement.GetProperty("v").GetString()
                    : document.RootElement.GetString();
            }

            public override void Write(System.Text.Json.Utf8JsonWriter writer, string value, System.Text.Json.JsonSerializerOptions options)
            {
                writer.WriteStartObject();
                writer.WriteString("v", value);
                writer.WriteEndObject();
            }
        }

        [Fact]
        public void Reads_FallBackToTheBasicApi_WhenTheStorageConnectionHasNoExtendedApi()
        {
            var connection = new Mock<JobStorageConnection> { CallBase = true };
            connection.Setup(x => x.GetAllEntriesFromHash(BatchJob.GetHashKey("b1"))).Returns(new Dictionary<string, string> { { "Description", "Basic" } });
            connection.Setup(x => x.GetAllItemsFromSet(BatchJob.GetJobsKey("b1"))).Returns(new HashSet<string> { "1", "2" });
            connection.Setup(x => x.GetAllItemsFromSet(BatchJob.GetContinuationsKey("b1"))).Returns(new HashSet<string>());
            connection.Setup(x => x.GetAllItemsFromSet(BatchJob.IndexKey)).Returns(new HashSet<string> { "b1" });

            var batch = BatchJob.Get(connection.Object, "b1");

            Assert.Equal(2, batch.Total);
            Assert.Empty(batch.Counts);
            Assert.Equal(2, BatchJob.GetJobIds(connection.Object, "b1").Count);
            Assert.Equal(1, BatchJob.Count(connection.Object));
            Assert.Equal("b1", Assert.Single(BatchJob.List(connection.Object, 0, 10)).Id);
        }

        [Fact]
        public void ContinueWith_CreatesAFanInOverTheBatchJobs_AndRecordsIt()
        {
            _hashes[BatchJob.GetHashKey("b1")] = new Dictionary<string, string>();
            Add(BatchJob.GetJobsKey("b1"), "1");
            Add(BatchJob.GetJobsKey("b1"), "2");
            string continuationOf = null;
            _client.Setup(x => x.Create(It.IsAny<Job>(), It.IsAny<AwaitingJobsState>())).Returns<Job, IState>((_, __) =>
            {
                continuationOf = BatchJob.CurrentContinuation;
                return "500";
            });

            var id = BatchJob.ContinueWith(_client.Object, _storage.Object, "b1", Job.FromExpression(() => Merge()), JobContinuationOptions.OnlyOnSucceededState);

            Assert.Equal("500", id);
            Assert.Equal("b1", continuationOf);
            Assert.Null(BatchJob.CurrentContinuation);
            _client.Verify(x => x.Create(It.Is<Job>(j => j.Method.Name == "Merge"), It.Is<AwaitingJobsState>(s => s.ParentIds.SequenceEqual(new[] { "1", "2" }) && s.Mode == FanInMode.All && s.Options == JobContinuationOptions.OnlyOnSucceededState)));
        }

        [Fact]
        public void ContinueWith_Throws_ForUnknownOrEmptyBatches()
        {
            Assert.Throws<InvalidOperationException>(() => BatchJob.ContinueWith(_client.Object, _storage.Object, "nope", Job.FromExpression(() => Merge())));

            _hashes[BatchJob.GetHashKey("empty")] = new Dictionary<string, string>();
            Assert.Throws<InvalidOperationException>(() => BatchJob.ContinueWith(_client.Object, _storage.Object, "empty", Job.FromExpression(() => Merge())));
        }

        [Fact]
        public void BatchSupport_RecordsContinuations_ThroughTheirParameter()
        {
            var filter = new BatchSupportAttribute();
            var context = new CreateContextMock();
            _hashes[BatchJob.GetHashKey("b1")] = new Dictionary<string, string>();
            Add(BatchJob.GetJobsKey("b1"), "1");
            _client.Setup(x => x.Create(It.IsAny<Job>(), It.IsAny<AwaitingJobsState>())).Returns<Job, IState>((_, state) =>
            {
                var creating = context.GetCreatingContext();
                filter.OnCreating(creating);

                // What the factory stores and the state machine applies
                var applyContext = new ApplyStateContextMock { NewStateObject = state };
                applyContext.BackgroundJob.Id = "500";
                applyContext.BackgroundJob.ParametersSnapshot = creating.Parameters.ToDictionary(x => x.Key, x => SerializationHelper.SerializeInternal(x.Value));
                filter.OnStateApplied(applyContext.Object, _transaction.Object);
                return "500";
            });

            BatchJob.ContinueWith(_client.Object, _storage.Object, "b1", Job.FromExpression(() => Merge()));

            Assert.Equal(new[] { "500" }, _sets[BatchJob.GetContinuationsKey("b1")]);
        }
    }
}
