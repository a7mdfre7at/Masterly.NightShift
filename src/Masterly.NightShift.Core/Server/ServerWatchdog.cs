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

using Masterly.NightShift.Logging;

namespace Masterly.NightShift.Server;

internal sealed class ServerWatchdog(TimeSpan checkInterval, TimeSpan serverTimeout) : IBackgroundProcess
{
    public static readonly TimeSpan DefaultCheckInterval = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan DefaultServerTimeout = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan MaxServerTimeout = TimeSpan.FromHours(24);
    public static readonly TimeSpan MaxServerCheckInterval = TimeSpan.FromHours(24);
    public static readonly TimeSpan MaxHeartbeatInterval = TimeSpan.FromHours(24);

    private readonly ILog _logger = LogProvider.For<ServerWatchdog>();

    private readonly TimeSpan _checkInterval = checkInterval;
    private readonly TimeSpan _serverTimeout = serverTimeout;

    public void Execute(BackgroundProcessContext context)
    {
        using (IStorageConnection connection = context.Storage.GetConnection())
        {
            int serversRemoved = connection.RemoveTimedOutServers(_serverTimeout);
            if (serversRemoved != 0)
            {
                _logger.Info($"{serversRemoved} servers were removed due to timeout");
            }

            PruneBusyWorkerReports(context, connection);
        }

        context.Wait(_checkInterval);
    }

    // The busy worker reports of servers that are gone: older than the timeout a server is removed after, so it
    // timed out or stopped without removing its own (whichever server's watchdog removed it). Best effort: a report
    // left behind is too old to count anyway
    private void PruneBusyWorkerReports(BackgroundProcessContext context, IStorageConnection connection)
    {
        try
        {
            ServerBusyWorkers.RemoveStale(connection, StorageTime.UtcNow(context.Storage, connection), _serverTimeout);
        }
        catch (Exception ex) when (ex.IsCatchableExceptionType())
        {
            _logger.DebugException("The busy worker reports of servers that are gone were not pruned", ex);
        }
    }

    public override string ToString()
    {
        return GetType().Name;
    }
}