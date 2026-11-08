#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text.Json.Serialization;
using System.Text.Json;
using System.Text;
using System.Threading.Tasks;
using Masterly.NightShift.Common;
using Masterly.NightShift.Server;
using Masterly.NightShift.Storage.Monitoring;
using Masterly.NightShift.Storage;

namespace Masterly.NightShift.Dashboard
{
    /// <summary>
    /// <c>GET /jobs/export/{jobId}</c>: everything the dashboard knows about one job as JSON (method, arguments,
    /// state history, parameters, console, notes), for a ticket or an incident report. <c>404</c> when the job
    /// is gone. Arguments marked <see cref="RedactedAttribute"/> are masked; when the job's method can't be
    /// loaded, the raw arguments and payload (<c>DBG_Args</c>, <c>DBG_Payload</c>) are left out, because
    /// nothing tells which of them are redacted.
    /// </summary>
    internal sealed class JobExportDispatcher : IDashboardDispatcher
    {
        /// <summary>
        /// Parameters a storage adds when the job's method can't be loaded, holding the raw arguments. Without the
        /// method, <see cref="RedactedAttribute"/> can't be read, so they may contain secrets.
        /// </summary>
        internal static readonly string[] RawArgumentParameters = { "DBG_Args", "DBG_Payload" };

        public Task Dispatch(DashboardContext context)
        {
            var jobId = RouteValues.Get(context, "JobId");
            var details = context.Storage.GetMonitoringApi().JobDetails(jobId);

            if (details == null)
            {
                context.Response.StatusCode = (int)HttpStatusCode.NotFound;
                return Task.CompletedTask;
            }

            IReadOnlyList<JobConsoleEntry> console;
            using (var connection = context.Storage.GetReadOnlyConnection())
            {
                console = JobConsole.ReadEntries(connection, jobId);
            }

            var parameters = details.Properties ?? new Dictionary<string, string>();
            var notes = parameters.TryGetValue(JobNotes.ParameterName, out var serializedNotes) ? JobNotes.Parse(serializedNotes) : Array.Empty<JobNote>();

            var report = new
            {
                id = jobId,
                exportedAt = DateTime.UtcNow,
                job = details.Job == null ? null : new
                {
                    type = details.Job.Type.AssemblyQualifiedName,
                    method = details.Job.Method.Name,
                    queue = details.Job.Queue,
                    arguments = details.Job.Method.GetParameters().Select((p, i) => new
                    {
                        name = p.Name,
                        type = p.ParameterType.FullName,
                        value = RedactedAttribute.IsRedacted(p) ? RedactedAttribute.Mask : i < details.Job.Args.Count ? details.Job.Args[i] : null,
                    }).ToArray(),
                    code = JobCodeRenderer.Render(details.Job),
                },
                loadError = details.LoadException?.Message,
                createdAt = details.CreatedAt,
                expireAt = details.ExpireAt,
                state = details.History != null && details.History.Count > 0 ? details.History[0].StateName : null,
                history = (details.History ?? new List<StateHistoryDto>()).Select(static h => new { state = h.StateName, reason = h.Reason, at = h.CreatedAt, data = h.Data }).ToArray(),
                parameters = parameters.Where(static p => p.Key != JobNotes.ParameterName && Array.IndexOf(RawArgumentParameters, p.Key) < 0).ToDictionary(static p => p.Key, static p => p.Value),
                notes = notes.Select(static n => new { at = n.WrittenAt, user = n.User, text = n.Text, acknowledges = n.Acknowledges }).ToArray(),
                console = console.Select(static e => new { at = JobConsole.FromTimestamp(e.Timestamp), level = e.Level?.ToString(), message = e.Message, checkpoint = e.Checkpoint }).ToArray(),
            };

            // Parameter names and state data keys keep their case, unlike the dashboard's usual camel-cased JSON
            context.Response.StatusCode = 200;
            context.Response.ContentType = "application/json";
            return context.Response.WriteAsync(JsonSerializer.Serialize(report, ReportSettings));
        }

        private static readonly JsonSerializerOptions ReportSettings =
            DashboardJson.Create(camelCaseDictionaryKeys: false, ignoreNulls: true, utcDates: true, indented: true);
    }

    /// <summary>
    /// <c>GET /jobs/{failed|succeeded|scheduled|processing|deleted}/export</c> and
    /// <c>/jobs/enqueued/export/{queue}</c>: the list as CSV (up to <see cref="MaxRows"/> rows), for spreadsheets
    /// and reports. Columns: <c>Id</c>, <c>State</c>, <c>Job</c>, <c>Time</c>, <c>Seconds</c> (duration or delay),
    /// <c>Server</c> (processing jobs), <c>ExceptionType</c> and <c>ExceptionMessage</c> (failed jobs, cut at
    /// <see cref="MaxMessageLength"/> characters). Each page of <see cref="PageSize"/> jobs is written as soon as
    /// it is read, so the whole file is never held in memory.
    /// </summary>
    internal sealed class JobListExportDispatcher : IDashboardDispatcher
    {
        private static readonly char[] CsvSpecialCharacters = { ',', '"', '\r', '\n' };

        public const int MaxRows = 10000;
        internal const int PageSize = 500;
        internal const int MaxMessageLength = 1000;

        private readonly string _state;

        public JobListExportDispatcher(string state)
        {
            _state = state;
        }

        public async Task Dispatch(DashboardContext context)
        {
            var monitor = context.Storage.GetMonitoringApi();
            var queue = context.UriMatch.Groups["Queue"].Success ? RouteValues.Get(context, "Queue") : null;

            // The first page is read before anything is sent, so a storage error still gives an error status
            var page = Fetch(monitor, queue, 0, PageSize);

            context.Response.StatusCode = 200;
            context.Response.ContentType = "text/csv; charset=utf-8";
            await context.Response.WriteAsync("Id,State,Job,Time,Seconds,Server,ExceptionType,ExceptionMessage\r\n").ConfigureAwait(false);

            var from = 0;
            while (page.Count > 0)
            {
                await context.Response.WriteAsync(FormatRows(page)).ConfigureAwait(false);

                from += page.Count;
                if (page.Count < PageSize || from >= MaxRows) break;

                page = Fetch(monitor, queue, from, Math.Min(PageSize, MaxRows - from));
            }
        }

        private static string FormatRows(List<Row> page)
        {
            var builder = new StringBuilder();

            foreach (var row in page)
            {
                builder.Append(Csv(row.Id)).Append(',').Append(Csv(row.State)).Append(',').Append(Csv(row.Job)).Append(',')
                    .Append(Csv(row.Time?.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture))).Append(',')
                    .Append(row.Seconds.HasValue ? row.Seconds.Value.ToString("0.###", CultureInfo.InvariantCulture) : String.Empty).Append(',')
                    .Append(Csv(row.Server)).Append(',')
                    .Append(Csv(row.ExceptionType)).Append(',').Append(Csv(Truncate(row.ExceptionMessage))).Append("\r\n");
            }

            return builder.ToString();
        }

        private static string? Truncate(string? message)
        {
            return message != null && message.Length > MaxMessageLength ? message.Substring(0, MaxMessageLength) + "…" : message;
        }

        private List<Row> Fetch(IMonitoringApi monitor, string? queue, int from, int count)
        {
            switch (_state)
            {
                case "failed":
                    return monitor.FailedJobs(from, count).Select(static p => new Row(p.Key, p.Value == null ? null : p.Value.InFailedState ? "Failed" : "Changed", p.Value?.Job?.ToString(), p.Value?.FailedAt, null, null, p.Value?.ExceptionType, p.Value?.ExceptionMessage)).ToList();
                case "succeeded":
                    return monitor.SucceededJobs(from, count).Select(static p => new Row(p.Key, p.Value == null ? null : p.Value.InSucceededState ? "Succeeded" : "Changed", p.Value?.Job?.ToString(), p.Value?.SucceededAt, p.Value?.TotalDuration / 1000.0, null, null, null)).ToList();
                case "scheduled":
                    return monitor.ScheduledJobs(from, count).Select(static p => new Row(p.Key, p.Value == null ? null : p.Value.InScheduledState ? "Scheduled" : "Changed", p.Value?.Job?.ToString(), p.Value?.EnqueueAt, p.Value == null ? null : (p.Value.EnqueueAt - DateTime.UtcNow).TotalSeconds, null, null, null)).ToList();
                case "processing":
                    return monitor.ProcessingJobs(from, count).Select(static p => new Row(p.Key, p.Value == null ? null : p.Value.InProcessingState ? "Processing" : "Changed", p.Value?.Job?.ToString(), p.Value?.StartedAt, p.Value?.StartedAt == null ? null : (DateTime.UtcNow - p.Value.StartedAt.Value).TotalSeconds, p.Value?.ServerId, null, null)).ToList();
                case "deleted":
                    return monitor is JobStorageMonitor storageMonitor
                        ? storageMonitor.DeletedJobs(from, count).Select(static p => new Row(p.Key, p.Value == null ? null : p.Value.InDeletedState ? "Deleted" : "Changed", p.Value?.Job?.ToString(), p.Value?.DeletedAt, null, null, null, null)).ToList()
                        : new List<Row>();
                case "enqueued":
                    return monitor.EnqueuedJobs(queue ?? "default", from, count).Select(static p => new Row(p.Key, p.Value?.State, p.Value?.Job?.ToString(), p.Value?.EnqueuedAt, p.Value?.EnqueuedAt == null ? null : (DateTime.UtcNow - p.Value.EnqueuedAt.Value).TotalSeconds, null, null, null)).ToList();
                default:
                    return new List<Row>();
            }
        }

        internal static string Csv(string? value)
        {
            if (String.IsNullOrEmpty(value)) return String.Empty;

            var needsQuotes = value!.IndexOfAny(CsvSpecialCharacters) >= 0;
            var escaped = value.Replace("\"", "\"\"");

            // A leading formula character would be executed by spreadsheets; some also read one after a tab or a CR
            if (escaped.Length > 0 && (escaped[0] == '=' || escaped[0] == '+' || escaped[0] == '-' || escaped[0] == '@' || escaped[0] == '\t' || escaped[0] == '\r'))
            {
                escaped = "'" + escaped;
                needsQuotes = true;
            }

            return needsQuotes ? "\"" + escaped + "\"" : escaped;
        }

        private sealed class Row
        {
            public Row(string id, string? state, string? job, DateTime? time, double? seconds, string? server, string? exceptionType, string? exceptionMessage)
            {
                Server = server;
                Id = id;
                State = state;
                Job = job;
                Time = time;
                Seconds = seconds;
                ExceptionType = exceptionType;
                ExceptionMessage = exceptionMessage;
            }

            public string Id { get; }
            public string? State { get; }
            public string? Job { get; }
            public DateTime? Time { get; }
            public double? Seconds { get; }
            public string? Server { get; }
            public string? ExceptionType { get; }
            public string? ExceptionMessage { get; }
        }
    }
}
