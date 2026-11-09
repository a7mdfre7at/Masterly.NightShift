// This file is part of Hangfire.
// Copyright © 2020 Hangfire OÜ.
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

using Masterly.NightShift.Storage.Monitoring;

namespace Masterly.NightShift.Dashboard;

/// <summary>
/// What a job details renderer, added with <c>UseJobDetailsRenderer</c>, gets: the page, the job identifier and the
/// job's details.
/// </summary>
/// <param name="Page">The job details page that renders.</param>
/// <param name="JobId">The identifier of the job.</param>
/// <param name="JobDetails">The job's details, as the storage returned them.</param>
public sealed record JobDetailsRendererDto([NotNull] RazorPage Page, [NotNull] string JobId, [NotNull] JobDetailsDto JobDetails)
{
    /// <summary>The job details page that renders.</summary>
    public RazorPage Page { get; } = Page ?? throw new ArgumentNullException(nameof(Page));

    /// <summary>The identifier of the job.</summary>
    public string JobId { get; } = JobId ?? throw new ArgumentNullException(nameof(JobId));

    /// <summary>The job's details, as the storage returned them.</summary>
    public JobDetailsDto JobDetails { get; } = JobDetails ?? throw new ArgumentNullException(nameof(JobDetails));
}

internal static class JobDetailsRenderer
{
    private static readonly NightShiftLock SyncRoot = new();
    private static readonly List<Tuple<int, Func<JobDetailsRendererDto, NonEscapedString>>> Renderers =
        [];

    public static IEnumerable<Tuple<int, Func<JobDetailsRendererDto, NonEscapedString>>> GetRenderers()
    {
        lock (SyncRoot)
        {
            return Renderers.AsReadOnly();
        }
    }

    public static void AddRenderer(int order, Func<JobDetailsRendererDto, NonEscapedString> renderer)
    {
        lock (SyncRoot)
        {
            Renderers.Add(Tuple.Create(order, renderer));
            Renderers.Sort(static (x, y) => x.Item1.CompareTo(y.Item1));
        }
    }
}