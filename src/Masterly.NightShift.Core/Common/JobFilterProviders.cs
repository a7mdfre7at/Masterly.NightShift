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
/// Provides a registration point for filters.
/// </summary>
public static class JobFilterProviders
{
    static JobFilterProviders()
    {
        // Added one by one, like GlobalJobFilters.Filters: Providers is set before any provider is added
#pragma warning disable IDE0028 // Simplify collection initialization
        Providers = new JobFilterProviderCollection();
        Providers.Add(GlobalJobFilters.Filters);
        Providers.Add(new JobFilterAttributeFilterProvider());
#pragma warning restore IDE0028
    }

    /// <summary>
    /// Provides a registration point for filters.
    /// </summary>
    public static JobFilterProviderCollection Providers { get; }
}
