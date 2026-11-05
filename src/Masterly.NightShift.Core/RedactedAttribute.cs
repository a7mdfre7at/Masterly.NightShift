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

#nullable enable

using System;
using System.Reflection;
using Masterly.NightShift.Annotations;

namespace Masterly.NightShift
{
    /// <summary>
    /// Marks a job parameter whose value must not be shown: the dashboard renders it as
    /// <c>"••••••"</c> on every page, "Copy as code" leaves it out and "Run again with changes" asks for it
    /// anew. Use it for passwords, tokens, personal data. The value itself is still stored with the job,
    /// so redaction protects dashboard viewers, not the storage.
    /// </summary>
    /// <example>
    /// <code>
    /// public Task Connect(string host, [Redacted] string password) { ... }
    /// </code>
    /// </example>
    [AttributeUsage(AttributeTargets.Parameter, AllowMultiple = false, Inherited = true)]
    public sealed class RedactedAttribute : Attribute
    {
        /// <summary>What the dashboard shows instead of a redacted value.</summary>
        public const string Mask = "••••••";

        /// <summary>Whether a parameter is marked with <see cref="RedactedAttribute"/>.</summary>
        public static bool IsRedacted([NotNull] ParameterInfo parameter)
        {
            if (parameter == null) throw new ArgumentNullException(nameof(parameter));
            return parameter.GetCustomAttribute<RedactedAttribute>(inherit: true) != null;
        }
    }
}
