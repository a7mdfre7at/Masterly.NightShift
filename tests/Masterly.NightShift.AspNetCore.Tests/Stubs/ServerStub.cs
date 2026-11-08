using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http.Features;

namespace Masterly.NightShift.AspNetCore.Tests.Stubs
{
    /// <summary>A web server that only reports the addresses it listens on.</summary>
    internal sealed class ServerStub : IServer
    {
        public ServerStub(IEnumerable<string> addresses)
        {
            var feature = new AddressesFeature();
            foreach (var address in addresses)
            {
                feature.Addresses.Add(address);
            }

            Features.Set<IServerAddressesFeature>(feature);
        }

        public IFeatureCollection Features { get; } = new FeatureCollection();

        public Task StartAsync<TContext>(IHttpApplication<TContext> application, CancellationToken cancellationToken)
            where TContext : notnull
        {
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public void Dispose()
        {
        }

        // Not ServerAddressesFeature: ASP.NET Core 2.x, which the net48 target uses, has it in the Hosting package only
        private sealed class AddressesFeature : IServerAddressesFeature
        {
            public ICollection<string> Addresses { get; } = new List<string>();
            public bool PreferHostingUrls { get; set; }
        }
    }
}
