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
using System.Linq;
using Masterly.NightShift.States;

namespace Masterly.NightShift.Dashboard
{
    /// <summary>One run of a job on one worker.</summary>
    internal sealed class TimelineSpan
    {
        public TimelineSpan(string jobId, string? method, long startMs, long endMs, string? outcome)
        {
            JobId = jobId;
            Method = method;
            StartMs = startMs;
            EndMs = endMs;
            Outcome = outcome;
        }

        public string JobId { get; }
        public string? Method { get; }

        /// <summary>Unix milliseconds.</summary>
        public long StartMs { get; }

        /// <summary>Unix milliseconds; "now" for a run still going.</summary>
        public long EndMs { get; }

        /// <summary>The state the run ended in, or <c>null</c> while it is running.</summary>
        public string? Outcome { get; }

        public bool IsRunning => Outcome == null;
        public long DurationMs => Math.Max(0, EndMs - StartMs);
    }

    /// <summary>A worker's row on the timeline.</summary>
    internal sealed class TimelineLane
    {
        public TimelineLane(string serverId, string workerId)
        {
            ServerId = serverId;
            WorkerId = workerId;
            Spans = new List<TimelineSpan>();
        }

        public string ServerId { get; }
        public string WorkerId { get; }
        public List<TimelineSpan> Spans { get; }

        /// <summary>The first eight characters of the worker id, enough to tell workers apart.</summary>
        public string ShortWorkerId => WorkerId.Length > 8 ? WorkerId.Substring(0, 8) : WorkerId;
    }

    /// <summary>
    /// What every worker in the cluster did recently, reconstructed from the activity feed: each Processing
    /// transition opens a span on the worker's lane, the job's next transition closes it with its outcome.
    /// The Timeline page draws the lanes as a Gantt chart.
    /// </summary>
    internal sealed class ClusterTimeline
    {
        private ClusterTimeline(IReadOnlyList<TimelineLane> lanes, long fromMs, long toMs, int runs)
        {
            Lanes = lanes;
            FromMs = fromMs;
            ToMs = toMs;
            Runs = runs;
        }

        public IReadOnlyList<TimelineLane> Lanes { get; }

        /// <summary>The start of the drawn window, Unix milliseconds.</summary>
        public long FromMs { get; }

        /// <summary>The end of the drawn window ("now"), Unix milliseconds.</summary>
        public long ToMs { get; }

        public int Runs { get; }

        public TimeSpan Window => TimeSpan.FromMilliseconds(Math.Max(0, ToMs - FromMs));

        public bool IsEmpty => Lanes.Count == 0;

        public int Servers => Lanes.Select(static l => l.ServerId).Distinct(StringComparer.Ordinal).Count();

        /// <summary>Builds the timeline from feed entries (any order) up to <paramref name="nowMs"/>.</summary>
        public static ClusterTimeline Build(IEnumerable<ActivityEntry> entries, long nowMs, TimeSpan? maxWindow = null)
        {
            if (entries == null) throw new ArgumentNullException(nameof(entries));

            var ordered = entries.Where(static e => e.JobId != null).OrderBy(static e => e.Timestamp).ToList();
            var open = new Dictionary<string, (ActivityEntry Entry, TimelineLane Lane)>(StringComparer.Ordinal);
            var lanes = new Dictionary<string, TimelineLane>(StringComparer.Ordinal);
            var runs = 0;

            foreach (var entry in ordered)
            {
                if (entry.State == ProcessingState.StateName)
                {
                    var serverId = entry.ServerId ?? "?";
                    var workerId = entry.WorkerId ?? "?";
                    var key = serverId + "\u0001" + workerId;
                    if (!lanes.TryGetValue(key, out var lane)) lanes[key] = lane = new TimelineLane(serverId, workerId);

                    // A job fetched twice (retry) ends its previous span where the new one starts
                    if (open.TryGetValue(entry.JobId!, out var previous))
                    {
                        previous.Lane.Spans.Add(new TimelineSpan(entry.JobId!, previous.Entry.Method, previous.Entry.Timestamp, entry.Timestamp, ProcessingState.StateName));
                    }

                    open[entry.JobId!] = (entry, lane);
                    runs++;
                    continue;
                }

                if (open.TryGetValue(entry.JobId!, out var started))
                {
                    started.Lane.Spans.Add(new TimelineSpan(entry.JobId!, started.Entry.Method ?? entry.Method, started.Entry.Timestamp, Math.Max(entry.Timestamp, started.Entry.Timestamp), entry.State));
                    open.Remove(entry.JobId!);
                }
            }

            foreach (var pair in open.Values)
            {
                pair.Lane.Spans.Add(new TimelineSpan(pair.Entry.JobId!, pair.Entry.Method, pair.Entry.Timestamp, nowMs, null));
            }

            var result = lanes.Values
                .Where(static l => l.Spans.Count > 0)
                .OrderBy(static l => l.ServerId, StringComparer.Ordinal)
                .ThenBy(static l => l.WorkerId, StringComparer.Ordinal)
                .ToList();

            var fromMs = result.Count == 0 ? nowMs : result.SelectMany(static l => l.Spans).Min(static s => s.StartMs);
            if (maxWindow.HasValue && nowMs - fromMs > maxWindow.Value.TotalMilliseconds)
            {
                fromMs = nowMs - (long)maxWindow.Value.TotalMilliseconds;
                foreach (var lane in result) lane.Spans.RemoveAll(s => s.EndMs < fromMs);
                result = result.Where(static l => l.Spans.Count > 0).ToList();
            }

            return new ClusterTimeline(result, fromMs, nowMs, runs);
        }
    }
}
