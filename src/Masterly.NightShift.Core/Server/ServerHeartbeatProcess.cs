// This file is part of Hangfire. Copyright © 2019 Hangfire OÜ.
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

using System.Diagnostics;
using Masterly.NightShift.Logging;

namespace Masterly.NightShift.Server;

internal sealed class ServerHeartbeatProcess(TimeSpan interval, TimeSpan serverTimeout, Action requestRestart) : IBackgroundProcess
{
    private readonly ILog _logger = LogProvider.GetLogger(typeof(ServerHeartbeatProcess));

    private readonly TimeSpan _interval = interval;
    private readonly TimeSpan _serverTimeout = serverTimeout;
    private readonly Action _requestRestart = requestRestart;
    private Stopwatch? _faultedSince;

    // Whether a failed busy worker report was logged as a warning (once is enough; the rest go to debug)
    private bool _reportWarned;

    public void Execute(BackgroundProcessContext context)
    {
        _logger.Trace($"{BackgroundServerProcess.GetServerTemplate(context.ServerId)} waiting for {_interval} delay before sending a heartbeat");

        context.ShutdownToken.WaitOrThrow(_interval);

        try
        {
            using (IStorageConnection connection = context.Storage.GetConnection())
            {
                connection.Heartbeat(context.ServerId);
                ReportBusyWorkers(context, connection);
            }

            if (_faultedSince == null)
            {
                _logger.Debug($"{BackgroundServerProcess.GetServerTemplate(context.ServerId)} heartbeat successfully sent");
            }
            else
            {
                _logger.Info($"{BackgroundServerProcess.GetServerTemplate(context.ServerId)} is now able to continue sending heartbeats");
                _faultedSince = null;
            }
        }
        catch (BackgroundServerGoneException)
        {
            if (!context.ShutdownToken.IsCancellationRequested)
            {
                _logger.Warn($"{BackgroundServerProcess.GetServerTemplate(context.ServerId)} was considered dead by other servers, restarting...");
                _requestRestart();
            }

            return;
        }
        catch (Exception ex) when (ex.IsCatchableExceptionType())
        {
            _logger.WarnException($"{BackgroundServerProcess.GetServerTemplate(context.ServerId)} encountered an exception while sending heartbeat", ex);

            _faultedSince ??= Stopwatch.StartNew();
            if (_faultedSince.Elapsed >= _serverTimeout)
            {
                _logger.Error($"{BackgroundServerProcess.GetServerTemplate(context.ServerId)} will be restarted due to server time out");

                _requestRestart();
                return;
            }
        }
    }

    /// <summary>
    /// Writes the server's busy workers into its own field of <see cref="ServerBusyWorkers.HashKey"/>, with the
    /// time by the clock the heartbeat used, for the dashboard's Servers page. Best effort: a failure is logged and
    /// changes nothing about the heartbeat, which succeeded already.
    /// </summary>
    private void ReportBusyWorkers(BackgroundProcessContext context, IStorageConnection connection)
    {
        if (context.BusyWorkers == null)
        {
            return;
        }

        try
        {
            DateTime takenAt = StorageTime.UtcNow(context.Storage, connection);
            string value = ServerBusyWorkers.FormatValue(context.BusyWorkers.Current, takenAt, _interval);
            ServerBusyWorkers.Write(connection, context.ServerId, value);
        }
        catch (Exception ex) when (ex.IsCatchableExceptionType())
        {
            string message = $"{BackgroundServerProcess.GetServerTemplate(context.ServerId)} could not report its busy workers; the dashboard counts them from the processing jobs meanwhile";
            if (!_reportWarned)
            {
                _reportWarned = true;
                _logger.WarnException(message, ex);
            }
            else
            {
                _logger.DebugException(message, ex);
            }
        }
    }
}