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

namespace Masterly.NightShift.Dashboard;

/// <summary>
/// Builds links to dashboard pages, relative to the dashboard's path.
/// </summary>
public class UrlHelper
{
    private readonly DashboardContext _context;

    public UrlHelper([NotNull] DashboardContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _context = context;
    }

    public string To(string relativePath)
    {
        return _context.Options.PrefixPath + _context.Request.PathBase + relativePath;
    }

    public string Home()
    {
        return To("/");
    }

    /// <summary>The link to a job's page; the id is written as one path segment, with <c>%</c>, <c>/</c> and <c>~</c> as <c>~25</c>, <c>~2F</c> and <c>~7E</c>.</summary>
    public string JobDetails(string jobId)
    {
        return To("/jobs/details/" + RouteValues.Encode(jobId));
    }

    public string LinkToQueues()
    {
        return To("/jobs/enqueued");
    }

    /// <summary>The link to a queue's page; the name is written as one path segment, like a job id.</summary>
    public string Queue(string queue)
    {
        return To("/jobs/enqueued/" + RouteValues.Encode(queue));
    }
}
