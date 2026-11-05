// This file is part of Hangfire. Copyright © 2026 Hangfire OÜ.
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
    /// is gone.
    /// </summary>
    internal sealed class JobExportDispatcher : IDashboardDispatcher
    {
        public Task Dispatch(DashboardContext context)
        {
            var jobId = Uri.UnescapeDataString(context.UriMatch.Groups["JobId"].Value);
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
                parameters = parameters.Where(static p => p.Key != JobNotes.ParameterName).ToDictionary(static p => p.Key, static p => p.Value),
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
    /// and reports. Columns: id, state, job, time, duration or delay in seconds, exception type, message.
    /// </summary>
    internal sealed class JobListExportDispatcher : IDashboardDispatcher
    {
        private static readonly char[] CsvSpecialCharacters = { ',', '"', '\r', '\n' };

        public const int MaxRows = 10000;
        private const int PageSize = 500;

        private readonly string _state;

        public JobListExportDispatcher(string state)
        {
            _state = state;
        }

        public Task Dispatch(DashboardContext context)
        {
            var monitor = context.Storage.GetMonitoringApi();
            var queue = context.UriMatch.Groups["Queue"].Success ? Uri.UnescapeDataString(context.UriMatch.Groups["Queue"].Value) : null;
            var builder = new StringBuilder();

            builder.Append("Id,State,Job,Time,Seconds,ExceptionType,ExceptionMessage\r\n");

            var rows = 0;
            for (var from = 0; from < MaxRows; from += PageSize)
            {
                var count = Math.Min(PageSize, MaxRows - from);
                var page = Fetch(monitor, queue, from, count);
                if (page.Count == 0) break;

                foreach (var row in page)
                {
                    builder.Append(Csv(row.Id)).Append(',').Append(Csv(row.State)).Append(',').Append(Csv(row.Job)).Append(',')
                        .Append(Csv(row.Time?.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture))).Append(',')
                        .Append(row.Seconds.HasValue ? row.Seconds.Value.ToString("0.###", CultureInfo.InvariantCulture) : String.Empty).Append(',')
                        .Append(Csv(row.ExceptionType)).Append(',').Append(Csv(row.ExceptionMessage)).Append("\r\n");
                    rows++;
                }

                if (page.Count < count) break;
            }

            context.Response.StatusCode = 200;
            context.Response.ContentType = "text/csv; charset=utf-8";
            return context.Response.WriteAsync(builder.ToString());
        }

        private List<Row> Fetch(IMonitoringApi monitor, string? queue, int from, int count)
        {
            switch (_state)
            {
                case "failed":
                    return monitor.FailedJobs(from, count).Select(static p => new Row(p.Key, p.Value == null ? null : p.Value.InFailedState ? "Failed" : "Changed", p.Value?.Job?.ToString(), p.Value?.FailedAt, null, p.Value?.ExceptionType, p.Value?.ExceptionMessage)).ToList();
                case "succeeded":
                    return monitor.SucceededJobs(from, count).Select(static p => new Row(p.Key, p.Value == null ? null : p.Value.InSucceededState ? "Succeeded" : "Changed", p.Value?.Job?.ToString(), p.Value?.SucceededAt, p.Value?.TotalDuration / 1000.0, null, null)).ToList();
                case "scheduled":
                    return monitor.ScheduledJobs(from, count).Select(static p => new Row(p.Key, p.Value == null ? null : p.Value.InScheduledState ? "Scheduled" : "Changed", p.Value?.Job?.ToString(), p.Value?.EnqueueAt, p.Value == null ? null : (p.Value.EnqueueAt - DateTime.UtcNow).TotalSeconds, null, null)).ToList();
                case "processing":
                    return monitor.ProcessingJobs(from, count).Select(static p => new Row(p.Key, p.Value == null ? null : p.Value.InProcessingState ? "Processing" : "Changed", p.Value?.Job?.ToString(), p.Value?.StartedAt, p.Value?.StartedAt == null ? null : (DateTime.UtcNow - p.Value.StartedAt.Value).TotalSeconds, p.Value?.ServerId, null)).ToList();
                case "deleted":
                    return monitor is JobStorageMonitor storageMonitor
                        ? storageMonitor.DeletedJobs(from, count).Select(static p => new Row(p.Key, p.Value == null ? null : p.Value.InDeletedState ? "Deleted" : "Changed", p.Value?.Job?.ToString(), p.Value?.DeletedAt, null, null, null)).ToList()
                        : new List<Row>();
                case "enqueued":
                    return monitor.EnqueuedJobs(queue ?? "default", from, count).Select(static p => new Row(p.Key, p.Value?.State, p.Value?.Job?.ToString(), p.Value?.EnqueuedAt, p.Value?.EnqueuedAt == null ? null : (DateTime.UtcNow - p.Value.EnqueuedAt.Value).TotalSeconds, null, null)).ToList();
                default:
                    return new List<Row>();
            }
        }

        internal static string Csv(string? value)
        {
            if (String.IsNullOrEmpty(value)) return String.Empty;

            var needsQuotes = value!.IndexOfAny(CsvSpecialCharacters) >= 0;
            var escaped = value.Replace("\"", "\"\"");

            // A leading formula character would be executed by spreadsheets
            if (escaped.Length > 0 && (escaped[0] == '=' || escaped[0] == '+' || escaped[0] == '-' || escaped[0] == '@'))
            {
                escaped = "'" + escaped;
                needsQuotes = true;
            }

            return needsQuotes ? "\"" + escaped + "\"" : escaped;
        }

        private sealed class Row
        {
            public Row(string id, string? state, string? job, DateTime? time, double? seconds, string? exceptionType, string? exceptionMessage)
            {
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
            public string? ExceptionType { get; }
            public string? ExceptionMessage { get; }
        }
    }
}
