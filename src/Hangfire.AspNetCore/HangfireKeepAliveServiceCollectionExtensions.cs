// This file is part of Hangfire. Copyright © 2026 Hangfire OÜ.
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
using System.Linq;
using Hangfire.Annotations;
using Hangfire.Server;
using Microsoft.Extensions.DependencyInjection;
#if !NETSTANDARD2_0
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
#endif

namespace Hangfire
{
    /// <summary>
    /// Registers <see cref="KeepAliveProcess"/> as an additional background process of the Hangfire server.
    /// </summary>
    public static class HangfireKeepAliveServiceCollectionExtensions
    {
        /// <summary>
        /// Keeps the web host from idling by requesting one of its own URLs at a fixed interval (5 minutes by
        /// default). Call it next to <c>AddHangfireServer()</c>. Without an explicit URL, on .NET 8 and later,
        /// the first address the server listens on is used with the given path; on other targets the URL is
        /// required. The request has to reach the host that applies the idle timeout, so behind IIS give the
        /// public address, for example <c>https://jobs.example.com/hangfire/stats</c>.
        /// </summary>
        /// <param name="services">The service collection.</param>
        /// <param name="configure">Changes to the defaults: URL, interval, timeout.</param>
        /// <param name="path">Path requested when the URL is resolved from the server's address. Default is <c>/hangfire/ping</c>, a route that answers without touching the storage.</param>
        /// <returns>The same collection, for chaining.</returns>
        public static IServiceCollection AddHangfireKeepAlive(
            [NotNull] this IServiceCollection services,
            [CanBeNull] Action<KeepAliveOptions> configure = null,
            [NotNull] string path = "/hangfire/ping")
        {
            if (services == null) throw new ArgumentNullException(nameof(services));
            if (path == null) throw new ArgumentNullException(nameof(path));

            services.AddSingleton<IBackgroundProcess>(provider =>
            {
                var options = new KeepAliveOptions();
                configure?.Invoke(options);

                if (options.Url == null && options.UrlFactory == null)
                {
#if NETSTANDARD2_0
                    throw new InvalidOperationException("Set KeepAliveOptions.Url: the server address can't be resolved automatically on this target framework.");
#else
                    options.UrlFactory = () => ResolveFromServer(provider, path);
#endif
                }

                return new KeepAliveProcess(options);
            });

            return services;
        }

#if !NETSTANDARD2_0
        private static string ResolveFromServer(IServiceProvider provider, string path)
        {
            var server = provider.GetService<IServer>();
            var address = server?.Features.Get<IServerAddressesFeature>()?.Addresses
                .OrderBy(static x => x.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .FirstOrDefault();

            if (String.IsNullOrEmpty(address))
            {
                return null;
            }

            // "http://*:80", "http://+:80" and "http://0.0.0.0:80" mean "any address": use the loopback for the request
            var normalized = address
                .Replace("://*", "://localhost")
                .Replace("://+", "://localhost")
                .Replace("://0.0.0.0", "://localhost")
                .Replace("://[::]", "://localhost")
                .TrimEnd('/');

            return normalized + (path.StartsWith("/", StringComparison.Ordinal) ? path : "/" + path);
        }
#endif
    }
}
