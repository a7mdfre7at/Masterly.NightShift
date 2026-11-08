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

using System;
using System.Collections.Generic;
using System.Threading;
using Masterly.NightShift.Annotations;
using Masterly.NightShift.Common;

namespace Masterly.NightShift.Server
{
    /// <summary>
    /// What a background process gets each time it runs: the server, the storage, and the tokens that signal the
    /// server's stop and shutdown.
    /// </summary>
    public class BackgroundProcessContext
    {
        [Obsolete("Use the constructor that takes the execution id and the stopping, stopped and shutdown tokens instead. Kept for code ported from Hangfire.")]
        public BackgroundProcessContext(
            [NotNull] string serverId,
            [NotNull] JobStorage storage,
            [NotNull] IDictionary<string, object> properties,
            CancellationToken cancellationToken)
            : this(serverId, storage, properties, Guid.NewGuid(), cancellationToken, cancellationToken, cancellationToken)
        {
        }

        public BackgroundProcessContext(
            [NotNull] string serverId,
            [NotNull] JobStorage storage, 
            [NotNull] IDictionary<string, object> properties, 
            Guid executionId,
            CancellationToken stoppingToken,
            CancellationToken stoppedToken,
            CancellationToken shutdownToken)
        {
            if (serverId == null) throw new ArgumentNullException(nameof(serverId));
            if (storage == null) throw new ArgumentNullException(nameof(storage));
            if (properties == null) throw new ArgumentNullException(nameof(properties));

            ServerId = serverId;
            Storage = storage;
            ExecutionId = executionId;
            Properties = new Dictionary<string, object>(properties, StringComparer.OrdinalIgnoreCase);
            StoppingToken = stoppingToken;
            StoppedToken = stoppedToken;
            ShutdownToken = shutdownToken;
        }
        
        [NotNull]
        public string ServerId { get; }

        [NotNull]
        public IReadOnlyDictionary<string, object> Properties { get; }

        [NotNull]
        public JobStorage Storage { get; }

        public Guid ExecutionId { get; }

        [Obsolete("Use the StoppingToken property instead. Kept for code ported from Hangfire.")]
        public CancellationToken CancellationToken => StoppingToken;

        public CancellationToken StoppingToken { get; }
        public CancellationToken StoppedToken { get; }
        public CancellationToken ShutdownToken { get; }

        public bool IsStopping => StoppingToken.IsCancellationRequested || StoppedToken.IsCancellationRequested || ShutdownToken.IsCancellationRequested;
        public bool IsStopped => StoppedToken.IsCancellationRequested || ShutdownToken.IsCancellationRequested;

        [Obsolete("Use IsStopping or IsStopped properties instead. Kept for code ported from Hangfire.")]
        public bool IsShutdownRequested => StoppingToken.IsCancellationRequested;

        public void Wait(TimeSpan timeout)
        {
            StoppingToken.WaitOrThrow(timeout);
        }
    }
}