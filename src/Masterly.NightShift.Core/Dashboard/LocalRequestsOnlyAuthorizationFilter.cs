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
using System.Net;

namespace Masterly.NightShift.Dashboard
{
    /// <summary>
    /// The default dashboard authorization filter: allows requests that come from the machine the app runs on,
    /// meant for development. A request counts as local when its remote IP is a loopback address or equals the
    /// connection's local IP, and it doesn't carry a header that a proxy adds when it forwards a request
    /// (<c>Forwarded</c>, <c>X-Forwarded-For</c>, <c>X-Forwarded-Host</c>, <c>X-Real-IP</c>).
    /// </summary>
    /// <remarks>
    /// A reverse proxy or a sidecar on the same machine (nginx, IIS, Envoy, Istio) connects from a loopback
    /// address, so without those headers every request it forwards looks local. Most proxies add them, but not all
    /// do by default. Don't rely on this filter in production: use an authorization policy or your own filter.
    /// </remarks>
    public class LocalRequestsOnlyAuthorizationFilter : IDashboardAuthorizationFilter
    {
        private static readonly string[] ForwardingHeaders = { "Forwarded", "X-Forwarded-For", "X-Forwarded-Host", "X-Real-IP" };

        public bool Authorize(DashboardContext context)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));

            // if unknown, assume not local
            if (String.IsNullOrEmpty(context.Request.RemoteIpAddress))
                return false;

            // A proxy sent it: the client is somewhere else, whatever the connection's address
            if (IsForwarded(context.Request))
                return false;

            // check if localhost
            if (context.Request.RemoteIpAddress == "127.0.0.1" || context.Request.RemoteIpAddress == "::1")
                return true;

            // compare with local address
            if (context.Request.RemoteIpAddress == context.Request.LocalIpAddress)
                return true;

            // Handle addresses such as ::ffff:127.0.0.1 (IP v4 mapped to IP v6)
            return IPAddress.TryParse(context.Request.RemoteIpAddress, out IPAddress address) && IPAddress.IsLoopback(address);
        }

        private static bool IsForwarded(DashboardRequest request)
        {
            foreach (var header in ForwardingHeaders)
            {
                if (!String.IsNullOrWhiteSpace(request.GetHeader(header)))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
