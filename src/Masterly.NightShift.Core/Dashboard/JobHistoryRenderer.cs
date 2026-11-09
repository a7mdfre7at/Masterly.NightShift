// This file is part of Hangfire. Copyright © 2013-2014 Hangfire OÜ.
// 
// Hangfire is free software: you can redistribute it and/or modify
// it under the terms of the GNU Lesser General Public License as 
// published by the Free Software Foundation, either version 3 
// of the License, or any later version.
// 
// Hangfire is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU Lesser General Public License for more details.
// 
// You should have received a copy of the GNU Lesser General Public 
// License along with Hangfire. If not, see <http://www.gnu.org/licenses/>.

using System.Globalization;
using System.Text;
using Masterly.NightShift.Dashboard.Resources;

namespace Masterly.NightShift.Dashboard;

/// <summary>
/// Renders the states of a job's history on the job details page, with renderers per state name that extensions can
/// register, and holds the colors of each state.
/// </summary>
public static class JobHistoryRenderer
{
    /// <summary>How many parents of a continuation the Awaiting state links to.</summary>
    internal const int MaxParentLinks = 50;

    private static readonly IDictionary<string, Func<HtmlHelper, IDictionary<string, string>, NonEscapedString>>
        Renderers = new Dictionary<string, Func<HtmlHelper, IDictionary<string, string>, NonEscapedString>>();

    private static readonly IDictionary<string, string> BackgroundStateColors
        = new Dictionary<string, string>();
    private static readonly IDictionary<string, string> ForegroundStateColors
        = new Dictionary<string, string>();
    private static readonly IDictionary<string, string> StateCssSuffixes
        = new Dictionary<string, string>();

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Performance", "CA1810:InitializeReferenceTypeStaticFieldsInline")]
    static JobHistoryRenderer()
    {
        Register(SucceededState.StateName, SucceededRenderer);
        Register(FailedState.StateName, FailedRenderer);
        Register(ProcessingState.StateName, ProcessingRenderer);
        Register(EnqueuedState.StateName, EnqueuedRenderer);
        Register(ScheduledState.StateName, ScheduledRenderer);
        Register(DeletedState.StateName, DeletedRenderer);
        Register(AwaitingState.StateName, AwaitingRenderer);

        BackgroundStateColors.Add(EnqueuedState.StateName, "#E3F0F1");
        BackgroundStateColors.Add(SucceededState.StateName, "#E0F5EC");
        BackgroundStateColors.Add(FailedState.StateName, "#FDE8EC");
        BackgroundStateColors.Add(ProcessingState.StateName, "#FFEDE4");
        BackgroundStateColors.Add(ScheduledState.StateName, "#E7EFFD");
        BackgroundStateColors.Add(DeletedState.StateName, "#EAF3F3");
        BackgroundStateColors.Add(AwaitingState.StateName, "#E7EFFD");

        ForegroundStateColors.Add(EnqueuedState.StateName, "#0F6E80");
        ForegroundStateColors.Add(SucceededState.StateName, "#059669");
        ForegroundStateColors.Add(FailedState.StateName, "#E11D48");
        ForegroundStateColors.Add(ProcessingState.StateName, "#E2622C");
        ForegroundStateColors.Add(ScheduledState.StateName, "#2563EB");
        ForegroundStateColors.Add(DeletedState.StateName, "#5F7882");
        ForegroundStateColors.Add(AwaitingState.StateName, "#2563EB");

        StateCssSuffixes.Add(EnqueuedState.StateName, "active");
        StateCssSuffixes.Add(SucceededState.StateName, "success");
        StateCssSuffixes.Add(FailedState.StateName, "danger");
        StateCssSuffixes.Add(ProcessingState.StateName, "warning");
        StateCssSuffixes.Add(ScheduledState.StateName, "info");
        StateCssSuffixes.Add(DeletedState.StateName, "inactive");
        StateCssSuffixes.Add(AwaitingState.StateName, "info");
    }

    /// <summary>
    /// The background color of a state: its own for a built-in state, else the color of its CSS suffix (see
    /// <see cref="AddStateCssSuffix"/>), else <c>"inherit"</c>.
    /// </summary>
    public static string GetBackgroundStateColor(string stateName)
    {
        return FindBackgroundStateColor(stateName) ?? "inherit";
    }

    /// <summary>
    /// The foreground color of a state: its own for a built-in state, else the color of its CSS suffix (see
    /// <see cref="AddStateCssSuffix"/>), else <c>"inherit"</c>. Lineage diagrams and the state timeline use it.
    /// </summary>
    public static string GetForegroundStateColor(string stateName)
    {
        return FindForegroundStateColor(stateName) ?? "inherit";
    }

    /// <summary>
    /// Gives a state's labels the CSS class <c>label-state-{cssSuffix}</c> and its current-state card the class
    /// <c>state-card-state-{cssSuffix}</c>. The built-in suffixes are <c>active</c>, <c>success</c>, <c>danger</c>,
    /// <c>warning</c>, <c>info</c> and <c>inactive</c>; with one of these the state also gets the matching colors in
    /// lineage diagrams and the state timeline. Calling it again for the same state replaces the suffix.
    /// </summary>
    public static void AddStateCssSuffix(string stateName, string cssSuffix)
    {
        StateCssSuffixes[stateName] = cssSuffix;
    }

    /// <summary>
    /// The CSS suffix of a state, or <c>null</c> when none is registered. A state without a suffix gets a
    /// neutral look.
    /// </summary>
    [CanBeNull]
    public static string GetStateCssSuffix(string stateName)
    {
        if (stateName == null || !StateCssSuffixes.TryGetValue(stateName, out var suffix))
        {
            return null;
        }

        return suffix;
    }

    [CanBeNull]
    internal static string FindForegroundStateColor(string stateName)
    {
        return FindStateColor(stateName, ForegroundStateColors);
    }

    [CanBeNull]
    internal static string FindBackgroundStateColor(string stateName)
    {
        return FindStateColor(stateName, BackgroundStateColors);
    }

    // A built-in state's own color, else the color of a built-in state with the same CSS suffix (only built-in
    // states have colors, and built-in states that share a suffix share colors too)
    [CanBeNull]
    private static string FindStateColor(string stateName, IDictionary<string, string> colors)
    {
        if (stateName == null) return null;
        if (colors.TryGetValue(stateName, out var color)) return color;
        if (!StateCssSuffixes.TryGetValue(stateName, out var suffix)) return null;

        foreach (var builtIn in colors)
        {
            if (StateCssSuffixes.TryGetValue(builtIn.Key, out var builtInSuffix)
                && string.Equals(builtInSuffix, suffix, StringComparison.Ordinal))
            {
                return builtIn.Value;
            }
        }

        return null;
    }

    public static void Register(string state, Func<HtmlHelper, IDictionary<string, string>, NonEscapedString> renderer)
    {
        if (!Renderers.ContainsKey(state))
        {
            Renderers.Add(state, renderer);
        }
        else
        {
            Renderers[state] = renderer;
        }
    }

    public static bool Exists(string state)
    {
        return Renderers.ContainsKey(state);
    }

    public static NonEscapedString RenderHistory(
        this HtmlHelper helper,
        string state, IDictionary<string, string> properties)
    {
        if (!Renderers.TryGetValue(state, out var renderer))
        {
            return DefaultRenderer(helper, properties);
        }

        if (renderer == null) return null;

        try
        {
            return renderer(helper, properties);
        }
        catch (Exception ex) when (ex.IsCatchableExceptionType())
        {
            // Data this dashboard can't read (a type it doesn't have, a value written by another version)
            // must not break the whole job page: show the raw state data instead
            return DefaultRenderer(helper, properties);
        }
    }

    public static NonEscapedString NullRenderer(HtmlHelper helper, IDictionary<string, string> properties)
    {
        return null;
    }

    public static NonEscapedString DefaultRenderer(HtmlHelper helper, IDictionary<string, string> stateData)
    {
        if (stateData == null || stateData.Count == 0) return null;

        var builder = new StringBuilder();
        builder.Append("<dl class=\"dl-horizontal\">");

        foreach (var item in stateData)
        {
            builder.Append(FormattableString.Invariant($"<dt>{helper.HtmlEncode(item.Key)}</dt>"));
            builder.Append(FormattableString.Invariant($"<dd>{helper.HtmlEncode(item.Value)}</dd>"));
        }

        builder.Append("</dl>");

        return new NonEscapedString(builder.ToString());
    }

    public static NonEscapedString SucceededRenderer(HtmlHelper html, IDictionary<string, string> stateData)
    {
        var builder = new StringBuilder();
        builder.Append("<dl class=\"dl-horizontal\">");

        var itemsAdded = false;

        if (stateData.TryGetValue("Latency", out var latencyString))
        {
            var latency = TimeSpan.FromMilliseconds(long.Parse(latencyString, CultureInfo.InvariantCulture));

            builder.Append(FormattableString.Invariant($"<dt>Latency:</dt><dd>{html.HtmlEncode(html.ToHumanDuration(latency, false))}</dd>"));

            itemsAdded = true;
        }

        if (stateData.TryGetValue("PerformanceDuration", out var durationString))
        {
            var duration = TimeSpan.FromMilliseconds(long.Parse(durationString, CultureInfo.InvariantCulture));
            builder.Append(FormattableString.Invariant($"<dt>Duration:</dt><dd>{html.HtmlEncode(html.ToHumanDuration(duration, false))}</dd>"));

            itemsAdded = true;
        }


        if (stateData.TryGetValue("Result", out var resultString) && !string.IsNullOrWhiteSpace(resultString))
        {
            var result = stateData["Result"];
            builder.Append(FormattableString.Invariant($"<dt>Result:</dt><dd>{html.HtmlEncode(result)}</dd>"));

            itemsAdded = true;
        }

        builder.Append("</dl>");

        if (!itemsAdded) return null;

        return new NonEscapedString(builder.ToString());
    }

    private static NonEscapedString FailedRenderer(HtmlHelper html, IDictionary<string, string> stateData)
    {
        var builder = new StringBuilder();
        var serverId = stateData.TryGetValue("ServerId", out var value) ? $" ({html.ServerId(value)})" : null;

        builder.Append(
            FormattableString.Invariant($"<h4 class=\"exception-type\">{html.HtmlEncode(stateData["ExceptionType"])}{serverId}</h4><p class=\"text-muted\">{html.HtmlEncode(stateData["ExceptionMessage"])}</p>"));

        if (stateData.TryGetValue("ExceptionDetails", out var details) && !string.IsNullOrWhiteSpace(details))
        {
            var stackTrace = html.StackTrace(details).ToString();
            builder.Append(FormattableString.Invariant($"<pre class=\"stack-trace\">{stackTrace}</pre>"));
        }

        return new NonEscapedString(builder.ToString());
    }

    private static NonEscapedString ProcessingRenderer(HtmlHelper helper, IDictionary<string, string> stateData)
    {
        var builder = new StringBuilder();
        builder.Append("<dl class=\"dl-horizontal\">");

        if (!stateData.TryGetValue("ServerId", out var serverId))
        {
            stateData.TryGetValue("ServerName", out serverId);
        }

        if (serverId != null)
        {
            builder.Append("<dt>Server:</dt>");
            builder.Append(FormattableString.Invariant($"<dd>{helper.ServerId(serverId)}</dd>"));
        }

        if (stateData.TryGetValue("WorkerId", out var workerId))
        {
            builder.Append("<dt>Worker:</dt>");
            builder.Append(FormattableString.Invariant($"<dd>{helper.HtmlEncode(workerId[..8])}</dd>"));
        }
        else if (stateData.TryGetValue("WorkerNumber", out var workerNumber))
        {
            builder.Append("<dt>Worker:</dt>");
            builder.Append(FormattableString.Invariant($"<dd>#{helper.HtmlEncode(workerNumber)}</dd>"));
        }

        builder.Append("</dl>");

        return new NonEscapedString(builder.ToString());
    }

    private static NonEscapedString EnqueuedRenderer(HtmlHelper helper, IDictionary<string, string> stateData)
    {
        stateData.TryGetValue("Queue", out var queue);

        // The state says "default" for a job with its own queue (Enqueue("emails", ...)), which is where it
        // really went, as EnqueuedState's handler picks it
        var jobQueue = (helper.Page as Pages.JobDetailsPage)?.RenderedJob?.Queue;
        if (jobQueue != null && (string.IsNullOrEmpty(queue) || EnqueuedState.DefaultQueue.Equals(queue, StringComparison.OrdinalIgnoreCase)))
        {
            queue = jobQueue;
        }

        if (!string.IsNullOrEmpty(queue) && !EnqueuedState.DefaultQueue.Equals(queue, StringComparison.OrdinalIgnoreCase))
        {
            return new NonEscapedString(
                $"<dl class=\"dl-horizontal\"><dt>Queue:</dt><dd>{helper.QueueLabel(queue)}</dd></dl>");
        }

        return null;
    }

    private static NonEscapedString ScheduledRenderer(HtmlHelper helper, IDictionary<string, string> stateData)
    {
        var enqueueAt = JobHelper.DeserializeDateTime(stateData["EnqueueAt"]);
        stateData.TryGetValue("Queue", out var queue);

        var sb = new StringBuilder();
        sb.Append("<dl class=\"dl-horizontal\">");
        sb.Append(FormattableString.Invariant($"<dt>Enqueue at:</dt><dd data-moment=\"{helper.HtmlEncode(JobHelper.ToTimestamp(enqueueAt).ToString(CultureInfo.InvariantCulture))}\">{helper.HtmlEncode(enqueueAt.ToString(CultureInfo.CurrentCulture))}</dd>"));

        if (!string.IsNullOrWhiteSpace(queue))
        {
            sb.Append(FormattableString.Invariant($"<dt>Queue:</dt><dd>{helper.QueueLabel(queue)}</dd>"));
        }

        sb.Append("</dl>");

        return new NonEscapedString(sb.ToString());
    }

    private static NonEscapedString AwaitingRenderer(HtmlHelper helper, IDictionary<string, string> stateData)
    {
        var builder = new StringBuilder();

        builder.Append("<dl class=\"dl-horizontal\">");

        if (stateData.TryGetValue("ParentIds", out var parentIds) && !string.IsNullOrEmpty(parentIds))
        {
            // A batch continuation has a parent per job of the batch: link the first ones only
            var ids = parentIds.Split(',');
            var links = ids.Take(MaxParentLinks).Select(id => helper.JobIdLink(id).ToString());
            var more = ids.Length > MaxParentLinks
                ? " " + helper.HtmlEncode(string.Format(CultureInfo.CurrentCulture, Strings.JobHistory_MoreParents, (ids.Length - MaxParentLinks).ToString(CultureInfo.CurrentCulture)))
                : string.Empty;
            var mode = stateData.TryGetValue("Mode", out var modeValue) ? modeValue : "All";

            builder.Append(FormattableString.Invariant($"<dt>Parents</dt><dd>{string.Join(" ", links)}{more} <span class=\"label label-default\">{helper.HtmlEncode(mode)}</span></dd>"));
        }
        else if (stateData.TryGetValue("ParentId", out var parentId))
        {
            builder.Append(FormattableString.Invariant($"<dt>Parent</dt><dd>{helper.JobIdLink(parentId)}</dd>"));
        }

        if (stateData.TryGetValue("NextState", out var nextStateString))
        {
            builder.Append(FormattableString.Invariant($"<dt>Next State</dt><dd>{RenderNextState(helper, nextStateString)}</dd>"));
        }

        if (stateData.TryGetValue("Options", out var optionsDescription))
        {
            if (Enum.TryParse(optionsDescription, out JobContinuationOptions options))
            {
                optionsDescription = options.ToString("G");
            }

            builder.Append(FormattableString.Invariant($"<dt>Options</dt><dd><code>{helper.HtmlEncode(optionsDescription)}</code></dd>"));
        }

        builder.Append("</dl>");

        return new NonEscapedString(builder.ToString());
    }

    private static string RenderNextState(HtmlHelper helper, string nextStateString)
    {
        try
        {
            var nextState = SerializationHelper.Deserialize<IState>(nextStateString, SerializationOption.TypedInternal);
            return helper.StateLabel(nextState?.Name ?? "(no state)").ToString();
        }
        catch (Exception ex) when (ex.IsCatchableExceptionType())
        {
            // The state's type isn't loaded here (a custom state from another application, or one that was
            // renamed): show the type name that was stored instead
            return $"<code>{helper.HtmlEncode(GetStoredTypeName(nextStateString) ?? "(unknown state)")}</code>";
        }
    }

    [CanBeNull]
    internal static string GetStoredTypeName([CanBeNull] string serializedState)
    {
        if (string.IsNullOrEmpty(serializedState)) return null;

        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(serializedState);
            if (document.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object &&
                document.RootElement.TryGetProperty("$type", out var type) &&
                type.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                var name = type.GetString();
                var comma = name?.IndexOf(',') ?? -1;
                return comma > 0 ? name[..comma] : name;
            }
        }
        catch (System.Text.Json.JsonException)
        {
        }

        return null;
    }

    private static NonEscapedString DeletedRenderer(HtmlHelper html, IDictionary<string, string> stateData)
    {
        if (stateData.TryGetValue("Exception", out var exception))
        {
            var exceptionInfo = SerializationHelper.Deserialize<ExceptionInfo>(exception);
            if (exceptionInfo != null)
            {
                var commaIndex = exceptionInfo.Type.IndexOf(",", StringComparison.OrdinalIgnoreCase);
                var typeName = commaIndex > 0
                    ? exceptionInfo.Type[..commaIndex]
                    : exceptionInfo.Type;

                return new NonEscapedString(
                    $"<h4 class=\"exception-type\">{html.HtmlEncode(typeName)}</h4><p class=\"text-muted\">{html.HtmlEncode(exceptionInfo.Message)}</p>");
            }
        }

        return null;
    }
}
