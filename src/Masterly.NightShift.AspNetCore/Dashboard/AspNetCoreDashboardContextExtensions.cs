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
using Microsoft.AspNetCore.Http;

namespace Masterly.NightShift.Dashboard
{
    /// <summary>
    /// Gives dashboard authorization filters and extensions access to the ASP.NET Core request.
    /// </summary>
    public static class AspNetCoreDashboardContextExtensions
    {
        /// <summary>
        /// Gets the <see cref="HttpContext"/> of a dashboard request served by ASP.NET Core.
        /// </summary>
        /// <param name="context">The dashboard context.</param>
        /// <exception cref="ArgumentException"><paramref name="context"/> isn't an <see cref="AspNetCoreDashboardContext"/>.</exception>
        public static HttpContext GetHttpContext([NotNull] this DashboardContext context)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));

            var aspNetCoreContext = context as AspNetCoreDashboardContext;
            if (aspNetCoreContext == null)
            {
                throw new ArgumentException($"Context argument should be of type `{nameof(AspNetCoreDashboardContext)}`!", nameof(context));
            }

            return aspNetCoreContext.HttpContext;
        }
    }
}