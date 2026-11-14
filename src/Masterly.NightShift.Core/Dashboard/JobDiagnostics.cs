using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using Masterly.NightShift.Dashboard.Resources;
using Masterly.NightShift.Storage.Monitoring;

namespace Masterly.NightShift.Dashboard;

internal enum JobDiagnosticSeverity
{
    Info,
    Warning,
    Problem,
}

/// <summary>One finding about a job, rendered on the details page.</summary>
/// <param name="Code">A stable identifier of the finding, for tests and CSS.</param>
/// <param name="Severity">How serious the finding is.</param>
/// <param name="Message">The finding, as HTML.</param>
/// <param name="Link">An optional dashboard URL to follow.</param>
/// <param name="LinkText">The text of <paramref name="Link"/>.</param>
internal sealed record JobDiagnostic(
    string Code,
    JobDiagnosticSeverity Severity,
    NonEscapedString Message,
    string? Link = null,
    string? LinkText = null);

/// <summary>
/// Answers "why isn't this job running?" on the details page: for an enqueued job, whether any running
/// server listens to its queue and whether processing is paused; for a scheduled one, when it is due and
/// whether that moment has passed; for an awaiting one, what its parents are doing; for a processing one,
/// whether its server is alive and whether the run is unusually long; for a failed one, why it won't be
/// retried. Open circuits are reported in every state. Every finding is a plain sentence with a link to
/// the place that fixes it.
/// </summary>
internal static partial class JobDiagnostics
{
    /// <summary>How long a job may wait in a served queue before the wait is called out.</summary>
    public static readonly TimeSpan WaitingThreshold = TimeSpan.FromMinutes(1);

    /// <summary>How late a scheduled job may be before the delayed job scheduler is suspected.</summary>
    public static readonly TimeSpan OverdueThreshold = TimeSpan.FromMinutes(1);

    /// <summary>How many parents of a continuation are read. A batch continuation waits for the whole batch.</summary>
    internal const int MaxParentsRead = 50;

    /// <summary>How many lines about single parents are shown, missing and failed ones first.</summary>
    internal const int MaxParentLines = 10;

    [GeneratedRegex("^" + AutomaticRetryAttribute.RetryAttemptReasonPrefix + @" (\d+) of (\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex RetryReasonRegex();

    public static IReadOnlyList<JobDiagnostic> Diagnose(RazorPage page, string jobId, JobDetailsDto details)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(details);
        if (details.History == null || details.History.Count == 0)
        {
            return [];
        }

        StateHistoryDto current = details.History[0];
        DateTime now = page.StorageUtcNow ?? page.ApplicationUtcNow;
        List<JobDiagnostic> findings = [];

        try
        {
            IMonitoringApi monitor = page.Storage.GetMonitoringApi();

            switch (current.StateName)
            {
                case var name when name == EnqueuedState.StateName:
                    DiagnoseEnqueued(page, monitor, details, current, now, findings);
                    break;
                case var name when name == ScheduledState.StateName:
                    DiagnoseScheduled(page, monitor, details, current, now, findings);
                    break;
                case var name when name == AwaitingState.StateName:
                    DiagnoseAwaiting(page, details, current, findings);
                    break;
                case var name when name == ProcessingState.StateName:
                    DiagnoseProcessing(page, monitor, details, current, now, findings);
                    break;
                case var name when name == FailedState.StateName:
                    DiagnoseFailed(page, details, findings);
                    break;
                default:
                    return findings;
            }

            if (details.Job != null && current.StateName != FailedState.StateName)
            {
                DiagnoseCircuits(page, details.Job, findings);
            }
        }
        catch (Exception ex) when (ex.IsCatchableExceptionType())
        {
            findings.Add(new JobDiagnostic("error", JobDiagnosticSeverity.Warning, new NonEscapedString(page.Html.HtmlEncode(string.Format(CultureInfo.CurrentCulture, Strings.Diagnostics_Error, ex.Message)))));
        }

        return findings;
    }

    private static void DiagnoseEnqueued(RazorPage page, IMonitoringApi monitor, JobDetailsDto details, StateHistoryDto current, DateTime now, List<JobDiagnostic> findings)
    {
        string queue = GetTargetQueue(details, current);
        IList<ServerDto> servers = monitor.Servers();
        TimeSpan waiting = now - current.CreatedAt;

        if (!CheckServers(page, servers, queue, now, findings))
        {
            return;
        }

        if (CheckDrain(page, findings, queue))
        {
            return;
        }

        long? length = DashboardHealth.GetQueueCounts(page.Storage, monitor)?.FirstOrDefault(x => string.Equals(x.Name, queue, StringComparison.OrdinalIgnoreCase))?.Length;

        if (waiting > WaitingThreshold)
        {
            findings.Add(new JobDiagnostic("waiting", JobDiagnosticSeverity.Warning, Format(page, Strings.Diagnostics_Waiting,
                page.Html.QueueLabel(queue), Encode(page, page.Html.ToHumanDuration(waiting, false)), length.HasValue ? length.Value.ToString(CultureInfo.CurrentCulture) : "?"),
                page.Url.To("/jobs/enqueued/" + queue), Strings.QueuesPage_Table_Queue));
        }
        else
        {
            findings.Add(new JobDiagnostic("healthy", JobDiagnosticSeverity.Info, Format(page, Strings.Diagnostics_Healthy, page.Html.QueueLabel(queue))));
        }
    }

    private static void DiagnoseScheduled(RazorPage page, IMonitoringApi monitor, JobDetailsDto details, StateHistoryDto current, DateTime now, List<JobDiagnostic> findings)
    {
        DateTime? enqueueAt = current.Data != null && current.Data.TryGetValue("EnqueueAt", out string? value) ? JobHelper.DeserializeNullableDateTime(value) : null;
        Match? retry = current.Reason != null ? RetryReasonRegex().Match(current.Reason) : null;

        if (retry != null && retry.Success)
        {
            findings.Add(new JobDiagnostic("retry", JobDiagnosticSeverity.Info, Format(page, Strings.Diagnostics_RetryPending, retry.Groups[1].Value, retry.Groups[2].Value),
                page.Url.To("/retries"), Strings.NavigationMenu_Retries));
        }
        else if (current.Reason != null && current.Reason.StartsWith(AutomaticRetryAttribute.RetryLaterReasonPrefix, StringComparison.OrdinalIgnoreCase))
        {
            findings.Add(new JobDiagnostic("retry-later", JobDiagnosticSeverity.Info, Encode(page, Strings.Diagnostics_RetryLater)));
        }

        IList<ServerDto> servers = monitor.Servers();

        if (enqueueAt.HasValue)
        {
            if (enqueueAt.Value < now - OverdueThreshold)
            {
                bool alive = servers.Any(x => IsAlive(page, x, now));
                findings.Add(new JobDiagnostic("overdue", JobDiagnosticSeverity.Problem, Format(page, alive ? Strings.Diagnostics_ScheduledOverdue : Strings.Diagnostics_ScheduledOverdue_NoServers, page.Html.RelativeTime(enqueueAt.Value)),
                    page.Url.To("/servers"), Strings.NavigationMenu_Servers));
            }
            else
            {
                findings.Add(new JobDiagnostic("due", JobDiagnosticSeverity.Info, Format(page, Strings.Diagnostics_ScheduledDue, page.Html.RelativeTime(enqueueAt.Value))));
            }
        }

        string queue = GetTargetQueue(details, current);
        CheckServers(page, servers, queue, now, findings);
        CheckDrain(page, findings, queue);
    }

    private static void DiagnoseAwaiting(RazorPage page, JobDetailsDto details, StateHistoryDto current, List<JobDiagnostic> findings)
    {
        if (current.Data == null)
        {
            return;
        }

        AwaitingJobsState? fanIn = AwaitingJobsState.FromStateData(current.Data);
        IReadOnlyList<string> parentIds = fanIn != null ? fanIn.ParentIds : current.Data.TryGetValue("ParentId", out string? parentId) && !string.IsNullOrEmpty(parentId) ? new[] { parentId } : [];
        JobContinuationOptions options = fanIn != null ? fanIn.Options : ParseOptions(current.Data);

        if (parentIds.Count == 0)
        {
            return;
        }

        // A batch continuation waits for every job of the batch, thousands maybe: read only the first parents
        int checkedCount = Math.Min(parentIds.Count, MaxParentsRead);
        int finished = 0;
        List<JobDiagnostic> problems = [];
        List<JobDiagnostic> others = [];

        using (IStorageConnection connection = page.Storage.GetReadOnlyConnection())
        {
            for (int i = 0; i < checkedCount; i++)
            {
                string id = parentIds[i];
                JobData? parent = connection.GetJobData(id);
                string? state = parent?.State;

                if (parent == null)
                {
                    problems.Add(new JobDiagnostic("parent-missing", JobDiagnosticSeverity.Problem, Format(page, Strings.Diagnostics_ParentMissing, Encode(page, page.Html.JobId(id, false).ToString()))));
                    continue;
                }

                NonEscapedString link = page.Html.JobIdLink(id);
                NonEscapedString label = page.Html.StateLabel(state);

                if (state == SucceededState.StateName || state == DeletedState.StateName)
                {
                    finished++;

                    if (fanIn == null && !ConditionMet(options, state))
                    {
                        problems.Add(new JobDiagnostic("condition-failed", JobDiagnosticSeverity.Problem, Format(page, Strings.Diagnostics_ConditionFailed, link, label)));
                    }
                    else if (fanIn == null)
                    {
                        others.Add(new JobDiagnostic("parent-finished", JobDiagnosticSeverity.Warning, Format(page, Strings.Diagnostics_ParentFinished, link, label)));
                    }
                }
                else if (state == FailedState.StateName)
                {
                    problems.Add(new JobDiagnostic("parent-failed", JobDiagnosticSeverity.Problem, Format(page, Strings.Diagnostics_ParentFailed, link)));
                }
                else if (state == ScheduledState.StateName && parent.State != null)
                {
                    StateData? parentState = connection.GetStateData(id);
                    bool retrying = parentState?.Reason != null && parentState.Reason.StartsWith(AutomaticRetryAttribute.RetryAttemptReasonPrefix, StringComparison.OrdinalIgnoreCase);
                    others.Add(new JobDiagnostic(retrying ? "parent-retrying" : "parent-waiting", JobDiagnosticSeverity.Info, Format(page, retrying ? Strings.Diagnostics_ParentRetrying : Strings.Diagnostics_ParentWaiting, link, label)));
                }
                else
                {
                    others.Add(new JobDiagnostic("parent-waiting", JobDiagnosticSeverity.Info, Format(page, Strings.Diagnostics_ParentWaiting, link, label)));
                }
            }
        }

        // Missing and failed parents first: they are what keeps the job here
        List<JobDiagnostic> lines = [.. problems, .. others];
        findings.AddRange(lines.Take(MaxParentLines));

        string? batchId = GetBatchContinuationOf(details);

        if (fanIn != null)
        {
            findings.Insert(0, SummarizeFanIn(page, fanIn, batchId, checkedCount, finished));
        }

        int notListed = Math.Max(0, lines.Count - MaxParentLines) + (parentIds.Count - checkedCount);
        if (notListed > 0)
        {
            findings.Add(new JobDiagnostic("parents-not-listed", JobDiagnosticSeverity.Info, Format(page, Strings.Diagnostics_ParentsNotListed, notListed.ToString(CultureInfo.CurrentCulture)),
                batchId != null ? page.Url.To("/batches/" + RouteValues.Encode(batchId)) : null, batchId != null ? Strings.JobDetailsPage_Batch : null));
        }
    }

    private static JobDiagnostic SummarizeFanIn(RazorPage page, AwaitingJobsState fanIn, string? batchId, int checkedCount, int finished)
    {
        int total = fanIn.ParentIds.Count;
        string mode = fanIn.Mode == FanInMode.All ? Strings.Diagnostics_FanIn_All : Strings.Diagnostics_FanIn_Any;

        if (checkedCount < total && batchId != null && page.Storage.HasFeature(JobStorageFeatures.ExtendedApi))
        {
            // The batch keeps counters, so every parent is counted without reading it
            JobBatch? batch;
            using (IStorageConnection connection = page.Storage.GetReadOnlyConnection())
            {
                batch = BatchJob.Get(connection, batchId);
            }

            if (batch != null)
            {
                checkedCount = total;
                finished = (int)Math.Min(total, batch.Succeeded + batch.Deleted);
            }
        }

        if (checkedCount < total)
        {
            return new JobDiagnostic("fan-in", JobDiagnosticSeverity.Info, Format(page, Strings.Diagnostics_FanIn_Sampled,
                finished.ToString(CultureInfo.CurrentCulture), checkedCount.ToString(CultureInfo.CurrentCulture), total.ToString(CultureInfo.CurrentCulture), mode));
        }

        return new JobDiagnostic("fan-in", JobDiagnosticSeverity.Info, Format(page, Strings.Diagnostics_FanIn,
            finished.ToString(CultureInfo.CurrentCulture), total.ToString(CultureInfo.CurrentCulture), mode));
    }

    private static string? GetBatchContinuationOf(JobDetailsDto details)
    {
        if (details.Properties == null || !details.Properties.TryGetValue(BatchJob.ContinuationParameterName, out string? value) || string.IsNullOrEmpty(value))
        {
            return null;
        }

        try
        {
            return SerializationHelper.Deserialize<string>(value, SerializationOption.User);
        }
        catch (Exception ex) when (ex.IsCatchableExceptionType())
        {
            // Not readable with these settings: the summary counts the parents it read instead
            return null;
        }
    }

    private static void DiagnoseProcessing(RazorPage page, IMonitoringApi monitor, JobDetailsDto details, StateHistoryDto current, DateTime now, List<JobDiagnostic> findings)
    {
        string? serverId = current.Data != null && current.Data.TryGetValue("ServerId", out string? value) ? value : null;
        DateTime? startedAt = current.Data != null && current.Data.TryGetValue("StartedAt", out string? started) ? JobHelper.DeserializeNullableDateTime(started) : null;
        TimeSpan running = startedAt.HasValue ? now - startedAt.Value : now - current.CreatedAt;
        ServerDto? server = serverId != null ? monitor.Servers().FirstOrDefault(x => x.Name == serverId) : null;

        if (serverId != null && server == null)
        {
            findings.Add(new JobDiagnostic("server-gone", JobDiagnosticSeverity.Problem, Format(page, Strings.Diagnostics_ServerGone, page.Html.ServerId(serverId)), page.Url.To("/servers"), Strings.NavigationMenu_Servers));
        }
        else if (server != null && !IsAlive(page, server, now))
        {
            findings.Add(new JobDiagnostic("server-stale", JobDiagnosticSeverity.Problem, Format(page, Strings.Diagnostics_ServerStale, page.Html.ServerId(serverId!), page.Html.RelativeTime(server.Heartbeat ?? server.StartedAt)), page.Url.To("/servers"), Strings.NavigationMenu_Servers)); // server was found by this id
        }

        JobInsight? insight = null;
        if (details.Job != null)
        {
            using JobStorageConnection? connection = page.Storage.GetReadOnlyConnection() as JobStorageConnection;
            if (connection != null)
            {
                insight = JobInsightsAttribute.Read(connection, JobInsightsAttribute.GetKey(details.Job));
            }
        }

        TimeSpan? usual = insight != null && insight.Durations.Length >= 3 ? TimeSpan.FromMilliseconds(insight.P95Ms) : (TimeSpan?)null;

        if (usual.HasValue && running > usual.Value + TimeSpan.FromSeconds(5) && running > TimeSpan.FromTicks((long)(usual.Value.Ticks * 1.5)))
        {
            findings.Add(new JobDiagnostic("slow", JobDiagnosticSeverity.Warning, Format(page, Strings.Diagnostics_Slow, Encode(page, page.Html.ToHumanDuration(running, false)), Encode(page, page.Html.ToHumanDuration(usual, false))),
                page.Url.To("/insights"), Strings.NavigationMenu_Insights));
        }
        else if (serverId != null && findings.Count == 0)
        {
            findings.Add(new JobDiagnostic("running", JobDiagnosticSeverity.Info, usual.HasValue
                ? Format(page, Strings.Diagnostics_Running_Usual, page.Html.ServerId(serverId), Encode(page, page.Html.ToHumanDuration(running, false)), Encode(page, page.Html.ToHumanDuration(usual, false)))
                : Format(page, Strings.Diagnostics_Running, page.Html.ServerId(serverId), Encode(page, page.Html.ToHumanDuration(running, false)))));
        }
    }

    private static void DiagnoseFailed(RazorPage page, JobDetailsDto details, List<JobDiagnostic> findings)
    {
        int retryCount = details.Properties != null && details.Properties.TryGetValue("RetryCount", out string? value) && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int count) ? count : 0;
        int retryLater = details.Properties != null && details.Properties.TryGetValue(AutomaticRetryAttribute.RetryLaterCountParameter, out string? laterValue) && int.TryParse(laterValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out int later) ? later : 0;

        if (retryCount > 0)
        {
            findings.Add(new JobDiagnostic("retries-exhausted", JobDiagnosticSeverity.Warning, Format(page, Strings.Diagnostics_RetriesExhausted, retryCount.ToString(CultureInfo.CurrentCulture))));
        }
        else
        {
            AutomaticRetryAttribute? attribute = details.Job?.Method.GetCustomAttribute<AutomaticRetryAttribute>(inherit: true) ?? details.Job?.Type.GetTypeInfo().GetCustomAttribute<AutomaticRetryAttribute>(inherit: true);
            bool disabled = attribute != null && attribute.Attempts == 0;
            findings.Add(new JobDiagnostic(disabled ? "retries-disabled" : "not-retried", JobDiagnosticSeverity.Warning, Encode(page, disabled ? Strings.Diagnostics_RetriesDisabled : Strings.Diagnostics_NotRetried)));
        }

        if (retryLater > 0)
        {
            findings.Add(new JobDiagnostic("retry-later-exhausted", JobDiagnosticSeverity.Info, Format(page, Strings.Diagnostics_RetryLaterRequests, retryLater.ToString(CultureInfo.CurrentCulture))));
        }
    }

    private static void DiagnoseCircuits(RazorPage page, Job job, List<JobDiagnostic> findings)
    {
        List<CircuitBreakerAttribute> attributes =
        [
            .. job.Method.GetCustomAttributes<CircuitBreakerAttribute>(inherit: true)
,
            .. job.Type.GetTypeInfo().GetCustomAttributes<CircuitBreakerAttribute>(inherit: true),
        ];

        if (attributes.Count == 0)
        {
            return;
        }

        using IStorageConnection connection = page.Storage.GetReadOnlyConnection();
        foreach (CircuitBreakerAttribute attribute in attributes)
        {
            string resource;
            try
            {
                resource = Server.ConcurrencyControl.FormatResourceKey(attribute.Resource, job, CircuitBreakerAttribute.MaxResourceLength);
            }
            catch (FormatException)
            {
                continue;
            }

            CircuitBreakerAttribute.CircuitState circuit = CircuitBreakerAttribute.ReadCircuit(connection, resource);
            if (circuit.State == CircuitBreakerAttribute.OpenState)
            {
                findings.Add(new JobDiagnostic("circuit-open", JobDiagnosticSeverity.Warning, Format(page, Strings.Diagnostics_CircuitOpen, Encode(page, resource), page.Html.RelativeTime(circuit.OpenedAt)),
                    page.Url.To("/insights"), Strings.NavigationMenu_Insights));
            }
        }
    }

    /// <summary>Reports servers that are missing or don't listen to the queue. Returns <c>false</c> when nothing can pick the job up.</summary>
    private static bool CheckServers(RazorPage page, IList<ServerDto> servers, string queue, DateTime now, List<JobDiagnostic> findings)
    {
        List<ServerDto> alive = [.. servers.Where(x => IsAlive(page, x, now))];

        if (alive.Count == 0)
        {
            findings.Add(new JobDiagnostic("no-servers", JobDiagnosticSeverity.Problem, Encode(page, servers.Count == 0 ? Strings.Diagnostics_NoServers : Strings.Diagnostics_ServersStale), page.Url.To("/servers"), Strings.NavigationMenu_Servers));
            return false;
        }

        List<ServerDto> listening = [.. alive.Where(x => x.Queues != null && x.Queues.Any(q => string.Equals(q, queue, StringComparison.OrdinalIgnoreCase)))];

        if (listening.Count == 0)
        {
            List<string> served = [.. alive.SelectMany(static x => x.Queues ?? Array.Empty<string>()).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(static x => x, StringComparer.OrdinalIgnoreCase)];
            findings.Add(new JobDiagnostic("queue-unserved", JobDiagnosticSeverity.Problem, Format(page, Strings.Diagnostics_QueueUnserved, page.Html.QueueLabel(queue),
                new NonEscapedString(string.Join(" ", served.Select(x => page.Html.QueueLabel(x).ToString())))), page.Url.To("/servers"), Strings.NavigationMenu_Servers));
            return false;
        }

        return true;
    }

    /// <summary>Reports drain mode and paused queues. Returns <c>true</c> when the job is held by either.</summary>
    private static bool CheckDrain(RazorPage page, List<JobDiagnostic> findings, string? queue = null)
    {
        DrainModeStatus status;
        using (IStorageConnection connection = page.Storage.GetReadOnlyConnection())
        {
            status = DrainMode.Read(connection);
        }

        if (status.Active)
        {
            string reason = string.IsNullOrEmpty(status.Reason) ? string.Empty : ": " + status.Reason;
            findings.Add(new JobDiagnostic("drain", JobDiagnosticSeverity.Warning, Format(page, Strings.Diagnostics_Drain, Encode(page, reason)), page.Url.To("/servers"), Strings.ServersPage_Resume));
            return true;
        }

        if (queue != null && status.PausedQueues.TryGetValue(queue, out string? queueReason))
        {
            string reason = string.IsNullOrEmpty(queueReason) ? string.Empty : ": " + queueReason;
            findings.Add(new JobDiagnostic("queue-paused", JobDiagnosticSeverity.Warning, Format(page, Strings.Diagnostics_QueuePaused, page.Html.QueueLabel(queue), Encode(page, reason)), page.Url.To("/jobs/enqueued"), Strings.QueuesPage_Title));
            return true;
        }

        return false;
    }

    private static string GetTargetQueue(JobDetailsDto details, StateHistoryDto current)
    {
        // The way EnqueuedState's handler picks it: a job's own queue wins over the state's "default"
        if (current.Data != null && current.Data.TryGetValue("Queue", out string? queue) && !string.IsNullOrEmpty(queue))
        {
            return details.Job?.Queue != null && EnqueuedState.DefaultQueue.Equals(queue, StringComparison.OrdinalIgnoreCase) ? details.Job.Queue : queue;
        }

        if (details.Job?.Queue != null)
        {
            return details.Job.Queue;
        }

        QueueAttribute? attribute = details.Job?.Method.GetCustomAttribute<QueueAttribute>(inherit: true) ?? details.Job?.Type.GetTypeInfo().GetCustomAttribute<QueueAttribute>(inherit: true);
        return attribute?.Queue ?? EnqueuedState.DefaultQueue;
    }

    private static bool IsAlive(RazorPage page, ServerDto server, DateTime now)
    {
        if (!server.Heartbeat.HasValue)
        {
            return true;
        }

        return server.Heartbeat.Value >= now - page.DashboardOptions.ServerPossiblyAbortedThreshold;
    }

    private static JobContinuationOptions ParseOptions(IDictionary<string, string> data)
    {
        if (data.TryGetValue("Options", out string? value) && !string.IsNullOrEmpty(value))
        {
            try
            {
                return Enum.Parse<JobContinuationOptions>(value, ignoreCase: true);
            }
            catch (Exception ex) when (ex.IsCatchableExceptionType())
            {
                // Unknown value: behave like the default
            }
        }

        return JobContinuationOptions.OnAnyFinishedState;
    }

    private static bool ConditionMet(JobContinuationOptions options, string? parentState)
    {
        if (options.HasFlag(JobContinuationOptions.OnlyOnSucceededState))
        {
            return parentState == SucceededState.StateName;
        }

        if (options.HasFlag(JobContinuationOptions.OnlyOnDeletedState))
        {
            return parentState == DeletedState.StateName;
        }

        return true;
    }

    private static NonEscapedString Encode(RazorPage page, string? text)
    {
        return new NonEscapedString(page.Html.HtmlEncode(text));
    }

    private static NonEscapedString Format(RazorPage page, string format, params object[] arguments)
    {
        object[] encoded = [.. arguments.Select(x => x is NonEscapedString raw ? raw.ToString() : page.Html.HtmlEncode(x?.ToString() ?? string.Empty))];
        return new NonEscapedString(string.Format(CultureInfo.CurrentCulture, format, encoded));
    }
}
