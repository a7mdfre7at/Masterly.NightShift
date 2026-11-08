using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Masterly.NightShift.Common;
using Moq;
using Xunit;

namespace Masterly.NightShift.Core.Tests
{
    public class DisableConcurrentExecutionAttributeFacts
    {
        private const int TimeoutInSeconds = 5;

        private readonly PerformContextMock _context;
        private readonly Mock<IDisposable> _distributedLock;

        public DisableConcurrentExecutionAttributeFacts()
        {
            _context = new PerformContextMock();
            _context.BackgroundJob.Job = Job.FromExpression(() => Sample());

            _distributedLock = new Mock<IDisposable>();
            _context.Connection
                .Setup(x => x.AcquireDistributedLock(It.IsAny<string>(), It.IsAny<TimeSpan>()))
                .Returns(_distributedLock.Object);
        }

        [Fact]
        public void Ctor_ThrowsAnException_WhenTimeoutInSecondsIsNegative()
        {
            var exception = Assert.Throws<ArgumentException>(
                () => new DisableConcurrentExecutionAttribute(-1));

            Assert.Equal("timeoutInSeconds", exception.ParamName);
            Assert.Contains("greater than or equal to zero", exception.Message);
        }

        [Fact]
        public void Ctor_AcceptsAZeroTimeout()
        {
            Assert.Equal(0, new DisableConcurrentExecutionAttribute(0).TimeoutSec);
        }

        [Fact]
        public void OnPerforming_UsesAHash_ForARedactedArgument_InTheLockName()
        {
            _context.BackgroundJob.Job = Job.FromExpression(() => SampleWithSecret("Tenant", "s3cret"));
            var attribute = new DisableConcurrentExecutionAttribute("lock-{0}-{1}", TimeoutInSeconds);

            attribute.OnPerforming(_context.GetPerformingContext());

            _context.Connection.Verify(x => x.AcquireDistributedLock(
                $"lock-tenant-{RedactedAttribute.Hash("s3cret")}",
                TimeSpan.FromSeconds(TimeoutInSeconds)));
        }

        [Fact]
        public void Ctor_CorrectlySets_TheTimeoutSecProperty()
        {
            var attribute = new DisableConcurrentExecutionAttribute(TimeoutInSeconds);

            Assert.Equal(TimeoutInSeconds, attribute.TimeoutSec);
        }

        [Fact]
        public void Ctor_CorrectlySets_BothResourceAndTimeoutSecProperties()
        {
            var attribute = new DisableConcurrentExecutionAttribute("my-resource", TimeoutInSeconds);

            Assert.Equal("my-resource", attribute.Resource);
            Assert.Equal(TimeoutInSeconds, attribute.TimeoutSec);
        }

        [Fact]
        public void OnPerforming_AcquiresDistributedLock_BasedOnJobTypeAndMethodName()
        {
            var attribute = CreateAttribute();

            attribute.OnPerforming(_context.GetPerformingContext());

            _context.Connection.Verify(x => x.AcquireDistributedLock(
                "DisableConcurrentExecutionAttributeFacts.Sample",
                TimeSpan.FromSeconds(TimeoutInSeconds)));
        }

        [Fact]
        public void OnPerforming_AcquiresDistributedLock_OnTheLowerCasedFormattedResource_WhenResourceIsSpecified()
        {
            _context.BackgroundJob.Job = Job.FromExpression(() => Sample("Some-Value"));
            var attribute = new DisableConcurrentExecutionAttribute("Prefix-{0}", TimeoutInSeconds);

            attribute.OnPerforming(_context.GetPerformingContext());

            _context.Connection.Verify(x => x.AcquireDistributedLock(
                "prefix-some-value",
                TimeSpan.FromSeconds(TimeoutInSeconds)));
        }

        [Fact]
        public void OnPerforming_ThrowsFormatException_WhenResourceFormatIsInvalid()
        {
            var attribute = new DisableConcurrentExecutionAttribute("resource-{1}", TimeoutInSeconds);

            Assert.Throws<FormatException>(
                () => attribute.OnPerforming(_context.GetPerformingContext()));
        }

        [Fact]
        public void OnPerforming_StoresAcquiredDistributedLock_InTheItemsCollection()
        {
            var attribute = CreateAttribute();
            var performingContext = _context.GetPerformingContext();

            attribute.OnPerforming(performingContext);

            Assert.Same(_distributedLock.Object, performingContext.Items["DistributedLock"]);
        }

        [Fact]
        public void OnPerformed_ReleasesTheAcquiredDistributedLock()
        {
            var attribute = CreateAttribute();
            attribute.OnPerforming(_context.GetPerformingContext());

            attribute.OnPerformed(_context.GetPerformedContext());

            _distributedLock.Verify(x => x.Dispose());
        }

        [Fact]
        public void OnPerforming_ShortensALongLockName_SoResourcesThatStartAlikeGetDifferentLocks()
        {
            // SQL Server cuts lock names at 255 characters, its schema included
            var prefix = new string('k', 260);
            var attribute = new DisableConcurrentExecutionAttribute("{0}", TimeoutInSeconds);
            var names = new List<string>();

            foreach (var argument in new[] { prefix + "a", prefix + "b" })
            {
                var context = new PerformContextMock();
                context.BackgroundJob.Job = Job.FromExpression(() => Sample(argument));
                context.Connection
                    .Setup(x => x.AcquireDistributedLock(It.IsAny<string>(), It.IsAny<TimeSpan>()))
                    .Callback<string, TimeSpan>((name, _) => names.Add(name))
                    .Returns(_distributedLock.Object);

                attribute.OnPerforming(context.GetPerformingContext());
            }

            Assert.Equal(2, names.Count);
            Assert.NotEqual(names[0], names[1]);
            Assert.All(names, static name => Assert.Equal(200, name.Length));
            Assert.All(names, static name => Assert.StartsWith("kkkk", name, StringComparison.Ordinal));
            Assert.All(names, static name => Assert.Contains("~", name, StringComparison.Ordinal));
        }

        [Fact]
        public void OnPerforming_KeepsALockNameOf200Characters_AsItIs()
        {
            var resource = new string('r', 200);
            _context.BackgroundJob.Job = Job.FromExpression(() => Sample(resource));
            var attribute = new DisableConcurrentExecutionAttribute("{0}", TimeoutInSeconds);

            attribute.OnPerforming(_context.GetPerformingContext());

            _context.Connection.Verify(x => x.AcquireDistributedLock(resource, TimeSpan.FromSeconds(TimeoutInSeconds)));
        }

        [Fact]
        public void OnPerformed_DoesNotThrow_WhenTheLockCannotBeReleased()
        {
            _distributedLock.Setup(x => x.Dispose()).Throws(new InvalidOperationException("connection is broken"));
            var attribute = CreateAttribute();
            attribute.OnPerforming(_context.GetPerformingContext());

            attribute.OnPerformed(_context.GetPerformedContext());

            _distributedLock.Verify(x => x.Dispose());
        }

        [Fact]
        public void OnPerformed_ThrowsAnException_WhenDistributedLockWasNotAcquired()
        {
            var attribute = CreateAttribute();

            Assert.Throws<InvalidOperationException>(
                () => attribute.OnPerformed(_context.GetPerformedContext()));
        }

        private static DisableConcurrentExecutionAttribute CreateAttribute()
        {
            return new DisableConcurrentExecutionAttribute(TimeoutInSeconds);
        }

        [SuppressMessage("Usage", "xUnit1013:Public method should be marked as test")]
        [SuppressMessage("ReSharper", "MemberCanBePrivate.Global")]
        public static void Sample()
        {
        }

        [SuppressMessage("Usage", "xUnit1013:Public method should be marked as test")]
        [SuppressMessage("ReSharper", "MemberCanBePrivate.Global")]
        public static void Sample(string value)
        {
        }

        [SuppressMessage("Usage", "xUnit1013:Public method should be marked as test")]
        public static void SampleWithSecret(string tenant, [Redacted] string secret)
        {
        }
    }
}
