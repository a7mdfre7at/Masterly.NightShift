using System;
using System.Diagnostics.CodeAnalysis;
using System.Collections.Generic;
using System.Linq;
using Masterly.NightShift.Client;
using Masterly.NightShift.Common;
using Masterly.NightShift.Server;
using Masterly.NightShift.States;
using Masterly.NightShift.Storage;
using Moq;
using Xunit;

namespace Masterly.NightShift.Core.Tests
{
    public class PreventDuplicatesAttributeFacts : IDisposable
    {
        private readonly CreateContextMock _context = new CreateContextMock();
        private readonly Mock<IWriteOnlyTransaction> _transaction = new Mock<IWriteOnlyTransaction>();
        private readonly Mock<IBackgroundJobStateChanger> _stateChanger = new Mock<IBackgroundJobStateChanger>();
        private readonly DateTime _now = new DateTime(2026, 11, 4, 12, 0, 0, DateTimeKind.Utc);
        private readonly Dictionary<string, Dictionary<string, string>> _hashes = new Dictionary<string, Dictionary<string, string>>();
        private readonly Dictionary<string, string> _states = new Dictionary<string, string>();

        public PreventDuplicatesAttributeFacts()
        {
            _context.Job = Job.FromExpression(() => Import("file.csv", 3));

            var connection = _context.Connection;
            connection.Setup(x => x.AcquireDistributedLock(It.IsAny<string>(), It.IsAny<TimeSpan>())).Returns(new Mock<IDisposable>().Object);
            connection.Setup(x => x.CreateWriteTransaction()).Returns(_transaction.Object);
            connection.Setup(x => x.GetAllEntriesFromHash(It.IsAny<string>())).Returns<string>(key => _hashes.TryGetValue(key, out var hash) ? hash : null);
            connection.Setup(x => x.GetStateData(It.IsAny<string>())).Returns<string>(id => _states.TryGetValue(id, out var name) ? new StateData { Name = name, Data = new Dictionary<string, string>() } : null);
            connection.Setup(x => x.GetJobData(It.IsAny<string>())).Returns<string>(id => _states.ContainsKey(id) || id == "created-no-state" ? new JobData() : null);

            _transaction.Setup(x => x.SetRangeInHash(It.IsAny<string>(), It.IsAny<IEnumerable<KeyValuePair<string, string>>>()))
                .Callback<string, IEnumerable<KeyValuePair<string, string>>>((key, pairs) =>
                {
                    if (!_hashes.TryGetValue(key, out var hash)) _hashes[key] = hash = new Dictionary<string, string>();
                    foreach (var pair in pairs) hash[pair.Key] = pair.Value;
                });
            _transaction.Setup(x => x.RemoveHash(It.IsAny<string>())).Callback<string>(key => _hashes.Remove(key));

            ConcurrencyControl.StateChanger = _stateChanger.Object;
            PreventDuplicatesAttribute.Clock = () => _now;
        }

        public void Dispose()
        {
            ConcurrencyControl.StateChanger = new BackgroundJobStateChanger();
            PreventDuplicatesAttribute.Clock = () => DateTime.UtcNow;
        }

        private string Key(PreventDuplicatesAttribute attribute) => PreventDuplicatesAttribute.GetKey(attribute.GetFingerprint(_context.Job));

        [Fact]
        public void GetFingerprint_IsStable_AndDependsOnArguments()
        {
            var attribute = new PreventDuplicatesAttribute();

            var first = attribute.GetFingerprint(Job.FromExpression(() => Import("file.csv", 3)));
            var same = attribute.GetFingerprint(Job.FromExpression(() => Import("file.csv", 3)));
            var other = attribute.GetFingerprint(Job.FromExpression(() => Import("file.csv", 4)));

            Assert.Equal(first, same);
            Assert.NotEqual(first, other);
            Assert.Equal(32, first.Length);
        }

        [Fact]
        public void GetFingerprint_DiffersForSameNamedTypes_InOtherNamespaces()
        {
            var attribute = new PreventDuplicatesAttribute();

            var billing = attribute.GetFingerprint(Job.FromExpression(() => Server.Billing.ReportJob.Run()));
            var shipping = attribute.GetFingerprint(Job.FromExpression(() => Server.Shipping.ReportJob.Run()));

            Assert.NotEqual(billing, shipping);
        }

        [Fact]
        public void GetFingerprint_DiffersForOverloads_WithTheSameSerializedArguments()
        {
            var attribute = new PreventDuplicatesAttribute();

            var asInt = attribute.GetFingerprint(Job.FromExpression(() => Charge(5)));
            var asLong = attribute.GetFingerprint(Job.FromExpression(() => Charge(5L)));

            Assert.NotEqual(asInt, asLong);
        }

        [Fact]
        public void GetFingerprint_UsesTheTemplate_WhenGiven()
        {
            var attribute = new PreventDuplicatesAttribute("import:{0}");

            Assert.Equal("import:file.csv", attribute.GetFingerprint(Job.FromExpression(() => Import("file.csv", 3))));
            Assert.Equal("import:file.csv", attribute.GetFingerprint(Job.FromExpression(() => Import("FILE.csv", 99))));
        }

        [Fact, StaticLock]
        public void OnCreating_ReservesTheFingerprint_AndSetsTheParameter_WhenNothingIsPending()
        {
            var attribute = new PreventDuplicatesAttribute();
            var creating = _context.GetCreatingContext();

            attribute.OnCreating(creating);

            Assert.False(creating.Canceled);
            Assert.Equal(attribute.GetFingerprint(_context.Job), creating.GetJobParameter<string>(PreventDuplicatesAttribute.FingerprintParameter));
            Assert.True(_hashes.ContainsKey(Key(attribute)));
            Assert.Equal(String.Empty, _hashes[Key(attribute)]["JobId"]);
            Assert.NotEmpty(_hashes[Key(attribute)]["Reservation"]);
        }

        [Fact, StaticLock]
        public void OnCreated_WritesTheJobId_IntoTheRecord()
        {
            var attribute = new PreventDuplicatesAttribute();
            attribute.OnCreating(_context.GetCreatingContext());

            attribute.OnCreated(_context.GetCreatedContext("new-job"));

            Assert.Equal("new-job", _hashes[Key(attribute)]["JobId"]);
            Assert.Equal("new-job", PreventDuplicatesAttribute.ReadOwner(_context.Connection.Object, attribute.GetFingerprint(_context.Job)));
        }

        [Fact, StaticLock]
        public void OnCreated_RemovesTheReservation_WhenCreationFailedOrWasCanceled()
        {
            var attribute = new PreventDuplicatesAttribute();
            attribute.OnCreating(_context.GetCreatingContext());

            attribute.OnCreated(_context.GetCreatedContext("new-job", exception: new Exception()));

            Assert.False(_hashes.ContainsKey(Key(attribute)));
        }

        [Fact, StaticLock]
        public void OnCreated_KeepsTheCreationsOwnException_WhenTheReservationCannotBeRemoved()
        {
            var attribute = new PreventDuplicatesAttribute();
            attribute.OnCreating(_context.GetCreatingContext());
            _transaction.Setup(x => x.Commit()).Throws(new TimeoutException("storage unavailable"));

            attribute.OnCreated(_context.GetCreatedContext("new-job", exception: new InvalidOperationException()));

            _transaction.Verify(x => x.RemoveHash(Key(attribute)));
        }

        [Fact, StaticLock]
        public void OnCreating_CancelsTheJob_WhenTheOwnerIsStillPending()
        {
            var attribute = new PreventDuplicatesAttribute();
            _hashes[Key(attribute)] = new Dictionary<string, string> { { "JobId", "existing" } };
            _states["existing"] = EnqueuedState.StateName;
            var creating = _context.GetCreatingContext();

            attribute.OnCreating(creating);

            Assert.True(creating.Canceled);
            Assert.Equal("existing", _hashes[Key(attribute)]["JobId"]);
        }

        [Fact, StaticLock]
        public void OnCreating_CancelsTheJob_WhenACreationIsInProgress()
        {
            var attribute = new PreventDuplicatesAttribute();
            _hashes[Key(attribute)] = new Dictionary<string, string> { { "JobId", "" }, { "Reservation", "abc" }, { "ReservedAt", JobHelper.SerializeDateTime(_now.AddSeconds(-10)) } };
            var creating = _context.GetCreatingContext();

            attribute.OnCreating(creating);

            Assert.True(creating.Canceled);
        }

        [Fact, StaticLock]
        public void OnCreating_IgnoresAStaleReservation()
        {
            var attribute = new PreventDuplicatesAttribute();
            _hashes[Key(attribute)] = new Dictionary<string, string> { { "JobId", "" }, { "Reservation", "abc" }, { "ReservedAt", JobHelper.SerializeDateTime(_now.AddMinutes(-10)) } };
            var creating = _context.GetCreatingContext();

            attribute.OnCreating(creating);

            Assert.False(creating.Canceled);
        }

        [Theory, StaticLock]
        [InlineData("Succeeded")]
        [InlineData("Deleted")]
        public void OnCreating_Proceeds_WhenTheOwnerReachedAFinalState(string state)
        {
            var attribute = new PreventDuplicatesAttribute();
            _hashes[Key(attribute)] = new Dictionary<string, string> { { "JobId", "existing" } };
            _states["existing"] = state;
            var creating = _context.GetCreatingContext();

            attribute.OnCreating(creating);

            Assert.False(creating.Canceled);
        }

        [Fact, StaticLock]
        public void OnCreating_Proceeds_WhenTheOwnerNoLongerExists()
        {
            var attribute = new PreventDuplicatesAttribute();
            _hashes[Key(attribute)] = new Dictionary<string, string> { { "JobId", "gone" } };
            var creating = _context.GetCreatingContext();

            attribute.OnCreating(creating);

            Assert.False(creating.Canceled);
        }

        [Fact, StaticLock]
        public void OnCreating_TreatsAnOwnerWithoutState_AsPending()
        {
            var attribute = new PreventDuplicatesAttribute();
            _hashes[Key(attribute)] = new Dictionary<string, string> { { "JobId", "created-no-state" } };
            var creating = _context.GetCreatingContext();

            attribute.OnCreating(creating);

            Assert.True(creating.Canceled);
        }

        [Theory, StaticLock]
        [InlineData("Enqueued")]
        [InlineData("Scheduled")]
        [InlineData("Awaiting")]
        public void ReplaceExisting_DeletesThePendingJob_OnlyOnceTheNewOneExists(string ownerState)
        {
            var attribute = new PreventDuplicatesAttribute { OnDuplicate = DuplicateHandling.ReplaceExisting };
            _hashes[Key(attribute)] = new Dictionary<string, string> { { "JobId", "existing" } };
            _states["existing"] = ownerState;
            var creating = _context.GetCreatingContext();

            attribute.OnCreating(creating);

            Assert.False(creating.Canceled);
            Assert.Equal(String.Empty, _hashes[Key(attribute)]["JobId"]);
            _stateChanger.Verify(x => x.ChangeState(It.IsAny<StateChangeContext>()), Times.Never);

            attribute.OnCreated(_context.GetCreatedContext("new-job"));

            Assert.Equal("new-job", _hashes[Key(attribute)]["JobId"]);
            _stateChanger.Verify(x => x.ChangeState(It.Is<StateChangeContext>(c =>
                c.BackgroundJobId == "existing"
                && c.NewState is DeletedState
                && c.ExpectedStates.Contains(EnqueuedState.StateName)
                && c.ExpectedStates.Contains(ScheduledState.StateName)
                && c.ExpectedStates.Contains(AwaitingState.StateName)
                && !c.ExpectedStates.Contains(ProcessingState.StateName))));
        }

        [Fact, StaticLock]
        public void ReplaceExisting_DoesNotReplaceAJobThatIsRunning()
        {
            var attribute = new PreventDuplicatesAttribute { OnDuplicate = DuplicateHandling.ReplaceExisting };
            _hashes[Key(attribute)] = new Dictionary<string, string> { { "JobId", "existing" } };
            _states["existing"] = ProcessingState.StateName;
            var creating = _context.GetCreatingContext();

            attribute.OnCreating(creating);

            Assert.True(creating.Canceled);
            Assert.Equal("existing", _hashes[Key(attribute)]["JobId"]);
            _stateChanger.Verify(x => x.ChangeState(It.IsAny<StateChangeContext>()), Times.Never);
        }

        [Fact, StaticLock]
        public void ReplaceExisting_DoesNotReplaceAJobThatIsStillBeingCreated()
        {
            var attribute = new PreventDuplicatesAttribute { OnDuplicate = DuplicateHandling.ReplaceExisting };
            _hashes[Key(attribute)] = new Dictionary<string, string> { { "JobId", "" }, { "Reservation", "abc" }, { "ReservedAt", JobHelper.SerializeDateTime(_now.AddSeconds(-10)) } };
            var creating = _context.GetCreatingContext();

            attribute.OnCreating(creating);

            Assert.True(creating.Canceled);
            Assert.Equal("abc", _hashes[Key(attribute)]["Reservation"]);
        }

        [Fact, StaticLock]
        public void ReplaceExisting_GivesTheFingerprintBack_WhenTheNewJobIsNotCreated()
        {
            var attribute = new PreventDuplicatesAttribute { OnDuplicate = DuplicateHandling.ReplaceExisting };
            _hashes[Key(attribute)] = new Dictionary<string, string> { { "JobId", "existing" } };
            _states["existing"] = EnqueuedState.StateName;
            attribute.OnCreating(_context.GetCreatingContext());

            attribute.OnCreated(_context.GetCreatedContext("new-job", exception: new InvalidOperationException()));

            Assert.Equal("existing", _hashes[Key(attribute)]["JobId"]);
            _stateChanger.Verify(x => x.ChangeState(It.IsAny<StateChangeContext>()), Times.Never);
        }

        [Fact, StaticLock]
        public void ReplaceExisting_KeepsTheNewJob_WhenThePendingOneStartedRunningMeanwhile()
        {
            var attribute = new PreventDuplicatesAttribute { OnDuplicate = DuplicateHandling.ReplaceExisting };
            _hashes[Key(attribute)] = new Dictionary<string, string> { { "JobId", "existing" } };
            _states["existing"] = EnqueuedState.StateName;
            _stateChanger.Setup(x => x.ChangeState(It.IsAny<StateChangeContext>())).Returns((IState)null);
            attribute.OnCreating(_context.GetCreatingContext());

            attribute.OnCreated(_context.GetCreatedContext("new-job"));

            Assert.Equal("new-job", _hashes[Key(attribute)]["JobId"]);
        }

        [Fact, StaticLock]
        public void ReplaceExisting_DoesNotFailTheCreation_WhenDeletingThePendingJobThrows()
        {
            var attribute = new PreventDuplicatesAttribute { OnDuplicate = DuplicateHandling.ReplaceExisting };
            _hashes[Key(attribute)] = new Dictionary<string, string> { { "JobId", "existing" } };
            _states["existing"] = EnqueuedState.StateName;
            _stateChanger.Setup(x => x.ChangeState(It.IsAny<StateChangeContext>())).Throws(new TimeoutException("storage unavailable"));
            attribute.OnCreating(_context.GetCreatingContext());

            attribute.OnCreated(_context.GetCreatedContext("new-job"));

            Assert.Equal("new-job", _hashes[Key(attribute)]["JobId"]);
        }

        [Fact, StaticLock]
        public void OnCreated_DoesNotFailTheCreation_WhenTheRecordCannotBeWritten()
        {
            var attribute = new PreventDuplicatesAttribute { OnDuplicate = DuplicateHandling.ReplaceExisting };
            _hashes[Key(attribute)] = new Dictionary<string, string> { { "JobId", "existing" } };
            _states["existing"] = EnqueuedState.StateName;
            attribute.OnCreating(_context.GetCreatingContext());
            _transaction.Setup(x => x.Commit()).Throws(new TimeoutException("storage unavailable"));

            attribute.OnCreated(_context.GetCreatedContext("new-job"));

            // The job it replaces is still deleted, so only the new one is pending
            _stateChanger.Verify(x => x.ChangeState(It.Is<StateChangeContext>(c => c.BackgroundJobId == "existing" && c.NewState is DeletedState)));
        }

        [Fact]
        public void GetFingerprint_ShortensALongTemplatedFingerprint_SoItsKeyFits()
        {
            var attribute = new PreventDuplicatesAttribute("import:{0}");
            var file = new string('f', 200) + ".csv";

            var fingerprint = attribute.GetFingerprint(Job.FromExpression(() => Import(file, 3)));

            Assert.StartsWith("import:fff", fingerprint, StringComparison.Ordinal);
            Assert.Contains("~", fingerprint);
            Assert.True(PreventDuplicatesAttribute.GetKey(fingerprint).Length <= 100);
            Assert.Equal(fingerprint, attribute.GetFingerprint(Job.FromExpression(() => Import(file, 4))));
        }

        [Fact, StaticLock]
        public void OnCreating_DoesNotExpireTheRecord_WhenTheStorageDoesNotSupportTheExtendedApi()
        {
            var storageTransaction = new Mock<JobStorageTransaction>();
            _context.Connection.Setup(x => x.CreateWriteTransaction()).Returns(storageTransaction.Object);
            var attribute = new PreventDuplicatesAttribute { ExpirationHours = 2 };

            attribute.OnCreating(_context.GetCreatingContext());

            storageTransaction.Verify(x => x.SetRangeInHash(Key(attribute), It.IsAny<IEnumerable<KeyValuePair<string, string>>>()));
            storageTransaction.Verify(x => x.ExpireHash(It.IsAny<string>(), It.IsAny<TimeSpan>()), Times.Never);
        }

        [Fact, StaticLock]
        public void OnCreating_DoesNothing_WhenAnotherFilterAlreadyCanceled()
        {
            var attribute = new PreventDuplicatesAttribute();
            var creating = _context.GetCreatingContext();
            creating.Canceled = true;

            attribute.OnCreating(creating);

            Assert.Empty(_hashes);
        }

        [Fact, StaticLock]
        public void OnCreating_ExpiresTheRecord_OnStorageTransactions()
        {
            var storageTransaction = new Mock<JobStorageTransaction>();
            _context.Connection.Setup(x => x.CreateWriteTransaction()).Returns(storageTransaction.Object);
            _context.Storage.Setup(x => x.HasFeature(JobStorageFeatures.ExtendedApi)).Returns(true);
            var attribute = new PreventDuplicatesAttribute { ExpirationHours = 2 };

            attribute.OnCreating(_context.GetCreatingContext());

            storageTransaction.Verify(x => x.ExpireHash(Key(attribute), TimeSpan.FromHours(2)));
        }

        [Fact, StaticLock]
        public void OnCreating_Proceeds_WhenTheOwnerFailed()
        {
            var attribute = new PreventDuplicatesAttribute();
            _hashes[Key(attribute)] = new Dictionary<string, string> { { "JobId", "existing" } };
            _states["existing"] = FailedState.StateName;
            var creating = _context.GetCreatingContext();

            attribute.OnCreating(creating);

            Assert.False(creating.Canceled);
        }

        [Fact, StaticLock]
        public void OnCreating_CancelsTheJob_WhenTheOwnerFailed_AndReleaseOnFailureIsOff()
        {
            var attribute = new PreventDuplicatesAttribute { ReleaseOnFailure = false };
            _hashes[Key(attribute)] = new Dictionary<string, string> { { "JobId", "existing" } };
            _states["existing"] = FailedState.StateName;
            var creating = _context.GetCreatingContext();

            attribute.OnCreating(creating);

            Assert.True(creating.Canceled);
        }

        [Fact, StaticLock]
        public void OnCreating_And_OnCreated_ExpireTheRecord_AfterTheScheduledTimePlusTheWindow()
        {
            var storageTransaction = new Mock<JobStorageTransaction>();
            _context.Connection.Setup(x => x.CreateWriteTransaction()).Returns(storageTransaction.Object);
            _context.Storage.Setup(x => x.HasFeature(JobStorageFeatures.ExtendedApi)).Returns(true);
            var attribute = new PreventDuplicatesAttribute();
            var createContext = new CreateContext(_context.Storage.Object, _context.Connection.Object, _context.Job, new ScheduledState(_now.AddDays(3)));
            var creating = new CreatingContext(createContext);

            attribute.OnCreating(creating);
            attribute.OnCreated(new CreatedContext(creating, new BackgroundJob("new-job", _context.Job, _now), false, null));

            storageTransaction.Verify(x => x.ExpireHash(Key(attribute), TimeSpan.FromDays(3) + TimeSpan.FromHours(24)), Times.Exactly(2));
        }

        [Fact]
        public void Defaults_ReleaseTheFingerprintOnFailure_AndExpireAfter24Hours()
        {
            var attribute = new PreventDuplicatesAttribute();

            Assert.True(attribute.ReleaseOnFailure);
            Assert.Equal(24, attribute.ExpirationHours);
        }

        [Fact]
        public void OnStateApplied_ReleasesTheFingerprint_WhenTheOwnerFails()
        {
            var attribute = new PreventDuplicatesAttribute();
            var fingerprint = attribute.GetFingerprint(_context.Job);
            var context = new ApplyStateContextMock { NewStateObject = new FailedState(new InvalidOperationException()) };
            context.BackgroundJob.Id = "owner";
            context.Connection.Setup(x => x.GetJobParameter("owner", PreventDuplicatesAttribute.FingerprintParameter)).Returns(SerializationHelper.Serialize(fingerprint));
            context.Connection.Setup(x => x.GetAllEntriesFromHash(PreventDuplicatesAttribute.GetKey(fingerprint))).Returns(new Dictionary<string, string> { { "JobId", "owner" } });

            attribute.OnStateApplied(context.Object, context.Transaction.Object);

            context.Transaction.Verify(x => x.RemoveHash(PreventDuplicatesAttribute.GetKey(fingerprint)));
        }

        [Fact]
        public void OnStateApplied_KeepsTheFingerprint_WhenTheOwnerFails_AndReleaseOnFailureIsOff()
        {
            var attribute = new PreventDuplicatesAttribute { ReleaseOnFailure = false };
            var fingerprint = attribute.GetFingerprint(_context.Job);
            var context = new ApplyStateContextMock { NewStateObject = new FailedState(new InvalidOperationException()) };
            context.BackgroundJob.Id = "owner";
            context.Connection.Setup(x => x.GetJobParameter("owner", PreventDuplicatesAttribute.FingerprintParameter)).Returns(SerializationHelper.Serialize(fingerprint));
            context.Connection.Setup(x => x.GetAllEntriesFromHash(PreventDuplicatesAttribute.GetKey(fingerprint))).Returns(new Dictionary<string, string> { { "JobId", "owner" } });

            attribute.OnStateApplied(context.Object, context.Transaction.Object);

            context.Transaction.Verify(x => x.RemoveHash(It.IsAny<string>()), Times.Never);
        }

        [Fact, StaticLock]
        public void OnStateApplied_ExtendsTheRecord_UntilTheNewScheduledTimePlusTheWindow_WhenTheOwnerIsRescheduled()
        {
            var attribute = new PreventDuplicatesAttribute { ExpirationHours = 2 };
            var fingerprint = attribute.GetFingerprint(_context.Job);
            var storageTransaction = new Mock<JobStorageTransaction>();
            var context = new ApplyStateContextMock { NewStateObject = new ScheduledState(_now.AddDays(2)) };
            context.Transaction = storageTransaction.As<IWriteOnlyTransaction>();
            context.Storage.Setup(x => x.HasFeature(JobStorageFeatures.ExtendedApi)).Returns(true);
            context.BackgroundJob.Id = "owner";
            context.Connection.Setup(x => x.GetJobParameter("owner", PreventDuplicatesAttribute.FingerprintParameter)).Returns(SerializationHelper.Serialize(fingerprint));
            context.Connection.Setup(x => x.GetAllEntriesFromHash(PreventDuplicatesAttribute.GetKey(fingerprint))).Returns(new Dictionary<string, string> { { "JobId", "owner" } });

            attribute.OnStateApplied(context.Object, storageTransaction.Object);

            storageTransaction.Verify(x => x.ExpireHash(PreventDuplicatesAttribute.GetKey(fingerprint), TimeSpan.FromDays(2) + TimeSpan.FromHours(2)));
            storageTransaction.Verify(x => x.RemoveHash(It.IsAny<string>()), Times.Never);
        }

        [Fact, StaticLock]
        public void OnStateApplied_DoesNotExtendTheRecord_WhenTheStorageDoesNotSupportTheExtendedApi()
        {
            var attribute = new PreventDuplicatesAttribute { ExpirationHours = 2 };
            var fingerprint = attribute.GetFingerprint(_context.Job);
            var storageTransaction = new Mock<JobStorageTransaction>();
            var context = new ApplyStateContextMock { NewStateObject = new ScheduledState(_now.AddDays(2)) };
            context.Transaction = storageTransaction.As<IWriteOnlyTransaction>();
            context.BackgroundJob.Id = "owner";
            context.Connection.Setup(x => x.GetJobParameter("owner", PreventDuplicatesAttribute.FingerprintParameter)).Returns(SerializationHelper.Serialize(fingerprint));
            context.Connection.Setup(x => x.GetAllEntriesFromHash(PreventDuplicatesAttribute.GetKey(fingerprint))).Returns(new Dictionary<string, string> { { "JobId", "owner" } });

            attribute.OnStateApplied(context.Object, storageTransaction.Object);

            storageTransaction.Verify(x => x.ExpireHash(It.IsAny<string>(), It.IsAny<TimeSpan>()), Times.Never);
        }

        [Fact]
        public void OnStateApplied_DoesNotExtendTheRecord_OfAnotherOwner()
        {
            var attribute = new PreventDuplicatesAttribute();
            var fingerprint = attribute.GetFingerprint(_context.Job);
            var storageTransaction = new Mock<JobStorageTransaction>();
            var context = new ApplyStateContextMock { NewStateObject = new ScheduledState(_now.AddDays(2)) };
            context.Transaction = storageTransaction.As<IWriteOnlyTransaction>();
            context.Storage.Setup(x => x.HasFeature(JobStorageFeatures.ExtendedApi)).Returns(true);
            context.BackgroundJob.Id = "old";
            context.Connection.Setup(x => x.GetJobParameter("old", PreventDuplicatesAttribute.FingerprintParameter)).Returns(SerializationHelper.Serialize(fingerprint));
            context.Connection.Setup(x => x.GetAllEntriesFromHash(PreventDuplicatesAttribute.GetKey(fingerprint))).Returns(new Dictionary<string, string> { { "JobId", "newer" } });

            attribute.OnStateApplied(context.Object, storageTransaction.Object);

            storageTransaction.Verify(x => x.ExpireHash(It.IsAny<string>(), It.IsAny<TimeSpan>()), Times.Never);
        }

        [Fact]
        public void OnStateApplied_ReleasesTheFingerprint_WhenTheOwnerReachesAFinalState()
        {
            var attribute = new PreventDuplicatesAttribute();
            var fingerprint = attribute.GetFingerprint(_context.Job);
            var context = new ApplyStateContextMock { NewStateObject = new SucceededState(null, 0, 0) };
            context.BackgroundJob.Id = "owner";
            context.Connection.Setup(x => x.GetJobParameter("owner", PreventDuplicatesAttribute.FingerprintParameter)).Returns(SerializationHelper.Serialize(fingerprint));
            context.Connection.Setup(x => x.GetAllEntriesFromHash(PreventDuplicatesAttribute.GetKey(fingerprint))).Returns(new Dictionary<string, string> { { "JobId", "owner" } });

            attribute.OnStateApplied(context.Object, context.Transaction.Object);

            context.Transaction.Verify(x => x.RemoveHash(PreventDuplicatesAttribute.GetKey(fingerprint)));
        }

        [Fact]
        public void OnStateApplied_KeepsTheFingerprint_WhenAnotherJobOwnsItNow()
        {
            var attribute = new PreventDuplicatesAttribute();
            var fingerprint = attribute.GetFingerprint(_context.Job);
            var context = new ApplyStateContextMock { NewStateObject = new DeletedState() };
            context.BackgroundJob.Id = "old";
            context.Connection.Setup(x => x.GetJobParameter("old", PreventDuplicatesAttribute.FingerprintParameter)).Returns(SerializationHelper.Serialize(fingerprint));
            context.Connection.Setup(x => x.GetAllEntriesFromHash(PreventDuplicatesAttribute.GetKey(fingerprint))).Returns(new Dictionary<string, string> { { "JobId", "newer" } });

            attribute.OnStateApplied(context.Object, context.Transaction.Object);

            context.Transaction.Verify(x => x.RemoveHash(It.IsAny<string>()), Times.Never);
        }

        [Fact]
        public void OnStateApplied_IgnoresNonFinalStates_AndJobsWithoutFingerprint()
        {
            var attribute = new PreventDuplicatesAttribute();
            var context = new ApplyStateContextMock { NewStateObject = new ProcessingState("s", "w") };

            attribute.OnStateApplied(context.Object, context.Transaction.Object);
            context.NewStateObject = new SucceededState(null, 0, 0);
            attribute.OnStateApplied(new ApplyStateContextMock { NewStateObject = new SucceededState(null, 0, 0) }.Object, context.Transaction.Object);

            context.Transaction.Verify(x => x.RemoveHash(It.IsAny<string>()), Times.Never);
        }

        [Fact]
        public void OnStateUnapplied_KeepsTheFingerprint()
        {
            var attribute = new PreventDuplicatesAttribute();
            var context = new ApplyStateContextMock { NewStateObject = new SucceededState(null, 0, 0) };

            attribute.OnStateUnapplied(context.Object, context.Transaction.Object);

            context.Transaction.Verify(x => x.RemoveHash(It.IsAny<string>()), Times.Never);
            context.Connection.Verify(x => x.GetJobParameter(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        }

        [SuppressMessage("Usage", "xUnit1013:Public method should be marked as test")]
        public static void Import(string file, int rows) { }

        [SuppressMessage("Usage", "xUnit1013:Public method should be marked as test")]
        public static void Charge(int amount) { }

        [SuppressMessage("Usage", "xUnit1013:Public method should be marked as test")]
        public static void Charge(long amount) { }
    }
}
