using System.Globalization;
using System.Net;
using System.Reflection;
using System.Text;
using Masterly.NightShift.Dashboard.Resources;
using Masterly.NightShift.Server;
using Masterly.NightShift.Storage.Monitoring;

namespace Masterly.NightShift.Dashboard;

/// <summary>How far a chain, or a group of its steps, has got.</summary>
internal enum ChainFlowStatus
{
    /// <summary>No step has started: they are enqueued, scheduled or waiting for their parents.</summary>
    Waiting,

    Running,
    Done,
    FinishedWithFailures,
}

/// <summary>Whether a step's start condition was met.</summary>
internal enum ChainFlowCondition
{
    /// <summary>The first step, or a step whose job expired.</summary>
    None,

    Met,

    /// <summary>The step waits for the steps before it.</summary>
    Waiting,

    /// <summary>
    /// The step won't run: it was deleted without running (its condition wasn't met, or the chain was deleted), or it
    /// still waits in a chain that has settled, after a step that failed for good.
    /// </summary>
    NotRun,
}

/// <summary>A step of a <see cref="ChainFlowchart"/>, with what its card shows.</summary>
internal sealed class ChainFlowStep(JobChainStepInfo info)
{
    public JobChainStepInfo Info { get; } = info;

    public ChainFlowCondition Condition { get; set; }

    /// <summary>The state's reason, or the exception message of a failed step.</summary>
    public string? Reason { get; set; }

    public double? Progress { get; set; }
    public string? ProgressMessage { get; set; }

    /// <summary>When the step started, or when its job was created when it never started.</summary>
    public DateTime? Time { get; set; }

    /// <summary>How long it ran, or has been running.</summary>
    public TimeSpan? Duration { get; set; }

    public int RetryCount { get; set; }

    public bool IsSettled => JobChains.IsSettledState(Info.State) || Condition == ChainFlowCondition.NotRun;
}

/// <summary>A level of a <see cref="ChainFlowchart"/>: a row of cards, in a box when it is a group.</summary>
internal sealed class ChainFlowLevel(int index, ChainLevelKind kind, List<ChainFlowStep> steps)
{
    public int Index { get; } = index;
    public ChainLevelKind Kind { get; } = kind;
    public List<ChainFlowStep> Steps { get; } = steps;

    /// <summary>Branches always form a group; joins form one when there are several.</summary>
    public bool IsBoxed => Kind == ChainLevelKind.Branches || Steps.Count > 1;
}

/// <summary>
/// A chain drawn as a flowchart, top to bottom: one row per level, parallel branches and the steps that react to
/// their outcome in boxes, each step a card with its state, start condition, progress and timing, and the merged
/// console of every step. Read from the chain's record and each step's job.
/// </summary>
internal sealed class ChainFlowchart
{
    /// <summary>How many steps are drawn at most; whole levels are drawn, the others are listed.</summary>
    public const int MaxSteps = 200;

    /// <summary>How many console lines the chain logs show, the newest.</summary>
    public const int MaxLogLines = 500;

    private const string RetryCountParameter = "RetryCount";

    private ChainFlowchart(JobChainInfo chain, List<ChainFlowLevel> levels, List<JobChainStepInfo> hidden, List<(ChainFlowStep Step, JobConsoleEntry Entry)> logs, int logLines)
    {
        Chain = chain;
        Levels = levels;
        Hidden = hidden;
        Logs = logs;
        LogLineCount = logLines;
    }

    public JobChainInfo Chain { get; }
    public IReadOnlyList<ChainFlowLevel> Levels { get; }

    /// <summary>The steps past <see cref="MaxSteps"/>, not drawn.</summary>
    public IReadOnlyList<JobChainStepInfo> Hidden { get; }

    /// <summary>The newest console lines of the drawn steps, oldest first.</summary>
    public IReadOnlyList<(ChainFlowStep Step, JobConsoleEntry Entry)> Logs { get; }

    /// <summary>How many console lines the steps have in all.</summary>
    public int LogLineCount { get; }

    public int GroupCount => Levels.Count(static x => x.IsBoxed);

    public static ChainFlowchart Build(JobStorage storage, JobChainInfo chain, DateTime now)
    {
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(chain);

        IMonitoringApi monitor = storage.GetMonitoringApi();
        List<ChainFlowLevel> levels = [];
        List<JobChainStepInfo> hidden = [];
        List<(ChainFlowStep, JobConsoleEntry)> logs = [];
        int drawn = 0;
        long logLines = 0;
        bool settled = chain.IsSettled;

        using IStorageConnection connection = storage.GetReadOnlyConnection();

        foreach (IGrouping<int, JobChainStepInfo> level in chain.Steps.GroupBy(static x => x.Level))
        {
            List<JobChainStepInfo> infos = [.. level];
            if (hidden.Count > 0 || (drawn > 0 && drawn + infos.Count > MaxSteps))
            {
                hidden.AddRange(infos);
                continue;
            }

            List<ChainFlowStep> steps = [];
            foreach (JobChainStepInfo info in infos)
            {
                ChainFlowStep step = Describe(monitor, connection, info, now);

                // A settled chain runs nothing more by itself: a step still waiting waits after a failure, for a
                // requeue, or was never told to start, and a server starts it within minutes (the chain runs again)
                if (settled && step.Condition == ChainFlowCondition.Waiting)
                {
                    step.Condition = ChainFlowCondition.NotRun;
                }

                steps.Add(step);

                List<JobConsoleEntry> console = ReadConsole(storage, connection, info.JobId, out long lines);
                logs.AddRange(console.Select(entry => (step, entry)));
                logLines += lines;
            }

            levels.Add(new ChainFlowLevel(level.Key, infos[0].LevelKind, steps));
            drawn += infos.Count;
        }

        List<(ChainFlowStep, JobConsoleEntry)> newest = [.. logs
            .OrderBy(static x => x.Item2.Timestamp)
            .ThenBy(static x => x.Item2.Sequence)
            .TakeLast(MaxLogLines)];

        return new ChainFlowchart(chain, levels, hidden, newest, (int)Math.Min(int.MaxValue, logLines));
    }

    /// <summary>Where a chain, or a group of steps, stands: <see cref="ChainFlowStatus.Waiting"/> until a step starts.</summary>
    public static ChainFlowStatus StatusOf(JobChainInfo chain)
    {
        ArgumentNullException.ThrowIfNull(chain);

        if (chain.IsSettled)
        {
            return chain.FailedCount > 0 ? ChainFlowStatus.FinishedWithFailures : ChainFlowStatus.Done;
        }

        return chain.Steps.All(static x => HasNotStarted(x.State)) ? ChainFlowStatus.Waiting : ChainFlowStatus.Running;
    }

    public static ChainFlowStatus StatusOf(IReadOnlyCollection<ChainFlowStep> steps)
    {
        ArgumentNullException.ThrowIfNull(steps);

        if (steps.All(static x => x.IsSettled || x.Info.State == null))
        {
            return steps.Any(static x => IsState(x.Info.StateOrOutcome, FailedState.StateName)) ? ChainFlowStatus.FinishedWithFailures : ChainFlowStatus.Done;
        }

        return steps.All(static x => HasNotStarted(x.Info.State)) ? ChainFlowStatus.Waiting : ChainFlowStatus.Running;
    }

    public static string StatusText(ChainFlowStatus status) => status switch
    {
        ChainFlowStatus.Waiting => Strings.ChainsPage_Status_Waiting,
        ChainFlowStatus.Done => Strings.ChainsPage_Status_Done,
        ChainFlowStatus.FinishedWithFailures => Strings.ChainsPage_Status_Failed,
        _ => Strings.ChainsPage_Status_Running,
    };

    public static string StatusCss(ChainFlowStatus status) => status switch
    {
        ChainFlowStatus.Waiting => "text-bg-secondary",
        ChainFlowStatus.Done => "text-bg-success",
        ChainFlowStatus.FinishedWithFailures => "text-bg-danger",
        _ => "text-bg-info",
    };

    /// <summary>What a step waits for, as its card says it.</summary>
    public static string DescribeCondition(JobChainStepInfo step)
    {
        ArgumentNullException.ThrowIfNull(step);

        return step.Link switch
        {
            ChainLink.Root => Strings.ChainFlow_FirstStep,
            ChainLink.AfterAll => Strings.ChainFlow_AllSucceeded,
            ChainLink.AfterAny => Strings.ChainFlow_AnySucceeded,
            ChainLink.AfterAnyFailed => Strings.ChainFlow_AnyFailed,
            ChainLink.AfterAnyFailedOrCanceled => Strings.ChainFlow_AnyFailedOrCanceled,
            ChainLink.AfterAllCompleted => Strings.ChainFlow_AllFinished,
            _ => DescribeOptions(step.Options),
        };
    }

    // Every outcome the options accept, for a combination such as OnlyOnSucceededState | OnlyOnFailedState
    private static string DescribeOptions(JobContinuationOptions options)
    {
        List<string> outcomes = [];
        if (options.HasFlag(JobContinuationOptions.OnlyOnSucceededState))
        {
            outcomes.Add(Strings.ChainFlow_OnSuccess);
        }

        if (options.HasFlag(JobContinuationOptions.OnlyOnFailedState))
        {
            outcomes.Add(Strings.ChainFlow_OnFailure);
        }

        if (options.HasFlag(JobContinuationOptions.OnlyOnDeletedState))
        {
            outcomes.Add(Strings.ChainFlow_OnDeletion);
        }

        if (options.HasFlag(JobContinuationOptions.OnlyOnProcessingState))
        {
            outcomes.Add(Strings.ChainFlow_OnceStarted);
        }

        return outcomes.Count == 0 ? Strings.ChainFlow_OnAnyOutcome : string.Join(" or ", outcomes);
    }

    /// <summary>Whether the step runs when something went wrong before it, so its links are drawn in the failure color.</summary>
    public static bool IsAlertCondition(JobChainStepInfo step)
    {
        ArgumentNullException.ThrowIfNull(step);

        return step.Link is ChainLink.AfterAnyFailed or ChainLink.AfterAnyFailedOrCanceled ||
               (step.Link == ChainLink.Then && (step.Options & (JobContinuationOptions.OnlyOnFailedState | JobContinuationOptions.OnlyOnSucceededState)) == JobContinuationOptions.OnlyOnFailedState);
    }

    private static ChainFlowStep Describe(IMonitoringApi monitor, IStorageConnection connection, JobChainStepInfo info, DateTime now)
    {
        ChainFlowStep step = new(info);
        JobDetailsDto? details = TryLoad(monitor, info.JobId);
        IList<StateHistoryDto> history = details?.History ?? [];

        int processing = -1;
        for (int i = 0; i < history.Count; i++)
        {
            if (IsState(history[i].StateName, ProcessingState.StateName))
            {
                processing = i;
                break;
            }
        }

        if (processing >= 0)
        {
            step.Time = history[processing].CreatedAt;
            step.Duration = processing == 0 ? now - history[processing].CreatedAt : history[processing - 1].CreatedAt - history[processing].CreatedAt;
        }
        else
        {
            step.Time = details?.CreatedAt;
        }

        StateHistoryDto? current = history.Count > 0 ? history[0] : null;
        if (current != null)
        {
            step.Reason = current.Data != null && current.Data.TryGetValue("ExceptionMessage", out string? message) && !string.IsNullOrEmpty(message) ? message : current.Reason;

            if (IsState(current.StateName, SucceededState.StateName) && current.Data != null &&
                current.Data.TryGetValue("PerformanceDuration", out string? performed) &&
                long.TryParse(performed, NumberStyles.Integer, CultureInfo.InvariantCulture, out long milliseconds))
            {
                step.Duration = TimeSpan.FromMilliseconds(milliseconds);
            }
        }

        if (details?.Properties != null && details.Properties.TryGetValue(RetryCountParameter, out string? retries))
        {
            step.RetryCount = int.TryParse(retries, NumberStyles.Integer, CultureInfo.InvariantCulture, out int count) ? count : 0;
        }

        JobProgress? progress = TryReadProgress(connection, info.JobId);
        step.Progress = progress?.Percent ?? (IsState(info.State, SucceededState.StateName) ? 100 : null);
        step.ProgressMessage = progress?.Message;

        step.Condition = info.ParentLevel < 0 || info.State == null ? ChainFlowCondition.None
            : IsState(info.State, AwaitingState.StateName) ? ChainFlowCondition.Waiting
            : IsState(info.State, DeletedState.StateName) && processing < 0 ? ChainFlowCondition.NotRun
            : ChainFlowCondition.Met;

        return step;
    }

    private static bool HasNotStarted(string? state)
    {
        return IsState(state, EnqueuedState.StateName) || IsState(state, ScheduledState.StateName) || IsState(state, AwaitingState.StateName);
    }

    private static bool IsState(string? state, string name) => name.Equals(state, StringComparison.OrdinalIgnoreCase);

    private static JobDetailsDto? TryLoad(IMonitoringApi monitor, string jobId)
    {
        try
        {
            return monitor.JobDetails(jobId);
        }
        catch (Exception ex) when (ex.IsCatchableExceptionType())
        {
            return null;
        }
    }

    private static JobProgress? TryReadProgress(IStorageConnection connection, string jobId)
    {
        try
        {
            return JobConsole.ReadProgress(connection, jobId);
        }
        catch (Exception ex) when (ex.IsCatchableExceptionType())
        {
            return null;
        }
    }

    /// <summary>
    /// The newest <see cref="MaxLogLines"/> lines of a step's console, and how many it has. A storage with ranges
    /// reads only those; another one reads the whole console, which its job keeps short.
    /// </summary>
    private static List<JobConsoleEntry> ReadConsole(JobStorage storage, IStorageConnection connection, string jobId, out long total)
    {
        try
        {
            if (connection is JobStorageConnection storageConnection && storage.HasFeature(JobStorageFeatures.ExtendedApi))
            {
                // The newest lines come first in the list
                string key = JobConsole.GetConsoleKey(jobId);
                total = storageConnection.GetListCount(key);
                return total == 0 ? [] : Parse(storageConnection.GetRangeFromList(key, 0, MaxLogLines - 1));
            }

            List<JobConsoleEntry> entries = [.. JobConsole.ReadEntries(connection, jobId)];
            total = entries.Count;
            return entries;
        }
        catch (Exception ex) when (ex.IsCatchableExceptionType())
        {
            total = 0;
            return [];
        }
    }

    private static List<JobConsoleEntry> Parse(List<string> items)
    {
        List<JobConsoleEntry> entries = new(items.Count);
        foreach (string item in items)
        {
            try
            {
                if (SerializationHelper.Deserialize<JobConsoleEntry>(item, SerializationOption.Internal) is { } entry)
                {
                    entries.Add(entry);
                }
            }
            catch (Exception ex) when (ex.IsCatchableExceptionType())
            {
                // A line in a format this version doesn't know is left out, as on the job's page
            }
        }

        return entries;
    }
}

/// <summary>
/// Draws a <see cref="ChainFlowchart"/>: cards and group boxes are HTML placed at computed positions, the
/// connectors an SVG layer behind them. The page's script zooms the stage and makes it full screen.
/// </summary>
internal static class ChainFlowchartRenderer
{
    internal const int CardWidth = 256;
    internal const int CardHeight = 162;
    private const int Gap = 20;
    private const int BoxPadding = 18;
    private const int BoxHeader = 44;
    private const int BusSpace = 26;
    private const int BoxFooter = 34;
    private const int LevelGap = 46;
    private const int Margin = 24;
    private const int MaxPerRow = 5;

    /// <summary>How long an argument is shown, and all the arguments of a step, at most.</summary>
    internal const int MaxArgumentLength = 64;

    /// <inheritdoc cref="MaxArgumentLength"/>
    internal const int MaxArgumentsLength = 200;

    public static NonEscapedString Render(RazorPage page, ChainFlowchart chart)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(chart);

        List<Block> blocks = [.. chart.Levels.Select(static x => new Block(x))];
        int width = Margin * 2 + (blocks.Count > 0 ? blocks.Max(static x => x.Width) : CardWidth);
        int y = Margin;
        foreach (Block block in blocks)
        {
            block.Place((width - block.Width) / 2, y);
            y += block.Height + LevelGap;
        }

        int height = blocks.Count > 0 ? y - LevelGap + Margin : Margin * 2;

        StringBuilder html = new();
        html.Append(FormattableString.Invariant($"<div class=\"hf-flow-viewport js-flow-viewport\"><div class=\"hf-flow-sizer js-flow-sizer\" style=\"width: {width}px; height: {height}px\"><div class=\"hf-flow-stage js-flow-stage\" data-width=\"{width}\" data-height=\"{height}\" style=\"width: {width}px; height: {height}px\">"));

        foreach (Block block in blocks.Where(static x => x.Level.IsBoxed))
        {
            AppendBox(html, block);
        }

        html.Append(FormattableString.Invariant($"<svg class=\"hf-flow-lines\" width=\"{width}\" height=\"{height}\" viewBox=\"0 0 {width} {height}\" aria-hidden=\"true\">"));
        for (int i = 0; i < blocks.Count; i++)
        {
            if (i > 0)
            {
                AppendLink(html, blocks[i - 1], blocks[i]);
            }

            AppendBus(html, blocks[i]);
        }

        html.Append("</svg>");

        bool canRetry = !page.IsReadOnly && chart.Chain.IsSettled;
        foreach (Block block in blocks)
        {
            for (int i = 0; i < block.Level.Steps.Count; i++)
            {
                AppendCard(html, page, block.Level.Steps[i], block.Cards[i], canRetry ? chart.Chain.ChainId : null);
            }
        }

        html.Append("</div></div></div>");
        return new NonEscapedString(html.ToString());
    }

    public static NonEscapedString RenderLogs(RazorPage page, ChainFlowchart chart)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(chart);

        StringBuilder html = new();
        if (chart.Logs.Count == 0)
        {
            html.Append("<div class=\"job-console-empty text-muted\">").Append(Encode(Strings.ChainLogs_Empty)).Append("</div>");
            return new NonEscapedString(html.ToString());
        }

        foreach ((ChainFlowStep step, JobConsoleEntry entry) in chart.Logs)
        {
            string time = JobConsole.FromTimestamp(entry.Timestamp).ToString("HH:mm:ss", CultureInfo.InvariantCulture);
            string css = entry.Checkpoint != null ? "job-console-checkpoint" : "job-console-" + (entry.Level?.ToString() ?? string.Empty).ToLowerInvariant();
            string source = $"{step.Info.Key} {StepName(page, step.Info)}";

            // The time (the browser shows it in its own time zone) and the text: the two columns of the console's grid
            html.Append("<div class=\"job-console-line ").Append(Encode(css)).Append("\">");
            html.Append("<span class=\"job-console-time\" data-live-time=\"").Append(entry.Timestamp.ToString(CultureInfo.InvariantCulture))
                .Append("\" data-moment-local=\"").Append((entry.Timestamp / 1000.0).ToString(CultureInfo.InvariantCulture))
                .Append("\" data-moment-local-format=\"timeWithSeconds\">").Append(time).Append("</span>");
            html.Append("<span class=\"job-console-text\"><a class=\"hf-chain-log-step\" href=\"").Append(Encode(page.Url.JobDetails(step.Info.JobId))).Append("\">").Append(Encode(source)).Append("</a> ");
            if (entry.Checkpoint != null)
            {
                html.Append(page.Html.Icon("flag-fill")).Append(' ');
            }

            html.Append(Encode(entry.Checkpoint ?? entry.Message ?? string.Empty));
            html.Append("</span></div>");
        }

        return new NonEscapedString(html.ToString());
    }

    private static void AppendBox(StringBuilder html, Block block)
    {
        ChainFlowLevel level = block.Level;
        ChainFlowStatus status = ChainFlowchart.StatusOf(level.Steps);
        string title = level.Kind == ChainLevelKind.Branches ? Strings.ChainFlow_Group : Strings.ChainFlow_Outcomes;

        html.Append(FormattableString.Invariant($"<div class=\"hf-flow-box hf-flow-box-{(level.Kind == ChainLevelKind.Branches ? "group" : "outcomes")}\" style=\"left: {block.X}px; top: {block.Y}px; width: {block.Width}px; height: {block.Height}px\">"));
        html.Append("<div class=\"hf-flow-box-head\"><span class=\"hf-flow-box-title\">").Append(Encode(title)).Append("</span>");
        html.Append("<span class=\"badge ").Append(ChainFlowchart.StatusCss(status)).Append(" text-uppercase\">").Append(Encode(ChainFlowchart.StatusText(status))).Append("</span></div>");

        if (level.Kind == ChainLevelKind.Branches)
        {
            int pending = level.Steps.Count(static x => !x.IsSettled && x.Info.State != null);
            string progress = pending == 0
                ? Strings.ChainFlow_GroupSettled
                : string.Format(CultureInfo.CurrentCulture, Strings.ChainFlow_GroupPending, pending, level.Steps.Count);
            int succeeded = level.Steps.Count(static x => SucceededState.StateName.Equals(x.Info.StateOrOutcome, StringComparison.OrdinalIgnoreCase));
            int failed = level.Steps.Count(static x => FailedState.StateName.Equals(x.Info.StateOrOutcome, StringComparison.OrdinalIgnoreCase));

            html.Append("<div class=\"hf-flow-box-foot\"><span>").Append(Encode(progress)).Append("</span><span>");
            html.Append(Encode(string.Format(CultureInfo.CurrentCulture, Strings.ChainFlow_Counts, succeeded, failed))).Append("</span></div>");
        }

        html.Append("</div>");
    }

    // From the bottom of a level to the top of the next one
    private static void AppendLink(StringBuilder html, Block from, Block to)
    {
        int x = to.X + to.Width / 2;
        bool taken = to.Level.Steps.Any(static s => s.Condition == ChainFlowCondition.Met);
        bool alert = !to.Level.IsBoxed && to.Level.Steps.All(static s => ChainFlowchart.IsAlertCondition(s.Info));

        html.Append(FormattableString.Invariant($"<path class=\"{EdgeCss(taken, alert, notRun: false)}\" d=\"M{x} {from.Y + from.Height} L{x} {to.Y}\"/>"));
    }

    // Inside a box: from its header down to every card of a single row
    private static void AppendBus(StringBuilder html, Block block)
    {
        if (!block.Level.IsBoxed || block.Rows > 1)
        {
            return;
        }

        int busY = block.Y + BoxHeader + BusSpace / 2;
        int center = block.X + block.Width / 2;
        html.Append(FormattableString.Invariant($"<path class=\"hf-flow-edge\" d=\"M{center} {block.Y + BoxHeader} L{center} {busY}\"/>"));

        if (block.Cards.Count > 1)
        {
            int left = block.Cards.Min(static c => c.X) + CardWidth / 2;
            int right = block.Cards.Max(static c => c.X) + CardWidth / 2;
            html.Append(FormattableString.Invariant($"<path class=\"hf-flow-edge\" d=\"M{left} {busY} L{right} {busY}\"/>"));
        }

        for (int i = 0; i < block.Cards.Count; i++)
        {
            ChainFlowStep step = block.Level.Steps[i];
            int x = block.Cards[i].X + CardWidth / 2;
            string css = EdgeCss(step.Condition == ChainFlowCondition.Met, ChainFlowchart.IsAlertCondition(step.Info), step.Condition == ChainFlowCondition.NotRun);
            html.Append(FormattableString.Invariant($"<path class=\"{css}\" d=\"M{x} {busY} L{x} {block.Cards[i].Y}\"/>"));
        }
    }

    private static string EdgeCss(bool taken, bool alert, bool notRun)
    {
        if (notRun)
        {
            return "hf-flow-edge hf-flow-edge-skipped";
        }

        if (!taken)
        {
            return "hf-flow-edge";
        }

        return alert ? "hf-flow-edge hf-flow-edge-alert" : "hf-flow-edge hf-flow-edge-taken";
    }

    // 'retryChain': the chain a step can be retried in, or null when none can
    private static void AppendCard(StringBuilder html, RazorPage page, ChainFlowStep step, (int X, int Y) position, string? retryChain)
    {
        JobChainStepInfo info = step.Info;
        // A step whose job expired shows what it finished as, when the chain kept it
        string state = info.State ?? (info.FinishedAs != null ? string.Format(CultureInfo.CurrentCulture, Strings.ChainFlow_FinishedAsExpired, info.FinishedAs) : Strings.Common_JobExpired);
        string color = info.StateOrOutcome != null ? JobHistoryRenderer.GetStateColorCss(info.StateOrOutcome) : "var(--ns-state-deleted)";
        bool dim = info.State == null || step.Condition == ChainFlowCondition.NotRun;
        string css = "hf-flow-card" + (dim ? " hf-flow-dim" : string.Empty) + (IsProcessing(info.State) ? " hf-flow-active" : string.Empty);

        html.Append(FormattableString.Invariant($"<div class=\"{css}\" style=\"left: {position.X}px; top: {position.Y}px; width: {CardWidth}px; height: {CardHeight}px; --hf-state: {Encode(color)}\""));
        if (!string.IsNullOrEmpty(step.Reason))
        {
            html.Append(" title=\"").Append(Encode(step.Reason)).Append('"');
        }

        html.Append('>');

        // Name and state
        string name = StepName(page, info);
        html.Append("<div class=\"hf-flow-card-head\"><a class=\"hf-flow-name\" href=\"").Append(Encode(page.Url.JobDetails(info.JobId))).Append("\" title=\"").Append(Encode(name)).Append("\">").Append(Encode(name)).Append("</a>");
        html.Append("<span class=\"hf-flow-state\">").Append(Encode(state)).Append("</span></div>");

        // Arguments
        string arguments = FormatArguments(info.Job);
        html.Append("<div class=\"hf-flow-args\" title=\"").Append(Encode(arguments)).Append("\">").Append(Encode(arguments)).Append("</div>");

        // Start condition
        html.Append("<div class=\"hf-flow-cond\"><div><div class=\"hf-flow-caption\">").Append(Encode(Strings.ChainFlow_StartCondition)).Append("</div>");
        html.Append("<div class=\"hf-flow-cond-value\">").Append(Encode(ChainFlowchart.DescribeCondition(info))).Append("</div></div>");
        string? chip = step.Condition switch
        {
            ChainFlowCondition.Met => "<span class=\"hf-flow-chip hf-flow-chip-met\">" + Encode(Strings.ChainFlow_ConditionMet) + "</span>",
            ChainFlowCondition.Waiting => "<span class=\"hf-flow-chip\">" + Encode(Strings.ChainFlow_Waiting) + "</span>",
            ChainFlowCondition.NotRun => "<span class=\"hf-flow-chip hf-flow-chip-skipped\">" + Encode(Strings.ChainFlow_NotRun) + "</span>",
            _ => null,
        };
        html.Append(chip).Append("</div>");

        // Progress
        double percent = Math.Clamp(step.Progress ?? 0, 0, 100);
        string percentText = step.Progress.HasValue ? percent.ToString("0", CultureInfo.InvariantCulture) + "%" : "–";
        html.Append("<div class=\"hf-flow-progress\"><div class=\"hf-flow-progress-label\"><span>").Append(Encode(step.ProgressMessage ?? Strings.ChainFlow_Progress)).Append("</span><span>").Append(percentText).Append("</span></div>");
        html.Append(FormattableString.Invariant($"<div class=\"hf-flow-bar\"><div style=\"width: {percent:0.#}%\"></div></div></div>"));

        // Job, time, duration, retries
        html.Append("<div class=\"hf-flow-foot\"><span class=\"hf-flow-meta\">").Append(Encode(page.Html.JobId(info.JobId).ToString()));
        if (step.Time.HasValue)
        {
            html.Append(" · ").Append(page.Html.RelativeTime(step.Time.Value).ToString());
        }

        if (step.Duration.HasValue && step.Duration.Value >= TimeSpan.Zero)
        {
            html.Append(" · ").Append(Encode(page.Html.ToHumanDuration(step.Duration.Value, false) ?? string.Empty));
        }

        html.Append("</span><span class=\"hf-flow-actions\">");
        if (step.RetryCount > 0)
        {
            string retries = string.Format(CultureInfo.CurrentCulture, Strings.ChainFlow_Retries, step.RetryCount);
            html.Append("<span class=\"hf-flow-retries\" title=\"").Append(Encode(retries)).Append("\">").Append(page.Html.Icon("arrow-repeat")).Append(' ').Append(step.RetryCount.ToString(CultureInfo.InvariantCulture)).Append("</span>");
        }

        // With the chain in the address: a step whose job expired no longer says what chain it belongs to
        if (retryChain != null && (FailedState.StateName.Equals(info.StateOrOutcome, StringComparison.OrdinalIgnoreCase) || DeletedState.StateName.Equals(info.StateOrOutcome, StringComparison.OrdinalIgnoreCase)))
        {
            html.Append("<button class=\"btn hf-btn-xs btn-primary\" data-ajax=\"").Append(Encode(page.Url.To("/chains/retry/" + RouteValues.Encode(retryChain) + "/" + RouteValues.Encode(info.JobId)))).Append("\" data-loading-text=\"").Append(Encode(Strings.ChainDetailsPage_Retrying)).Append("\">");
            html.Append(page.Html.Icon("arrow-repeat")).Append(' ').Append(Encode(Strings.ChainDetailsPage_RetryFromHere)).Append("</button>");
        }

        html.Append("</span></div></div>");
    }

    internal static string StepName(RazorPage page, JobChainStepInfo step)
    {
        return step.Job != null ? page.Html.JobName(step.Job) : step.Key;
    }

    /// <summary>
    /// The step's arguments, short: strings quoted, numbers and dates as invariant text, objects as their type, each
    /// cut at <see cref="MaxArgumentLength"/> characters and the whole at <see cref="MaxArgumentsLength"/>. The
    /// values the server supplies (the console, the context, cancellation tokens, results of earlier jobs) are left out.
    /// </summary>
    internal static string FormatArguments(Job? job)
    {
        if (job == null)
        {
            return "()";
        }

        ParameterInfo[] parameters = job.Method.GetParameters();
        IEnumerable<string> shown = job.Args
            .Select((value, index) => (Value: value, Parameter: index < parameters.Length ? parameters[index] : null))
            .Where(static x => x.Parameter == null || !IsSupplied(x.Parameter))
            .Select(static x => Shorten(FormatArgument(x.Value), MaxArgumentLength));

        return "(" + Shorten(string.Join(", ", shown), MaxArgumentsLength) + ")";
    }

    private static string Shorten(string text, int length) => text.Length <= length ? text : string.Concat(text.AsSpan(0, length - 1), "\u2026");

    private static bool IsSupplied(ParameterInfo parameter)
    {
        Type type = parameter.ParameterType;
        return type == typeof(CancellationToken) || type == typeof(IJobCancellationToken) || type == typeof(IJobConsole) || type == typeof(PerformContext) ||
               parameter.IsDefined(typeof(FromResultAttribute), inherit: true) || parameter.IsDefined(typeof(FromParameterAttribute), inherit: true);
    }

    private static string FormatArgument(object? value) => value switch
    {
        null => "null",
        string text => "\"" + text + "\"",
        bool flag => flag ? "true" : "false",
        DateTime date => date.ToString("u", CultureInfo.InvariantCulture),
        DateTimeOffset date => date.ToString("u", CultureInfo.InvariantCulture),
        Enum item => item.ToString(),
        IFormattable formattable when value.GetType().IsPrimitive || value is decimal || value is Guid || value is TimeSpan => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => "{" + value.GetType().Name + "}",
    };

    private static bool IsProcessing(string? state) => ProcessingState.StateName.Equals(state, StringComparison.OrdinalIgnoreCase);

    private static string Encode(string value) => WebUtility.HtmlEncode(value);

    /// <summary>A level placed on the stage, with the position of each card.</summary>
    private sealed class Block
    {
        public Block(ChainFlowLevel level)
        {
            Level = level;
            Columns = Math.Min(level.Steps.Count, MaxPerRow);
            Rows = (level.Steps.Count + Columns - 1) / Columns;

            int content = Columns * CardWidth + (Columns - 1) * Gap;
            int contentHeight = Rows * CardHeight + (Rows - 1) * Gap;
            Width = level.IsBoxed ? content + BoxPadding * 2 : content;
            Height = level.IsBoxed
                ? BoxHeader + (Rows == 1 ? BusSpace : BoxPadding / 2) + contentHeight + (level.Kind == ChainLevelKind.Branches ? BoxFooter : BoxPadding)
                : contentHeight;
        }

        public ChainFlowLevel Level { get; }
        public int Columns { get; }
        public int Rows { get; }
        public int Width { get; }
        public int Height { get; }
        public int X { get; private set; }
        public int Y { get; private set; }
        public List<(int X, int Y)> Cards { get; } = [];

        public void Place(int x, int y)
        {
            X = x;
            Y = y;

            int top = Level.IsBoxed ? y + BoxHeader + (Rows == 1 ? BusSpace : BoxPadding / 2) : y;
            int inner = Level.IsBoxed ? Width - BoxPadding * 2 : Width;
            int left = Level.IsBoxed ? x + BoxPadding : x;

            for (int i = 0; i < Level.Steps.Count; i++)
            {
                int row = i / Columns;
                int inRow = Math.Min(Columns, Level.Steps.Count - row * Columns);
                int rowWidth = inRow * CardWidth + (inRow - 1) * Gap;
                int column = i % Columns;
                Cards.Add((left + (inner - rowWidth) / 2 + column * (CardWidth + Gap), top + row * (CardHeight + Gap)));
            }
        }
    }
}
