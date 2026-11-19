using System.Diagnostics.CodeAnalysis;
using Masterly.NightShift.Core.Tests.Stubs;
using Masterly.NightShift.Dashboard;
using Masterly.NightShift.Dashboard.Pages;
using Masterly.NightShift.Dashboard.Resources;
using Masterly.NightShift.Storage.Monitoring;

namespace Masterly.NightShift.Core.Tests.Dashboard;

public class JobLineageFacts
{
    private readonly Mock<IMonitoringApi> _monitor = new();
    private readonly Dictionary<string, JobDetailsDto> _jobs = [];

    public JobLineageFacts()
    {
        _monitor.Setup(x => x.JobDetails(It.IsAny<string>())).Returns<string>(id => _jobs.TryGetValue(id, out JobDetailsDto? details) ? details : null);
    }

    [SuppressMessage("Usage", "xUnit1013:Public method should be marked as test")]
    public static void Step(string name) { }

    private JobDetailsDto Add(string id, string state, string parentId = null, string options = null, string[] continuations = null, string[] fanInChildren = null, string[] fanInParents = null, FanInMode mode = FanInMode.All)
    {
        List<StateHistoryDto> history = [];

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

        Dictionary<string, string> properties = [];
        if (continuations != null)
        {
            properties["Continuations"] = SerializationHelper.Serialize(continuations.Select(c => new ContinuationsSupportAttribute.Continuation { JobId = c, Options = JobContinuationOptions.OnlyOnSucceededState }).ToList(), SerializationOption.User);
        }

        if (fanInChildren != null)
        {
            properties[FanInContinuationsSupportAttribute.ParameterName] = SerializationHelper.Serialize(fanInChildren.ToList(), SerializationOption.User);
        }

        JobDetailsDto details = new() { Job = Job.FromExpression(() => Step(id)), History = history, Properties = properties, CreatedAt = DateTime.UtcNow };
        _jobs[id] = details;
        return details;
    }

    [Fact]
    public void Build_WithoutRelatives_HasOnlyTheJobItself()
    {
        JobDetailsDto details = Add("1", SucceededState.StateName);

        JobLineage lineage = JobLineage.Build(_monitor.Object, "1", details);

        Assert.False(lineage.HasRelatives);
        JobLineageNode node = Assert.Single(lineage.Nodes);
        Assert.True(node.IsCurrent);
        Assert.Equal("JobLineageFacts.Step", node.Name);
        Assert.Equal(SucceededState.StateName, node.State);
    }

    [Fact]
    public void Build_FollowsParentsUp_AndContinuationsDown()
    {
        Add("root", SucceededState.StateName, continuations: ["mid"]);
        JobDetailsDto mid = Add("mid", ProcessingState.StateName, parentId: "root", options: "OnlyOnSucceededState", continuations: ["leaf-a", "leaf-b"]);
        Add("leaf-a", AwaitingState.StateName, parentId: "mid");
        Add("leaf-b", AwaitingState.StateName, parentId: "mid");

        JobLineage lineage = JobLineage.Build(_monitor.Object, "mid", mid);

        Assert.True(lineage.HasRelatives);
        Assert.Equal(["root", "mid", "leaf-a", "leaf-b"], lineage.Nodes.Select(n => n.Id));
        Assert.Equal([-1, 0, 1, 1], lineage.Nodes.Select(n => n.Depth));
        Assert.Equal(3, lineage.Edges.Count);
        Assert.Contains(lineage.Edges, e => e.ParentId == "root" && e.ChildId == "mid" && e.Label == "after success");
        Assert.Contains(lineage.Edges, e => e.ParentId == "mid" && e.ChildId == "leaf-a");
        Assert.False(lineage.Truncated);
    }

    [Fact]
    public void Build_UnderstandsFanIn_InBothDirections()
    {
        Add("eu", SucceededState.StateName, fanInChildren: ["merge"]);
        JobDetailsDto us = Add("us", ProcessingState.StateName, fanInChildren: ["merge"]);
        Add("merge", AwaitingState.StateName, fanInParents: ["eu", "us"], mode: FanInMode.All);

        JobLineage lineage = JobLineage.Build(_monitor.Object, "us", us);

        Assert.Equal(["eu", "us", "merge"], lineage.Nodes.Select(n => n.Id));
        Assert.Equal(2, lineage.Edges.Count);
        Assert.All(lineage.Edges, e => Assert.Equal("merge", e.ChildId));
        Assert.All(lineage.Edges, e => Assert.Equal("all of", e.Label));
        Assert.Equal(0, lineage.Nodes.Single(n => n.Id == "eu").Depth);
        Assert.False(lineage.Nodes.Single(n => n.Id == "eu").IsCurrent);
        Assert.True(lineage.Nodes.Single(n => n.Id == "us").IsCurrent);
        Assert.Equal(1, lineage.Nodes.Single(n => n.Id == "merge").Depth);
    }

    [Fact]
    public void Build_DrawsAFewNodesPastTheLimit_ForAContinuationOfAThousandsOfParents()
    {
        // A batch continuation waits for every job of the batch: a node was added for each, 3 MB of page
        string[] parents = [.. Enumerable.Range(1, 5000).Select(static i => "p" + i)];
        JobDetailsDto merge = Add("merge", AwaitingState.StateName, fanInParents: parents);

        JobLineage lineage = JobLineage.Build(_monitor.Object, "merge", merge);

        Assert.True(lineage.Truncated);
        Assert.Equal(JobLineage.MaxNodes + JobLineage.MaxBoundaryNodes, lineage.Nodes.Count);
        Assert.Equal(lineage.Nodes.Count - 1, lineage.Edges.Count);
        _monitor.Verify(x => x.JobDetails(It.IsAny<string>()), Times.AtMost(JobLineage.MaxNodes));
    }

    [Fact]
    public void Build_MarksExpiredJobs_AndStops()
    {
        JobDetailsDto child = Add("child", AwaitingState.StateName, parentId: "vanished");

        JobLineage lineage = JobLineage.Build(_monitor.Object, "child", child);

        JobLineageNode parent = lineage.Nodes.Single(n => n.Id == "vanished");
        Assert.False(parent.Exists);
        Assert.Equal(-1, parent.Depth);
        Assert.True(lineage.HasRelatives);
    }

    [Fact]
    public void Build_StopsAtMaxDepth_AndSaysSo()
    {
        string? previous = null;
        for (int i = 0; i < JobLineage.MaxDepth + 3; i++)
        {
            string id = "n" + i;
            if (previous != null)
            {
                _jobs[previous].Properties["Continuations"] = SerializationHelper.Serialize(new List<ContinuationsSupportAttribute.Continuation> { new() { JobId = id, Options = JobContinuationOptions.OnAnyFinishedState } }, SerializationOption.User);
            }

            Add(id, i == 0 ? SucceededState.StateName : AwaitingState.StateName, parentId: previous);
            previous = id;
        }

        JobLineage lineage = JobLineage.Build(_monitor.Object, "n0", _jobs["n0"]);

        Assert.True(lineage.Truncated);
        Assert.Equal(JobLineage.MaxDepth + 1, lineage.Nodes.Max(n => n.Depth));
        Assert.False(lineage.Nodes.Single(n => n.Depth == JobLineage.MaxDepth + 1).Exists);
    }

    [Fact]
    public void Render_DrawsEveryNodeAsALink_AndEveryEdgeAsAPath()
    {
        Add("root", SucceededState.StateName, continuations: ["mid"]);
        JobDetailsDto mid = Add("mid", ProcessingState.StateName, parentId: "root", options: "OnlyOnSucceededState");
        JobLineage lineage = JobLineage.Build(_monitor.Object, "mid", mid);

        Mock<JobStorage> storage = new();
        JobDetailsPage page = new("mid");
        page.Assign(new DashboardContextStub(new DashboardOptions(), new DashboardRequestStub(), storage.Object));

        string svg = JobLineageRenderer.Render(page, lineage).ToString();

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
        JobDetailsDto details = Add("1", SucceededState.StateName);
        details.Properties["Continuations"] = "{not json";

        JobLineage lineage = JobLineage.Build(_monitor.Object, "1", details);

        Assert.False(lineage.HasRelatives);
        Assert.Empty(lineage.Edges);
    }

    [Fact]
    public void Build_TreatsAJobTheStorageFailsToLoad_AsExpired()
    {
        JobDetailsDto child = Add("child", AwaitingState.StateName, parentId: "broken");
        _monitor.Setup(x => x.JobDetails("broken")).Throws(new InvalidOperationException("storage down"));

        JobLineage lineage = JobLineage.Build(_monitor.Object, "child", child);

        JobLineageNode parent = lineage.Nodes.Single(n => n.Id == "broken");
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
    [InlineData("OnlyOnFailedState", "after failure")]
    [InlineData("OnlyOnProcessingState", "once started")]
    [InlineData("1", "after success")]
    [InlineData("OnlyOnSucceededState, OnlyOnFailedState", "after success or after failure")]
    [InlineData("6", "after delete or after failure")]
    [InlineData("", null)]
    public void Build_LabelsTheParentEdge_FromTheContinuationOptions(string options, string expected)
    {
        Add("parent", SucceededState.StateName);
        JobDetailsDto child = Add("child", AwaitingState.StateName, parentId: "parent", options: options);

        JobLineage lineage = JobLineage.Build(_monitor.Object, "child", child);

        JobLineageEdge edge = Assert.Single(lineage.Edges);
        Assert.Equal(expected, edge.Label);
    }

    [Fact]
    public void Build_DescribesAJobWithoutHistoryOrMethod()
    {
        JobDetailsDto details = new() { Job = null, History = [], Properties = null };

        JobLineage lineage = JobLineage.Build(_monitor.Object, "1", details);

        JobLineageNode node = Assert.Single(lineage.Nodes);
        Assert.True(node.Exists);
        Assert.Equal(Strings.Common_CannotFindTargetMethod, node.Name);
        Assert.Equal(Strings.Common_NoState, node.State);
    }

    [Fact]
    public void Build_ThrowsOnNullArguments()
    {
        JobDetailsDto details = Add("1", SucceededState.StateName);

        Assert.Throws<ArgumentNullException>(() => JobLineage.Build(null, "1", details));
        Assert.Throws<ArgumentNullException>(() => JobLineage.Build(_monitor.Object, null, details));
        Assert.Throws<ArgumentNullException>(() => JobLineage.Build(_monitor.Object, "1", null));
    }

    [Fact]
    public void Render_DrawsExpiredJobs_AsMissingCards()
    {
        JobDetailsDto child = Add("child", AwaitingState.StateName, parentId: "vanished");
        JobLineage lineage = JobLineage.Build(_monitor.Object, "child", child);

        string svg = JobLineageRenderer.Render(CreatePage("child"), lineage).ToString();

        Assert.Equal(1, CountOf(svg, "hf-lineage-missing"));
        Assert.Contains(">" + Strings.Common_JobExpired + "</text>", svg);
        Assert.Contains("/jobs/details/vanished", svg);
        Assert.Equal(1, CountOf(svg, "<path class=\"hf-lineage-edge\""));
    }

    [Fact]
    public void Render_TruncatesLongNames_AndEncodesThem()
    {
        JobDetailsDto details = Add("1", SucceededState.StateName);
        JobLineage lineage = JobLineage.Build(_monitor.Object, "1", details);
        lineage.Nodes[0].Name = "<script>AVeryLongTypeName.AVeryLongMethodName</script>";

        string svg = JobLineageRenderer.Render(CreatePage("1"), lineage).ToString();

        Assert.DoesNotContain("<script>", svg);
        Assert.Contains(">&lt;script&gt;AVeryLongTypeName…</text>", svg);
    }

    [Fact]
    public void Render_ThrowsOnNullArguments()
    {
        JobLineage lineage = JobLineage.Build(_monitor.Object, "1", Add("1", SucceededState.StateName));

        Assert.Throws<ArgumentNullException>(() => JobLineageRenderer.Render(null, lineage));
        Assert.Throws<ArgumentNullException>(() => JobLineageRenderer.Render(CreatePage("1"), null));
    }

    private static RazorPage CreatePage(string jobId)
    {
        JobDetailsPage page = new(jobId);
        page.Assign(new DashboardContextStub(new DashboardOptions(), new DashboardRequestStub(), new Mock<JobStorage>().Object));
        return page;
    }

    private static int CountOf(string text, string needle)
    {
        int count = 0;
        for (int index = text.IndexOf(needle, StringComparison.Ordinal); index >= 0; index = text.IndexOf(needle, index + 1, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }
}
