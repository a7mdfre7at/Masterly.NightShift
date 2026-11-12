using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Masterly.NightShift.Core.Tests.Dashboard.Pages;
using Masterly.NightShift.Dashboard;
using Masterly.NightShift.Dashboard.Pages;
using Masterly.NightShift.Server;
using Masterly.NightShift.Storage.Monitoring;

namespace Masterly.NightShift.Core.Tests.Dashboard;

public class ChainFlowchartFacts
{
    private static readonly DateTime Now = new(2026, 11, 12, 9, 0, 0, DateTimeKind.Utc);

    private readonly PageRenderer _renderer = new();

    [SuppressMessage("Usage", "xUnit1013:Public method should be marked as test")]
    public static void Step(string name) { }

    [SuppressMessage("Usage", "xUnit1013:Public method should be marked as test")]
    public static void Report(string name, int count, IJobConsole console, CancellationToken token) { }

    [Fact]
    public void Build_PutsEachLevelInARow_AndGroupsBranchesAndJoins()
    {
        JobChainInfo chain = SetUpOrderChain();

        ChainFlowchart chart = ChainFlowchart.Build(_renderer.Storage.Object, chain, Now);

        Assert.Equal([ChainLevelKind.Root, ChainLevelKind.Branches, ChainLevelKind.Joins], chart.Levels.Select(static x => x.Kind));
        Assert.Equal([1, 2, 2], chart.Levels.Select(static x => x.Steps.Count));
        Assert.Equal([false, true, true], chart.Levels.Select(static x => x.IsBoxed));
        Assert.Equal(2, chart.GroupCount);
        Assert.Empty(chart.Hidden);
    }

    [Fact]
    public void Build_ReadsEachStepsTiming_RetriesAndProgress()
    {
        JobChainInfo chain = SetUpOrderChain();
        _renderer.Connection.Setup(x => x.GetJobParameter("2", JobConsole.ProgressParameter)).Returns("{\"p\":40,\"m\":\"Posting\",\"t\":0}");

        ChainFlowchart chart = ChainFlowchart.Build(_renderer.Storage.Object, chain, Now);
        ChainFlowStep root = chart.Levels[0].Steps[0];
        ChainFlowStep failed = chart.Levels[1].Steps[1];

        Assert.Equal(Now.AddMinutes(-10), root.Time);
        Assert.Equal(TimeSpan.FromMilliseconds(1500), root.Duration);
        Assert.Equal(100, root.Progress);
        Assert.Equal(Now.AddMinutes(-9), failed.Time);
        Assert.Equal(TimeSpan.FromSeconds(30), failed.Duration);
        Assert.Equal(2, failed.RetryCount);
        Assert.Equal("ERP rejected the order", failed.Reason);
        Assert.Equal(40, chart.Levels[1].Steps[0].Progress);
        Assert.Equal("Posting", chart.Levels[1].Steps[0].ProgressMessage);
    }

    [Fact]
    public void Build_SaysWhetherEachStepsConditionWasMet_AndThatWaitingStepsOfASettledChainWontRun()
    {
        JobChainInfo chain = SetUpOrderChain();

        ChainFlowchart chart = ChainFlowchart.Build(_renderer.Storage.Object, chain, Now);

        Assert.Equal(ChainFlowCondition.None, chart.Levels[0].Steps[0].Condition);
        Assert.Equal(ChainFlowCondition.Met, chart.Levels[1].Steps[0].Condition);
        Assert.Equal(ChainFlowCondition.NotRun, chart.Levels[2].Steps[0].Condition);
        Assert.Equal(ChainFlowCondition.Met, chart.Levels[2].Steps[1].Condition);
    }

    [Fact]
    public void Build_KeepsStepsWaiting_WhileTheChainRuns_AndShowsDeletedStepsThatNeverRan_AsNotRun()
    {
        JobChain definition = JobChain.StartWith(() => Step("a")).Then(() => Step("b")).Then(() => Step("c"));
        SetUpChain("c2", definition, ["10", "11", "12"]);
        SetUpJob("10", Processing(Now.AddMinutes(-1)), Enqueued(Now.AddMinutes(-2)));
        SetUpJob("11", Awaiting(Now.AddMinutes(-2)));
        SetUpJob("12", new StateHistoryDto { StateName = DeletedState.StateName, CreatedAt = Now, Reason = "Condition not met" }, Awaiting(Now.AddMinutes(-2)));

        ChainFlowchart chart = ChainFlowchart.Build(_renderer.Storage.Object, _renderer.Storage.Object.GetChain("c2")!, Now);

        Assert.Equal(ChainFlowCondition.Waiting, chart.Levels[1].Steps[0].Condition);
        Assert.Equal(ChainFlowCondition.NotRun, chart.Levels[2].Steps[0].Condition);
        Assert.Equal(TimeSpan.FromMinutes(1), chart.Levels[0].Steps[0].Duration);
    }

    [Fact]
    public void Build_TreatsAnExpiredStep_AsHavingNoCondition()
    {
        JobChain definition = JobChain.StartWith(() => Step("a")).Then(() => Step("b"));
        SetUpChain("c3", definition, ["20", "21"]);
        SetUpJob("20", Succeeded(Now));

        ChainFlowchart chart = ChainFlowchart.Build(_renderer.Storage.Object, _renderer.Storage.Object.GetChain("c3")!, Now);

        ChainFlowStep expired = chart.Levels[1].Steps[0];
        Assert.Null(expired.Info.State);
        Assert.Equal(ChainFlowCondition.None, expired.Condition);
        Assert.Null(expired.Time);
    }

    [Fact]
    public void Build_MergesTheStepsConsoles_InTimeOrder()
    {
        JobChainInfo chain = SetUpOrderChain();
        _renderer.Connection.Setup(x => x.GetAllItemsFromList(JobConsole.GetConsoleKey("1"))).Returns(["{\"t\":100,\"s\":1,\"l\":1,\"m\":\"first\"}", "{\"t\":300,\"s\":2,\"l\":1,\"m\":\"third\"}"]);
        _renderer.Connection.Setup(x => x.GetAllItemsFromList(JobConsole.GetConsoleKey("3"))).Returns(["{\"t\":200,\"s\":1,\"l\":3,\"m\":\"second\"}"]);

        ChainFlowchart chart = ChainFlowchart.Build(_renderer.Storage.Object, chain, Now);

        Assert.Equal(["first", "second", "third"], chart.Logs.Select(static x => x.Entry.Message));
        Assert.Equal(["1", "3", "1"], chart.Logs.Select(static x => x.Step.Info.JobId));
        Assert.Equal(3, chart.LogLineCount);
    }

    [Fact]
    public void Build_KeepsOnlyTheNewestLines_OfALongConsole()
    {
        JobChainInfo chain = SetUpOrderChain();
        _renderer.Connection.Setup(x => x.GetAllItemsFromList(JobConsole.GetConsoleKey("1")))
            .Returns([.. Enumerable.Range(1, ChainFlowchart.MaxLogLines + 5).Select(static i => $"{{\"t\":{i},\"s\":{i},\"l\":1,\"m\":\"line {i}\"}}")]);

        ChainFlowchart chart = ChainFlowchart.Build(_renderer.Storage.Object, chain, Now);

        Assert.Equal(ChainFlowchart.MaxLogLines, chart.Logs.Count);
        Assert.Equal("line 6", chart.Logs[0].Entry.Message);
        Assert.Equal(ChainFlowchart.MaxLogLines + 5, chart.LogLineCount);
    }

    [Fact]
    public void Build_DrawsWholeLevels_UpToTheLimit_AndListsTheRest()
    {
        JobChain definition = JobChain.StartWith(() => Step("root"))
            .ThenMany(g =>
            {
                for (int i = 0; i < ChainFlowchart.MaxSteps; i++) g.Branch(() => Step("branch"));
            })
            .Then(() => Step("last"));
        List<string> ids = [.. Enumerable.Range(0, ChainFlowchart.MaxSteps + 2).Select(static i => i.ToString(CultureInfo.InvariantCulture))];
        SetUpChain("big", definition, ids);

        ChainFlowchart chart = ChainFlowchart.Build(_renderer.Storage.Object, _renderer.Storage.Object.GetChain("big")!, Now);

        Assert.Single(chart.Levels);
        Assert.Equal(ChainFlowchart.MaxSteps + 1, chart.Hidden.Count);
    }

    [Fact]
    public void Build_ThrowsOnNullArguments()
    {
        JobChainInfo chain = SetUpOrderChain();

        Assert.Throws<ArgumentNullException>(() => ChainFlowchart.Build(null, chain, Now));
        Assert.Throws<ArgumentNullException>(() => ChainFlowchart.Build(_renderer.Storage.Object, null, Now));
    }

    [Theory]
    [InlineData(new[] { "Enqueued", "Awaiting" }, "Waiting")]
    [InlineData(new[] { "Scheduled", "Awaiting" }, "Waiting")]
    [InlineData(new[] { "Processing", "Awaiting" }, "Running")]
    [InlineData(new[] { "Succeeded", "Enqueued" }, "Running")]
    [InlineData(new[] { "Succeeded", "Succeeded" }, "Done")]
    [InlineData(new[] { "Failed", "Awaiting" }, "FinishedWithFailures")]
    public void StatusOf_SaysWhereTheChainStands(string[] states, string expected)
    {
        JobChainInfo chain = new("c", 1, Now, ChainDeleteBehavior.Cascade,
        [
            new JobChainStepInfo("0.0", "1", states[0], 0, -1),
            new JobChainStepInfo("1.0", "2", states[1], 1, 0),
        ]);

        Assert.Equal(Enum.Parse<ChainFlowStatus>(expected), ChainFlowchart.StatusOf(chain));
    }

    [Fact]
    public void StatusOf_AGroup_CountsStepsThatWontRun_AsSettled()
    {
        ChainFlowStep succeeded = new(new JobChainStepInfo("1.0", "1", SucceededState.StateName, 1, 0)) { Condition = ChainFlowCondition.Met };
        ChainFlowStep notRun = new(new JobChainStepInfo("1.1", "2", AwaitingState.StateName, 1, 0)) { Condition = ChainFlowCondition.NotRun };
        ChainFlowStep failed = new(new JobChainStepInfo("1.2", "3", FailedState.StateName, 1, 0)) { Condition = ChainFlowCondition.Met };

        Assert.Equal(ChainFlowStatus.Done, ChainFlowchart.StatusOf([succeeded, notRun]));
        Assert.Equal(ChainFlowStatus.FinishedWithFailures, ChainFlowchart.StatusOf([succeeded, failed]));
        Assert.Throws<ArgumentNullException>(() => ChainFlowchart.StatusOf((IReadOnlyCollection<ChainFlowStep>)null));
        Assert.Throws<ArgumentNullException>(() => ChainFlowchart.StatusOf((JobChainInfo)null));
    }

    [Theory]
    [InlineData("Root", JobContinuationOptions.OnlyOnSucceededState, "First step", false)]
    [InlineData("Then", JobContinuationOptions.OnlyOnSucceededState, "On success", false)]
    [InlineData("Then", JobContinuationOptions.OnlyOnFailedState, "On failure", true)]
    [InlineData("Then", JobContinuationOptions.OnlyOnDeletedState, "On deletion", false)]
    [InlineData("Then", JobContinuationOptions.OnlyOnProcessingState, "Once started", false)]
    [InlineData("Then", JobContinuationOptions.OnAnyFinishedState, "On any outcome", false)]
    [InlineData("AfterAll", JobContinuationOptions.OnlyOnSucceededState, "All succeeded", false)]
    [InlineData("AfterAny", JobContinuationOptions.OnlyOnSucceededState, "Any succeeded", false)]
    [InlineData("AfterAnyFailed", JobContinuationOptions.OnlyOnSucceededState, "Any failed", true)]
    [InlineData("AfterAnyFailedOrCanceled", JobContinuationOptions.OnlyOnSucceededState, "Any failed or canceled", true)]
    [InlineData("AfterAllCompleted", JobContinuationOptions.OnlyOnSucceededState, "All finished", false)]
    public void DescribeCondition_NamesWhatTheStepWaitsFor(string link, JobContinuationOptions options, string expected, bool alert)
    {
        JobChainStepInfo step = new("1.0", "1", null, 1, 0) { Link = Enum.Parse<ChainLink>(link), Options = options };

        Assert.Equal(expected, ChainFlowchart.DescribeCondition(step));
        Assert.Equal(alert, ChainFlowchart.IsAlertCondition(step));
    }

    [Fact]
    public void FormatArguments_ShowsTheCallersValues_AndLeavesOutWhatTheServerSupplies()
    {
        Job job = Job.FromExpression(() => Report("balance-sheet", 3, null, CancellationToken.None));

        Assert.Equal("(\"balance-sheet\", 3)", ChainFlowchartRenderer.FormatArguments(job));
        Assert.Equal("()", ChainFlowchartRenderer.FormatArguments(null));
    }

    [Fact]
    public void Render_PlacesACardPerStep_InBoxesForGroups_WithLinksBetweenLevels()
    {
        JobChainInfo chain = SetUpOrderChain();
        RazorPage page = CreatePage();

        string html = ChainFlowchartRenderer.Render(page, ChainFlowchart.Build(_renderer.Storage.Object, chain, Now)).ToString();

        Assert.Equal(5, CountOf(html, "--hf-state:"));
        Assert.Equal(1, CountOf(html, "hf-flow-box-group"));
        Assert.Equal(1, CountOf(html, "hf-flow-box-outcomes"));
        Assert.Contains("data-width=\"", html, StringComparison.Ordinal);
        Assert.Contains("hf-flow-edge hf-flow-edge-alert", html, StringComparison.Ordinal);
        Assert.Contains("hf-flow-edge hf-flow-edge-skipped", html, StringComparison.Ordinal);
        Assert.Contains("href=\"/jobs/details/3\"", html, StringComparison.Ordinal);
        Assert.Contains("Condition met", html, StringComparison.Ordinal);
        Assert.Contains("Not run", html, StringComparison.Ordinal);
        Assert.Contains("1 succeeded &#183; 1 failed", html, StringComparison.Ordinal);
        Assert.Contains("title=\"ERP rejected the order\"", html, StringComparison.Ordinal);
        Assert.Contains("Retries: 2", html, StringComparison.Ordinal);
        Assert.Contains("/chains/retry/3", html, StringComparison.Ordinal);
        Assert.DoesNotContain("/chains/retry/1\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_OffersNoRetry_ToReadOnlyUsers()
    {
        JobChainInfo chain = SetUpOrderChain();
        _renderer.Options = new DashboardOptions { IsReadOnlyFunc = static _ => true };

        string html = ChainFlowchartRenderer.Render(CreatePage(), ChainFlowchart.Build(_renderer.Storage.Object, chain, Now)).ToString();

        Assert.DoesNotContain("/chains/retry/", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_EncodesWhatJobsWrote()
    {
        JobChain definition = JobChain.StartWith(() => Step("<script>"));
        SetUpChain("c4", definition, ["40"]);
        SetUpJob("40", new StateHistoryDto { StateName = FailedState.StateName, CreatedAt = Now, Data = new Dictionary<string, string> { ["ExceptionMessage"] = "<b>bad</b>" } });
        _renderer.Connection.Setup(x => x.GetAllItemsFromList(JobConsole.GetConsoleKey("40"))).Returns(["{\"t\":1,\"s\":1,\"l\":1,\"m\":\"<img src=x>\"}"]);
        ChainFlowchart chart = ChainFlowchart.Build(_renderer.Storage.Object, _renderer.Storage.Object.GetChain("c4")!, Now);

        string html = ChainFlowchartRenderer.Render(CreatePage(), chart) + ChainFlowchartRenderer.RenderLogs(CreatePage(), chart).ToString();

        Assert.DoesNotContain("<script>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<b>bad", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<img", html, StringComparison.Ordinal);
        Assert.Contains("&lt;img src=x&gt;", html, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderLogs_SaysNoStepWrote_WhenTheConsolesAreEmpty()
    {
        JobChainInfo chain = SetUpOrderChain();

        string html = ChainFlowchartRenderer.RenderLogs(CreatePage(), ChainFlowchart.Build(_renderer.Storage.Object, chain, Now)).ToString();

        Assert.Contains("No step has written to its console.", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_ThrowsOnNullArguments()
    {
        ChainFlowchart chart = ChainFlowchart.Build(_renderer.Storage.Object, SetUpOrderChain(), Now);

        Assert.Throws<ArgumentNullException>(() => ChainFlowchartRenderer.Render(null, chart));
        Assert.Throws<ArgumentNullException>(() => ChainFlowchartRenderer.Render(CreatePage(), null));
        Assert.Throws<ArgumentNullException>(() => ChainFlowchartRenderer.RenderLogs(null, chart));
        Assert.Throws<ArgumentNullException>(() => ChainFlowchartRenderer.RenderLogs(CreatePage(), null));
    }

    // Process (succeeded) → [Confirm (succeeded), Post (failed, 2 retries)] → [after all: Notify (awaiting), after any failed: Alert (succeeded)]
    private JobChainInfo SetUpOrderChain()
    {
        JobChain definition = JobChain.StartWith(() => Step("process"))
            .ThenMany(g => g.Branch(() => Step("confirm")).Branch(() => Step("post")))
            .ThenAfterAll(() => Step("notify"))
            .ThenAfterAnyFailed(() => Step("alert"));
        SetUpChain("c1", definition, ["1", "2", "3", "4", "5"]);

        SetUpJob("1",
            new StateHistoryDto { StateName = SucceededState.StateName, CreatedAt = Now.AddMinutes(-9.9), Data = new Dictionary<string, string> { ["PerformanceDuration"] = "1500" } },
            Processing(Now.AddMinutes(-10)),
            Enqueued(Now.AddMinutes(-11)));
        SetUpJob("2", Succeeded(Now.AddMinutes(-9)), Processing(Now.AddMinutes(-9.5)), Awaiting(Now.AddMinutes(-11)));
        SetUpJob("3",
            new StateHistoryDto { StateName = FailedState.StateName, CreatedAt = Now.AddMinutes(-9).AddSeconds(30), Data = new Dictionary<string, string> { ["ExceptionMessage"] = "ERP rejected the order" } },
            Processing(Now.AddMinutes(-9)),
            Awaiting(Now.AddMinutes(-11)));
        SetUpJob("4", Awaiting(Now.AddMinutes(-11)));
        SetUpJob("5", Succeeded(Now.AddMinutes(-8)), Processing(Now.AddMinutes(-8.5)), Awaiting(Now.AddMinutes(-11)));
        _renderer.Monitoring.Setup(x => x.JobDetails("3")).Returns(Details("3", "post", 2, _states["3"]));

        return _renderer.Storage.Object.GetChain("c1")!;
    }

    private readonly Dictionary<string, StateHistoryDto[]> _states = [];

    private void SetUpChain(string chainId, JobChain definition, IReadOnlyList<string> jobIds)
    {
        List<string> keys = [];
        for (int level = 0; level < definition.Levels.Count; level++)
        {
            for (int i = 0; i < definition.Levels[level].Steps.Count; i++) keys.Add($"{level}.{i}");
        }

        string jobs = "{" + string.Join(",", keys.Zip(jobIds, static (key, id) => $"\"{key}\":\"{id}\"")) + "}";
        _renderer.Connection.Setup(x => x.GetAllEntriesFromHash(JobChains.GetKey(chainId))).Returns(new Dictionary<string, string>
        {
            ["Definition"] = JobChains.ToDefinition(definition),
            ["Jobs"] = jobs,
            ["Generation"] = "1",
            ["CreatedAt"] = JobHelper.SerializeDateTime(Now.AddMinutes(-11)),
        });
    }

    private void SetUpJob(string jobId, params StateHistoryDto[] history)
    {
        _states[jobId] = history;
        _renderer.Connection.Setup(x => x.GetStateData(jobId)).Returns(new StateData { Name = history[0].StateName, Reason = history[0].Reason, Data = history[0].Data });
        _renderer.Monitoring.Setup(x => x.JobDetails(jobId)).Returns(Details(jobId, jobId, 0, history));
    }

    private static JobDetailsDto Details(string jobId, string name, int retries, StateHistoryDto[] history)
    {
        Dictionary<string, string> properties = [];
        if (retries > 0) properties["RetryCount"] = retries.ToString(CultureInfo.InvariantCulture);

        return new JobDetailsDto
        {
            Job = Job.FromExpression(() => Step(name)),
            CreatedAt = history[^1].CreatedAt,
            Properties = properties,
            History = [.. history],
        };
    }

    private static StateHistoryDto Succeeded(DateTime at) => new() { StateName = SucceededState.StateName, CreatedAt = at, Data = new Dictionary<string, string>() };
    private static StateHistoryDto Processing(DateTime at) => new() { StateName = ProcessingState.StateName, CreatedAt = at, Data = new Dictionary<string, string>() };
    private static StateHistoryDto Enqueued(DateTime at) => new() { StateName = EnqueuedState.StateName, CreatedAt = at, Data = new Dictionary<string, string>() };
    private static StateHistoryDto Awaiting(DateTime at) => new() { StateName = AwaitingState.StateName, CreatedAt = at, Data = new Dictionary<string, string>() };

    private RazorPage CreatePage()
    {
        ChainDetailsPage page = new("c1");
        page.Assign(new Stubs.DashboardContextStub(_renderer.Options, _renderer.Request, _renderer.Storage.Object));
        return page;
    }

    private static int CountOf(string text, string needle)
    {
        int count = 0;
        for (int index = text.IndexOf(needle, StringComparison.Ordinal); index >= 0; index = text.IndexOf(needle, index + 1, StringComparison.Ordinal)) count++;
        return count;
    }
}
