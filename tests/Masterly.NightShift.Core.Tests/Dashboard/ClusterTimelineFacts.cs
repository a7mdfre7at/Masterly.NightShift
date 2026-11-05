using System;
using System.Collections.Generic;
using System.Linq;
using Masterly.NightShift.Dashboard;
using Masterly.NightShift.States;
using Xunit;

namespace Masterly.NightShift.Core.Tests.Dashboard
{
    public class ClusterTimelineFacts
    {
        private static ActivityEntry Entry(long t, string job, string state, string server = null, string worker = null, string method = "App.Work")
        {
            return new ActivityEntry { Timestamp = t, JobId = job, State = state, ServerId = server, WorkerId = worker, Method = method };
        }

        [Fact]
        public void Build_PairsProcessingWithTheNextTransition_PerWorker()
        {
            var entries = new[]
            {
                Entry(1000, "a", EnqueuedState.StateName),
                Entry(2000, "a", ProcessingState.StateName, "srv-1", "w1"),
                Entry(5000, "a", SucceededState.StateName),
                Entry(2500, "b", ProcessingState.StateName, "srv-1", "w2"),
                Entry(4000, "b", FailedState.StateName),
                Entry(6000, "c", ProcessingState.StateName, "srv-2", "w9"),
            };

            var timeline = ClusterTimeline.Build(entries.Reverse(), 8000);

            Assert.Equal(3, timeline.Lanes.Count);
            Assert.Equal(new[] { "srv-1", "srv-1", "srv-2" }, timeline.Lanes.Select(l => l.ServerId));
            Assert.Equal(2, timeline.Servers);
            Assert.Equal(3, timeline.Runs);
            Assert.Equal(2000, timeline.FromMs);
            Assert.Equal(8000, timeline.ToMs);

            var w1 = Assert.Single(timeline.Lanes[0].Spans);
            Assert.Equal(2000, w1.StartMs);
            Assert.Equal(5000, w1.EndMs);
            Assert.Equal(SucceededState.StateName, w1.Outcome);
            Assert.Equal("App.Work", w1.Method);

            var w2 = Assert.Single(timeline.Lanes[1].Spans);
            Assert.Equal(FailedState.StateName, w2.Outcome);
            Assert.Equal(1500, w2.DurationMs);

            var running = Assert.Single(timeline.Lanes[2].Spans);
            Assert.True(running.IsRunning);
            Assert.Equal(8000, running.EndMs);
        }

        [Fact]
        public void Build_EndsARefetchedRunWhereTheNewOneStarts()
        {
            var entries = new[]
            {
                Entry(1000, "a", ProcessingState.StateName, "srv", "w1"),
                Entry(3000, "a", ProcessingState.StateName, "srv", "w2"),
                Entry(4000, "a", SucceededState.StateName),
            };

            var timeline = ClusterTimeline.Build(entries, 5000);

            Assert.Equal(2, timeline.Runs);
            Assert.Equal(ProcessingState.StateName, Assert.Single(timeline.Lanes[0].Spans).Outcome);
            Assert.Equal(1000, timeline.Lanes[0].Spans[0].StartMs);
            Assert.Equal(3000, timeline.Lanes[0].Spans[0].EndMs);
            Assert.Equal(SucceededState.StateName, Assert.Single(timeline.Lanes[1].Spans).Outcome);
        }

        [Fact]
        public void Build_ClipsToTheWindow_AndDropsEmptyLanes()
        {
            var entries = new[]
            {
                Entry(0, "old", ProcessingState.StateName, "srv", "w1"),
                Entry(100, "old", SucceededState.StateName),
                Entry(9000, "new", ProcessingState.StateName, "srv", "w2"),
                Entry(9500, "new", SucceededState.StateName),
            };

            var timeline = ClusterTimeline.Build(entries, 10000, TimeSpan.FromMilliseconds(2000));

            Assert.Equal(8000, timeline.FromMs);
            Assert.Equal("w2", Assert.Single(timeline.Lanes).WorkerId);
            Assert.Equal(TimeSpan.FromSeconds(2), timeline.Window);
        }

        [Fact]
        public void Build_IsEmpty_WithoutProcessingEntries()
        {
            var timeline = ClusterTimeline.Build(new[] { Entry(1, "a", EnqueuedState.StateName) }, 10);

            Assert.True(timeline.IsEmpty);
            Assert.Equal(0, timeline.Runs);
        }

        [Fact]
        public void ShortWorkerId_TakesEightCharacters()
        {
            Assert.Equal("abcdefgh", new TimelineLane("s", "abcdefgh-1234").ShortWorkerId);
            Assert.Equal("w1", new TimelineLane("s", "w1").ShortWorkerId);
        }
    }
}
