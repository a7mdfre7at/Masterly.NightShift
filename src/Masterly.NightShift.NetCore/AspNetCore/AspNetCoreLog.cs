// This file is part of Hangfire. Copyright © 2016 Hangfire OÜ.
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
using Microsoft.Extensions.Logging;
using LogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace Masterly.NightShift.AspNetCore;

internal sealed class AspNetCoreLog : ILog
{
    private static readonly Func<object, Exception?, string> MessageFormatterFunc = MessageFormatter;

    private readonly ILogger _targetLogger;

    public AspNetCoreLog([NotNull] ILogger targetLogger)
    {
        ArgumentNullException.ThrowIfNull(targetLogger);
        _targetLogger = targetLogger;
    }

    public bool Log(Logging.LogLevel logLevel, Func<string> messageFunc, Exception? exception = null)
    {
        var targetLogLevel = ToTargetLogLevel(logLevel);

        // When messageFunc is null, Masterly.NightShift.Logging
        // just determines is logging enabled.
        if (messageFunc == null)
        {
            return _targetLogger.IsEnabled(targetLogLevel);
        }

        // Only build the message when it will be written
        if (!_targetLogger.IsEnabled(targetLogLevel))
        {
            return false;
        }

        _targetLogger.Log(targetLogLevel, 0, messageFunc(), exception, MessageFormatterFunc);
        return true;
    }

    private static LogLevel ToTargetLogLevel(Logging.LogLevel logLevel)
    {
        return logLevel switch
        {
            Logging.LogLevel.Trace => LogLevel.Trace,
            Logging.LogLevel.Debug => LogLevel.Debug,
            Logging.LogLevel.Info => LogLevel.Information,
            Logging.LogLevel.Warn => LogLevel.Warning,
            Logging.LogLevel.Error => LogLevel.Error,
            Logging.LogLevel.Fatal => LogLevel.Critical,
            _ => LogLevel.None,
        };
    }

    private static string MessageFormatter(object state, Exception? exception)
    {
        return state.ToString()!; // ToString of a log state
    }
}
