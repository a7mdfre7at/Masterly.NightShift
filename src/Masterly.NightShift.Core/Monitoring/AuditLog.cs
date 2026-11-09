using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;
using Masterly.NightShift.Dashboard;
using Masterly.NightShift.Logging;

namespace Masterly.NightShift;

/// <summary>One action somebody took on the dashboard.</summary>
public sealed class AuditEntry
{
    /// <summary>When, as a Unix timestamp in seconds.</summary>
    [JsonInclude, JsonPropertyName("t")]
    public long Timestamp { get; set; }

    /// <summary>The authenticated user, when the host knows one.</summary>
    [JsonInclude, JsonPropertyName("u"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? User { get; set; }

    /// <summary>The client address.</summary>
    [JsonInclude, JsonPropertyName("ip"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RemoteIpAddress { get; set; }

    /// <summary>The dashboard path that was posted to, for example <c>/jobs/failed/requeue</c>.</summary>
    [JsonInclude, JsonPropertyName("a")]
    public string Action { get; set; } = string.Empty;

    /// <summary>What the action was about: job ids, recurring job ids, a queue, a tag. At most <see cref="AuditLog.MaxTargets"/> are kept.</summary>
    [JsonInclude, JsonPropertyName("g"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? Targets { get; set; }

    /// <summary>How many targets there were, when more than <see cref="AuditLog.MaxTargets"/>.</summary>
    [JsonInclude, JsonPropertyName("n"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? TargetCount { get; set; }

    /// <summary>A free-text detail: the reason given, the job created, the manifest outcome.</summary>
    [JsonInclude, JsonPropertyName("d"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Detail { get; set; }

    /// <summary>The HTTP status the action answered with.</summary>
    [JsonInclude, JsonPropertyName("s"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int Status { get; set; }

    /// <summary>When, as UTC.</summary>
    [JsonIgnore]
    public DateTime OccurredAt => JobHelper.FromTimestamp(Timestamp);
}

/// <summary>
/// Records who did what on the dashboard: requeues, deletes, pauses, imports, jobs run from the Catalog, in
/// the storage list <c>audit</c> (the newest <see cref="MaxEntries"/>). The Audit page shows it. The user
/// comes from <see cref="DashboardContext.GetUserName"/>, which the ASP.NET Core adapter answers from
/// <c>HttpContext.User</c>; the address from the request. <see cref="Enabled"/> turns recording off.
/// </summary>
public static class AuditLog
{
    /// <summary>The storage list the entries live in.</summary>
    public const string ListKey = "audit";

    /// <summary>How many targets of one action are kept by id.</summary>
    public const int MaxTargets = 25;

    /// <summary>How many entries are kept. Default: 1000.</summary>
    public static int MaxEntries { get; set; } = 1000;

    /// <summary>Whether dashboard actions are recorded. Default: on.</summary>
    public static bool Enabled { get; set; } = true;

    private static readonly ILog Logger = LogProvider.GetLogger(typeof(AuditLog));

    /// <summary>Replaceable for tests.</summary>
    internal static Func<DateTime> Clock { get; set; } = static () => JobClock.UtcNow;

    /// <summary>
    /// Records an action taken through the given dashboard request: writes it to the library's log at the
    /// Information level (logger <c>Masterly.NightShift.AuditLog</c>), so the record outlives the list, and adds
    /// it to the storage list. Never throws: a failing storage must not break the action, it is logged as a
    /// warning instead.
    /// </summary>
    public static void Record([NotNull] DashboardContext context, [CanBeNull] IEnumerable<string>? targets = null, [CanBeNull] string? detail = null, int status = 204)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!Enabled) return;

        var targetList = targets?.Where(static t => !string.IsNullOrEmpty(t)).ToList();

        var entry = new AuditEntry
        {
            Timestamp = JobHelper.ToTimestamp(Clock()),
            User = Truncate(context.GetUserName(), 200),
            RemoteIpAddress = Truncate(SafeRemoteIp(context), 64),
            Action = context.Request.Path ?? string.Empty,
            Targets = targetList == null || targetList.Count == 0 ? null : targetList.Take(MaxTargets).ToList(),
            TargetCount = targetList != null && targetList.Count > MaxTargets ? targetList.Count : (int?)null,
            Detail = Truncate(detail, 500),
            Status = status,
        };

        // Logged before the storage write, so it is kept even when the storage fails
        Logger.Info(FormatForLog(entry));

        try
        {
            using var connection = context.Storage.GetConnection();
            using var transaction = connection.CreateWriteTransaction();
            transaction.InsertToList(ListKey, MonitoringJson.Serialize(entry));
            transaction.TrimList(ListKey, 0, Math.Max(0, MaxEntries - 1));
            transaction.Commit();
        }
        catch (Exception ex) when (ex.IsCatchableExceptionType())
        {
            // The action already happened; losing its audit line is better than failing the request
            Logger.WarnException($"Could not add dashboard action {entry.Action} to the audit log in the storage; the Information log line written for it is its only record.", ex);
        }
    }

    /// <summary>
    /// Reads the newest entries, newest first. Returns nothing when <paramref name="count"/> is zero or less, when
    /// the connection isn't a <see cref="JobStorageConnection"/>, or when the storage can't read lists.
    /// </summary>
    public static IReadOnlyList<AuditEntry> Read([NotNull] IStorageConnection connection, int count)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (count <= 0) return [];

        if (connection is not JobStorageConnection storageConnection) return [];

        List<string> items;
        try
        {
            items = storageConnection.GetRangeFromList(ListKey, 0, count - 1) ?? [];
        }
        catch (NotSupportedException)
        {
            // A storage without the extended API (JobStorageFeatures.ExtendedApi) can't read lists
            return [];
        }

        var result = new List<AuditEntry>(items.Count);

        foreach (var item in items)
        {
            try
            {
                var entry = MonitoringJson.Deserialize<AuditEntry>(item);
                if (entry != null) result.Add(entry);
            }
            catch (Exception ex) when (ex.IsCatchableExceptionType())
            {
                // Skip lines written in a format we don't read
            }
        }

        return result;
    }

    /// <summary>How many entries are kept right now; 0 when the storage can't count lists.</summary>
    public static long Count([NotNull] IStorageConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (connection is not JobStorageConnection storageConnection) return 0;

        try
        {
            return storageConnection.GetListCount(ListKey);
        }
        catch (NotSupportedException)
        {
            // A storage without the extended API (JobStorageFeatures.ExtendedApi)
            return 0;
        }
    }

    /// <summary>A short English description of a dashboard action path, for example "Requeue failed jobs".</summary>
    public static string Describe([NotNull] string action)
    {
        ArgumentNullException.ThrowIfNull(action);

        var path = action.Trim('/');
        foreach (var pair in Descriptions)
        {
            if (path.StartsWith(pair.Key, StringComparison.OrdinalIgnoreCase)) return pair.Value;
        }

        return action;
    }

    private static readonly KeyValuePair<string, string>[] Descriptions =
    [
        new("jobs/enqueued/delete-all", "Delete every enqueued job"),
        new("jobs/enqueued/delete", "Delete enqueued jobs"),
        new("jobs/enqueued/requeue", "Requeue enqueued jobs"),
        new("jobs/scheduled/delete-all", "Delete every scheduled job"),
        new("jobs/scheduled/enqueue", "Enqueue scheduled jobs now"),
        new("jobs/scheduled/delete", "Delete scheduled jobs"),
        new("jobs/processing/delete-all", "Delete every processing job"),
        new("jobs/processing/delete", "Delete processing jobs"),
        new("jobs/processing/requeue", "Requeue processing jobs"),
        new("jobs/succeeded/delete-all", "Delete every succeeded job"),
        new("jobs/succeeded/requeue", "Requeue succeeded jobs"),
        new("jobs/failed/delete-all", "Delete every failed job"),
        // Before "jobs/failed/requeue", which is a prefix of it
        new("jobs/failed/requeue-all", "Requeue every failed job"),
        new("jobs/failed/requeue", "Requeue failed jobs"),
        new("jobs/failed/delete", "Delete failed jobs"),
        new("jobs/deleted/requeue", "Requeue deleted jobs"),
        new("jobs/awaiting/delete-all", "Delete every awaiting job"),
        new("jobs/awaiting/enqueue", "Enqueue awaiting jobs now"),
        new("jobs/awaiting/delete", "Delete awaiting jobs"),
        new("jobs/actions/requeue", "Requeue a job"),
        new("jobs/actions/delete", "Delete a job"),
        new("retries/delete-all", "Delete every retry"),
        new("recurring/remove-all", "Remove every recurring job"),
        new("recurring/remove", "Remove recurring jobs"),
        new("recurring/trigger", "Trigger recurring jobs"),
        new("recurring/pause", "Pause recurring jobs"),
        new("recurring/resume", "Resume recurring jobs"),
        new("recurring/update", "Edit a recurring job"),
        new("recurring/import", "Import recurring jobs"),
        new("maintenance/clear-jobs", "Clear all jobs"),
        new("maintenance/reset", "Reset the storage"),
        new("maintenance/pause", "Pause processing"),
        new("maintenance/resume", "Resume processing"),
        new("queues/pause", "Pause a queue"),
        new("queues/resume", "Resume a queue"),
        new("catalog/run", "Run a job from the catalog"),
        new("insights/circuits/reset", "Close a circuit"),
        // Before "tags/forget", which is a prefix of it
        new("tags/forget-expired", "Remove expired jobs from a tag"),
        new("tags/forget", "Forget a tag"),
        new("batches/requeue-failed", "Requeue a batch's failed jobs"),
        new("batches/delete", "Delete a batch's pending jobs"),
    ];

    // Unicode line and paragraph separators, which some log viewers also break lines at
    private const char LineSeparator = (char)0x2028;
    private const char ParagraphSeparator = (char)0x2029;

    private static string FormatForLog(AuditEntry entry)
    {
        var text = new StringBuilder("Dashboard action ").Append(entry.Action)
            .Append(" by ").Append(entry.User ?? "an anonymous user")
            .Append(" from ").Append(entry.RemoteIpAddress ?? "an unknown address");

        if (entry.Targets != null)
        {
            text.Append(" on ").Append(string.Join(", ", entry.Targets));
            if (entry.TargetCount != null) text.Append(" (").Append(entry.TargetCount.Value.ToString(CultureInfo.InvariantCulture)).Append(" targets)");
        }

        text.Append(": HTTP ").Append(entry.Status.ToString(CultureInfo.InvariantCulture));
        if (entry.Detail != null) text.Append(", ").Append(entry.Detail);

        // The user name, the targets and the detail (a note's text, a pause reason) come from requests: a line
        // break in them would start what looks like another entry in a plain-text log. Only the log line is
        // changed; the stored entry keeps the text as it was.
        for (var i = 0; i < text.Length; i++)
        {
            if (char.IsControl(text[i]) || text[i] == LineSeparator || text[i] == ParagraphSeparator) text[i] = ' ';
        }

        return text.Append('.').ToString();
    }

    private static string? SafeRemoteIp(DashboardContext context)
    {
        try
        {
            return context.Request.RemoteIpAddress;
        }
        catch (Exception ex) when (ex.IsCatchableExceptionType())
        {
            return null;
        }
    }

    private static string? Truncate(string? value, int max)
    {
        if (string.IsNullOrEmpty(value)) return null;
        return value.Length <= max ? value : value[..max];
    }
}
