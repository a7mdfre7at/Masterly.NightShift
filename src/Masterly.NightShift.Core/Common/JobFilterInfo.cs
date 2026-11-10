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

using Masterly.NightShift.Client;
using Masterly.NightShift.Server;

namespace Masterly.NightShift.Common;

/// <summary>
/// Encapsulates information about the available job filters.
/// </summary>
internal readonly struct JobFilterInfo
{
    /// <summary>
    /// Initializes a new instance of the <see cref="JobFilterInfo"/> class using the specified filters collection.
    /// </summary>
    /// <param name="filters">The filters collection.</param>
    public JobFilterInfo(IEnumerable<JobFilter> filters)
    {
        // The provider collection hands out its cached array, which is used as it is
        JobFilter[] filtersList = filters as JobFilter[] ?? [.. filters];
        ClientFilters = new FilterCollection<IClientFilter>(filtersList);
        ServerFilters = new FilterCollection<IServerFilter>(filtersList);
        ElectStateFilters = new FilterCollection<IElectStateFilter>(filtersList);
        ApplyStateFilters = new FilterCollection<IApplyStateFilter>(filtersList);
        ClientExceptionFiltersReversed = new ReversedFilterCollection<IClientExceptionFilter>(filtersList);
        ServerExceptionFiltersReversed = new ReversedFilterCollection<IServerExceptionFilter>(filtersList);
    }

    /// <summary>
    /// Gets all the client filters in the application.
    /// </summary>
    /// 
    /// <returns>
    /// The client filters.
    /// </returns>
    public FilterCollection<IClientFilter> ClientFilters { get; }

    /// <summary>
    /// Gets all the server filters in the application.
    /// </summary>
    /// 
    /// <returns>
    /// The server filters.
    /// </returns>
    public FilterCollection<IServerFilter> ServerFilters { get; }

    /// <summary>
    /// Gets all the stat changing filters in the application.
    /// </summary>
    /// 
    /// <returns>
    /// The state changing filters.
    /// </returns>
    public FilterCollection<IElectStateFilter> ElectStateFilters { get; }

    /// <summary>
    /// Gets all the state changed filters in the application.
    /// </summary>
    /// 
    /// <returns>
    /// The state changed filters.
    /// </returns>
    public FilterCollection<IApplyStateFilter> ApplyStateFilters { get; }

    /// <summary>
    /// Gets all the client exception filters in the application.
    /// </summary>
    /// 
    /// <returns>
    /// The client exception filters.
    /// </returns>
    public ReversedFilterCollection<IClientExceptionFilter> ClientExceptionFiltersReversed { get; }

    /// <summary>
    /// Gets all the server exception filters in the application.
    /// </summary>
    /// 
    /// <returns>
    /// The server exception filters.
    /// </returns>
    public ReversedFilterCollection<IServerExceptionFilter> ServerExceptionFiltersReversed { get; }

    public readonly struct FilterCollection<T>(JobFilter[] filters)
    {
        public Enumerator GetEnumerator() => new(filters);

        public ref struct Enumerator(JobFilter[] filters)
        {
            private readonly JobFilter[] _filters = filters;
            private int _index = 0;

            public bool MoveNext()
            {
                JobFilter[] localFilters = _filters;

                while (_index < localFilters.Length)
                {
                    if (localFilters[_index++].Instance is T instance)
                    {
                        Current = instance;
                        return true;
                    }
                }

                return MoveNextRare();
            }

            public T Current { get; private set; } = default!; // Read only after MoveNext returned true

            private bool MoveNextRare()
            {
                _index = _filters.Length + 1;
                Current = default!; // Read only after MoveNext returned true
                return false;
            }
        }
    }

    public readonly struct ReversedFilterCollection<T>(JobFilter[] filters)
    {
        public ReversedEnumerator GetEnumerator() => new(filters);

        public ref struct ReversedEnumerator(JobFilter[] filters)
        {
            private readonly JobFilter[] _filters = filters;
            private int _index = filters.Length - 1;

            public bool MoveNext()
            {
                JobFilter[] localFilters = _filters;

                while (_index >= 0)
                {
                    if (localFilters[_index--].Instance is T instance)
                    {
                        Current = instance;
                        return true;
                    }
                }

                return MoveNextRare();
            }

            public T Current { get; private set; } = default!; // Read only after MoveNext returned true

            private bool MoveNextRare()
            {
                _index = -1;
                Current = default!; // Read only after MoveNext returned true
                return false;
            }
        }
    }
}
