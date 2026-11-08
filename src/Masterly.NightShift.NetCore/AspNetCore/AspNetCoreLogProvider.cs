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

namespace Masterly.NightShift.AspNetCore
{
    /// <summary>
    /// Writes Masterly.NightShift's log entries to <c>Microsoft.Extensions.Logging</c>. <c>AddNightShift</c>
    /// installs it when the container has an <see cref="ILoggerFactory"/>.
    /// </summary>
    public class AspNetCoreLogProvider : ILogProvider
    {
        private readonly ILoggerFactory _loggerFactory;

        /// <summary>
        /// Initializes the provider.
        /// </summary>
        /// <param name="loggerFactory">Creates the loggers.</param>
        public AspNetCoreLogProvider([NotNull] ILoggerFactory loggerFactory)
        {
            if (loggerFactory == null) throw new ArgumentNullException(nameof(loggerFactory));
            _loggerFactory = loggerFactory;
        }

        /// <summary>
        /// Gets a logger whose category is <paramref name="name"/>.
        /// </summary>
        /// <param name="name">The logger name.</param>
        public ILog GetLogger(string name)
        {
            return new AspNetCoreLog(_loggerFactory.CreateLogger(name));
        }
    }
}