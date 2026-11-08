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
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Masterly.NightShift.Annotations;
using Microsoft.AspNetCore.Http;

namespace Masterly.NightShift.Dashboard
{
    internal sealed class AspNetCoreDashboardRequest : DashboardRequest
    {
        private readonly HttpContext _context;

        public AspNetCoreDashboardRequest([NotNull] HttpContext context)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));
            _context = context;
        }

        public override string Method => _context.Request.Method;
        public override string Path => _context.Request.Path.Value;
        public override string PathBase => _context.Request.PathBase.Value;
        public override string LocalIpAddress => _context.Connection.LocalIpAddress?.ToString();
        public override string RemoteIpAddress => _context.Connection.RemoteIpAddress?.ToString();
        public override string GetQuery(string key) => _context.Request.Query[key];

        public override async Task<IList<string>> GetFormValuesAsync(string key)
        {
            if (!_context.Request.HasFormContentType)
            {
                // No form to read: jQuery's $.post(url) without data sends no body and no Content-Type, and
                // ReadFormAsync throws for that. Every form field is then simply missing.
                return Array.Empty<string>();
            }

            IFormCollection form;

            try
            {
                form = await _context.Request.ReadFormAsync();
            }
            catch (InvalidDataException ex)
            {
                // FormOptions limits, such as more than 1024 values: answered with 413 by the middleware
                throw new DashboardFormTooLargeException(ex);
            }

            return form[key];
        }

        public override string GetHeader(string name)
        {
            if (name == null) throw new ArgumentNullException(nameof(name));

            return _context.Request.Headers.TryGetValue(name, out var values) && values.Count > 0
                ? values.ToString()
                : null;
        }
    }

    /// <summary>The posted form exceeds the limits of ASP.NET Core's <c>FormOptions</c>.</summary>
    internal sealed class DashboardFormTooLargeException : Exception
    {
        public DashboardFormTooLargeException(InvalidDataException innerException)
            : base(innerException.Message, innerException)
        {
        }
    }
}
