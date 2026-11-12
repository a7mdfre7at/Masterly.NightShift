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

using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Reflection;

namespace Masterly.NightShift.Common;

/// <summary>
/// Represents the collection of filter providers for the application.
/// </summary>
public class JobFilterProviderCollection : Collection<IJobFilterProvider>, IJobFilterProvider
{
    private static readonly JobFilterComparer FilterComparer = new();

    // The filters of a job method, kept until any filter collection changes. Only used while every provider is
    // one of the built-in ones, whose answer depends on the job's type and method alone.
    private readonly ConcurrentDictionary<(Type, MethodInfo), CachedFilters> _cache = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="JobFilterProviderCollection"/> 
    /// class.
    /// </summary>
    public JobFilterProviderCollection()
    {
    }

    public JobFilterProviderCollection(params IJobFilterProvider[] providers)
        : base(providers)
    {
    }

    /// <summary>
    /// Returns the collection of filter providers.
    /// </summary>
    /// <param name="job">Job, can be null.</param>
    /// <returns>The collection of filter providers.</returns>
    /// <remarks>The result is shared between calls: treat it as read-only.</remarks>
    public IEnumerable<JobFilter> GetFilters(Job? job)
    {
        if (job == null || !IsCacheable())
        {
            return CombineFilters(job);
        }

        int version = JobFilterVersion.Current;
        (Type, MethodInfo) key = (job.Type, job.Method);

        if (_cache.TryGetValue(key, out CachedFilters? cached) && cached.Version == version)
        {
            return cached.Filters;
        }

        // The version was read before the filters: a change made meanwhile makes this entry stale, not wrong
        JobFilter[] filters = [.. CombineFilters(job)];
        _cache[key] = new CachedFilters(version, filters);
        return filters;
    }

    /// <inheritdoc />
    protected override void InsertItem(int index, IJobFilterProvider item)
    {
        base.InsertItem(index, item);
        JobFilterVersion.Increment();
    }

    /// <inheritdoc />
    protected override void SetItem(int index, IJobFilterProvider item)
    {
        base.SetItem(index, item);
        JobFilterVersion.Increment();
    }

    /// <inheritdoc />
    protected override void RemoveItem(int index)
    {
        base.RemoveItem(index);
        JobFilterVersion.Increment();
    }

    /// <inheritdoc />
    protected override void ClearItems()
    {
        base.ClearItems();
        JobFilterVersion.Increment();
    }

    private bool IsCacheable()
    {
        for (int i = 0; i < Items.Count; i++)
        {
            IJobFilterProvider provider = Items[i];
            bool builtIn = provider.GetType() == typeof(JobFilterCollection)
                || (provider.GetType() == typeof(JobFilterAttributeFilterProvider)
                    && ((JobFilterAttributeFilterProvider)provider).CachesAttributeInstances);

            if (!builtIn)
            {
                return false;
            }
        }

        return true;
    }

    private List<JobFilter> CombineFilters(Job? job)
    {
        List<JobFilter> combinedFilters = [];

        for (int i = 0; i < Items.Count; i++)
        {
            combinedFilters.AddRange(Items[i].GetFilters(job));
        }

        if (combinedFilters.Count > 1)
        {
            // Sorting before removing duplicates in the correct order
            combinedFilters = [.. combinedFilters.OrderBy(static filter => filter, FilterComparer)];
            RemoveDuplicates(combinedFilters);
        }

        return combinedFilters;
    }

    private static void RemoveDuplicates(List<JobFilter> filters)
    {
        HashSet<Type> visitedTypes = [];

        // Remove duplicates from the back forward
        for (int i = filters.Count - 1; i >= 0; i--)
        {
            object filterInstance = filters[i].Instance;

            if (!visitedTypes.Add(filterInstance.GetType()) && !AllowMultiple(filterInstance))
            {
                filters.RemoveAt(i);
            }
        }
    }

    private static bool AllowMultiple(object filterInstance)
    {
        if (filterInstance is IJobFilter jobFilter)
        {
            return jobFilter.AllowMultiple;
        }

        return true;
    }

    private sealed record CachedFilters(int Version, JobFilter[] Filters);

    private sealed class JobFilterComparer : IComparer<JobFilter>
    {
        public int Compare(JobFilter? x, JobFilter? y)
        {
            // Nulls always have to be less than non-nulls
            if (x == null && y == null)
            {
                return 0;
            }
            if (x == null)
            {
                return -1;
            }
            if (y == null)
            {
                return 1;
            }

            // Sort first by order...

            if (x.Order < y.Order)
            {
                return -1;
            }
            if (x.Order > y.Order)
            {
                return 1;
            }

            // ...then by scope

            if (x.Scope < y.Scope)
            {
                return -1;
            }
            if (x.Scope > y.Scope)
            {
                return 1;
            }

            return 0;
        }
    }
}