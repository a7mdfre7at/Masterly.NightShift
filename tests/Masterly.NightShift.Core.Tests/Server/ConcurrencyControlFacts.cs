using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using Masterly.NightShift.Common;
using Masterly.NightShift.Server;
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
    }
}
