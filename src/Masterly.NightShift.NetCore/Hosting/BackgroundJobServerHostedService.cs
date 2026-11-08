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

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Masterly.NightShift.Annotations;
using Masterly.NightShift.Client;
using Masterly.NightShift.Server;
using Masterly.NightShift.States;
using Microsoft.Extensions.Hosting;

namespace Masterly.NightShift
{
    /// <summary>
    /// Runs a <see cref="BackgroundJobServer"/> as an <see cref="IHostedService"/>. <c>AddNightShiftServer</c>
    /// registers it. With an <see cref="IHostApplicationLifetime"/>, the server starts once the application has
    /// started and gets the stop signal when the application begins to stop; without one it starts at once.
    /// </summary>
    public class BackgroundJobServerHostedService : IHostedService, IDisposable
    {
        private readonly BackgroundJobServerOptions _options;
        private readonly JobStorage _storage;
        private readonly IEnumerable<IBackgroundProcess> _additionalProcesses;
        private readonly IHostApplicationLifetime _hostApplicationLifetime;
        private readonly IBackgroundJobFactory _factory;
        private readonly IBackgroundJobPerformer _performer;
        private readonly IBackgroundJobStateChanger _stateChanger;

        private IBackgroundProcessingServer _processingServer;

        /// <summary>
        /// Initializes the service, which starts the server at once.
        /// </summary>
        /// <param name="storage">The storage the server processes jobs from.</param>
        /// <param name="options">The server options.</param>
        /// <param name="additionalProcesses">Processes the server runs besides its own.</param>
        public BackgroundJobServerHostedService(
            [NotNull] JobStorage storage,
            [NotNull] BackgroundJobServerOptions options,
            [NotNull] IEnumerable<IBackgroundProcess> additionalProcesses)
#pragma warning disable 618
            : this(storage, options, additionalProcesses, null, null, null)
#pragma warning restore 618
        {
        }

        /// <summary>
        /// Initializes the service, which starts the server when the application has started.
        /// </summary>
        /// <param name="storage">The storage the server processes jobs from.</param>
        /// <param name="options">The server options.</param>
        /// <param name="additionalProcesses">Processes the server runs besides its own.</param>
        /// <param name="hostApplicationLifetime">The application's lifetime, or <c>null</c> to start the server at once.</param>
        public BackgroundJobServerHostedService(
            [NotNull] JobStorage storage,
            [NotNull] BackgroundJobServerOptions options,
            [NotNull] IEnumerable<IBackgroundProcess> additionalProcesses,
            [CanBeNull] IHostApplicationLifetime hostApplicationLifetime)
#pragma warning disable 618
            : this(storage, options, additionalProcesses, null, null, null, hostApplicationLifetime)
#pragma warning restore 618
        {
        }

        /// <summary>
        /// Initializes the service with the job factory, performer and state changer the server uses, which
        /// starts the server at once.
        /// </summary>
        /// <param name="storage">The storage the server processes jobs from.</param>
        /// <param name="options">The server options.</param>
        /// <param name="additionalProcesses">Processes the server runs besides its own.</param>
        /// <param name="factory">The job factory, or <c>null</c>.</param>
        /// <param name="performer">The job performer, or <c>null</c>.</param>
        /// <param name="stateChanger">The state changer, or <c>null</c>.</param>
        [Obsolete("This constructor uses an obsolete BackgroundJobServer constructor. Use a constructor without the factory, performer and state changer instead. Kept for code ported from Hangfire.")]
        public BackgroundJobServerHostedService(
            [NotNull] JobStorage storage,
            [NotNull] BackgroundJobServerOptions options,
            [NotNull] IEnumerable<IBackgroundProcess> additionalProcesses,
            [CanBeNull] IBackgroundJobFactory factory,
            [CanBeNull] IBackgroundJobPerformer performer,
            [CanBeNull] IBackgroundJobStateChanger stateChanger)
            : this(storage, options, additionalProcesses, factory, performer, stateChanger, null)
        {
        }

        /// <summary>
        /// Initializes the service with the job factory, performer and state changer the server uses (all three,
        /// or they are ignored), which starts the server when the application has started.
        /// </summary>
        /// <param name="storage">The storage the server processes jobs from.</param>
        /// <param name="options">The server options.</param>
        /// <param name="additionalProcesses">Processes the server runs besides its own.</param>
        /// <param name="factory">The job factory, or <c>null</c>.</param>
        /// <param name="performer">The job performer, or <c>null</c>.</param>
        /// <param name="stateChanger">The state changer, or <c>null</c>.</param>
        /// <param name="hostApplicationLifetime">The application's lifetime, or <c>null</c> to start the server at once.</param>
        [Obsolete("This constructor uses an obsolete BackgroundJobServer constructor. Use a constructor without the factory, performer and state changer instead. Kept for code ported from Hangfire.")]
        public BackgroundJobServerHostedService(
            [NotNull] JobStorage storage,
            [NotNull] BackgroundJobServerOptions options,
            [NotNull] IEnumerable<IBackgroundProcess> additionalProcesses,
            [CanBeNull] IBackgroundJobFactory factory,
            [CanBeNull] IBackgroundJobPerformer performer,
            [CanBeNull] IBackgroundJobStateChanger stateChanger
            ,
            [CanBeNull] IHostApplicationLifetime hostApplicationLifetime
            )
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _storage = storage ?? throw new ArgumentNullException(nameof(storage));

            _additionalProcesses = additionalProcesses;

            _factory = factory;
            _performer = performer;
            _stateChanger = stateChanger;

            _hostApplicationLifetime = hostApplicationLifetime;
            _hostApplicationLifetime?.ApplicationStopping.Register(SendStopSignal);
        }

        /// <summary>
        /// Starts the server, or arranges for it to start when the application has started.
        /// </summary>
        /// <param name="cancellationToken">Not used.</param>
        public Task StartAsync(CancellationToken cancellationToken)
        {
            if (_hostApplicationLifetime != null)
            {
                // https://github.com/HangfireIO/Hangfire/issues/2117
                _hostApplicationLifetime.ApplicationStarted.Register(InitializeProcessingServer);
            }
            else
            {
                InitializeProcessingServer();
            }

            return Task.CompletedTask;
        }

        /// <summary>
        /// Sends the stop signal to the server and waits for it to shut down.
        /// </summary>
        /// <param name="cancellationToken">Stops waiting.</param>
        public async Task StopAsync(CancellationToken cancellationToken)
        {
            var server = _processingServer;
            if (server == null) return;

            try
            {
                server.SendStop();
                await server.WaitForShutdownAsync(cancellationToken);
            }
            catch (ObjectDisposedException)
            {
                // Due to a bug in ASP.NET Core's Testing package, the StopAsync method
                // can be called twice and from different threads, please see
                // https://github.com/dotnet/aspnetcore/issues/40271 for details.
                // This is not a case for regular applications but can fail integration tests
                // when the second call to the StopAsync method attempts to be performed on
                // an already disposed object.
                // This is not a big deal, however, it's still a workaround that should be
                // removed one day.
                // TODO: Remove this workaround and don't rely on this behavior for simplicity.
            }
        }

        /// <summary>
        /// Disposes the server.
        /// </summary>
        public void Dispose()
        {
            _processingServer?.Dispose();
            _processingServer = null;
            GC.SuppressFinalize(this);
        }
        
        private void InitializeProcessingServer()
        {
            _processingServer = _factory != null && _performer != null && _stateChanger != null
#pragma warning disable 618
                ? new BackgroundJobServer(_options, _storage, _additionalProcesses, null, null, _factory, _performer,
                    _stateChanger)
#pragma warning restore 618
                : new BackgroundJobServer(_options, _storage, _additionalProcesses);
        }

        private void SendStopSignal()
        {
            try
            {
                _processingServer?.SendStop();
            }
            catch (ObjectDisposedException)
            {
                // Please see the comment regarding this exception above.
            }
        }
    }
}
