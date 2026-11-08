// This file is part of Hangfire.
// Copyright © 2021 Hangfire OÜ.
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
using System.Threading;
using System.Threading.Tasks;
using Masterly.NightShift.Annotations;
using Masterly.NightShift.Server;
using Microsoft.Extensions.Hosting;

namespace Masterly.NightShift
{
    /// <summary>
    /// Stops an already running <see cref="IBackgroundProcessingServer"/> with the host. The
    /// <c>AddNightShiftServer</c> overload that takes a factory registers it.
    /// </summary>
    public sealed class BackgroundProcessingServerHostedService : IHostedService, IDisposable
    {
        private IBackgroundProcessingServer _server;

        /// <summary>
        /// Initializes the service.
        /// </summary>
        /// <param name="server">The server.</param>
        public BackgroundProcessingServerHostedService([NotNull] IBackgroundProcessingServer server)
            : this(server, null)
        {
        }

        /// <summary>
        /// Initializes the service; the server gets the stop signal when the application begins to stop.
        /// </summary>
        /// <param name="server">The server.</param>
        /// <param name="lifetime">The application's lifetime, or <c>null</c>.</param>
        public BackgroundProcessingServerHostedService(
            [NotNull] IBackgroundProcessingServer server,
            [CanBeNull] IHostApplicationLifetime lifetime)
        {
            _server = server ?? throw new ArgumentNullException(nameof(server));
            lifetime?.ApplicationStopping.Register(server.SendStop);
        }

        /// <summary>
        /// Does nothing: the server already runs.
        /// </summary>
        /// <param name="cancellationToken">Not used.</param>
        public Task StartAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        /// <summary>
        /// Sends the stop signal to the server and waits for it to shut down.
        /// </summary>
        /// <param name="cancellationToken">Stops waiting.</param>
        public Task StopAsync(CancellationToken cancellationToken)
        {
            _server?.SendStop();
            return _server?.WaitForShutdownAsync(cancellationToken) ?? Task.CompletedTask;
        }

        /// <summary>
        /// Disposes the server.
        /// </summary>
        public void Dispose()
        {
            _server?.Dispose();
            _server = null;
        }
    }
}
