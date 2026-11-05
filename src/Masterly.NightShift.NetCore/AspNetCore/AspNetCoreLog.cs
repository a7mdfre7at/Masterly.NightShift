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

using System;
using Masterly.NightShift.Annotations;
using Masterly.NightShift.Logging;
using Microsoft.Extensions.Logging;
using LogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace Masterly.NightShift.AspNetCore
{
    internal sealed class AspNetCoreLog : ILog
    {
        private static readonly Func<object, Exception, string> MessageFormatterFunc = MessageFormatter;

        private readonly ILogger _targetLogger;

        public AspNetCoreLog([NotNull] ILogger targetLogger)
        {
            if (targetLogger == null) throw new ArgumentNullException(nameof(targetLogger));
            _targetLogger = targetLogger;
        }

        public bool Log(Logging.LogLevel logLevel, Func<string> messageFunc, Exception exception = null)
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
            switch (logLevel)
            {
                case Logging.LogLevel.Trace:
                    return LogLevel.Trace;
                case Logging.LogLevel.Debug:
                    return LogLevel.Debug;
                case Logging.LogLevel.Info:
                    return LogLevel.Information;
                case Logging.LogLevel.Warn:
                    return LogLevel.Warning;
                case Logging.LogLevel.Error:
                    return LogLevel.Error;
                case Logging.LogLevel.Fatal:
                    return LogLevel.Critical;
            }

            return LogLevel.None;
        }

        private static string MessageFormatter(object state, Exception exception)
        {
            return state.ToString();
        }
    }
}
