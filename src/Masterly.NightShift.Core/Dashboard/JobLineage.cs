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
using System.Text;
using Masterly.NightShift.Common;
using Masterly.NightShift.Dashboard.Resources;
using Masterly.NightShift.States;
using Masterly.NightShift.Storage;
using Masterly.NightShift.Storage.Monitoring;

namespace Masterly.NightShift.Dashboard
{
    /// <summary>A job in a <see cref="JobLineage"/>.</summary>
    internal sealed class JobLineageNode
    {
        public JobLineageNode(string id, int depth)
        {
            Id = id;
            Depth = depth;
        }

        public string Id { get; }

        /// <summary>0 for the job the page shows, negative for its ancestors, positive for its descendants.</summary>
        public int Depth { get; }

        /// <summary><c>Type.Method</c>, or <c>null</c> when the job expired.</summary>
        public string? Name { get; set; }

        public string? State { get; set; }

        public bool Exists => State != null || Name != null;

        /// <summary>Whether this is the job the page shows. Siblings (other parents of a fan-in child) share its depth.</summary>
        public bool IsCurrent { get; internal set; }
    }

    /// <summary>A continuation relationship: <see cref="ChildId"/> waits for <see cref="ParentId"/>.</summary>
    internal sealed class JobLineageEdge
    {
        public JobLineageEdge(string parentId, string childId, string? label)
        {
            ParentId = parentId;
            ChildId = childId;
            Label = label;
        }

        public string ParentId { get; }
        public string ChildId { get; }
        public string? Label { get; }
    }

    /// <summary>
    /// The continuation graph around one job: the parents it waited for (and theirs, up to a few levels), the
    /// continuations waiting for it (and theirs), including fan-in continuations. Built from the
    /// <c>Awaiting</c> state data of each job and the <c>Continuations</c> / <c>FanInContinuations</c>
    /// parameters, so it needs no extra storage. The details page draws it as a diagram.
    /// </summary>
    internal sealed class JobLineage
    {
        /// <summary>How many jobs are loaded at most, counting the one shown.</summary>
        public const int MaxNodes = 40;

        /// <summary>How many levels are followed in each direction.</summary>
        public const int MaxDepth = 4;

        private JobLineage(string currentId, List<JobLineageNode> nodes, List<JobLineageEdge> edges, bool truncated)
        {
            CurrentId = currentId;
            Nodes = nodes;
            Edges = edges;
            Truncated = truncated;
        }

        public string CurrentId { get; }
        public IReadOnlyList<JobLineageNode> Nodes { get; }
        public IReadOnlyList<JobLineageEdge> Edges { get; }

        /// <summary>Whether the graph was cut at <see cref="MaxNodes"/> or <see cref="MaxDepth"/>.</summary>
        public bool Truncated { get; }

        /// <summary>Whether there is anything worth drawing: at least one other job.</summary>
        public bool HasRelatives => Nodes.Count > 1;

        public static JobLineage Build(IMonitoringApi monitor, string jobId, JobDetailsDto details)
        {
            if (monitor == null) throw new ArgumentNullException(nameof(monitor));
            if (jobId == null) throw new ArgumentNullException(nameof(jobId));
            if (details == null) throw new ArgumentNullException(nameof(details));

            var nodes = new Dictionary<string, JobLineageNode>(StringComparer.Ordinal);
            var edges = new List<JobLineageEdge>();
            var edgeKeys = new HashSet<string>(StringComparer.Ordinal);
            var queue = new Queue<(string Id, int Depth, JobDetailsDto? Details)>();
            var truncated = false;

            var fanInModes = new Dictionary<string, string>(StringComparer.Ordinal);

            var current = new JobLineageNode(jobId, 0) { IsCurrent = true };
            Describe(current, details);
            nodes.Add(jobId, current);
            queue.Enqueue((jobId, 0, details));

            while (queue.Count > 0)
            {
                var (id, depth, loaded) = queue.Dequeue();
                var jobDetails = loaded ?? TryLoad(monitor, id);

                if (loaded == null && nodes.TryGetValue(id, out var node))
                {
                    Describe(node, jobDetails);
                }

                if (jobDetails == null) continue;

                // Upwards: the parents this job waited for
                if (depth <= 0)
                {
                    foreach (var (parentId, label) in GetParents(jobDetails))
                    {
                        if (!AddEdge(parentId, id, label)) continue;
                        Visit(parentId, depth - 1);
                    }
                }

                // Downwards: the continuations waiting for this job
                if (depth >= 0)
                {
                    foreach (var (childId, label) in GetChildren(jobDetails))
                    {
                        if (!AddEdge(id, childId, label)) continue;
                        Visit(childId, depth + 1);
                    }
                }

                // A fan-in child below us waits for other jobs too: show them beside their siblings
                if (depth > 0 && fanInModes.ContainsKey(id))
                {
                    foreach (var (parentId, label) in GetParents(jobDetails))
                    {
                        if (!AddEdge(parentId, id, label)) continue;
                        Visit(parentId, depth - 1);
                    }
                }
            }

            // A fan-in child carries its mode in its own Awaiting state, so its edges are labelled once it is loaded
            var finalEdges = edges
                .Select(e => e.Label == null && fanInModes.TryGetValue(e.ChildId, out var mode) ? new JobLineageEdge(e.ParentId, e.ChildId, mode) : e)
                .ToList();

            return new JobLineage(jobId, nodes.Values.OrderBy(static x => x.Depth).ThenBy(static x => x.Id, StringComparer.Ordinal).ToList(), finalEdges, truncated);

            void Describe(JobLineageNode node, JobDetailsDto? loadedDetails)
            {
                if (loadedDetails == null) return;

                node.Name = loadedDetails.Job != null
                    ? $"{JobMethodCallRenderer.GetNameWithoutGenericArity(loadedDetails.Job.Type)}.{loadedDetails.Job.Method.Name}"
                    : Strings.Common_CannotFindTargetMethod;
                node.State = loadedDetails.History != null && loadedDetails.History.Count > 0 ? loadedDetails.History[0].StateName : Strings.Common_NoState;

                if (node.State == AwaitingState.StateName && loadedDetails.History![0].Data != null)
                {
                    var fanIn = AwaitingJobsState.FromStateData(loadedDetails.History[0].Data);
                    if (fanIn != null)
                    {
                        fanInModes[node.Id] = fanIn.Mode == FanInMode.All ? Strings.Lineage_All : Strings.Lineage_Any;
                    }
                }
            }

            bool AddEdge(string parentId, string childId, string? label)
            {
                if (String.IsNullOrEmpty(parentId) || String.IsNullOrEmpty(childId)) return false;
                if (!edgeKeys.Add(parentId + "\u0001" + childId)) return false;

                edges.Add(new JobLineageEdge(parentId, childId, label));
                return true;
            }

            void Visit(string id, int depth)
            {
                if (nodes.ContainsKey(id)) return;

                if (Math.Abs(depth) > MaxDepth || nodes.Count >= MaxNodes)
                {
                    truncated = true;
                    nodes.Add(id, new JobLineageNode(id, depth) { Name = null, State = null });
                    return;
                }

                nodes.Add(id, new JobLineageNode(id, depth));
                queue.Enqueue((id, depth, null));
            }

            IEnumerable<(string, string?)> GetParents(JobDetailsDto job)
            {
                var awaiting = job.History?.FirstOrDefault(static x => x.StateName == AwaitingState.StateName);
                if (awaiting?.Data == null) yield break;

                var fanIn = AwaitingJobsState.FromStateData(awaiting.Data);
                if (fanIn != null)
                {
                    var label = fanIn.Mode == FanInMode.All ? Strings.Lineage_All : Strings.Lineage_Any;
                    foreach (var parentId in fanIn.ParentIds) yield return (parentId, label);
                    yield break;
                }

                if (awaiting.Data.TryGetValue("ParentId", out var single) && !String.IsNullOrEmpty(single))
                {
                    yield return (single, DescribeOptions(awaiting.Data.TryGetValue("Options", out var options) ? options : null));
                }
            }

            IEnumerable<(string, string?)> GetChildren(JobDetailsDto job)
            {
                if (job.Properties == null) yield break;

                if (job.Properties.TryGetValue("Continuations", out var serialized) && !String.IsNullOrEmpty(serialized))
                {
                    List<ContinuationsSupportAttribute.Continuation> continuations;
                    try
                    {
                        continuations = ContinuationsSupportAttribute.DeserializeContinuations(serialized);
                    }
                    catch (Exception ex) when (ex.IsCatchableExceptionType())
                    {
                        continuations = new List<ContinuationsSupportAttribute.Continuation>();
                    }

                    foreach (var continuation in continuations)
                    {
                        yield return (continuation.JobId, DescribeOptions(continuation.Options.ToString("G")));
                    }
                }

                if (job.Properties.TryGetValue(FanInContinuationsSupportAttribute.ParameterName, out var fanIn) && !String.IsNullOrEmpty(fanIn))
                {
                    foreach (var childId in FanInContinuationsSupportAttribute.Parse(fanIn))
                    {
                        yield return (childId, null);
                    }
                }
            }
        }

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

        private static string? DescribeOptions(string? options)
        {
            if (String.IsNullOrEmpty(options)) return null;

            // Stored as a number by AwaitingState, as a name by the Continuations parameter
            JobContinuationOptions parsed;
            try
            {
                parsed = (JobContinuationOptions)Enum.Parse(typeof(JobContinuationOptions), options, ignoreCase: true);
            }
            catch (Exception ex) when (ex.IsCatchableExceptionType())
            {
                return Strings.Lineage_AfterFinish;
            }

            if (parsed.HasFlag(JobContinuationOptions.OnlyOnSucceededState)) return Strings.Lineage_AfterSuccess;
            if (parsed.HasFlag(JobContinuationOptions.OnlyOnDeletedState)) return Strings.Lineage_AfterDelete;
            return Strings.Lineage_AfterFinish;
        }
    }

    /// <summary>Draws a <see cref="JobLineage"/> as an inline SVG: one column per depth, state-coloured cards, curved links.</summary>
    internal static class JobLineageRenderer
    {
        private const int NodeWidth = 196;
        private const int NodeHeight = 48;
        private const int ColumnGap = 84;
        private const int RowGap = 14;
        private const int Padding = 8;
        private const int MaxNameLength = 26;

        public static NonEscapedString Render(RazorPage page, JobLineage lineage)
        {
            if (page == null) throw new ArgumentNullException(nameof(page));
            if (lineage == null) throw new ArgumentNullException(nameof(lineage));

            var columns = lineage.Nodes.GroupBy(static x => x.Depth).OrderBy(static g => g.Key).Select(static g => g.ToList()).ToList();
            var tallest = columns.Max(static c => c.Count);
            var height = Padding * 2 + tallest * NodeHeight + (tallest - 1) * RowGap;
            var width = Padding * 2 + columns.Count * NodeWidth + (columns.Count - 1) * ColumnGap;

            var positions = new Dictionary<string, (double X, double Y)>(StringComparer.Ordinal);
            for (var c = 0; c < columns.Count; c++)
            {
                var column = columns[c];
                var columnHeight = column.Count * NodeHeight + (column.Count - 1) * RowGap;
                var top = Padding + (height - Padding * 2 - columnHeight) / 2.0;

                for (var r = 0; r < column.Count; r++)
                {
                    positions[column[r].Id] = (Padding + c * (NodeWidth + ColumnGap), top + r * (NodeHeight + RowGap));
                }
            }

            var svg = new StringBuilder();
            svg.Append(FormattableString.Invariant($"<svg class=\"hf-lineage-svg\" viewBox=\"0 0 {width} {height}\" width=\"{width}\" height=\"{height}\" role=\"img\" aria-label=\"{Encode(Strings.JobDetailsPage_Lineage)}\">"));

            foreach (var edge in lineage.Edges)
            {
                if (!positions.TryGetValue(edge.ParentId, out var from) || !positions.TryGetValue(edge.ChildId, out var to)) continue;

                var x1 = from.X + NodeWidth;
                var y1 = from.Y + NodeHeight / 2.0;
                var x2 = to.X;
                var y2 = to.Y + NodeHeight / 2.0;
                var dx = Math.Max(24, (x2 - x1) / 2.0);

                svg.Append(FormattableString.Invariant($"<path class=\"hf-lineage-edge\" d=\"M{x1:0.#} {y1:0.#} C{x1 + dx:0.#} {y1:0.#} {x2 - dx:0.#} {y2:0.#} {x2:0.#} {y2:0.#}\"/>"));

                if (!String.IsNullOrEmpty(edge.Label))
                {
                    svg.Append(FormattableString.Invariant($"<text class=\"hf-lineage-edge-label\" x=\"{(x1 + x2) / 2:0.#}\" y=\"{(y1 + y2) / 2 - 5:0.#}\" text-anchor=\"middle\">{Encode(edge.Label!)}</text>"));
                }
            }

            foreach (var node in lineage.Nodes)
            {
                var (x, y) = positions[node.Id];
                var color = node.State != null ? JobHistoryRenderer.GetForegroundStateColor(node.State) : "#999";
                var css = "hf-lineage-node" + (node.IsCurrent ? " hf-lineage-current" : String.Empty) + (node.Exists ? String.Empty : " hf-lineage-missing");
                var title = node.Exists ? $"{page.Html.JobId(node.Id, false)} · {node.Name} · {node.State}" : String.Format(CultureInfo.CurrentCulture, Strings.JobDetailsPage_JobExpired, node.Id);

                svg.Append(FormattableString.Invariant($"<a href=\"{Encode(page.Url.JobDetails(node.Id))}\" class=\"{css}\" style=\"--hf-state: {Encode(color)}\"><title>{Encode(title)}</title>"));
                svg.Append(FormattableString.Invariant($"<rect x=\"{x}\" y=\"{y}\" width=\"{NodeWidth}\" height=\"{NodeHeight}\" rx=\"8\" ry=\"8\"/>"));
                svg.Append(FormattableString.Invariant($"<rect class=\"hf-lineage-bar\" x=\"{x}\" y=\"{y + 6}\" width=\"4\" height=\"{NodeHeight - 12}\" rx=\"2\" ry=\"2\"/>"));
                svg.Append(FormattableString.Invariant($"<text class=\"hf-lineage-id\" x=\"{x + 14}\" y=\"{y + 19}\">{Encode(page.Html.JobId(node.Id).ToString())}</text>"));

                if (node.Exists)
                {
                    var state = node.State ?? String.Empty;
                    svg.Append(FormattableString.Invariant($"<text class=\"hf-lineage-state\" x=\"{x + NodeWidth - 10}\" y=\"{y + 19}\" text-anchor=\"end\">{Encode(state)}</text>"));
                    svg.Append(FormattableString.Invariant($"<text class=\"hf-lineage-name\" x=\"{x + 14}\" y=\"{y + 37}\">{Encode(Truncate(node.Name ?? String.Empty))}</text>"));
                }
                else
                {
                    svg.Append(FormattableString.Invariant($"<text class=\"hf-lineage-name\" x=\"{x + 14}\" y=\"{y + 37}\">{Encode(Strings.Common_JobExpired)}</text>"));
                }

                svg.Append("</a>");
            }

            svg.Append("</svg>");
            return new NonEscapedString(svg.ToString());
        }

        private static string Truncate(string value)
        {
            return value.Length <= MaxNameLength ? value : value.Substring(0, MaxNameLength - 1) + "…";
        }

        private static string Encode(string value)
        {
            return WebUtility.HtmlEncode(value);
        }
    }
}
