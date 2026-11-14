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

using System.Collections.Concurrent;
using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json.Serialization;
using Masterly.NightShift.Server;

namespace Masterly.NightShift.Storage;

/// <summary>
/// The serialized form of a job's method call (type, method, parameter types and arguments), as stored with the
/// job.
/// </summary>
[method: JsonConstructor]
public class InvocationData(string type, string method, string? parameterTypes, string? arguments, string? queue)
{
    private static readonly
        ConcurrentDictionary<MethodDeserializerCacheKey, MethodDeserializerCacheValue>
        MethodDeserializerCache = new();

    private static readonly
        ConcurrentDictionary<MethodSerializerCacheKey, MethodSerializerCacheValue>
        MethodSerializerCache = new();

    private static readonly ConcurrentDictionary<Tuple<Func<Type, string>, string>, string[]>
        ParameterTypesDeserializerCache = new();

    public InvocationData(string type, string method, string? parameterTypes, string? arguments)
        : this(type, method, parameterTypes, arguments, null)
    {
    }

    public string Type { get; } = type;
    public string Method { get; } = method;
    public string? ParameterTypes { get; } = parameterTypes;

    // Written as null when missing, as the payload below Version_170 always had it
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? Arguments { get; set; } = arguments;

    [JsonInclude, JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Queue { get; } = queue;

    public Job DeserializeJob()
    {
        Func<string, Type> typeResolver = TypeHelper.CurrentTypeResolver;
        JobLoadFailure failure = JobLoadFailure.TypeOrMethod;

        try
        {
            CachedDeserializeMethod(typeResolver, Type, Method, ParameterTypes, out Type? type, out MethodInfo? method);

            failure = JobLoadFailure.Arguments;
            object[] arguments;

            if (Arguments != null && !Arguments.Equals("[]", StringComparison.Ordinal))
            {
                string[]? argumentsArray = SerializationHelper.Deserialize<string[]>(ArgumentCompression.Decompress(Arguments));
                arguments = DeserializeArguments(method, argumentsArray);
            }
            else
            {
                arguments = [];
            }

            failure = JobLoadFailure.Unknown;
            return new Job(type, method, arguments, Queue) { ArgumentsReadBack = true };
        }
        catch (Exception ex) when (ex.IsCatchableExceptionType())
        {
            string message = failure switch
            {
                JobLoadFailure.TypeOrMethod => "Could not load the job: its type or method was not found. See inner exception for the details.",
                JobLoadFailure.Arguments => "Could not load the job: its arguments could not be deserialized. See inner exception for the details.",
                _ => "Could not load the job. See inner exception for the details."
            };

            throw new JobLoadException(message, ex) { Failure = failure };
        }
    }

    public static InvocationData SerializeJob(Job job)
    {
        CachedSerializeMethod(
            TypeHelper.CurrentTypeSerializer,
            job.Type,
            job.Method,
            out string? typeName,
            out string? methodName,
            out string? parameterTypes);

        string arguments = job.Args.Count == 0
            ? "[]"
            : ArgumentCompression.Compress(SerializationHelper.Serialize(
                SerializeArguments(job.Method, job.Args, ensureReadsBack: !job.ArgumentsReadBack)));

        return new InvocationData(typeName, methodName, parameterTypes, arguments, job.Queue);
    }

    public static InvocationData DeserializePayload(string payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        JobPayload? jobPayload = null;
        Exception? exception = null;

        try
        {
            jobPayload = SerializationHelper.Deserialize<JobPayload>(payload) ?? throw new InvalidOperationException("Deserialize<JobPayload> returned `null` for a non-null payload.");
        }
        catch (Exception ex) when (ex.IsCatchableExceptionType())
        {
            exception = ex;
        }

        if (exception == null && jobPayload!.TypeName != null && jobPayload.MethodName != null) // exception == null: it was read
        {
            return new InvocationData(
                jobPayload.TypeName,
                jobPayload.MethodName,
                SerializationHelper.Serialize(jobPayload.ParameterTypes),
                SerializationHelper.Serialize(jobPayload.Arguments),
                jobPayload.Queue);
        }

        InvocationData? data = SerializationHelper.Deserialize<InvocationData>(payload) ?? throw new InvalidOperationException("Deserialize<InvocationData> returned `null` for a non-null payload.");
        if (data.Type == null || data.Method == null)
        {
            data = SerializationHelper.Deserialize<InvocationData>(payload, SerializationOption.User);
        }
        else if (data.ParameterTypes == null)
        {
            // Written below Version_170 with a naming policy that renamed "ParameterTypes" ("parameter_types")
            // but kept "Type" and "Method" readable: only the user options know the name
            data = DeserializeWithUserNames(payload) ?? data;
        }

        return data!; // Not the JSON null: the first read would have thrown
    }

    private static InvocationData? DeserializeWithUserNames(string payload)
    {
        try
        {
            InvocationData? data = SerializationHelper.Deserialize<InvocationData>(payload, SerializationOption.User);
            return data?.Type != null && data.Method != null && data.ParameterTypes != null ? data : null;
        }
        catch (Exception ex) when (ex.IsCatchableExceptionType())
        {
            return null;
        }
    }

    public string? SerializePayload(bool excludeArguments = false)
    {
        if (GlobalConfiguration.HasCompatibilityLevel(CompatibilityLevel.Version_170))
        {
            string[] parameterTypes = DeserializeParameterTypesArray(TypeHelper.CurrentTypeSerializer, ParameterTypes!); // A readable payload has them
            string[]? arguments = excludeArguments ? null : SerializationHelper.Deserialize<string[]>(ArgumentCompression.Decompress(Arguments));

            return SerializationHelper.Serialize(new JobPayload
            {
                TypeName = Type,
                MethodName = Method,
                ParameterTypes = parameterTypes?.Length > 0 ? parameterTypes : null,
                Arguments = arguments?.Length > 0 ? arguments : null,
                Queue = Queue
            });
        }

        // With the internal options at every level, like the format above: the application's options could rename
        // the members (a snake_case naming policy) or leave them out (IgnoreReadOnlyProperties), and then no server
        // finds the method. Every server reads these PascalCase names with its default settings, and servers that
        // split internal from user options (Hangfire 1.7 and later) whatever their own settings are.
        return SerializationHelper.SerializeInternal(excludeArguments
            ? new InvocationData(Type, Method, ParameterTypes, null, Queue)
            : this);
    }

    private static string[] DeserializeParameterTypesArray(Func<Type, string> typeSerializer, string parameterTypes)
    {
        return ParameterTypesDeserializerCache.GetOrAdd(Tuple.Create(typeSerializer, parameterTypes), static tuple =>
        {
            try
            {
                return SerializationHelper.Deserialize<string[]>(tuple.Item2)!; // Not the JSON null
            }
            catch (Exception outerException) when (outerException.IsCatchableExceptionType())
            {
                try
                {
                    Type[]? types = SerializationHelper.Deserialize<Type[]>(tuple.Item2);
                    return [.. types!.Select(tuple.Item1)]; // Not the JSON null
                }
                catch (Exception ex) when (ex.IsCatchableExceptionType())
                {
                    ExceptionDispatchInfo.Capture(outerException).Throw();
                    throw;
                }
            }
        });
    }

    // ensureReadsBack: refuse a value type that would be read back as its default value, for arguments user code
    // gave; arguments read back from stored data are written as they are
    internal static string[] SerializeArguments(MethodInfo methodInfo, IReadOnlyList<object> arguments, bool ensureReadsBack = true)
    {
        List<string> serializedArguments = new(arguments.Count);
        ParameterInfo[] parameters = methodInfo.GetParameters();

        for (int i = 0; i < parameters.Length; i++)
        {
            ParameterInfo parameter = parameters[i];
            object argument = arguments[i];

            string? value;

            if (argument != null)
            {
                if (!GlobalConfiguration.HasCompatibilityLevel(CompatibilityLevel.Version_170) &&
                    argument is DateTime dateTime)
                {
                    value = dateTime.ToString("o", CultureInfo.InvariantCulture);
                }
                else if (argument is CancellationToken)
                {
                    // CancellationToken type instances are substituted with ShutdownToken 
                    // during the background job performance, so we don't need to store 
                    // their values.
                    value = null;
                }
                else
                {
                    // A value type that would be read back as its default value fails here, not when the job runs
                    if (ensureReadsBack)
                    {
                        SerializationHelper.EnsureArgumentReadsBack(argument, parameter.ParameterType);
                    }

                    value = SerializationHelper.Serialize(
                        argument,
                        parameter.ParameterType,
                        SerializationOption.User);
                }
            }
            else
            {
                value = null;
            }

            // Logic, related to optional parameters and their default values, 
            // can be skipped, because it is impossible to omit them in 
            // lambda-expressions (leads to a compile-time error).

            serializedArguments.Add(value!); // A null argument is stored as null
        }

        return [.. serializedArguments];
    }

    internal static object[] DeserializeArguments(MethodInfo methodInfo, string[]? arguments)
    {
        if (arguments == null)
        {
            return [];
        }

        ParameterInfo[] parameters = methodInfo.GetParameters();
        List<object> result = new(arguments.Length);

        for (int i = 0; i < parameters.Length; i++)
        {
            ParameterInfo parameter = parameters[i];
            string argument = arguments[i];

            object? value;

            if (CoreBackgroundJobPerformer.Substitutions.ContainsKey(parameter.ParameterType))
            {
                value = parameter.ParameterType.GetTypeInfo().IsValueType
                    ? Activator.CreateInstance(parameter.ParameterType)
                    : null;
            }
            else
            {
                value = DeserializeArgument(argument, parameter.ParameterType);
            }

            result.Add(value!); // A null argument stays null
        }

        return [.. result];
    }

    private static void CachedSerializeMethod(
        Func<Type, string> typeSerializer, Type type, MethodInfo methodInfo,
        out string typeName, out string methodName, out string parameterTypes)
    {
        MethodSerializerCacheValue entry = MethodSerializerCache.GetOrAdd(
            new MethodSerializerCacheKey { TypeSerializer = typeSerializer, Type = type, Method = methodInfo },
            static key =>
            {
                return new MethodSerializerCacheValue
                {
                    TypeName = key.TypeSerializer(key.Type),
                    MethodName = key.Method.Name,
                    ParameterTypes = SerializationHelper.Serialize(
                        key.Method.GetParameters().Select(x => key.TypeSerializer(x.ParameterType)).ToArray())
                };
            });

        typeName = entry.TypeName;
        methodName = entry.MethodName;
        parameterTypes = entry.ParameterTypes;
    }

    private static void CachedDeserializeMethod(
        Func<string, Type> typeResolver, string typeName, string methodName, string? parameterTypes,
        out Type type, out MethodInfo methodInfo)
    {
        MethodDeserializerCacheValue entry = MethodDeserializerCache.GetOrAdd(
            new MethodDeserializerCacheKey { TypeResolver = typeResolver, TypeName = typeName, MethodName = methodName, ParameterTypes = parameterTypes! }, // A readable payload has them
            static key =>
            {
                Type type = key.TypeResolver(key.TypeName);
                string[] parameterTypesArray = DeserializeParameterTypesArray(TypeHelper.CurrentTypeSerializer, key.ParameterTypes!); // A readable payload has them
                Type[]? parameterTypes = parameterTypesArray?.Select(key.TypeResolver).ToArray();
                MethodInfo? method = type.GetNonOpenMatchingMethod(key.MethodName, parameterTypes);

                if (method == null)
                {
                    string parametersString = parameterTypes != null
                        ? string.Join(", ", parameterTypes.Select(static x => x.Name))
                        : key.ParameterTypes ?? string.Empty;

                    throw new InvalidOperationException(
                        $"The type `{type.FullName}` does not contain a method with signature `{key.MethodName}({parametersString})`");
                }

                return new MethodDeserializerCacheValue { Type = type, Method = method };
            });

        type = entry.Type;
        methodInfo = entry.Method;
    }

    private static object? DeserializeArgument(string argument, Type type)
    {
        object? value;
        try
        {
            value = SerializationHelper.Deserialize(argument, type, SerializationOption.User);
        }
        catch (Exception jsonException) when (jsonException.IsCatchableExceptionType())
        {
            if (type == typeof(object))
            {
                // Special case for handling object types, because string can not
                // be converted to object type.
                value = argument;
            }
            else if ((type == typeof(DateTime) || type == typeof(DateTime?)) && ParseDateTimeArgument(argument, out DateTime dateTime))
            {
                value = dateTime;
            }
            else
            {
                try
                {
                    TypeConverter converter = TypeDescriptor.GetConverter(type);

                    // ReferenceConverter can't correctly convert the serialized
                    // data. This may happen when FromJson method threw an exception,
                    // we should rethrow it instead of trying to deserialize.
                    if (converter.GetType() == typeof(ReferenceConverter))
                    {
                        ExceptionDispatchInfo.Capture(jsonException).Throw();
                        throw;
                    }

                    value = converter.ConvertFromInvariantString(argument);
                }
                catch (Exception ex) when (ex.IsCatchableExceptionType())
                {
                    ExceptionDispatchInfo.Capture(jsonException).Throw();
                    throw;
                }
            }
        }
        return value;
    }

    internal static bool ParseDateTimeArgument(string argument, out DateTime value)
    {
        bool result = DateTime.TryParseExact(
            argument,
            "MM/dd/yyyy HH:mm:ss.ffff",
            CultureInfo.CurrentCulture,
            DateTimeStyles.None,
            out DateTime dateTime);

        if (!result)
        {
            result = DateTime.TryParse(
                argument,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out dateTime);
        }

        value = dateTime;
        return result;
    }

    private sealed class JobPayload
    {
        [JsonInclude, JsonPropertyName("t")]
        public string? TypeName { get; set; }

        [JsonInclude, JsonPropertyName("m")]
        public string? MethodName { get; set; }

        [JsonInclude, JsonPropertyName("p"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string[]? ParameterTypes { get; set; }

        [JsonInclude, JsonPropertyName("a"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string[]? Arguments { get; set; }

        [JsonInclude, JsonPropertyName("q"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Queue { get; set; }
    }

    private readonly record struct MethodDeserializerCacheKey
    {
        public Func<string, Type> TypeResolver { get; init; }
        public string TypeName { get; init; }
        public string MethodName { get; init; }
        public string ParameterTypes { get; init; }
    }

    private readonly record struct MethodDeserializerCacheValue
    {
        public Type Type { get; init; }
        public MethodInfo Method { get; init; }
    }

    private readonly record struct MethodSerializerCacheKey
    {
        public Func<Type, string> TypeSerializer { get; init; }
        public Type Type { get; init; }
        public MethodInfo Method { get; init; }
    }

    private readonly record struct MethodSerializerCacheValue
    {
        public string TypeName { get; init; }
        public string MethodName { get; init; }
        public string ParameterTypes { get; init; }
    }
}