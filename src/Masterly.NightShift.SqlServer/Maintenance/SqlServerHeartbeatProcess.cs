// This file is part of Hangfire. Copyright © 2021 Hangfire OÜ.
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
using Masterly.NightShift.Common;
using Masterly.NightShift.Server;

namespace Masterly.NightShift.SqlServer;

internal sealed class SqlServerHeartbeatProcess : IBackgroundProcess
{
    private readonly ConcurrentDictionary<SqlServerTimeoutJob, object> _items =
        new();

    internal int TrackedCount => _items.Count;

    public void Track(SqlServerTimeoutJob item)
    {
        _items.TryAdd(item, null);
    }

    public void Untrack(SqlServerTimeoutJob item)
    {
        _items.TryRemove(item, out _);
    }

    public void Execute(BackgroundProcessContext context)
    {
        Execute(context.ShutdownToken);
    }

    public void Execute(CancellationToken cancellationToken)
    {
        foreach (var item in _items)
        {
            item.Key.ExecuteKeepAliveQueryIfRequired();
        }

        cancellationToken.Wait(TimeSpan.FromSeconds(1));
    }
}