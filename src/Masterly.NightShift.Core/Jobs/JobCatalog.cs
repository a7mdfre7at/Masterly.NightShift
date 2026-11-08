#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading;
using Masterly.NightShift.Annotations;
using Masterly.NightShift.Common;
using Masterly.NightShift.Logging;
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
        /// The form input the dashboard offers: <c>checkbox</c> (<see cref="bool"/>), <c>number</c>, <c>datetime</c>,
        /// <c>text</c>, <c>select</c> (enums and <see cref="Nullable{T}"/> of <see cref="bool"/>; a multiple choice
        /// for a <see cref="FlagsAttribute"/> enum) or <c>json</c>.
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
    /// <see cref="Add(Type)"/>. Generic methods, methods with <c>ref</c>/<c>out</c> parameters, overrides of
    /// <see cref="object"/> members and compiler-generated methods (a record's <c>Equals</c>) are left out.
    /// </summary>
    public static class JobCatalog
    {
        private static readonly ILog Logger = LogProvider.GetLogger(typeof(JobCatalog));
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

                var entry = TryCreateEntry(invoker.Type, method, invoker.Kind);
                if (entry != null) entries[entry.Id] = entry;
            }

            foreach (var type in Volatile.Read(ref _additionalTypes))
            {
                foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
                {
                    if (!IsRunnable(method)) continue;

                    var entry = TryCreateEntry(type, method, null);
                    if (entry != null && !entries.ContainsKey(entry.Id))
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
        /// ones give <c>null</c>. Dates keep what <see cref="CreateJob"/> needs to give the same value back: a
        /// <see cref="DateTimeOffset"/> its offset, a UTC <see cref="DateTime"/> a trailing <c>Z</c>.
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
                // ISO 8601 with what keeps the moment: "Z" for a UTC time, the offset for a local one, nothing for
                // an unspecified one (the shape the HTML datetime-local input takes). Fractions only when present.
                return dateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK", CultureInfo.InvariantCulture);
            }

            if (value is DateTimeOffset dateTimeOffset)
            {
                // With its offset, so the same instant, and the same offset, comes back
                return dateTimeOffset.ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz", CultureInfo.InvariantCulture);
            }

            if (underlying == typeof(string) || underlying == typeof(bool) || underlying == typeof(char) || underlying.GetTypeInfo().IsEnum || value is IFormattable || value == null)
            {
                return FormatDefault(value);
            }

            // Written as the parameter's type, so an interface keeps the "$type" CreateJob needs, and without type
            // names in object values, which CreateJob refuses
            return SerializationHelper.SerializeInput(value, type);
        }

        /// <summary>
        /// Builds a job from an entry and the user's textual arguments: plain text for strings, enum names,
        /// plain values for <see cref="Guid"/>, <see cref="TimeSpan"/> and <see cref="char"/> (a quoted JSON
        /// string works too), JSON for everything else (numbers, booleans and dates in their usual notation).
        /// Injected parameters get <c>null</c>, which the server replaces when the job runs. A
        /// <see cref="DateTimeOffset"/> given without an offset is read as UTC; a <see cref="DateTime"/> ending in
        /// <c>Z</c> has <see cref="DateTimeKind.Utc"/>.
        /// </summary>
        /// <remarks>
        /// A value of type <see cref="object"/>, the argument itself or a member of it, is read as plain JSON (a
        /// primitive or a <c>JsonElement</c>), and a <c>"$type"</c> property in it is refused,
        /// so the text can't name a type to create. An interface or abstract parameter still needs a <c>"$type"</c>,
        /// which must name a type assignable to it.
        /// </remarks>
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

            // The "(null)" choice of a bool? or nullable enum select, and the JSON null of other nullable types
            if (underlying != type && text.Trim() == "null") return null;

            if (underlying == typeof(bool))
            {
                return IsTrue(text);
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
                text = "\"" + text.Trim().Trim('"') + "\"";
            }

            try
            {
                // These get a text input, so take the plain value an operator types; a quoted JSON string still works
                if ((underlying == typeof(Guid) || underlying == typeof(TimeSpan) || underlying == typeof(char)) && !IsJsonString(text) &&
                    !(underlying != type && text.Trim() == "null"))
                {
                    return ParsePlainText(text, underlying);
                }

                if (underlying == typeof(DateTimeOffset) && !HasUtcOffset(text))
                {
                    // A date and time without an offset, as the datetime-local input gives it, is read as UTC, so
                    // the server's own time zone never decides which moment it is
                    var wallClock = (DateTime)SerializationHelper.DeserializeInput(text, typeof(DateTime));
                    return new DateTimeOffset(DateTime.SpecifyKind(wallClock, DateTimeKind.Unspecified), TimeSpan.Zero);
                }

                return SerializationHelper.DeserializeInput(text, type);
            }
            catch (Exception ex) when (ex.IsCatchableExceptionType())
            {
                throw new ArgumentException($"Parameter '{parameter.Name}' ({parameter.TypeName}): {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Turns the values a form posted for one parameter into the single text <see cref="CreateJob"/> reads. The
        /// run form sends a hidden <c>false</c> before each checkbox, so an unchecked box still gives a value: a
        /// <see cref="bool"/> is <c>true</c> when any value is. A <see cref="FlagsAttribute"/> enum's choices are
        /// combined (<c>Instance, Public</c>); its hidden <c>0</c> makes an empty choice mean no flags rather than
        /// the default. Other parameters take the first value. No value at all gives <c>null</c> (the default).
        /// </summary>
        internal static string? CombineFormValues([NotNull] JobCatalogParameter parameter, [CanBeNull] IList<string?>? values)
        {
            if (values == null || values.Count == 0) return null;
            if (values.Count == 1) return values[0];

            var underlying = Nullable.GetUnderlyingType(parameter.Type) ?? parameter.Type;

            if (underlying == typeof(bool))
            {
                return values.Any(static v => v != null && IsTrue(v)) ? "true" : values.FirstOrDefault(static v => !String.IsNullOrEmpty(v)) ?? values[0];
            }

            if (IsFlagsEnum(underlying))
            {
                var names = values.Where(static v => !String.IsNullOrWhiteSpace(v) && v!.Trim() != "0").Select(static v => v!.Trim()).ToList();
                if (names.Count > 1) names.RemoveAll(static v => v == "null");

                return names.Count == 0 ? "0" : String.Join(", ", names);
            }

            return values[0];
        }

        /// <summary>
        /// What the dashboard's run form needs to build the field of a parameter, pre-filled with
        /// <paramref name="value"/> (a text of <see cref="FormatArguments"/> or <see cref="FormatDefault"/>).
        /// </summary>
        internal static object GetFormField([NotNull] JobCatalogParameter parameter, [CanBeNull] string? value, bool redacted)
        {
            var underlying = Nullable.GetUnderlyingType(parameter.Type) ?? parameter.Type;
            var input = parameter.InputKind;

            return new
            {
                name = parameter.Name,
                type = parameter.TypeName,
                input,
                @default = value ?? String.Empty,
                hasDefault = parameter.HasDefaultValue,
                redacted,
                options = input != "select" ? null : underlying == typeof(bool) ? new[] { "true", "false" } : Enum.GetNames(underlying),
                nullable = Nullable.GetUnderlyingType(parameter.Type) != null,
                multiple = IsFlagsEnum(underlying),
            };
        }

        private static bool IsTrue(string text)
        {
            return text.Trim().ToLowerInvariant() is "on" or "true" or "1" or "yes";
        }

        private static bool IsFlagsEnum(Type type)
        {
            return type.GetTypeInfo().IsEnum && type.GetTypeInfo().IsDefined(typeof(FlagsAttribute), inherit: false);
        }

        private static bool IsJsonString(string text)
        {
            return text.Length >= 2 && text[0] == '"' && text[text.Length - 1] == '"';
        }

        // "Z" or "+05:00" after the time, inside the quotes ConvertArgument added
        private static readonly Regex UtcOffset = new Regex(@"T[0-9:.,]+(?:[Zz]|[+-][0-9]{2}(?::?[0-9]{2})?)""$", RegexOptions.CultureInvariant);

        private static bool HasUtcOffset(string quotedText)
        {
            return UtcOffset.IsMatch(quotedText);
        }

        private static object ParsePlainText(string text, Type type)
        {
            if (type == typeof(Guid)) return new Guid(text.Trim());
            if (type == typeof(TimeSpan))
            {
                // TimeSpan.Parse reads a lone number as days, which is rarely what "3600" means
                if (text.IndexOf(':') < 0) throw new FormatException($"'{text.Trim()}' is not a time span; use hh:mm:ss, for example 00:30:00.");
                return TimeSpan.Parse(text.Trim(), CultureInfo.InvariantCulture);
            }
            if (text.Length == 1) return text[0];

            throw new FormatException($"'{text}' is not a single character.");
        }

        // A method whose filter attributes can't be created (an invalid [Queue] name, for example) is left out with
        // a warning, so one bad method doesn't break the whole catalog
        private static JobCatalogEntry? TryCreateEntry(Type type, MethodInfo method, JobInvokerKind? kind)
        {
            try
            {
                return new JobCatalogEntry(type, method, kind);
            }
            catch (Exception ex) when (ex.IsCatchableExceptionType())
            {
                Logger.WarnException($"Job catalog: left out {type.Name}.{method.Name}, because its attributes can't be read.", ex);
                return null;
            }
        }

        internal static bool IsRunnable(MethodInfo method)
        {
            if (method.IsGenericMethodDefinition || method.ContainsGenericParameters) return false;
            if (method.IsSpecialName || method.DeclaringType == typeof(object)) return false;

            // Equals, GetHashCode and ToString overrides, and what the compiler adds to a record (<Clone>$, Equals)
            if (method.GetBaseDefinition().DeclaringType == typeof(object)) return false;
            if (method.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false)) return false;
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

            if (underlying == typeof(bool)) return underlying == type ? "checkbox" : "select";
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
                char c => c.ToString(CultureInfo.InvariantCulture),
                Enum e => e.ToString(),
                IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
                _ => SerializationHelper.Serialize(value, SerializationOption.User),
            };
        }
    }
}
