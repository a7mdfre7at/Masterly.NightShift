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

namespace Masterly.NightShift.Common;

/// <summary>
/// Defines a filter provider for filter attributes.
/// </summary>
/// <param name="cacheAttributeInstances">Whether the attribute instances read from a job type and method are cached.</param>
public class JobFilterAttributeFilterProvider(bool cacheAttributeInstances) : IJobFilterProvider
{
    internal bool CachesAttributeInstances { get; } = cacheAttributeInstances;

    /// <summary>
    /// Initializes a new instance of the  <see cref="JobFilterAttributeFilterProvider"/>
    /// class with the attribute instance caching enabled.
    /// </summary>
    public JobFilterAttributeFilterProvider()
        : this(true)
    {
    }

    protected virtual IEnumerable<JobFilterAttribute> GetTypeAttributes(Job job)
    {
        return job.GetTypeFilterAttributes(CachesAttributeInstances);
    }

    protected virtual IEnumerable<JobFilterAttribute> GetMethodAttributes(Job job)
    {
        return job.GetMethodFilterAttributes(CachesAttributeInstances);
    }

    public virtual IEnumerable<JobFilter> GetFilters(Job? job)
    {
        if (job == null) return [];

        IEnumerable<JobFilterAttribute> typeAttributes = GetTypeAttributes(job);
        IEnumerable<JobFilterAttribute> methodAttributes = GetMethodAttributes(job);

        List<JobFilter>? combined = null;

        foreach (JobFilterAttribute typeAttribute in typeAttributes)
        {
            (combined ??= []).Add(new JobFilter(typeAttribute, JobFilterScope.Type, null));
        }

        foreach (JobFilterAttribute methodAttribute in methodAttributes)
        {
            (combined ??= []).Add(new JobFilter(methodAttribute, JobFilterScope.Method, null));
        }

        return combined ?? Enumerable.Empty<JobFilter>();
    }
}
