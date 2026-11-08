using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Masterly.NightShift.Common;
using Masterly.NightShift.Logging;
using Masterly.NightShift.States;
using Masterly.NightShift.Storage;
using Moq;
using Xunit;

namespace Masterly.NightShift.Core.Tests
{
    public class ActivityFeedFacts : IDisposable
    {
        private readonly DateTime _now = new DateTime(2026, 11, 4, 12, 0, 0, DateTimeKind.Utc);
        private readonly Mock<JobStorageConnection> _feedConnection = new Mock<JobStorageConnection>();
        private readonly Mock<IWriteOnlyTransaction> _feedTransaction = new Mock<IWriteOnlyTransaction>();
        private readonly List<string> _inserted = new List<string>();
        private readonly List<Action> _scheduled = new List<Action>();

        public ActivityFeedFacts()
        {
            ActivityFeed.Clock = () => _now;
            ActivityFeed.Enabled = true;
            ActivityFeed.MaxEntries = 200;
            ActivityFeedWriter.Schedule = static flush => flush();

            _feedConnection.Setup(x => x.CreateWriteTransaction()).Returns(_feedTransaction.Object);
            _feedTransaction.Setup(x => x.InsertToList(ActivityFeed.ListKey, It.IsAny<string>())).Callback<string, string>((_, value) => _inserted.Add(value));
        }

        public void Dispose()
        {
            ActivityFeed.Clock = () => DateTime.UtcNow;
            ActivityFeed.Enabled = false;
            ActivityFeed.MaxEntries = 1000;
            ActivityFeedWriter.Schedule = static flush => System.Threading.ThreadPool.QueueUserWorkItem(static state => ((Action)state)(), flush);
        }

        private ApplyStateContextMock CreateContext(IState state, string oldStateName = null)
        {
            var context = new ApplyStateContextMock { NewStateObject = state, OldStateName = oldStateName };
            context.Storage.Setup(x => x.GetConnection()).Returns(_feedConnection.Object);
            return context;
        }

        [Fact, StaticLock]
        public void Filter_WritesAnEntry_InATransactionOfItsOwn_NeverInTheStateTransaction()
        {
            var context = CreateContext(new ProcessingState("server-1", "worker"), "Enqueued");
            context.BackgroundJob.Id = "42";

            new ActivityFeedFilter().OnStateApplied(context.Object, context.Transaction.Object);

            var entry = JsonNode.Parse(Assert.Single(_inserted));
            Assert.Equal("42", entry["j"].GetValue<string>());
            Assert.Equal("Processing", entry["s"].GetValue<string>());
            Assert.Equal("Enqueued", entry["o"].GetValue<string>());
            Assert.Equal("server-1", entry["v"].GetValue<string>());
            Assert.Equal("BackgroundJobMock.SomeMethod", entry["m"].GetValue<string>());
            Assert.Equal(JobHelper.ToTimestamp(_now) * 1000, entry["t"].GetValue<long>());
            _feedTransaction.Verify(x => x.Commit());
            context.Transaction.VerifyNoOtherCalls();
        }

        [Fact, StaticLock]
        public void Filter_WaitsForTheCommit_WhenTheStateChangeHasPostCommitActions()
        {
            var context = CreateContext(new EnqueuedState());
            var afterCommit = new PostCommitActions();
            context.Object.AfterCommit = afterCommit;

            new ActivityFeedFilter().OnStateApplied(context.Object, context.Transaction.Object);

            Assert.Empty(_inserted);

            afterCommit.Run();

            Assert.Single(_inserted);
        }

        [Fact, StaticLock]
        public void Filter_DoesNotFailTheStateChange_WhenTheStorageFails_AndLogsAWarning()
        {
            var provider = new RecordingLogProvider();
            var context = CreateContext(new EnqueuedState());
            _feedTransaction.Setup(x => x.Commit()).Throws(new InvalidOperationException("deadlock victim"));

            using (provider.Use())
            {
                new ActivityFeedFilter().OnStateApplied(context.Object, context.Transaction.Object);
            }

            Assert.Contains(provider.Entries, e => e.Level == LogLevel.Warn && e.Exception is InvalidOperationException);
        }

        [Fact, StaticLock]
        public void Filter_OnStateUnapplied_WritesNothing()
        {
            var context = CreateContext(new ProcessingState("server-1", "worker"), "Enqueued");

            new ActivityFeedFilter().OnStateUnapplied(context.Object, context.Transaction.Object);

            context.Transaction.VerifyNoOtherCalls();
            Assert.Empty(_inserted);
        }

        [Fact, StaticLock]
        public void Filter_RecordsTheReason_AndSkipsEmptyFields()
        {
            var context = CreateContext(new DeletedState { Reason = "Held" });
            context.BackgroundJob.Job = null;

            new ActivityFeedFilter().OnStateApplied(context.Object, context.Transaction.Object);

            var entry = JsonNode.Parse(Assert.Single(_inserted));
            Assert.Equal("Held", entry["r"].GetValue<string>());
            Assert.Null(entry["m"]);
            Assert.Null(entry["o"]);
            Assert.Null(entry["v"]);
        }

        [Fact, StaticLock]
        public void Filter_DoesNothing_WhenDisabled()
        {
            ActivityFeed.Enabled = false;
            var context = CreateContext(new EnqueuedState());

            new ActivityFeedFilter().OnStateApplied(context.Object, context.Transaction.Object);

            Assert.Empty(_inserted);
            context.Storage.Verify(x => x.GetConnection(), Times.Never);
        }

        [Fact, StaticLock]
        public void Filter_WritesEntries_WithItsOwnSerializerOptions_NotTheUserOnes()
        {
            // A source-generated context that doesn't know ActivityEntry, like an app's own JsonSerializerContext
            SerializationHelper.SetUserSerializerSettings(new JsonSerializerOptions { TypeInfoResolver = new EmptyResolver() });
            try
            {
                var context = CreateContext(new EnqueuedState());

                new ActivityFeedFilter().OnStateApplied(context.Object, context.Transaction.Object);

                Assert.Equal("Enqueued", JsonNode.Parse(Assert.Single(_inserted))["s"].GetValue<string>());
            }
            finally
            {
                SerializationHelper.SetUserSerializerSettings(null);
            }
        }

        [Fact, StaticLock]
        public void Writer_TrimsOnItsFirstWrite_ThenOnceEveryTrimIntervalEntries()
        {
            ActivityFeed.MaxEntries = 50;
            var storage = new Mock<JobStorage>();
            storage.Setup(x => x.GetConnection()).Returns(_feedConnection.Object);
            var writer = ActivityFeedWriter.For(storage.Object);

            for (var i = 0; i < ActivityFeedWriter.TrimInterval + 1; i++)
            {
                writer.Add($"{{\"t\":{i}}}");
            }

            // Writes 1 and TrimInterval + 1 trimmed
            _feedTransaction.Verify(x => x.TrimList(ActivityFeed.ListKey, 0, 49), Times.Exactly(2));
            _feedTransaction.Verify(x => x.Commit(), Times.Exactly(ActivityFeedWriter.TrimInterval + 1));
        }

        [Fact, StaticLock]
        public void Writer_WritesEveryQueuedEntry_InOrder_InOneTransaction()
        {
            ActivityFeedWriter.Schedule = flush => _scheduled.Add(flush);
            var storage = new Mock<JobStorage>();
            storage.Setup(x => x.GetConnection()).Returns(_feedConnection.Object);
            var writer = ActivityFeedWriter.For(storage.Object);

            writer.Add("1");
            writer.Add("2");
            writer.Add("3");

            var flush = Assert.Single(_scheduled);
            flush();

            Assert.Equal(new[] { "1", "2", "3" }, _inserted);
            _feedTransaction.Verify(x => x.Commit(), Times.Once);
            storage.Verify(x => x.GetConnection(), Times.Once);
        }

        [Fact, StaticLock]
        public void Writer_StartsANewFlush_AfterTheLastOneFinished()
        {
            ActivityFeedWriter.Schedule = flush => _scheduled.Add(flush);
            var storage = new Mock<JobStorage>();
            storage.Setup(x => x.GetConnection()).Returns(_feedConnection.Object);
            var writer = ActivityFeedWriter.For(storage.Object);

            writer.Add("1");
            _scheduled.Single()();
            writer.Add("2");

            Assert.Equal(2, _scheduled.Count);
            _scheduled[1]();
            Assert.Equal(new[] { "1", "2" }, _inserted);
        }

        [Fact, StaticLock]
        public void Writer_DropsEntries_BeyondTheLimit_AndWarnsOnce()
        {
            ActivityFeedWriter.Schedule = flush => _scheduled.Add(flush);
            var provider = new RecordingLogProvider();
            var storage = new Mock<JobStorage>();
            storage.Setup(x => x.GetConnection()).Returns(_feedConnection.Object);
            var writer = ActivityFeedWriter.For(storage.Object);

            using (provider.Use())
            {
                for (var i = 0; i < ActivityFeedWriter.MaxPending + 5; i++)
                {
                    writer.Add("x");
                }
            }

            _scheduled.Single()();

            Assert.Equal(ActivityFeedWriter.MaxPending, _inserted.Count);
            Assert.Single(provider.Entries, e => e.Level == LogLevel.Warn && e.Message.Contains("dropping"));
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

        [Fact, StaticLock]
        public void Read_FallsBackToTheUserSerializerSettings_ForEntriesWrittenWithThem()
        {
            // An entry written by an earlier build with the user's settings, which wrote numbers in hex
            SerializationHelper.SetUserSerializerSettings(new JsonSerializerOptions { Converters = { new HexInt64Converter() } });
            try
            {
                var connection = new Mock<JobStorageConnection>();
                connection.Setup(x => x.GetRangeFromList(ActivityFeed.ListKey, 0, 0)).Returns(new List<string> { "{\"t\":\"0x10\",\"j\":\"a\",\"s\":\"Enqueued\"}" });

                var entry = Assert.Single(ActivityFeed.Read(connection.Object, 1));

                Assert.Equal(16, entry.Timestamp);
            }
            finally
            {
                SerializationHelper.SetUserSerializerSettings(null);
            }
        }

        [Fact]
        public void Read_ReturnsNothing_ForPlainConnections_OrZeroCount()
        {
            Assert.Empty(ActivityFeed.Read(new Mock<IStorageConnection>().Object, 10));
            Assert.Empty(ActivityFeed.Read(new Mock<JobStorageConnection>().Object, 0));
        }

        [Fact]
        public void Read_ReturnsNothing_WhenTheStorageCannotReadLists()
        {
            var connection = new Mock<JobStorageConnection> { CallBase = true };

            Assert.Empty(ActivityFeed.Read(connection.Object, 10));
        }

        private sealed class EmptyResolver : IJsonTypeInfoResolver
        {
            public JsonTypeInfo GetTypeInfo(Type type, JsonSerializerOptions options) => null;
        }

        private sealed class HexInt64Converter : JsonConverter<long>
        {
            public override long Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            {
                return Convert.ToInt64(reader.GetString().Substring(2), 16);
            }

            public override void Write(Utf8JsonWriter writer, long value, JsonSerializerOptions options)
            {
                writer.WriteStringValue($"0x{value:x}");
            }
        }
    }
}
