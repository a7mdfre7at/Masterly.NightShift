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

using System.Reflection;

namespace Masterly.NightShift.Dashboard;

internal sealed class CombinedResourceDispatcher : EmbeddedResourceDispatcher
{
    private static readonly byte[] NewLine = [(byte)'\n'];

    private readonly IEnumerable<Tuple<Assembly, string>> _resources;

    public CombinedResourceDispatcher(
        [NotNull] string contentType,
        [NotNull] IEnumerable<Tuple<Assembly, string>> resources)
        : base(contentType, null, null)
    {
        ArgumentNullException.ThrowIfNull(resources);
        _resources = resources;
    }

    protected override async Task WriteResponse(DashboardResponse response)
    {
        IEnumerable<Tuple<Assembly, string>> copy;

        lock (_resources)
        {
            copy = [.. _resources];
        }

        foreach (Tuple<Assembly, string> resource in copy)
        {
            await WriteResource(
                response,
                resource.Item1,
                resource.Item2).ConfigureAwait(false);

            // A file can end with a line comment and no line break (bootstrap.bundle.min.js ends with its
            // sourceMappingURL), which would swallow the start of the next file
            await response.Body.WriteAsync(NewLine).ConfigureAwait(false);
        }
    }
}
