using System;
using System.Collections.Generic;
using Masterly.NightShift.Core.Tests.Stubs;
using Masterly.NightShift.Dashboard;
using Xunit;

namespace Masterly.NightShift.Core.Tests.Dashboard
{
    public class LocalRequestsOnlyAuthorizationFilterFacts
    {
        [Theory]
        [InlineData("127.0.0.1", "127.0.0.1")]
        [InlineData("::1", "::1")]
        [InlineData("::ffff:127.0.0.1", "::ffff:127.0.0.1")]
        [InlineData("127.0.0.6", "10.0.0.5")]
        [InlineData("10.0.0.5", "10.0.0.5")]
        public void Authorize_AllowsLocalRequests(string remote, string local)
        {
            var context = Create(remote, local);

            Assert.True(new LocalRequestsOnlyAuthorizationFilter().Authorize(context));
        }

        [Theory]
        [InlineData("10.0.0.7", "10.0.0.5")]
        [InlineData(null, "10.0.0.5")]
        [InlineData("", "10.0.0.5")]
        public void Authorize_DeniesRemoteOrUnknownAddresses(string remote, string local)
        {
            var context = Create(remote, local);

            Assert.False(new LocalRequestsOnlyAuthorizationFilter().Authorize(context));
        }

        [Theory]
        [InlineData("X-Forwarded-For", "203.0.113.9")]
        [InlineData("x-forwarded-host", "jobs.example.com")]
        [InlineData("Forwarded", "for=203.0.113.9")]
        [InlineData("X-Real-IP", "203.0.113.9")]
        public void Authorize_DeniesRequestsForwardedByAProxy_EvenFromLoopback(string header, string value)
        {
            // nginx, IIS, Envoy or an Istio sidecar on the same machine connect from a loopback address
            var context = Create("127.0.0.1", "127.0.0.1", new Dictionary<string, string> { { header, value } });

            Assert.False(new LocalRequestsOnlyAuthorizationFilter().Authorize(context));
        }

        [Fact]
        public void Authorize_IgnoresEmptyForwardingHeaders()
        {
            var context = Create("127.0.0.1", "127.0.0.1", new Dictionary<string, string> { { "X-Forwarded-For", " " } });

            Assert.True(new LocalRequestsOnlyAuthorizationFilter().Authorize(context));
        }

        [Fact]
        public void GetHeader_ReturnsNull_ForHostsThatDoNotExposeHeaders()
        {
            Assert.Null(new DashboardRequestStub().GetHeader("X-Forwarded-For"));
        }

        private static DashboardContextStub Create(string remote, string local, Dictionary<string, string> headers = null)
        {
            return new DashboardContextStub(new DashboardOptions(), new HeaderRequest(remote, local, headers ?? new Dictionary<string, string>()));
        }

        private sealed class HeaderRequest : DashboardRequestStub
        {
            private readonly string _remote;
            private readonly string _local;
            private readonly Dictionary<string, string> _headers;

            public HeaderRequest(string remote, string local, Dictionary<string, string> headers)
            {
                _remote = remote;
                _local = local;
                _headers = new Dictionary<string, string>(headers, StringComparer.OrdinalIgnoreCase);
            }

            public override string RemoteIpAddress => _remote;
            public override string LocalIpAddress => _local;

            public override string GetHeader(string name)
            {
                return _headers.TryGetValue(name, out var value) ? value : null;
            }
        }
    }
}
