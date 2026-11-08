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
using System.IO;
using System.Net;
using System.Threading.Tasks;
using Masterly.NightShift.Annotations;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Masterly.NightShift.Dashboard
{
    /// <summary>
    /// The ASP.NET Core middleware that serves the dashboard: it authorizes each request with the options'
    /// filters, checks that commands come from the dashboard's own pages, and dispatches the request to its route.
    /// <c>UseNightShiftDashboard</c> and <c>MapNightShiftDashboard</c> add it.
    /// </summary>
    public class AspNetCoreDashboardMiddleware
    {
        internal const string NoAntiforgeryMessage =
            "The dashboard only accepts this request from its own pages. Register antiforgery (services.AddAntiforgery()) " +
            "for token-based protection, or send the X-Requested-With header from the dashboard's origin.";

        private readonly RequestDelegate _next;
        private readonly JobStorage _storage;
        private readonly DashboardOptions _options;
        private readonly RouteCollection _routes;
        private readonly bool _finalizeWhenNotFound;

        /// <summary>
        /// Initializes the middleware; requests for paths without a route go to <paramref name="next"/>.
        /// </summary>
        /// <param name="next">The next middleware.</param>
        /// <param name="storage">The storage the dashboard shows.</param>
        /// <param name="options">The dashboard options.</param>
        /// <param name="routes">The dashboard routes.</param>
        public AspNetCoreDashboardMiddleware(
            [NotNull] RequestDelegate next,
            [NotNull] JobStorage storage,
            [NotNull] DashboardOptions options,
            [NotNull] RouteCollection routes)
            : this(next, storage, options, routes, finalizeWhenNotFound: false)
        {
        }

        /// <summary>
        /// Initializes the middleware.
        /// </summary>
        /// <param name="next">The next middleware.</param>
        /// <param name="storage">The storage the dashboard shows.</param>
        /// <param name="options">The dashboard options.</param>
        /// <param name="routes">The dashboard routes.</param>
        /// <param name="finalizeWhenNotFound">Whether a request for a path without a route gets 404 instead of
        /// going to <paramref name="next"/>.</param>
        public AspNetCoreDashboardMiddleware(
            [NotNull] RequestDelegate next,
            [NotNull] JobStorage storage,
            [NotNull] DashboardOptions options,
            [NotNull] RouteCollection routes,
            bool finalizeWhenNotFound)
        {
            if (next == null) throw new ArgumentNullException(nameof(next));
            if (storage == null) throw new ArgumentNullException(nameof(storage));
            if (options == null) throw new ArgumentNullException(nameof(options));
            if (routes == null) throw new ArgumentNullException(nameof(routes));

            _next = next;
            _storage = storage;
            _options = options;
            _routes = routes;
            _finalizeWhenNotFound = finalizeWhenNotFound;
        }

        /// <summary>
        /// Serves a dashboard request.
        /// </summary>
        /// <param name="httpContext">The request's HTTP context.</param>
        public async Task Invoke(HttpContext httpContext)
        {
            var context = new AspNetCoreDashboardContext(_storage, _options, httpContext);
            var findResult = _routes.FindDispatcher(httpContext.Request.Path.Value, httpContext.Request.Method);

            if (findResult == null)
            {
                if (_finalizeWhenNotFound)
                {
                    // When UsePathBase method is used, such as in MapNightShiftDashboard, we should
                    // set 404 status code explicitly to handle non-found endpoints, because no one
                    // will do this for us.
                    // https://github.com/HangfireIO/Hangfire/issues/1729
                    // https://github.com/HangfireIO/Hangfire/issues/2541
                    SetResponseStatusCode(httpContext, (int)HttpStatusCode.NotFound);
                    return;
                }

                await _next.Invoke(httpContext);
                return;
            }

            // ReSharper disable once LoopCanBeConvertedToQuery
            foreach (var filter in _options.Authorization)
            {
                if (!filter.Authorize(context))
                {
                    SetResponseStatusCode(httpContext, GetUnauthorizedStatusCode(httpContext));
                    return;
                }
            }

            foreach (var filter in _options.AsyncAuthorization)
            {
                if (!await filter.AuthorizeAsync(context))
                {
                    SetResponseStatusCode(httpContext, GetUnauthorizedStatusCode(httpContext));
                    return;
                }
            }

            if (!_options.IgnoreAntiforgeryToken)
            {
                var antiforgery = httpContext.RequestServices.GetService<IAntiforgery>();

                if (antiforgery != null)
                {
                    bool requestValid;

                    try
                    {
                        requestValid = await antiforgery.IsRequestValidAsync(httpContext);
                    }
                    catch (Exception ex) when ((ex is AntiforgeryValidationException || ex is InvalidDataException) && !httpContext.Response.HasStarted)
                    {
                        // Without the token in a header, antiforgery reads the form to find it, and a form over the
                        // server's limits (FormOptions) can't be read
                        if (ex is InvalidDataException || ex.InnerException is InvalidDataException)
                        {
                            await RejectAsync(httpContext, (int)HttpStatusCode.RequestEntityTooLarge, "The form is too large for this server.");
                        }
                        else
                        {
                            SetResponseStatusCode(httpContext, (int)HttpStatusCode.Forbidden);
                        }

                        return;
                    }

                    if (!requestValid)
                    {
                        // Invalid or missing CSRF token
                        SetResponseStatusCode(httpContext, (int) HttpStatusCode.Forbidden);
                        return;
                    }
                }
                else if (!IsSameOriginScriptRequest(httpContext.Request))
                {
                    // Without IAntiforgery there is no token to check. Fail closed: an unsafe request must come
                    // from the dashboard's own scripts, which a page on another site can't imitate.
                    await RejectAsync(httpContext, (int)HttpStatusCode.Forbidden, NoAntiforgeryMessage);
                    return;
                }
            }

            SetFramingHeaders(httpContext.Response);

            context.UriMatch = findResult.Item2;

            try
            {
                await findResult.Item1.Dispatch(context);
            }
            catch (DashboardFormTooLargeException ex) when (!httpContext.Response.HasStarted)
            {
                await RejectAsync(httpContext, (int)HttpStatusCode.RequestEntityTooLarge, $"The form is too large for this server: {ex.Message}");
            }
        }

        /// <summary>
        /// Whether a request may run when no <see cref="IAntiforgery"/> is registered: a safe method (<c>GET</c>,
        /// <c>HEAD</c>, <c>OPTIONS</c>, <c>TRACE</c>), or a request that carries the <c>X-Requested-With</c> header
        /// the dashboard's scripts send and doesn't come from another site.
        /// </summary>
        /// <remarks>
        /// A form on another site can't add a custom header, and a script on another origin can only add one after a
        /// CORS preflight. <c>Sec-Fetch-Site</c>, which browsers set themselves, must then say <c>same-origin</c>.
        /// Requests from browsers that don't send it are checked with <c>Origin</c>, which must be the request's own
        /// origin: the same scheme, host and port (<c>X-Forwarded-Host</c> is not trusted here; behind a proxy that
        /// rewrites <c>Host</c>, register antiforgery). A request without <c>Origin</c> doesn't come from a browser
        /// page on another site, so it is accepted; a script outside a browser can send anything, which is what
        /// authorization filters are for.
        /// </remarks>
        internal static bool IsSameOriginScriptRequest(HttpRequest request)
        {
            if (HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method) ||
                HttpMethods.IsOptions(request.Method) || HttpMethods.IsTrace(request.Method))
            {
                return true;
            }

            if (String.IsNullOrWhiteSpace(request.Headers["X-Requested-With"]))
            {
                return false;
            }

            var fetchSite = request.Headers["Sec-Fetch-Site"].ToString();
            if (!String.IsNullOrEmpty(fetchSite))
            {
                return "same-origin".Equals(fetchSite, StringComparison.OrdinalIgnoreCase)
                    || "none".Equals(fetchSite, StringComparison.OrdinalIgnoreCase);
            }

            var origin = request.Headers["Origin"].ToString();
            if (String.IsNullOrEmpty(origin))
            {
                // Browsers send Origin with every cross-origin POST, so this one isn't from another site's page
                return true;
            }

            if (!Uri.TryCreate(origin, UriKind.Absolute, out var originUri))
            {
                // Including "null", sent by sandboxed frames and local files
                return false;
            }

            return IsRequestOrigin(originUri, request);
        }

        // The whole origin must match: a page served on another port or over another scheme of the same host is
        // another origin
        private static bool IsRequestOrigin(Uri origin, HttpRequest request)
        {
            var host = request.Host;
            if (!host.HasValue) return false;

            if (!String.Equals(origin.Scheme, request.Scheme, StringComparison.OrdinalIgnoreCase)) return false;
            if (!String.Equals(origin.Host, host.Host, StringComparison.OrdinalIgnoreCase)) return false;

            return origin.Port == (host.Port ?? GetDefaultPort(request.Scheme));
        }

        private static int GetDefaultPort(string scheme)
        {
            if ("https".Equals(scheme, StringComparison.OrdinalIgnoreCase)) return 443;
            if ("http".Equals(scheme, StringComparison.OrdinalIgnoreCase)) return 80;
            return -1;
        }

        private static void SetFramingHeaders(HttpResponse response)
        {
            // Pages on other sites can't show the dashboard in a frame and trick users into pressing its buttons
            if (!response.Headers.ContainsKey("X-Frame-Options"))
            {
                response.Headers["X-Frame-Options"] = "SAMEORIGIN";
            }

            if (!response.Headers.ContainsKey("Content-Security-Policy"))
            {
                response.Headers["Content-Security-Policy"] = "frame-ancestors 'self'";
            }
        }

        private static async Task RejectAsync(HttpContext httpContext, int statusCode, string message)
        {
            if (httpContext.Response.HasStarted) return;

            httpContext.Response.StatusCode = statusCode;
            httpContext.Response.ContentType = "text/plain; charset=utf-8";
            await httpContext.Response.WriteAsync(message);
        }

        private static void SetResponseStatusCode(HttpContext httpContext, int statusCode)
        {
            if (!httpContext.Response.HasStarted)
            {
                httpContext.Response.StatusCode = statusCode;
            }
        }

        private static int GetUnauthorizedStatusCode(HttpContext httpContext)
        {
            return httpContext.User?.Identity?.IsAuthenticated == true
                ? (int)HttpStatusCode.Forbidden
                : (int)HttpStatusCode.Unauthorized;
        }
    }
}
