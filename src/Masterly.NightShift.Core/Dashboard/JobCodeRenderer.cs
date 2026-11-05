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
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using Masterly.NightShift.Annotations;
using Masterly.NightShift.Common;
using Masterly.NightShift.Server;

namespace Masterly.NightShift.Dashboard
{
    /// <summary>
    /// Renders the C# statement that re-creates a job, for the "Copy as code" button on the job details page:
    /// <c>BackgroundJob.Enqueue&lt;Service&gt;(x =&gt; x.Method("arg", 42));</c>. Arguments are written as
    /// literals where C# has them, and as <c>FromJson&lt;T&gt;("...")</c> otherwise.
    /// </summary>
    public static class JobCodeRenderer
    {
        private const int MaxArgumentLength = 2000;

        /// <summary>Renders the statement. A job that could not be loaded renders a comment.</summary>
        public static string Render([CanBeNull] Job? job)
        {
            if (job == null) return "// The job could not be loaded.";

            var builder = new StringBuilder();
            var method = job.Method;
            var parameters = method.GetParameters();

            builder.Append("BackgroundJob.Enqueue");

            if (!method.IsStatic)
            {
                builder.Append('<').Append(TypeName(job.Type)).Append(">(x => x.");
            }
            else
            {
                builder.Append("(() => ").Append(TypeName(job.Type)).Append('.');
            }

            builder.Append(method.Name);

            if (method.IsGenericMethod)
            {
                builder.Append('<').Append(String.Join(", ", method.GetGenericArguments().Select(TypeName))).Append('>');
            }

            builder.Append('(');

            var arguments = new List<string>(parameters.Length);
            for (var i = 0; i < parameters.Length; i++)
            {
                var parameter = parameters[i];

                if (CoreBackgroundJobPerformer.Substitutions.ContainsKey(parameter.ParameterType))
                {
                    arguments.Add(parameter.ParameterType == typeof(System.Threading.CancellationToken) ? "CancellationToken.None" : "null");
                    continue;
                }

                if (RedactedAttribute.IsRedacted(parameter))
                {
                    arguments.Add($"/* {parameter.Name}: redacted */ {(parameter.ParameterType == typeof(string) ? "\"\"" : "default")}");
                    continue;
                }

                var raw = i < job.Args.Count ? job.Args[i] : null;
                arguments.Add(Literal(raw, parameter.ParameterType));
            }

            var inline = String.Join(", ", arguments);
            if (inline.Length > 80)
            {
                builder.AppendLine();
                builder.Append("    ").Append(String.Join("," + Environment.NewLine + "    ", arguments));
            }
            else
            {
                builder.Append(inline);
            }

            builder.Append("));");

            return builder.ToString();
        }

        /// <summary>Renders one value as a C# literal, or a <c>FromJson</c> call when no literal exists.</summary>
        public static string Literal([CanBeNull] object? value, [NotNull] Type type)
        {
            if (type == null) throw new ArgumentNullException(nameof(type));
            if (value == null) return "null";

            var underlying = Nullable.GetUnderlyingType(type) ?? type;

            switch (value)
            {
                case string s:
                    return Quote(s);
                case bool b:
                    return b ? "true" : "false";
                case char c:
                    return "'" + (c == '\'' ? "\\'" : c.ToString()) + "'";
                case Enum e:
                    return $"{TypeName(underlying)}.{e}";
                case Guid g:
                    return $"Guid.Parse(\"{g}\")";
                case TimeSpan ts:
                    return $"TimeSpan.Parse(\"{ts.ToString(null, CultureInfo.InvariantCulture)}\")";
                case DateTime dt:
                    return $"DateTime.Parse(\"{dt.ToString("o", CultureInfo.InvariantCulture)}\")";
                case DateTimeOffset dto:
                    return $"DateTimeOffset.Parse(\"{dto.ToString("o", CultureInfo.InvariantCulture)}\")";
                case int or short or byte or sbyte or ushort:
                    return Convert.ToString(value, CultureInfo.InvariantCulture)!;
                case long l:
                    return l.ToString(CultureInfo.InvariantCulture) + "L";
                case uint ui:
                    return ui.ToString(CultureInfo.InvariantCulture) + "U";
                case ulong ul:
                    return ul.ToString(CultureInfo.InvariantCulture) + "UL";
                case float f:
                    return f.ToString("R", CultureInfo.InvariantCulture) + "f";
                case double d:
                    return d.ToString("R", CultureInfo.InvariantCulture) + (Math.Abs(d % 1) < Double.Epsilon ? ".0" : String.Empty);
                case decimal m:
                    return m.ToString(CultureInfo.InvariantCulture) + "m";
            }

            string json;
            try
            {
                json = SerializationHelper.Serialize(value, SerializationOption.User);
            }
            catch (Exception ex) when (ex.IsCatchableExceptionType())
            {
                json = value.ToString() ?? String.Empty;
            }

            if (json.Length > MaxArgumentLength)
            {
                return $"/* {TypeName(type)}: value too big to inline */ null";
            }

            return $"FromJson<{TypeName(type)}>({Quote(json)})";
        }

        internal static string Quote(string value)
        {
            if (value.IndexOf('\n') >= 0 || value.IndexOf('\r') >= 0)
            {
                return "@\"" + value.Replace("\"", "\"\"") + "\"";
            }

            return "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }

        internal static string TypeName(Type type)
        {
            var underlying = Nullable.GetUnderlyingType(type);
            if (underlying != null) return TypeName(underlying) + "?";

            if (type == typeof(string)) return "string";
            if (type == typeof(int)) return "int";
            if (type == typeof(long)) return "long";
            if (type == typeof(bool)) return "bool";
            if (type == typeof(double)) return "double";
            if (type == typeof(decimal)) return "decimal";
            if (type == typeof(object)) return "object";
            if (type.IsArray) return TypeName(type.GetElementType()!) + "[]";

            if (type.GetTypeInfo().IsGenericType)
            {
                var name = type.Name.Substring(0, type.Name.IndexOf('`'));
                return $"{name}<{String.Join(", ", type.GetTypeInfo().GetGenericArguments().Select(TypeName))}>";
            }

            return type.Name;
        }
    }
}
