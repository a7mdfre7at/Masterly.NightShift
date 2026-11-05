using System;
using System.Collections.Generic;
using Masterly.NightShift.Common;
using Masterly.NightShift.States;
using Masterly.NightShift.Storage;
using Moq;
using System.Text.Json.Nodes;
using Xunit;

namespace Masterly.NightShift.Core.Tests
{
    public class ActivityFeedFacts : IDisposable
    {
        private readonly DateTime _now = new DateTime(2026, 11, 4, 12, 0, 0, DateTimeKind.Utc);

        public ActivityFeedFacts()
        {
            ActivityFeed.Clock = () => _now;
            ActivityFeed.Enabled = true;
            ActivityFeed.MaxEntries = 200;
        }

        public void Dispose()
        {
            ActivityFeed.Clock = () => DateTime.UtcNow;
            ActivityFeed.Enabled = true;
            ActivityFeed.MaxEntries = 200;
        }

        [Fact, StaticLock]
        public void Filter_InsertsAnEntry_AndTrimsTheList()
        {
            var context = new ApplyStateContextMock { NewStateObject = new ProcessingState("server-1", "worker"), OldStateName = "Enqueued" };
            context.BackgroundJob.Id = "42";
            string inserted = null;
            context.Transaction.Setup(x => x.InsertToList(ActivityFeed.ListKey, It.IsAny<string>())).Callback<string, string>((_, value) => inserted = value);

            new ActivityFeedFilter().OnStateApplied(context.Object, context.Transaction.Object);

            var entry = JsonNode.Parse(inserted);
            Assert.Equal("42", entry["j"].GetValue<string>());
            Assert.Equal("Processing", entry["s"].GetValue<string>());
            Assert.Equal("Enqueued", entry["o"].GetValue<string>());
            Assert.Equal("server-1", entry["v"].GetValue<string>());
            Assert.Equal("BackgroundJobMock.SomeMethod", entry["m"].GetValue<string>());
            Assert.Equal(JobHelper.ToTimestamp(_now) * 1000, entry["t"].GetValue<long>());
            context.Transaction.Verify(x => x.TrimList(ActivityFeed.ListKey, 0, 199));
        }

        [Fact, StaticLock]
        public void Filter_OnStateUnapplied_WritesNothing()
        {
            var context = new ApplyStateContextMock { NewStateObject = new ProcessingState("server-1", "worker"), OldStateName = "Enqueued" };

            new ActivityFeedFilter().OnStateUnapplied(context.Object, context.Transaction.Object);

            context.Transaction.VerifyNoOtherCalls();
        }

        [Fact, StaticLock]
        public void Filter_RecordsTheReason_AndSkipsEmptyFields()
        {
            var context = new ApplyStateContextMock { NewStateObject = new DeletedState { Reason = "Held" } };
            context.BackgroundJob.Job = null;
            string inserted = null;
            context.Transaction.Setup(x => x.InsertToList(ActivityFeed.ListKey, It.IsAny<string>())).Callback<string, string>((_, value) => inserted = value);

            new ActivityFeedFilter().OnStateApplied(context.Object, context.Transaction.Object);

            var entry = JsonNode.Parse(inserted);
            Assert.Equal("Held", entry["r"].GetValue<string>());
            Assert.Null(entry["m"]);
            Assert.Null(entry["o"]);
            Assert.Null(entry["v"]);
        }

        [Fact, StaticLock]
        public void Filter_DoesNothing_WhenDisabled()
        {
            ActivityFeed.Enabled = false;
            var context = new ApplyStateContextMock { NewStateObject = new EnqueuedState() };

            new ActivityFeedFilter().OnStateApplied(context.Object, context.Transaction.Object);

            context.Transaction.Verify(x => x.InsertToList(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        }

        [Fact, StaticLock]
        public void Filter_UsesTheConfiguredCapacity()
        {
            ActivityFeed.MaxEntries = 50;
            var context = new ApplyStateContextMock { NewStateObject = new EnqueuedState() };

            new ActivityFeedFilter().OnStateApplied(context.Object, context.Transaction.Object);

            context.Transaction.Verify(x => x.TrimList(ActivityFeed.ListKey, 0, 49));
        }

        [Fact]
        public void Read_ReturnsEntries_NewestFirst_SkippingGarbage()
        {
            var connection = new Mock<JobStorageConnection>();
            connection.Setup(x => x.GetRangeFromList(ActivityFeed.ListKey, 0, 2)).Returns(new List<string>
            {
                "{\"t\":3,\"j\":\"c\",\"s\":\"Succeeded\"}",
                "not json",
                "{\"t\":1,\"j\":\"a\",\"s\":\"Enqueued\",\"m\":\"X.Y\"}",
            });

            var entries = ActivityFeed.Read(connection.Object, 3);

            Assert.Equal(2, entries.Count);
            Assert.Equal("c", entries[0].JobId);
            Assert.Equal("X.Y", entries[1].Method);
        }

        [Fact]
        public void Read_ReturnsNothing_ForPlainConnections_OrZeroCount()
        {
            Assert.Empty(ActivityFeed.Read(new Mock<IStorageConnection>().Object, 10));
            Assert.Empty(ActivityFeed.Read(new Mock<JobStorageConnection>().Object, 0));
        }
    }
}
