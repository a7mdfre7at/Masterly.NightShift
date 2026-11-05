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
using System.Threading;
using Masterly.NightShift.Annotations;
using Masterly.NightShift.Common;
using Masterly.NightShift.Processing;
using Masterly.NightShift.Server;

namespace Masterly.NightShift
{
    /// <summary>One parameter of a catalog entry.</summary>
    public sealed class JobCatalogParameter
    {
        internal JobCatalogParameter(ParameterInfo parameter, bool injected)
        {
            Name = parameter.Name ?? $"arg{parameter.Position}";
            Type = parameter.ParameterType;
            Injected = injected;
            HasDefaultValue = parameter.HasDefaultValue;
            DefaultValue = parameter.HasDefaultValue ? parameter.DefaultValue : null;
        }

        /// <summary>The parameter name.</summary>
        public string Name { get; }

        /// <summary>The parameter type.</summary>
        public Type Type { get; }

        /// <summary>Whether the server injects the value (<see cref="PerformContext"/>, <see cref="IJobConsole"/>, cancellation tokens).</summary>
        public bool Injected { get; }

        /// <summary>Whether the method declares a default value.</summary>
        public bool HasDefaultValue { get; }

        /// <summary>The declared default value.</summary>
        public object? DefaultValue { get; }

        /// <summary>A short, readable type name, for forms.</summary>
        public string TypeName => JobCatalog.GetFriendlyName(Type);

        /// <summary>
        /// The form input the dashboard offers: <c>checkbox</c>, <c>number</c>, <c>datetime</c>, <c>text</c>,
        /// <c>select</c> (enums) or <c>json</c>.
        /// </summary>
        public string InputKind => JobCatalog.GetInputKind(Type);
    }

    /// <summary>A job method the dashboard can run.</summary>
    public sealed class JobCatalogEntry
    {
        internal JobCatalogEntry(Type type, MethodInfo method, JobInvokerKind? invoker)
        {
            Type = type;
            Method = method;
            Invoker = invoker;
            Parameters = method.GetParameters()
                .Select(static p => new JobCatalogParameter(p, CoreBackgroundJobPerformer.Substitutions.ContainsKey(p.ParameterType)))
                .ToArray();
            Filters = method.GetCustomAttributes<JobFilterAttribute>(inherit: true).Cast<Attribute>()
                .Concat(type.GetTypeInfo().GetCustomAttributes<JobFilterAttribute>(inherit: true))
                .Concat(method.GetCustomAttributes<JobInterceptorAttribute>(inherit: true))
                .Concat(type.GetTypeInfo().GetCustomAttributes<JobInterceptorAttribute>(inherit: true))
                .Select(static a => a.GetType().Name.Replace("Attribute", String.Empty))
                .Distinct()
                .OrderBy(static x => x, StringComparer.Ordinal)
                .ToArray();
        }

        /// <summary>The declaring type as the job sees it.</summary>
        public Type Type { get; }

        /// <summary>The method.</summary>
        public MethodInfo Method { get; }

        /// <summary>The parameters, in order.</summary>
        public IReadOnlyList<JobCatalogParameter> Parameters { get; }

        /// <summary>Names of the job filter and interceptor attributes on the method and its type, for example <c>AutomaticRetry</c> or <c>JobTimeout</c>.</summary>
        public IReadOnlyList<string> Filters { get; }

        /// <summary>How the method is invoked, when known.</summary>
        public JobInvokerKind? Invoker { get; }

        /// <summary>A stable identifier of the entry: <c>{type}|{method}|{parameter types}</c>.</summary>
        public string Id => JobCatalog.GetId(Type, Method);

        /// <summary>The key under which <see cref="JobInsightsAttribute"/> records the method.</summary>
        public string InsightsKey => $"{Type.ToGenericTypeString()}.{Method.Name}";

        /// <summary>Whether the method returns a task.</summary>
        public bool IsAsync => Method.ReturnType.IsTaskLike(out _);

        /// <summary>The parameters the user has to supply.</summary>
        public IEnumerable<JobCatalogParameter> UserParameters => Parameters.Where(static p => !p.Injected);
    }

    /// <summary>
    /// The job methods the dashboard can run from its Catalog page: every method with a generated invoker (the
    /// source generator finds them at build time) plus the types added with <see cref="Add{T}"/> or
    /// <see cref="Add(Type)"/>. Generic methods and methods with <c>ref</c>/<c>out</c> parameters are left out.
    /// </summary>
    public static class JobCatalog
    {
        private static readonly object SyncRoot = new object();
        private static Type[] _additionalTypes = Array.Empty<Type>();

        /// <summary>Adds every public instance and static method of <typeparamref name="T"/> to the catalog.</summary>
        public static void Add<T>()
        {
            Add(typeof(T));
        }

        /// <summary>Adds every public instance and static method of a type to the catalog.</summary>
        public static void Add([NotNull] Type type)
        {
            if (type == null) throw new ArgumentNullException(nameof(type));

            lock (SyncRoot)
            {
                if (Array.IndexOf(_additionalTypes, type) >= 0) return;

                var types = new Type[_additionalTypes.Length + 1];
                Array.Copy(_additionalTypes, types, _additionalTypes.Length);
                types[_additionalTypes.Length] = type;
                _additionalTypes = types;
            }
        }

        /// <summary>Removes the types added with <see cref="Add(Type)"/>.</summary>
        public static void Clear()
        {
            lock (SyncRoot)
            {
                _additionalTypes = Array.Empty<Type>();
            }
        }

        /// <summary>Lists the catalog, ordered by type and method name.</summary>
        public static IReadOnlyList<JobCatalogEntry> GetEntries()
        {
            var entries = new Dictionary<string, JobCatalogEntry>(StringComparer.Ordinal);

            foreach (var invoker in JobInvokerRegistry.GetGeneratedInvokers())
            {
                MethodInfo method;
                try
                {
                    method = invoker.Method;
                }
                catch (Exception ex) when (ex.IsCatchableExceptionType())
                {
                    continue;
                }

                if (!IsRunnable(method)) continue;

                var entry = new JobCatalogEntry(invoker.Type, method, invoker.Kind);
                entries[entry.Id] = entry;
            }

            foreach (var type in Volatile.Read(ref _additionalTypes))
            {
                foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
                {
                    if (!IsRunnable(method)) continue;

                    var entry = new JobCatalogEntry(type, method, null);
                    if (!entries.ContainsKey(entry.Id))
                    {
                        entries[entry.Id] = entry;
                    }
                }
            }

            return entries.Values
                .OrderBy(static e => e.Type.FullName, StringComparer.Ordinal)
                .ThenBy(static e => e.Method.Name, StringComparer.Ordinal)
                .ThenBy(static e => e.Parameters.Count)
                .ToArray();
        }

        /// <summary>Finds an entry by its <see cref="JobCatalogEntry.Id"/>.</summary>
        public static JobCatalogEntry? Find([NotNull] string id)
        {
            if (id == null) throw new ArgumentNullException(nameof(id));
            return GetEntries().FirstOrDefault(e => String.Equals(e.Id, id, StringComparison.Ordinal));
        }

        /// <summary>
        /// Describes the method of an existing job, whether or not it is in the catalog, so the dashboard can
        /// run the job again with other arguments ("Run again with changes" on the details page).
        /// </summary>
        public static JobCatalogEntry CreateEntry([NotNull] Job job)
        {
            if (job == null) throw new ArgumentNullException(nameof(job));

            // Only a lookup: resolving an invoker here would register a compiled one as a side effect
            var invoker = JobInvokerRegistry.TryGetGeneratedInvoker(job.Method, out var generated) && generated != null ? generated.Kind : (JobInvokerKind?)null;

            return new JobCatalogEntry(job.Type, job.Method, invoker);
        }

        /// <summary>
        /// Renders a job's arguments as the texts the dashboard's run form takes, in parameter order, so the
        /// form can be pre-filled from an existing job. Injected parameters and <see cref="RedactedAttribute"/>
        /// ones give <c>null</c>.
        /// </summary>
        public static IReadOnlyList<string?> FormatArguments([NotNull] Job job)
        {
            if (job == null) throw new ArgumentNullException(nameof(job));

            var parameters = job.Method.GetParameters();
            var result = new string?[parameters.Length];

            for (var i = 0; i < parameters.Length; i++)
            {
                var parameter = parameters[i];
                if (CoreBackgroundJobPerformer.Substitutions.ContainsKey(parameter.ParameterType) || RedactedAttribute.IsRedacted(parameter)) continue;

                var value = i < job.Args.Count ? job.Args[i] : null;
                result[i] = FormatArgument(value, parameter.ParameterType);
            }

            return result;
        }

        internal static string FormatArgument(object? value, Type type)
        {
            var underlying = Nullable.GetUnderlyingType(type) ?? type;

            if (value is DateTime dateTime)
            {
                // The HTML datetime-local input wants this exact shape, in the viewer's local time
                return dateTime.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
            }

            if (value is DateTimeOffset dateTimeOffset)
            {
                return dateTimeOffset.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
            }

            if (underlying == typeof(string) || underlying == typeof(bool) || underlying.GetTypeInfo().IsEnum || value is IFormattable || value == null)
            {
                return FormatDefault(value);
            }

            return SerializationHelper.Serialize(value, SerializationOption.User);
        }

        /// <summary>
        /// Builds a job from an entry and the user's textual arguments: plain text for strings, JSON for
        /// everything else (numbers, booleans and dates in their usual notation). Injected parameters get
        /// <c>null</c>, which the server replaces when the job runs.
        /// </summary>
        public static Job CreateJob([NotNull] JobCatalogEntry entry, [NotNull] IReadOnlyDictionary<string, string?> arguments, [CanBeNull] string? queue = null)
        {
            if (entry == null) throw new ArgumentNullException(nameof(entry));
            if (arguments == null) throw new ArgumentNullException(nameof(arguments));

            var values = new object?[entry.Parameters.Count];

            for (var i = 0; i < entry.Parameters.Count; i++)
            {
                var parameter = entry.Parameters[i];
                if (parameter.Injected) continue;

                arguments.TryGetValue(parameter.Name, out var text);
                values[i] = ConvertArgument(parameter, text);
            }

            return new Job(entry.Type, entry.Method, values!, String.IsNullOrWhiteSpace(queue) ? null : queue);
        }

        internal static object? ConvertArgument(JobCatalogParameter parameter, string? text)
        {
            var type = parameter.Type;

            if (text == null || text.Length == 0)
            {
                if (parameter.HasDefaultValue) return parameter.DefaultValue;
                if (type == typeof(string)) return String.Empty;
                if (!type.GetTypeInfo().IsValueType || Nullable.GetUnderlyingType(type) != null) return null;
                if (type == typeof(bool)) return false;

                throw new ArgumentException($"Parameter '{parameter.Name}' ({parameter.TypeName}) needs a value.");
            }

            if (type == typeof(string)) return text;

            var underlying = Nullable.GetUnderlyingType(type) ?? type;

            if (underlying == typeof(bool))
            {
                return text is "on" or "true" or "1" or "yes";
            }

            if (underlying.GetTypeInfo().IsEnum)
            {
                try
                {
                    return Enum.Parse(underlying, text, ignoreCase: true);
                }
                catch (ArgumentException)
                {
                    throw new ArgumentException($"'{text}' is not a value of {underlying.Name}.");
                }
            }

            if (underlying == typeof(DateTime) || underlying == typeof(DateTimeOffset))
            {
                // The HTML datetime-local input gives "2026-11-04T18:30" without quotes
                text = "\"" + text.Trim('"') + "\"";
            }

            try
            {
                return SerializationHelper.Deserialize(text, type, SerializationOption.User);
            }
            catch (Exception ex) when (ex.IsCatchableExceptionType())
            {
                throw new ArgumentException($"Parameter '{parameter.Name}' ({parameter.TypeName}): {ex.Message}", ex);
            }
        }

        internal static bool IsRunnable(MethodInfo method)
        {
            if (method.IsGenericMethodDefinition || method.ContainsGenericParameters) return false;
            if (method.IsSpecialName || method.DeclaringType == typeof(object)) return false;
            if (method.GetParameters().Any(static p => p.ParameterType.IsByRef || p.IsOut)) return false;

            return true;
        }

        internal static string GetId(Type type, MethodInfo method)
        {
            var parameters = String.Join(",", method.GetParameters().Select(static p => p.ParameterType.FullName ?? p.ParameterType.Name));
            return $"{type.FullName}|{method.Name}|{parameters}";
        }

        internal static string GetFriendlyName(Type type)
        {
            var underlying = Nullable.GetUnderlyingType(type);
            if (underlying != null) return GetFriendlyName(underlying) + "?";

            if (type == typeof(string)) return "string";
            if (type == typeof(int)) return "int";
            if (type == typeof(long)) return "long";
            if (type == typeof(bool)) return "bool";
            if (type == typeof(double)) return "double";
            if (type == typeof(decimal)) return "decimal";
            if (type == typeof(float)) return "float";
            if (type == typeof(object)) return "object";
            if (type.IsArray) return GetFriendlyName(type.GetElementType()!) + "[]";

            if (type.GetTypeInfo().IsGenericType)
            {
                var name = type.Name.Substring(0, type.Name.IndexOf('`'));
                return $"{name}<{String.Join(", ", type.GetTypeInfo().GetGenericArguments().Select(GetFriendlyName))}>";
            }

            return type.Name;
        }

        internal static string GetInputKind(Type type)
        {
            var underlying = Nullable.GetUnderlyingType(type) ?? type;

            if (underlying == typeof(bool)) return "checkbox";
            if (underlying.GetTypeInfo().IsEnum) return "select";
            if (underlying == typeof(DateTime) || underlying == typeof(DateTimeOffset)) return "datetime";
            if (underlying == typeof(string) || underlying == typeof(Guid) || underlying == typeof(TimeSpan) || underlying == typeof(char)) return "text";

            switch (Type.GetTypeCode(underlying))
            {
                case TypeCode.Byte:
                case TypeCode.SByte:
                case TypeCode.Int16:
                case TypeCode.UInt16:
                case TypeCode.Int32:
                case TypeCode.UInt32:
                case TypeCode.Int64:
                case TypeCode.UInt64:
                case TypeCode.Single:
                case TypeCode.Double:
                case TypeCode.Decimal:
                    return "number";
            }

            return "json";
        }

        internal static string FormatDefault(object? value)
        {
            return value switch
            {
                null => String.Empty,
                bool b => b ? "true" : "false",
                string s => s,
                Enum e => e.ToString(),
                IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
                _ => SerializationHelper.Serialize(value, SerializationOption.User),
            };
        }
    }
}
