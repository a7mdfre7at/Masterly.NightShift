using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using Masterly.NightShift.Common;
using Masterly.NightShift.Server;
using Masterly.NightShift.States;
using Masterly.NightShift.Storage;
using Moq;
using Xunit;

namespace Masterly.NightShift.Core.Tests.Server
{
    public class ConcurrencyControlFacts
    {
        private readonly PerformContextMock _context = new PerformContextMock();

        [Fact]
        public void FormatResource_ThrowsAnException_WhenJobIsNull()
        {
            var exception = Assert.Throws<ArgumentNullException>(() => ConcurrencyControl.FormatResource("x", null));
            Assert.Equal("job", exception.ParamName);
        }

        [Fact]
        public void FormatResource_ThrowsFormatException_NamingTheTemplate_WhenItRefersToAMissingArgument()
        {
            var job = Job.FromExpression(() => Sample("a"));

            var exception = Assert.Throws<FormatException>(() => ConcurrencyControl.FormatResource("tenant-{3}", job));

            Assert.Contains("`tenant-{3}`", exception.Message);
            Assert.IsType<FormatException>(exception.InnerException);
        }

        [Fact]
        public void FormatResource_FormatsTheTemplate_WithTheJobArguments()
        {
            var job = Job.FromExpression(() => Sample("Contoso"));

            Assert.Equal("tenant-contoso", ConcurrencyControl.FormatResource("tenant-{0}", job));
        }

        [Fact]
        public void FormatResourceKey_ReturnsResourcesThatFit_Unchanged()
        {
            var tenant = new string('a', 91 - "tenant-".Length);
            var job = Job.FromExpression(() => Sample(tenant));

            Assert.Equal("tenant-" + tenant, ConcurrencyControl.FormatResourceKey("tenant-{0}", job, 91));
        }

        [Fact]
        public void FormatResourceKey_ShortensLongResources_ToAPrefixAndAHashOfTheWholeText()
        {
            var first = ConcurrencyControl.FormatResourceKey("tenant-{0}", Job.FromExpression(() => Sample(new string('a', 200) + "1")), 91);
            var same = ConcurrencyControl.FormatResourceKey("tenant-{0}", Job.FromExpression(() => Sample(new string('A', 200) + "1")), 91);
            var other = ConcurrencyControl.FormatResourceKey("tenant-{0}", Job.FromExpression(() => Sample(new string('a', 200) + "2")), 91);

            Assert.Equal(91, first.Length);
            Assert.StartsWith("tenant-aaaa", first, StringComparison.Ordinal);
            Assert.Equal('~', first[91 - 33]);
            Assert.Equal(first, same);
            Assert.NotEqual(first, other);
        }

        [Fact]
        public void FormatResourceKey_LeavesTheDefaultResourceUnchanged()
        {
            var resource = ConcurrencyControl.FormatResourceKey(null, Job.FromExpression(() => Sample(null)), 91);

            Assert.Equal("masterly.nightshift.core.tests.server.concurrencycontrolfacts.sample(system.string)", resource);
        }

        [Fact]
        public void FormatResource_GivesSameNamedTypesInOtherNamespaces_DifferentDefaultResources()
        {
            var billing = ConcurrencyControl.FormatResource(null, Job.FromExpression(() => Billing.ReportJob.Run()));
            var shipping = ConcurrencyControl.FormatResource(null, Job.FromExpression(() => Shipping.ReportJob.Run()));

            Assert.Equal("masterly.nightshift.core.tests.server.billing.reportjob.run()", billing);
            Assert.Equal("masterly.nightshift.core.tests.server.shipping.reportjob.run()", shipping);
        }

        [Fact]
        public void GetMethodIdentity_IncludesTheParameterTypes_SoOverloadsDiffer()
        {
            var byNumber = ConcurrencyControl.GetMethodIdentity(Job.FromExpression(() => Overloaded(1)));
            var byText = ConcurrencyControl.GetMethodIdentity(Job.FromExpression(() => Overloaded("1")));

            Assert.Equal("Masterly.NightShift.Core.Tests.Server.ConcurrencyControlFacts.Overloaded(System.Int32)", byNumber);
            Assert.Equal("Masterly.NightShift.Core.Tests.Server.ConcurrencyControlFacts.Overloaded(System.String)", byText);
        }

        [Fact]
        public void GetMethodIdentity_NamesGenericArguments_NestedTypes_AndArrays_WithTheirNamespaces()
        {
            var identity = ConcurrencyControl.GetMethodIdentity(Job.FromExpression<Billing.Outer<Shipping.ReportJob>.Inner<int>>(
                x => x.Handle<Billing.ReportJob>(null, null)));

            Assert.Equal(
                "Masterly.NightShift.Core.Tests.Server.Billing.Outer<Masterly.NightShift.Core.Tests.Server.Shipping.ReportJob>.Inner<System.Int32>"
                + ".Handle<Masterly.NightShift.Core.Tests.Server.Billing.ReportJob>"
                + "(System.Collections.Generic.List<Masterly.NightShift.Core.Tests.Server.Billing.ReportJob>,System.String[])",
                identity);
        }

        [Fact]
        public void MaxResourceLengths_KeepEveryKey_WithinOneHundredCharacters_AndNoShorter()
        {
            Assert.Equal(91, ThrottleAttribute.MaxResourceLength);
            Assert.Equal(79, RateLimitAttribute.MaxResourceLength);
            Assert.Equal(83, CircuitBreakerAttribute.MaxResourceLength);
            Assert.Equal(88, PreventDuplicatesAttribute.MaxFingerprintLength);

            var longest = ConcurrencyControl.ShortenResource(new string('x', 500), CircuitBreakerAttribute.MaxResourceLength);
            Assert.Equal(100, $"circuit:{longest}:failures".Length);

            // The window is part of the rate limit key; the longest window has ten digits
            var longestRateLimit = ConcurrencyControl.ShortenResource(new string('x', 500), RateLimitAttribute.MaxResourceLength);
            Assert.Equal(100, $"ratelimit:{longestRateLimit}:{Int32.MaxValue}".Length);
        }

        [Theory]
        [InlineData(65)]
        [InlineData(91)]
        public void ThrottleResources_ThatFitTheirKey_KeepTheirExactName(int length)
        {
            var resource = new string('r', length);

            Assert.Equal(resource, ConcurrencyControl.ShortenResource(resource, ThrottleAttribute.MaxResourceLength));
        }

        [Fact]
        public void ShortenResource_DoesNotCutASurrogatePairInTwo()
        {
            // Every character is half of a surrogate pair, so the cut must move to keep pairs whole
            var resource = String.Concat(Enumerable.Repeat("\U0001F600", 100));

            var shortened = ConcurrencyControl.ShortenResource(resource, 64);

            Assert.False(Char.IsHighSurrogate(shortened[shortened.IndexOf('~') - 1]));
            Assert.True(shortened.Length <= 64);
        }

        [Fact]
        public void GetStateChanger_ReturnsTheWorkersStateChanger_FromTheItems()
        {
            var stateChanger = new Mock<IBackgroundJobStateChanger>().Object;
            _context.Object.Items[ConcurrencyControl.StateChangerItem] = stateChanger;

            Assert.Same(stateChanger, ConcurrencyControl.GetStateChanger(_context.Object));
        }

        [Fact, StaticLock]
        public void GetStateChanger_FallsBackToTheDefault_OutsideAWorker()
        {
            Assert.Same(ConcurrencyControl.StateChanger, ConcurrencyControl.GetStateChanger(_context.Object));
        }

        [Fact]
        public void Reschedule_UsesTheWorkersStateChanger_AndTellsTheWorkerTheJobLeftProcessing()
        {
            var stateChanger = new Mock<IBackgroundJobStateChanger>();
            var performing = _context.GetPerformingContext();
            performing.Items[ConcurrencyControl.StateChangerItem] = stateChanger.Object;

            ConcurrencyControl.Reschedule(performing, new DateTime(2026, 11, 4, 12, 0, 0, DateTimeKind.Utc), "busy");

            stateChanger.Verify(x => x.ChangeState(It.Is<StateChangeContext>(c =>
                c.NewState is ScheduledState && c.ExpectedStates != null && c.ExpectedStates.Contains(ProcessingState.StateName))));
            Assert.True(performing.Canceled);
            Assert.True(performing.Items.ContainsKey(ConcurrencyControl.RescheduledItem));
        }

        [Fact]
        public void Reschedule_TriesAgain_AfterATransientStorageError_InsteadOfFailingTheRun()
        {
            var stateChanger = new Mock<IBackgroundJobStateChanger>();
            var attempts = 0;
            stateChanger.Setup(x => x.ChangeState(It.IsAny<StateChangeContext>())).Returns(() =>
            {
                if (++attempts == 1) throw new InvalidOperationException("deadlock victim");
                return new ScheduledState(DateTime.UtcNow);
            });
            _context.CancellationToken.Setup(x => x.ShutdownToken).Returns(CancellationToken.None);
            var performing = _context.GetPerformingContext();
            performing.Items[ConcurrencyControl.StateChangerItem] = stateChanger.Object;

            ConcurrencyControl.Reschedule(performing, new DateTime(2026, 11, 4, 12, 0, 0, DateTimeKind.Utc), "busy");

            Assert.Equal(2, attempts);
            Assert.True(performing.Canceled);
            Assert.True(performing.Items.ContainsKey(ConcurrencyControl.RescheduledItem));
        }

        [Fact]
        public void Reschedule_LeavesTheJobAlone_WhenAnAttemptThatReportedAnError_HadCommitted()
        {
            // The second attempt expects Processing, finds the job already Scheduled, and changes nothing
            var stateChanger = new Mock<IBackgroundJobStateChanger>();
            var attempts = 0;
            stateChanger.Setup(x => x.ChangeState(It.Is<StateChangeContext>(c => c.ExpectedStates.Contains(ProcessingState.StateName)))).Returns(() =>
            {
                if (++attempts == 1) throw new TimeoutException("the commit's answer was lost");
                return null;
            });
            _context.CancellationToken.Setup(x => x.ShutdownToken).Returns(CancellationToken.None);
            var performing = _context.GetPerformingContext();
            performing.Items[ConcurrencyControl.StateChangerItem] = stateChanger.Object;

            ConcurrencyControl.Reschedule(performing, new DateTime(2026, 11, 4, 12, 0, 0, DateTimeKind.Utc), "busy");

            Assert.Equal(2, attempts);
            Assert.True(performing.Canceled);
            Assert.True(performing.Items.ContainsKey(ConcurrencyControl.RescheduledItem));
        }

        [Fact]
        public void Reschedule_ThrowsTheLastError_AfterThreeFailedAttempts()
        {
            var stateChanger = new Mock<IBackgroundJobStateChanger>();
            stateChanger.Setup(x => x.ChangeState(It.IsAny<StateChangeContext>())).Throws(new InvalidOperationException("storage is down"));
            _context.CancellationToken.Setup(x => x.ShutdownToken).Returns(CancellationToken.None);
            var performing = _context.GetPerformingContext();
            performing.Items[ConcurrencyControl.StateChangerItem] = stateChanger.Object;

            Assert.Throws<InvalidOperationException>(() => ConcurrencyControl.Reschedule(performing, DateTime.UtcNow, "busy"));

            stateChanger.Verify(x => x.ChangeState(It.IsAny<StateChangeContext>()), Times.Exactly(3));
            Assert.False(performing.Canceled);
            Assert.False(performing.Items.ContainsKey(ConcurrencyControl.RescheduledItem));
        }

        [Fact]
        public void Reschedule_StopsTrying_WhenShutdownIsRequested()
        {
            // The worker requeues the job when the run ends with OperationCanceledException during shutdown
            var stateChanger = new Mock<IBackgroundJobStateChanger>();
            stateChanger.Setup(x => x.ChangeState(It.IsAny<StateChangeContext>())).Throws(new InvalidOperationException("storage is down"));
            _context.CancellationToken.Setup(x => x.ShutdownToken).Returns(new CancellationToken(true));
            var performing = _context.GetPerformingContext();
            performing.Items[ConcurrencyControl.StateChangerItem] = stateChanger.Object;

            Assert.Throws<OperationCanceledException>(() => ConcurrencyControl.Reschedule(performing, DateTime.UtcNow, "busy"));

            stateChanger.Verify(x => x.ChangeState(It.IsAny<StateChangeContext>()), Times.Once);
        }

        [Fact]
        public void GetExpiringTransaction_ReturnsTheTransaction_OnlyWhenTheStorageSupportsTheExtendedApi()
        {
            var transaction = new Mock<JobStorageTransaction>().Object;
            var storage = new Mock<JobStorage>();

            Assert.Null(ConcurrencyControl.GetExpiringTransaction(storage.Object, transaction));
            Assert.Null(ConcurrencyControl.GetExpiringTransaction(null, transaction));

            storage.Setup(x => x.HasFeature(JobStorageFeatures.ExtendedApi)).Returns(true);

            Assert.Same(transaction, ConcurrencyControl.GetExpiringTransaction(storage.Object, transaction));
            Assert.Null(ConcurrencyControl.GetExpiringTransaction(storage.Object, new Mock<IWriteOnlyTransaction>().Object));
        }

        [Fact]
        public void Wait_AcceptsDelaysLongerThanAWaitHandleDoes()
        {
            _context.CancellationToken.Setup(x => x.ShutdownToken).Returns(new CancellationToken(true));

            // WaitOne throws ArgumentOutOfRangeException above about 24.8 days
            Assert.Throws<OperationCanceledException>(() => ConcurrencyControl.Wait(_context.Object, TimeSpan.FromDays(40)));
        }

        [Fact]
        public void Wait_ThrowsOperationCanceled_WithoutWaiting_WhenShutdownIsRequested()
        {
            _context.CancellationToken.Setup(x => x.ShutdownToken).Returns(new CancellationToken(true));

            Assert.Throws<OperationCanceledException>(() => ConcurrencyControl.Wait(_context.Object, TimeSpan.FromMinutes(5)));
        }

        [Fact]
        public void Wait_ReturnsAfterTheDelay_WhenShutdownIsNotRequested()
        {
            _context.CancellationToken.Setup(x => x.ShutdownToken).Returns(CancellationToken.None);

            ConcurrencyControl.Wait(_context.Object, TimeSpan.FromMilliseconds(1));

            _context.CancellationToken.VerifyGet(x => x.ShutdownToken);
        }

        [Fact]
        public void Skip_CancelsThePerformance_AndRecordsTheReason()
        {
            var performing = _context.GetPerformingContext();

            ConcurrencyControl.Skip(performing, "busy");

            Assert.True(performing.Canceled);
            Assert.Equal("busy", performing.Items[BackgroundJobPerformer.ContextCanceledKey]);
        }

        [SuppressMessage("Usage", "xUnit1013:Public method should be marked as test")]
        public static void Sample(string tenant)
        {
        }

        [SuppressMessage("Usage", "xUnit1013:Public method should be marked as test")]
        public static void Overloaded(int value)
        {
        }

        [SuppressMessage("Usage", "xUnit1013:Public method should be marked as test")]
        public static void Overloaded(string value)
        {
        }
    }
}

namespace Masterly.NightShift.Core.Tests.Server.Billing
{
    public class ReportJob
    {
        public static void Run()
        {
        }
    }

    public class Outer<T>
    {
        public class Inner<TValue>
        {
            public void Handle<TItem>(List<TItem> items, string[] names)
            {
            }
        }
    }
}

namespace Masterly.NightShift.Core.Tests.Server.Shipping
{
    public class ReportJob
    {
        public static void Run()
        {
        }
    }
}
