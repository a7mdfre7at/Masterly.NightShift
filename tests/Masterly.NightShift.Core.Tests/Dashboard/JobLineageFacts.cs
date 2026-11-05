using System;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics.CodeAnalysis;
using Masterly.NightShift.Common;
using Masterly.NightShift.Core.Tests.Stubs;
using Masterly.NightShift.Dashboard;
using Masterly.NightShift.Dashboard.Pages;
using Masterly.NightShift.Dashboard.Resources;
using Masterly.NightShift.States;
using Masterly.NightShift.Storage;
using Masterly.NightShift.Storage.Monitoring;
using Moq;
using Xunit;

namespace Masterly.NightShift.Core.Tests.Dashboard
{
    public class JobLineageFacts
    {
        private readonly Mock<IMonitoringApi> _monitor = new Mock<IMonitoringApi>();
        private readonly Dictionary<string, JobDetailsDto> _jobs = new Dictionary<string, JobDetailsDto>();

        public JobLineageFacts()
        {
            _monitor.Setup(x => x.JobDetails(It.IsAny<string>())).Returns<string>(id => _jobs.TryGetValue(id, out var details) ? details : null);
        }

        [SuppressMessage("Usage", "xUnit1013:Public method should be marked as test")]
        public static void Step(string name) { }

        private JobDetailsDto Add(string id, string state, string parentId = null, string options = null, string[] continuations = null, string[] fanInChildren = null, string[] fanInParents = null, FanInMode mode = FanInMode.All)
        {
            var history = new List<StateHistoryDto>();

            if (fanInParents != null)
            {
                history.Add(new StateHistoryDto { StateName = state, Data = new AwaitingJobsState(fanInParents, new EnqueuedState(), JobContinuationOptions.OnAnyFinishedState, mode).SerializeData() });
            }
            else if (parentId != null)
            {
                history.Add(new StateHistoryDto { StateName = state, Data = new Dictionary<string, string> { { "ParentId", parentId }, { "Options", options ?? "OnAnyFinishedState" } } });
                if (state != AwaitingState.StateName)
                {
                    history.Insert(0, new StateHistoryDto { StateName = state, Data = new Dictionary<string, string>() });
                    history[1].StateName = AwaitingState.StateName;
                }
            }
            else
            {
                history.Add(new StateHistoryDto { StateName = state, Data = new Dictionary<string, string>() });
            }

            var properties = new Dictionary<string, string>();
            if (continuations != null)
            {
                properties["Continuations"] = SerializationHelper.Serialize(continuations.Select(c => new ContinuationsSupportAttribute.Continuation { JobId = c, Options = JobContinuationOptions.OnlyOnSucceededState }).ToList(), SerializationOption.User);
            }

            if (fanInChildren != null)
            {
                properties[FanInContinuationsSupportAttribute.ParameterName] = SerializationHelper.Serialize(fanInChildren.ToList(), SerializationOption.User);
            }

            var details = new JobDetailsDto { Job = Job.FromExpression(() => Step(id)), History = history, Properties = properties, CreatedAt = DateTime.UtcNow };
            _jobs[id] = details;
            return details;
        }

        [Fact]
        public void Build_WithoutRelatives_HasOnlyTheJobItself()
        {
            var details = Add("1", SucceededState.StateName);

            var lineage = JobLineage.Build(_monitor.Object, "1", details);

            Assert.False(lineage.HasRelatives);
            var node = Assert.Single(lineage.Nodes);
            Assert.True(node.IsCurrent);
            Assert.Equal("JobLineageFacts.Step", node.Name);
            Assert.Equal(SucceededState.StateName, node.State);
        }

        [Fact]
        public void Build_FollowsParentsUp_AndContinuationsDown()
        {
            Add("root", SucceededState.StateName, continuations: new[] { "mid" });
            var mid = Add("mid", ProcessingState.StateName, parentId: "root", options: "OnlyOnSucceededState", continuations: new[] { "leaf-a", "leaf-b" });
            Add("leaf-a", AwaitingState.StateName, parentId: "mid");
            Add("leaf-b", AwaitingState.StateName, parentId: "mid");

            var lineage = JobLineage.Build(_monitor.Object, "mid", mid);

            Assert.True(lineage.HasRelatives);
            Assert.Equal(new[] { "root", "mid", "leaf-a", "leaf-b" }, lineage.Nodes.Select(n => n.Id));
            Assert.Equal(new[] { -1, 0, 1, 1 }, lineage.Nodes.Select(n => n.Depth));
            Assert.Equal(3, lineage.Edges.Count);
            Assert.Contains(lineage.Edges, e => e.ParentId == "root" && e.ChildId == "mid" && e.Label == "after success");
            Assert.Contains(lineage.Edges, e => e.ParentId == "mid" && e.ChildId == "leaf-a");
            Assert.False(lineage.Truncated);
        }

        [Fact]
        public void Build_UnderstandsFanIn_InBothDirections()
        {
            Add("eu", SucceededState.StateName, fanInChildren: new[] { "merge" });
            var us = Add("us", ProcessingState.StateName, fanInChildren: new[] { "merge" });
            Add("merge", AwaitingState.StateName, fanInParents: new[] { "eu", "us" }, mode: FanInMode.All);

            var lineage = JobLineage.Build(_monitor.Object, "us", us);

            Assert.Equal(new[] { "eu", "us", "merge" }, lineage.Nodes.Select(n => n.Id));
            Assert.Equal(2, lineage.Edges.Count);
            Assert.All(lineage.Edges, e => Assert.Equal("merge", e.ChildId));
            Assert.All(lineage.Edges, e => Assert.Equal("all of", e.Label));
            Assert.Equal(0, lineage.Nodes.Single(n => n.Id == "eu").Depth);
            Assert.False(lineage.Nodes.Single(n => n.Id == "eu").IsCurrent);
            Assert.True(lineage.Nodes.Single(n => n.Id == "us").IsCurrent);
            Assert.Equal(1, lineage.Nodes.Single(n => n.Id == "merge").Depth);
        }

        [Fact]
        public void Build_MarksExpiredJobs_AndStops()
        {
            var child = Add("child", AwaitingState.StateName, parentId: "vanished");

            var lineage = JobLineage.Build(_monitor.Object, "child", child);

            var parent = lineage.Nodes.Single(n => n.Id == "vanished");
            Assert.False(parent.Exists);
            Assert.Equal(-1, parent.Depth);
            Assert.True(lineage.HasRelatives);
        }

        [Fact]
        public void Build_StopsAtMaxDepth_AndSaysSo()
        {
            var previous = (string)null;
            for (var i = 0; i < JobLineage.MaxDepth + 3; i++)
            {
                var id = "n" + i;
                if (previous != null) _jobs[previous].Properties["Continuations"] = SerializationHelper.Serialize(new List<ContinuationsSupportAttribute.Continuation> { new ContinuationsSupportAttribute.Continuation { JobId = id, Options = JobContinuationOptions.OnAnyFinishedState } }, SerializationOption.User);
                Add(id, i == 0 ? SucceededState.StateName : AwaitingState.StateName, parentId: previous);
                previous = id;
            }

            var lineage = JobLineage.Build(_monitor.Object, "n0", _jobs["n0"]);

            Assert.True(lineage.Truncated);
            Assert.Equal(JobLineage.MaxDepth + 1, lineage.Nodes.Max(n => n.Depth));
            Assert.False(lineage.Nodes.Single(n => n.Depth == JobLineage.MaxDepth + 1).Exists);
        }

        [Fact]
        public void Render_DrawsEveryNodeAsALink_AndEveryEdgeAsAPath()
        {
            Add("root", SucceededState.StateName, continuations: new[] { "mid" });
            var mid = Add("mid", ProcessingState.StateName, parentId: "root", options: "OnlyOnSucceededState");
            var lineage = JobLineage.Build(_monitor.Object, "mid", mid);

            var storage = new Mock<JobStorage>();
            var page = new JobDetailsPage("mid");
            page.Assign(new DashboardContextStub(new DashboardOptions(), new DashboardRequestStub(), storage.Object));

            var svg = JobLineageRenderer.Render(page, lineage).ToString();

            Assert.StartsWith("<svg", svg);
            Assert.Equal(2, CountOf(svg, "<a href="));
            Assert.Equal(1, CountOf(svg, "<path class=\"hf-lineage-edge\""));
            Assert.Contains("hf-lineage-current", svg);
            Assert.Contains("after success", svg);
            Assert.Contains("/jobs/details/root", svg);
        }

        [Fact]
        public void Build_IgnoresMalformedContinuations()
        {
            var details = Add("1", SucceededState.StateName);
            details.Properties["Continuations"] = "{not json";

            var lineage = JobLineage.Build(_monitor.Object, "1", details);

            Assert.False(lineage.HasRelatives);
            Assert.Empty(lineage.Edges);
        }

        [Fact]
        public void Build_TreatsAJobTheStorageFailsToLoad_AsExpired()
        {
            var child = Add("child", AwaitingState.StateName, parentId: "broken");
            _monitor.Setup(x => x.JobDetails("broken")).Throws(new InvalidOperationException("storage down"));

            var lineage = JobLineage.Build(_monitor.Object, "child", child);

            var parent = lineage.Nodes.Single(n => n.Id == "broken");
            Assert.False(parent.Exists);
            Assert.Null(parent.Name);
            Assert.Equal(-1, parent.Depth);
            Assert.False(lineage.Truncated);
        }

        [Theory]
        [InlineData("not an option", "after finish")]
        [InlineData("OnAnyFinishedState", "after finish")]
        [InlineData("OnlyOnSucceededState", "after success")]
        [InlineData("OnlyOnDeletedState", "after delete")]
        [InlineData("1", "after success")]
        [InlineData("", null)]
        public void Build_LabelsTheParentEdge_FromTheContinuationOptions(string options, string expected)
        {
            Add("parent", SucceededState.StateName);
            var child = Add("child", AwaitingState.StateName, parentId: "parent", options: options);

            var lineage = JobLineage.Build(_monitor.Object, "child", child);

            var edge = Assert.Single(lineage.Edges);
            Assert.Equal(expected, edge.Label);
        }

        [Fact]
        public void Build_DescribesAJobWithoutHistoryOrMethod()
        {
            var details = new JobDetailsDto { Job = null, History = new List<StateHistoryDto>(), Properties = null };

            var lineage = JobLineage.Build(_monitor.Object, "1", details);

            var node = Assert.Single(lineage.Nodes);
            Assert.True(node.Exists);
            Assert.Equal(Strings.Common_CannotFindTargetMethod, node.Name);
            Assert.Equal(Strings.Common_NoState, node.State);
        }

        [Fact]
        public void Build_ThrowsOnNullArguments()
        {
            var details = Add("1", SucceededState.StateName);

            Assert.Throws<ArgumentNullException>(() => JobLineage.Build(null, "1", details));
            Assert.Throws<ArgumentNullException>(() => JobLineage.Build(_monitor.Object, null, details));
            Assert.Throws<ArgumentNullException>(() => JobLineage.Build(_monitor.Object, "1", null));
        }

        [Fact]
        public void Render_DrawsExpiredJobs_AsMissingCards()
        {
            var child = Add("child", AwaitingState.StateName, parentId: "vanished");
            var lineage = JobLineage.Build(_monitor.Object, "child", child);

            var svg = JobLineageRenderer.Render(CreatePage("child"), lineage).ToString();

            Assert.Equal(1, CountOf(svg, "hf-lineage-missing"));
            Assert.Contains(">" + Strings.Common_JobExpired + "</text>", svg);
            Assert.Contains("/jobs/details/vanished", svg);
            Assert.Equal(1, CountOf(svg, "<path class=\"hf-lineage-edge\""));
        }

        [Fact]
        public void Render_TruncatesLongNames_AndEncodesThem()
        {
            var details = Add("1", SucceededState.StateName);
            var lineage = JobLineage.Build(_monitor.Object, "1", details);
            lineage.Nodes[0].Name = "<script>AVeryLongTypeName.AVeryLongMethodName</script>";

            var svg = JobLineageRenderer.Render(CreatePage("1"), lineage).ToString();

            Assert.DoesNotContain("<script>", svg);
            Assert.Contains(">&lt;script&gt;AVeryLongTypeName…</text>", svg);
        }

        [Fact]
        public void Render_ThrowsOnNullArguments()
        {
            var lineage = JobLineage.Build(_monitor.Object, "1", Add("1", SucceededState.StateName));

            Assert.Throws<ArgumentNullException>(() => JobLineageRenderer.Render(null, lineage));
            Assert.Throws<ArgumentNullException>(() => JobLineageRenderer.Render(CreatePage("1"), null));
        }

        private static RazorPage CreatePage(string jobId)
        {
            var page = new JobDetailsPage(jobId);
            page.Assign(new DashboardContextStub(new DashboardOptions(), new DashboardRequestStub(), new Mock<JobStorage>().Object));
            return page;
        }

        private static int CountOf(string text, string needle)
        {
            var count = 0;
            for (var index = text.IndexOf(needle, StringComparison.Ordinal); index >= 0; index = text.IndexOf(needle, index + 1, StringComparison.Ordinal)) count++;
            return count;
        }
    }
}
