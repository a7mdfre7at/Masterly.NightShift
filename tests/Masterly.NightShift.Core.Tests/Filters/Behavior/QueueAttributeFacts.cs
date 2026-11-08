using System;
using System.Diagnostics.CodeAnalysis;
using Masterly.NightShift.Common;
using Masterly.NightShift.States;
using Moq;
using Xunit;

namespace Masterly.NightShift.Core.Tests
{
    public class QueueAttributeFacts
    {
        private readonly ElectStateContextMock _context;

        public QueueAttributeFacts()
        {
            _context = new ElectStateContextMock
            {
                ApplyContext = { NewStateObject = new EnqueuedState("queue") }
            };
        }

        [Fact]
        public void Ctor_CorrectlySets_AllPropertyValues()
        {
            var filter = new QueueAttribute("hello");
            Assert.Equal("hello", filter.Queue);
        }

        [Theory]
        [InlineData("Critical")]
        [InlineData("my queue")]
        [InlineData("reports.eu")]
        public void Ctor_ThrowsAnArgumentException_ForAnInvalidQueueName(string queue)
        {
            var exception = Assert.Throws<ArgumentException>(() => new QueueAttribute(queue));

            Assert.Equal("queue", exception.ParamName);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("  ")]
        public void Ctor_ThrowsAnArgumentNullException_ForAMissingQueueName(string queue)
        {
            var exception = Assert.Throws<ArgumentNullException>(() => new QueueAttribute(queue));

            Assert.Equal("queue", exception.ParamName);
        }

        [Fact]
        public void Ctor_AcceptsATemplate_AndLeavesItsCheckForLater()
        {
            var filter = new QueueAttribute("reports-{0}");

            Assert.Equal("reports-{0}", filter.Queue);
        }

        [Fact]
        public void OnStateElection_OverridesTheQueue_OfTheCandidateState()
        {
            var filter = new QueueAttribute("override");
            filter.OnStateElection(_context.Object);

            Assert.Equal("override", ((EnqueuedState)_context.Object.CandidateState).Queue);
        }

        [Fact]
        public void OnStateElection_DoesNotDoAnything_IfStateIsNotEnqueuedState()
        {
            var filter = new QueueAttribute("override");
            var context = new ElectStateContextMock
            {
                ApplyContext = { NewState = new Mock<IState>() }
            };

            // Does not throw
            filter.OnStateElection(context.Object);
        }

        [Fact]
        public void OnStateElection_FormatsATemplate_WithTheJobArguments()
        {
            _context.ApplyContext.BackgroundJob.Job = Job.FromExpression(() => Build("eu", 7));

            new QueueAttribute("reports-{0}").OnStateElection(_context.Object);

            Assert.Equal("reports-eu", ((EnqueuedState)_context.Object.CandidateState).Queue);
        }

        [Fact]
        public void OnStateElection_ThrowsAClearException_WhenATemplateProducesAnInvalidQueueName()
        {
            _context.ApplyContext.BackgroundJob.Job = Job.FromExpression(() => Build("EU", 7));

            var exception = Assert.Throws<InvalidOperationException>(
                () => new QueueAttribute("reports-{0}").OnStateElection(_context.Object));

            Assert.Contains("'reports-{0}'", exception.Message);
            Assert.Contains("'reports-EU'", exception.Message);
        }

        [Fact]
        public void OnStateElection_ThrowsAFormatException_WhenATemplateRefersToAMissingArgument()
        {
            _context.ApplyContext.BackgroundJob.Job = Job.FromExpression(() => Build("eu", 7));

            var exception = Assert.Throws<FormatException>(
                () => new QueueAttribute("reports-{5}").OnStateElection(_context.Object));

            Assert.Contains("'reports-{5}'", exception.Message);
        }

        [Fact]
        public void OnStateElection_UsesTheRealValue_OfARedactedArgumentInATemplate()
        {
            // Servers listen to real queue names, so a hash would route the job to a queue nobody serves
            _context.ApplyContext.BackgroundJob.Job = Job.FromExpression(() => Import("tenant-key", 1));

            new QueueAttribute("import-{0}").OnStateElection(_context.Object);

            Assert.Equal("import-tenant-key", ((EnqueuedState)_context.Object.CandidateState).Queue);
        }

        [SuppressMessage("Usage", "xUnit1013:Public method should be marked as test")]
        public static void Build(string region, int reportId)
        {
        }

        [SuppressMessage("Usage", "xUnit1013:Public method should be marked as test")]
        public static void Import([Redacted] string apiKey, int batch)
        {
        }
    }
}
