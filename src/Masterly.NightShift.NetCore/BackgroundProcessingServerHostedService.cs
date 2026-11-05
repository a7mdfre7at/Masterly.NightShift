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
    public sealed class BackgroundProcessingServerHostedService : IHostedService, IDisposable
    {
        private IBackgroundProcessingServer _server;

#if NET8_0_OR_GREATER
        public BackgroundProcessingServerHostedService([NotNull] IBackgroundProcessingServer server)
            : this(server, null)
        {
        }

        public BackgroundProcessingServerHostedService(
            [NotNull] IBackgroundProcessingServer server,
            [CanBeNull] IHostApplicationLifetime lifetime)
        {
            _server = server ?? throw new ArgumentNullException(nameof(server));
            lifetime?.ApplicationStopping.Register(server.SendStop);
        }
#else
        public BackgroundProcessingServerHostedService([NotNull] IBackgroundProcessingServer server)
        {
            _server = server ?? throw new ArgumentNullException(nameof(server));
        }
#endif

        public Task StartAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            _server?.SendStop();
            return _server?.WaitForShutdownAsync(cancellationToken) ?? Task.CompletedTask;
        }

        public void Dispose()
        {
            _server?.Dispose();
            _server = null;
        }
    }
}
