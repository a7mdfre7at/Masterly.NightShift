using System.Buffers;
using System.Collections;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Globalization;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.Serialization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Masterly.NightShift.Common;

/// <summary>
/// Writes a state with a leading <c>"$type"</c> property naming its type, and reads it back as that type.
/// It's the format stored data already uses, so states saved before can still be read.
/// </summary>
internal sealed class StateJsonConverter : JsonConverter<IState>
{
    public override IState? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using JsonDocument document = JsonDocument.ParseValue(ref reader);
        JsonElement root = document.RootElement;

        if (root.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (!TypeNamedJson.TryGetTypeName(root, out string? typeName))
        {
            throw new JsonException($"A state must be a JSON object with a \"{TypeNamedJson.TypeProperty}\" property.");
        }

        return (IState?)TypeNamedJson.Read(root, TypeNamedJson.ResolveType(typeName, typeof(IState), options), options);
    }

    public override void Write(Utf8JsonWriter writer, IState value, JsonSerializerOptions options)
    {
        TypeNamedJson.Write(writer, value, options);
    }
}

/// <summary>
/// Writes a value as a JSON object with a leading <c>"$type"</c> property naming its actual type, and reads such
/// an object back as that type. States, values declared as <see cref="object"/> when type names are kept, and
/// values declared as an interface or abstract class are stored this way.
/// </summary>
/// <remarks>
/// Only a leading <c>"$type"</c> names a type: Newtonsoft.Json and this class always write it first. A value whose
/// own members include a <c>"$type"</c> or a <c>"$values"</c> (a dictionary with such a key, a
/// <see cref="JsonElement"/> with such a property) is written as <c>{"$type": …, "$values": {…}}</c>, its members
/// inside <c>"$values"</c>, so they read back as they were and never name the type the value is read as.
/// </remarks>
internal static class TypeNamedJson
{
    public const string TypeProperty = "$type";
    public const string ValuesProperty = "$values";

    // The reference id Newtonsoft.Json writes before "$type" with PreserveReferencesHandling
    public const string IdProperty = "$id";

    /// <summary>
    /// Writes every member of the value's actual type, after a <c>"$type"</c> property naming that type. A value
    /// that isn't written as a JSON object (a number, a string, an array) is written as it is, without a type.
    /// </summary>
    public static void Write(Utf8JsonWriter writer, object value, JsonSerializerOptions options)
    {
        Type type = value.GetType();
        JsonElement element = JsonSerializer.SerializeToElement(value, type, options);
        if (element.ValueKind != JsonValueKind.Object)
        {
            element.WriteTo(writer);
            return;
        }

        writer.WriteStartObject();
        writer.WriteString(TypeProperty, JsonTypeNames.Simple(TypeHelper.CurrentTypeSerializer(type)));
        if (HasMetadataMember(element))
        {
            writer.WritePropertyName(ValuesProperty);
            element.WriteTo(writer);
        }
        else
        {
            foreach (JsonProperty property in element.EnumerateObject())
            {
                property.WriteTo(writer);
            }
        }
        writer.WriteEndObject();
    }

    /// <summary>
    /// Returns the type name of an object that starts with a <c>"$type"</c> property, or with a <c>"$id"</c> and
    /// then a <c>"$type"</c>, as Newtonsoft.Json writes them with <c>PreserveReferencesHandling</c>.
    /// </summary>
    public static bool TryGetTypeName(JsonElement root, out string typeName)
    {
        return TryGetTypeName(root, out typeName, out _);
    }

    /// <summary>
    /// Returns the type name of an object that starts with a <c>"$type"</c> property, after an optional
    /// <c>"$id"</c>, and how many members that is (1 or 2): the members that aren't the value's own.
    /// </summary>
    public static bool TryGetTypeName(JsonElement root, out string typeName, out int metadataMembers)
    {
        if (root.ValueKind == JsonValueKind.Object)
        {
            int index = 0;
            foreach (JsonProperty property in root.EnumerateObject())
            {
                if (index == 0 && property.NameEquals(IdProperty))
                {
                    index++;
                    continue;
                }

                if (property.NameEquals(TypeProperty) && property.Value.ValueKind == JsonValueKind.String)
                {
                    typeName = property.Value.GetString()!; // The token is a string
                    metadataMembers = index + 1;
                    return true;
                }

                break;
            }
        }

        typeName = null!; // Unused when this returns false
        metadataMembers = 0;
        return false;
    }

    /// <summary>Tells whether an object has a <c>"$type"</c> property anywhere.</summary>
    public static bool HasTypeProperty(JsonElement root)
    {
        return root.ValueKind == JsonValueKind.Object && root.TryGetProperty(TypeProperty, out _);
    }

    /// <summary>
    /// Resolves a <c>"$type"</c> name through the configured type resolver, and checks that it names a concrete
    /// type assignable to <paramref name="expectedType"/>, so stored data can't make a server create anything else.
    /// For text an operator typed (<paramref name="options"/> read input), the name must first name such a type in
    /// an assembly that is already loaded, or be the name the configured type serializer gives such a type, so the
    /// text can't make Masterly.NightShift load anything.
    /// </summary>
    public static Type ResolveType(string typeName, Type expectedType, JsonSerializerOptions options)
    {
        if (RefusedTypeInputJsonConverter.IsInput(options) &&
            !IsUsable(LoadedTypes.Find(typeName), expectedType) &&
            LoadedTypes.FindSerialized(typeName, expectedType) == null)
        {
            throw new JsonException($"The type '{typeName}' in \"{TypeProperty}\" isn't a loaded concrete type assignable to '{expectedType}'.");
        }

        Type type = TypeHelper.CurrentTypeResolver(typeName);
        if (!IsUsable(type, expectedType))
        {
            throw new JsonException($"The type '{typeName}' in \"{TypeProperty}\" isn't a concrete type assignable to '{expectedType}'.");
        }

        return type;
    }

    private static bool IsUsable(Type? type, Type expectedType)
    {
        return type != null && expectedType.IsAssignableFrom(type) && !type.IsInterface && !type.IsAbstract;
    }

    /// <summary>
    /// Reads an object written by <see cref="Write"/> as <paramref name="type"/>: the members after the leading
    /// <c>"$type"</c> (and a <c>"$id"</c> before it), or the value of <c>"$values"</c> when it is the only other
    /// member.
    /// </summary>
    public static object? Read(JsonElement root, Type type, JsonSerializerOptions options)
    {
        return TryGetValues(root, out JsonElement values)
            ? values.Deserialize(type, options)
            : JsonSerializer.Deserialize(WithoutTypeProperty(root), type, options);
    }

    // {"$type": …, "$values": …} and nothing else (after a leading "$id"): a collection the previous serializer
    // wrapped, or a value whose own members include "$type" or "$values"
    private static bool TryGetValues(JsonElement root, out JsonElement values)
    {
        values = default;
        if (!TryGetTypeName(root, out _, out int metadataMembers))
        {
            return false;
        }

        int index = 0;
        bool found = false;
        foreach (JsonProperty property in root.EnumerateObject())
        {
            if (index == metadataMembers && property.NameEquals(ValuesProperty))
            {
                values = property.Value;
                found = true;
            }
            else if (index >= metadataMembers)
            {
                return false;
            }

            index++;
        }

        return found;
    }

    private static bool HasMetadataMember(JsonElement element)
    {
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (property.NameEquals(TypeProperty) || property.NameEquals(ValuesProperty))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The object without its leading <c>"$type"</c> property and a <c>"$id"</c> before it; any later property is
    /// kept.
    /// </summary>
    public static byte[] WithoutTypeProperty(JsonElement root)
    {
        TryGetTypeName(root, out _, out int metadataMembers);

        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = new(stream))
        {
            writer.WriteStartObject();
            int index = 0;
            foreach (JsonProperty property in root.EnumerateObject())
            {
                if (index >= metadataMembers)
                {
                    property.WriteTo(writer);
                }

                index++;
            }
            writer.WriteEndObject();
        }

        return stream.ToArray();
    }
}

/// <summary>
/// Finds a type by name among the assemblies already loaded, without loading any: for type names in text an
/// operator typed, which must not make the process load an assembly or a type of the text's choosing.
/// </summary>
internal static class LoadedTypes
{
    private static readonly Assembly CoreLibrary = typeof(object).Assembly;

    /// <summary>The type, or <c>null</c> when the name doesn't name a type of an assembly already loaded.</summary>
    public static Type? Find(string typeName)
    {
        try
        {
            return Type.GetType(typeName, FindAssembly, FindType, throwOnError: false);
        }
        catch (Exception ex) when (ex.IsCatchableExceptionType())
        {
            return null;
        }
    }

    private static Assembly? FindAssembly(AssemblyName name)
    {
        if (string.Equals(name.Name, "mscorlib", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(name.Name, CoreLibrary.GetName().Name, StringComparison.OrdinalIgnoreCase))
        {
            return CoreLibrary;
        }

        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (string.Equals(assembly.GetName().Name, name.Name, StringComparison.OrdinalIgnoreCase))
            {
                return assembly;
            }
        }

        return null;
    }

    private static Type? FindType(Assembly? assembly, string name, bool ignoreCase)
    {
        return (assembly ?? CoreLibrary).GetType(name, throwOnError: false, ignoreCase);
    }

    // By declared type: the names the type serializer gives the loaded concrete types assignable to it
    private static readonly ConcurrentDictionary<Type, SerializedNames> SerializedNamesCache = new();

    /// <summary>
    /// Finds, among the assemblies already loaded, a concrete type assignable to <paramref name="expectedType"/>
    /// that the configured type serializer names <paramref name="typeName"/> (without version, culture or public
    /// key), so the name a custom <c>UseTypeSerializer</c> writes is accepted. Only the declared type's assembly and
    /// the loaded assemblies that reference it are searched, nothing is loaded by name, and the names are kept per
    /// declared type until the type serializer changes or another assembly is loaded.
    /// </summary>
    public static Type? FindSerialized(string typeName, Type expectedType)
    {
        Func<Type, string> serializer = TypeHelper.CurrentTypeSerializer;
        Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();

        if (!SerializedNamesCache.TryGetValue(expectedType, out SerializedNames? names) ||
            names.Serializer != serializer || names.AssemblyCount != assemblies.Length)
        {
            names = new SerializedNames(serializer, assemblies.Length, CollectSerializedNames(expectedType, serializer, assemblies));
            SerializedNamesCache[expectedType] = names;
        }

        string? simple = JsonTypeNames.Simple(typeName);
        return simple != null && names.Types.TryGetValue(simple, out Type? type) ? type : null;
    }

    private static Dictionary<string, Type> CollectSerializedNames(Type expectedType, Func<Type, string> serializer, Assembly[] assemblies)
    {
        Dictionary<string, Type> types = new(StringComparer.Ordinal);
        string? expectedAssembly = expectedType.Assembly.GetName().Name;

        foreach (Assembly assembly in assemblies)
        {
            if (assembly != expectedType.Assembly && !References(assembly, expectedAssembly))
            {
                continue;
            }

            foreach (Type type in GetLoadableTypes(assembly))
            {
                if (type.IsInterface || type.IsAbstract || type.ContainsGenericParameters || !expectedType.IsAssignableFrom(type))
                {
                    continue;
                }

                try
                {
                    string? name = JsonTypeNames.Simple(serializer(type));
                    if (name != null)
                    {
                        types.TryAdd(name, type);
                    }
                }
                catch (Exception ex) when (ex.IsCatchableExceptionType())
                {
                    // A serializer that can't name the type: no input can name it either
                }
            }
        }

        return types;
    }

    private static bool References(Assembly assembly, string? assemblyName)
    {
        try
        {
            foreach (AssemblyName reference in assembly.GetReferencedAssemblies())
            {
                if (string.Equals(reference.Name, assemblyName, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }
        catch (Exception ex) when (ex.IsCatchableExceptionType())
        {
            // An assembly whose references can't be read has no type to offer
        }

        return false;
    }

    private static IEnumerable<Type> GetLoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.OfType<Type>(); // Only the types that loaded
        }
        catch (Exception ex) when (ex.IsCatchableExceptionType())
        {
            return [];
        }
    }

    private sealed record SerializedNames(Func<Type, string> Serializer, int AssemblyCount, Dictionary<string, Type> Types);
}

/// <summary>
/// Holds a value that <see cref="SerializationHelper"/> writes with <see cref="TypeNamedJson"/>, because its
/// declared type is an interface or abstract class that couldn't be read back without the actual type.
/// </summary>
[JsonConverter(typeof(Converter))]
internal sealed class TypeNamedValue(object value)
{
    public object Value { get; } = value;

    private sealed class Converter : JsonConverter<TypeNamedValue>
    {
        public override TypeNamedValue Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            throw new NotSupportedException("Values are read back through SerializationHelper, with their declared type.");
        }

        public override void Write(Utf8JsonWriter writer, TypeNamedValue value, JsonSerializerOptions options)
        {
            TypeNamedJson.Write(writer, value.Value, options);
        }
    }
}

/// <summary>Writes a <see cref="Type"/> as its name, using the configured type serializer and resolver.</summary>
internal sealed class TypeNameJsonConverter : JsonConverter<Type>
{
    public override bool CanConvert(Type typeToConvert) => typeof(Type).IsAssignableFrom(typeToConvert);

    public override Type? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return null;
        }

        string? name = reader.GetString();
        return string.IsNullOrEmpty(name) ? null : TypeHelper.CurrentTypeResolver(name);
    }

    public override void Write(Utf8JsonWriter writer, Type value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(TypeHelper.CurrentTypeSerializer(value));
    }
}

/// <summary>
/// For text an operator types in the dashboard: a <see cref="Type"/> value is refused instead of resolved, because
/// resolving it would load whatever type the text names. Written like <see cref="TypeNameJsonConverter"/>, so a
/// form can still show a value.
/// </summary>
internal sealed class RefusedTypeInputJsonConverter : JsonConverter<Type>
{
    /// <summary>
    /// Tells whether the options read text an operator typed: the dashboard's input options put this converter
    /// first.
    /// </summary>
    public static bool IsInput(JsonSerializerOptions options)
    {
        return options.Converters.Count > 0 && options.Converters[0] is RefusedTypeInputJsonConverter;
    }

    public override bool CanConvert(Type typeToConvert) => typeof(Type).IsAssignableFrom(typeToConvert);

    public override Type? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return null;
        }

        throw new JsonException("A System.Type value can't be entered from the dashboard.");
    }

    public override void Write(Utf8JsonWriter writer, Type value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(TypeHelper.CurrentTypeSerializer(value));
    }
}

/// <summary>
/// Writes enums as numbers, and reads either numbers or names (ignoring case), so values stored
/// either way can be read.
/// </summary>
internal sealed class LenientEnumConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert) => typeToConvert.IsEnum;

    public override JsonConverter? CreateConverter(Type typeToConvert, JsonSerializerOptions options)
    {
        return (JsonConverter?)Activator.CreateInstance(typeof(LenientEnumConverter<>).MakeGenericType(typeToConvert));
    }

    private sealed class LenientEnumConverter<TEnum> : JsonConverter<TEnum> where TEnum : struct, Enum
    {
        private static readonly bool IsUnsigned = Type.GetTypeCode(Enum.GetUnderlyingType(typeof(TEnum))) is
            TypeCode.Byte or TypeCode.UInt16 or TypeCode.UInt32 or TypeCode.UInt64;

        public override TEnum Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            return reader.TokenType switch
            {
                JsonTokenType.Number => IsUnsigned
                                        ? (TEnum)Enum.ToObject(typeof(TEnum), reader.GetUInt64())
                                        : (TEnum)Enum.ToObject(typeof(TEnum), reader.GetInt64()),
                JsonTokenType.String => Parse(reader.GetString()!), // The token is a string
                _ => throw new JsonException($"Can't convert {reader.TokenType} to {typeof(TEnum).Name}."),
            };
        }

        public override void Write(Utf8JsonWriter writer, TEnum value, JsonSerializerOptions options)
        {
            if (IsUnsigned)
            {
                writer.WriteNumberValue(Convert.ToUInt64(value, CultureInfo.InvariantCulture));
            }
            else
            {
                writer.WriteNumberValue(Convert.ToInt64(value, CultureInfo.InvariantCulture));
            }
        }

        public override TEnum ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            return Parse(reader.GetString()!); // The token is a string
        }

        public override void WriteAsPropertyName(Utf8JsonWriter writer, TEnum value, JsonSerializerOptions options)
        {
            writer.WritePropertyName(value.ToString());
        }

        private static TEnum Parse(string text)
        {
            if (Enum.TryParse<TEnum>(text, ignoreCase: true, out TEnum result))
            {
                return result;
            }

            throw new JsonException($"'{text}' isn't a value of {typeof(TEnum).Name}.");
        }
    }
}

/// <summary>
/// Reads a value declared as <see cref="object"/> as a primitive where possible: a boolean, a whole number as
/// <see cref="long"/>, any other number as <see cref="double"/>, an ISO 8601 date as <see cref="DateTime"/>,
/// other text as <see cref="string"/>. Objects and arrays become a <see cref="JsonElement"/>.
/// </summary>
/// <remarks>
/// <para>
/// With <c>typeNames</c>, an object value is written with a leading <c>"$type"</c> property and read back as that
/// type, the way arguments of type <see cref="object"/> were stored with the recommended settings before.
/// </para>
/// <para>
/// With <c>rejectTypeNames</c>, used for text an operator types in the dashboard, values are written without
/// type names and an object with a <c>"$type"</c> property is refused instead of read, so typed text can't
/// make Masterly.NightShift create a type it names.
/// </para>
/// </remarks>
internal sealed class InferredObjectJsonConverter(bool typeNames, bool rejectTypeNames) : JsonConverter<object>
{
    private readonly bool _rejectTypeNames = rejectTypeNames;

    public InferredObjectJsonConverter(bool typeNames)
        : this(typeNames, rejectTypeNames: false)
    {
    }

    /// <summary>Tells whether object values are written with their type name and read back as that type.</summary>
    public bool TypeNames { get; } = typeNames && !rejectTypeNames;

    public override object? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.True: return true;
            case JsonTokenType.False: return false;
            case JsonTokenType.Null: return null;
            case JsonTokenType.Number:
                return reader.TryGetInt64(out long whole) ? (object)whole : reader.GetDouble();
            case JsonTokenType.String:
                return reader.TryGetDateTime(out DateTime date) ? date : reader.GetString();
        }

        using JsonDocument document = JsonDocument.ParseValue(ref reader);
        JsonElement root = document.RootElement;

        if (_rejectTypeNames && TypeNamedJson.HasTypeProperty(root))
        {
            throw new JsonException(
                $"A \"{TypeNamedJson.TypeProperty}\" property can't be used here: a value of type object is read as plain JSON.");
        }

        // Only a leading "$type" names the type: a later one is the value's own member
        if (TypeNames && TypeNamedJson.TryGetTypeName(root, out string? typeName))
        {
            return TypeNamedJson.Read(root, TypeHelper.CurrentTypeResolver(typeName), options);
        }

        return root.Clone();
    }

    public override void Write(Utf8JsonWriter writer, object value, JsonSerializerOptions options)
    {
        Type type = value.GetType();
        if (type == typeof(object))
        {
            writer.WriteStartObject();
            writer.WriteEndObject();
            return;
        }

        if (!TypeNames)
        {
            JsonSerializer.Serialize(writer, value, type, options);
            return;
        }

        TypeNamedJson.Write(writer, value, options);
    }
}

/// <summary>
/// Reads an array either as a plain JSON array or wrapped as <c>{"$type": …, "$values": […]}</c>, the form data
/// written with type names everywhere uses. Arrays are always written plainly.
/// </summary>
internal sealed class LegacyTypedArrayConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert)
    {
        return typeToConvert.IsArray && typeToConvert.GetArrayRank() == 1 && typeToConvert != typeof(byte[]);
    }

    public override JsonConverter? CreateConverter(Type typeToConvert, JsonSerializerOptions options)
    {
        return (JsonConverter?)Activator.CreateInstance(
            typeof(ArrayConverter<>).MakeGenericType(typeToConvert.GetElementType()!)); // An array type has an element type
    }

    private sealed class ArrayConverter<TElement> : JsonConverter<TElement[]>
    {
        public override TElement[]? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.StartObject)
            {
                using JsonDocument document = JsonDocument.ParseValue(ref reader);
                if (!document.RootElement.TryGetProperty("$values", out JsonElement values))
                {
                    throw new JsonException($"Expected an array or a \"$values\" property for {typeToConvert.Name}.");
                }

                return JsonSerializer.Deserialize<TElement[]>(values.GetRawText(), options);
            }

            if (reader.TokenType != JsonTokenType.StartArray)
            {
                throw new JsonException($"Can't convert {reader.TokenType} to {typeToConvert.Name}.");
            }

            List<TElement> items = [];
            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            {
                items.Add(JsonSerializer.Deserialize<TElement>(ref reader, options)!); // A null element stays null
            }

            return [.. items];
        }

        public override void Write(Utf8JsonWriter writer, TElement[] value, JsonSerializerOptions options)
        {
            writer.WriteStartArray();
            foreach (TElement? item in value)
            {
                // Every member of the actual item, as for other values; object and IState have their own converters
                bool declared = typeof(TElement) == typeof(object) || typeof(TElement) == typeof(IState) || item == null;
                JsonSerializer.Serialize(writer, item, declared ? typeof(TElement) : item!.GetType(), options); // declared is true when the item is null
            }
            writer.WriteEndArray();
        }
    }
}

/// <summary>
/// Reads collections and dictionaries that the previous serializer stored with their type name. With type names
/// on, it did that when the actual type differed from the declared one (an <c>IReadOnlyList&lt;T&gt;</c> holding a
/// <c>List&lt;T&gt;</c>, an <c>IEnumerable&lt;T&gt;</c> holding an array): a collection was wrapped as
/// <c>{"$type": …, "$values": […]}</c>, and a dictionary got a leading <c>"$type"</c> property. The JSON is
/// rewritten without them, guided by the declared type, and then read as usual: the values are read as the
/// declared type, and the stored type name is ignored, so stored data can't make a server create a type it names.
/// Arrays wrapped this way are read by <see cref="LegacyTypedArrayConverterFactory"/>; this also covers collections
/// nested in arrays, collections and dictionaries. With the options for text an operator typed
/// (<see cref="RefusedTypeInputJsonConverter.IsInput"/>), a dictionary's leading <c>"$type"</c> is never resolved:
/// it is an ordinary key.
/// </summary>
internal static class LegacyCollectionJson
{
    private const string ValuesProperty = "$values";

    private static readonly ConcurrentDictionary<Type, Shape> Shapes = new();

    /// <summary>
    /// Tells whether JSON for <paramref name="type"/> may hold such a wrapped collection or dictionary somewhere
    /// this class rewrites: the value itself, or the items of an array, collection or dictionary.
    /// </summary>
    public static bool MayHoldWrapped(Type type)
    {
        Shape shape = GetShape(type);
        return shape.Kind == ShapeKind.Collection || shape.Kind == ShapeKind.Dictionary ||
               (shape.Kind == ShapeKind.Array && shape.Item.Kind != ShapeKind.None);
    }

    /// <summary>
    /// Tells, from the first token of a value and without reading it, whether it may need to be rewritten: an
    /// object for a collection, a dictionary with a leading <c>"$type"</c>, or items that may be wrapped themselves.
    /// </summary>
    public static bool MayBeWrapped(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
    {
        Shape shape = GetShape(type);
        bool collection = shape.Kind is ShapeKind.Collection or ShapeKind.Array;

        switch (reader.TokenType)
        {
            case JsonTokenType.StartArray:
                return collection && shape.Item.Kind != ShapeKind.None;
            case JsonTokenType.StartObject when collection:
                return true;
            case JsonTokenType.StartObject when shape.Kind == ShapeKind.Dictionary:
                if (shape.Item.Kind != ShapeKind.None)
                {
                    return true;
                }

                if (RefusedTypeInputJsonConverter.IsInput(options))
                {
                    return false;
                }

                // A leading "$id" may come before the "$type": the document read next decides
                Utf8JsonReader copy = reader;
                return copy.Read() && copy.TokenType == JsonTokenType.PropertyName &&
                       (copy.ValueTextEquals(TypeNamedJson.IdProperty) ||
                        (copy.ValueTextEquals(TypeNamedJson.TypeProperty) &&
                         copy.Read() && copy.TokenType == JsonTokenType.String && IsLegacyTypeName(copy.GetString(), shape.Type)));
            default:
                return false;
        }
    }

    /// <summary>Tells whether a value with this shape needs to be rewritten to be read with <paramref name="options"/>.</summary>
    public static bool IsWrapped(JsonElement element, Type type, JsonSerializerOptions options)
    {
        return IsWrapped(element, GetShape(type), !RefusedTypeInputJsonConverter.IsInput(options));
    }

    /// <summary>Returns the JSON without the type names the previous serializer added to collections and dictionaries.</summary>
    public static byte[] Unwrap(JsonElement element, Type type, JsonSerializerOptions options)
    {
        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = new(stream))
        {
            Write(element, GetShape(type), writer, !RefusedTypeInputJsonConverter.IsInput(options));
        }

        return stream.ToArray();
    }

    /// <summary>Reads <paramref name="json"/> as <paramref name="type"/>, rewriting it first when it holds wrapped collections.</summary>
    public static object? Deserialize(string json, Type type, JsonSerializerOptions options)
    {
        JsonDocumentOptions documentOptions = new()
        {
            AllowTrailingCommas = options.AllowTrailingCommas,
            CommentHandling = options.ReadCommentHandling,
            MaxDepth = options.MaxDepth
        };

        using JsonDocument document = JsonDocument.Parse(json, documentOptions);
        JsonElement root = document.RootElement;

        return IsWrapped(root, type, options)
            ? JsonSerializer.Deserialize(Unwrap(root, type, options), type, options)
            : root.Deserialize(type, options);
    }

    // typeNames: a dictionary's leading "$type" may be the previous serializer's type name, not a key
    private static bool IsWrapped(JsonElement element, Shape shape, bool typeNames)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object when shape.Kind is ShapeKind.Collection or ShapeKind.Array:
                return true;
            case JsonValueKind.Object when shape.Kind == ShapeKind.Dictionary:
                if (typeNames && CountTypeNameEntries(element, shape.Type!) > 0) // Only collection shapes reach this, and they have a type
                {
                    return true;
                }

                if (shape.Item.Kind == ShapeKind.None)
                {
                    return false;
                }

                foreach (JsonProperty property in element.EnumerateObject())
                {
                    if (IsWrapped(property.Value, shape.Item, typeNames))
                    {
                        return true;
                    }
                }
                return false;
            case JsonValueKind.Array when (shape.Kind == ShapeKind.Collection || shape.Kind == ShapeKind.Array) && shape.Item.Kind != ShapeKind.None:
                foreach (JsonElement item in element.EnumerateArray())
                {
                    if (IsWrapped(item, shape.Item, typeNames))
                    {
                        return true;
                    }
                }
                return false;
            default:
                return false;
        }
    }

    private static void Write(JsonElement element, Shape shape, Utf8JsonWriter writer, bool typeNames)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object when shape.Kind is ShapeKind.Collection or ShapeKind.Array:
                // A collection that isn't wrapped is left to fail as it would have
                if (element.TryGetProperty(ValuesProperty, out JsonElement values))
                {
                    Write(values, shape, writer, typeNames);
                }
                else
                {
                    element.WriteTo(writer);
                }

                return;
            case JsonValueKind.Object when shape.Kind == ShapeKind.Dictionary:
                writer.WriteStartObject();
                int skip = typeNames ? CountTypeNameEntries(element, shape.Type!) : 0; // Only collection shapes reach this, and they have a type
                foreach (JsonProperty property in element.EnumerateObject())
                {
                    if (skip > 0)
                    {
                        skip--;
                        continue;
                    }

                    writer.WritePropertyName(property.Name);
                    Write(property.Value, shape.Item, writer, typeNames);
                }
                writer.WriteEndObject();
                return;
            case JsonValueKind.Array when (shape.Kind == ShapeKind.Collection || shape.Kind == ShapeKind.Array) && shape.Item.Kind != ShapeKind.None:
                writer.WriteStartArray();
                foreach (JsonElement item in element.EnumerateArray())
                {
                    Write(item, shape.Item, writer, typeNames);
                }
                writer.WriteEndArray();
                return;
            default:
                element.WriteTo(writer);
                return;
        }
    }

    // A dictionary whose first entry (after a "$id") is a "$type" naming its type: what the previous serializer
    // wrote. The number of entries that aren't the dictionary's own, or 0.
    private static int CountTypeNameEntries(JsonElement element, Type declaredType)
    {
        return TypeNamedJson.TryGetTypeName(element, out string typeName, out int metadataMembers) &&
               IsLegacyTypeName(typeName, declaredType)
            ? metadataMembers
            : 0;
    }

    /// <summary>
    /// Tells whether the value of a dictionary's leading <c>"$type"</c> entry is the type name the previous
    /// serializer wrote there, rather than an ordinary key: a name the configured type resolver resolves to a
    /// dictionary type assignable to the declared type, or of the same generic type definition.
    /// </summary>
    private static bool IsLegacyTypeName(string? value, Type? declaredType)
    {
        // A type name always names an assembly or a namespace
        if (string.IsNullOrWhiteSpace(value) || declaredType == null || !value.AsSpan().ContainsAny(TypeNameSeparators))
        {
            return false;
        }

        Type type;
        try
        {
            type = TypeHelper.CurrentTypeResolver(value);
        }
        catch (Exception ex) when (ex.IsCatchableExceptionType())
        {
            return false;
        }

        if (type == null || !IsDictionaryType(type))
        {
            return false;
        }

        if (declaredType.IsAssignableFrom(type))
        {
            return true;
        }

        return type.IsGenericType && declaredType.IsGenericType &&
               type.GetGenericTypeDefinition() == declaredType.GetGenericTypeDefinition();
    }

    private static readonly SearchValues<char> TypeNameSeparators = SearchValues.Create(".,");

    private static bool IsDictionaryType(Type type)
    {
        if (typeof(IDictionary).IsAssignableFrom(type))
        {
            return true;
        }

        foreach (Type? candidate in type.IsInterface ? type.GetInterfaces().Concat([type]) : type.GetInterfaces())
        {
            if (!candidate.IsGenericType)
            {
                continue;
            }

            Type definition = candidate.GetGenericTypeDefinition();
            if (definition == typeof(IDictionary<,>) || definition == typeof(IReadOnlyDictionary<,>))
            {
                return true;
            }
        }

        return false;
    }

    private static Shape GetShape(Type type)
    {
        return Shapes.GetOrAdd(type, static t => CreateShape(t, depth: 0));
    }

    private static Shape CreateShape(Type type, int depth)
    {
        // Nested deeper than any stored data plausibly is: read as it is
        if (depth > 8)
        {
            return Shape.None;
        }

        if (type.IsArray)
        {
            return type.GetArrayRank() == 1 && type != typeof(byte[])
                ? new Shape(ShapeKind.Array, CreateShape(type.GetElementType()!, depth + 1)) // An array type has an element type
                : Shape.None;
        }

        if (type == typeof(string) || type.IsPrimitive || !typeof(IEnumerable).IsAssignableFrom(type))
        {
            return Shape.None;
        }

        // JsonArray, JsonObject and other types System.Text.Json reads its own way, and types with a converter of their own
        if (type.Namespace != null && type.Namespace.StartsWith("System.Text.Json", StringComparison.Ordinal))
        {
            return Shape.None;
        }

        if (type.IsDefined(typeof(JsonConverterAttribute), inherit: false))
        {
            return Shape.None;
        }

        Type[] interfaces = type.IsInterface ? [.. type.GetInterfaces(), .. new[] { type }] : type.GetInterfaces();
        Type? itemType = null;

        foreach (Type candidate in interfaces)
        {
            if (!candidate.IsGenericType)
            {
                continue;
            }

            Type definition = candidate.GetGenericTypeDefinition();
            if (definition == typeof(IDictionary<,>) || definition == typeof(IReadOnlyDictionary<,>))
            {
                return new Shape(ShapeKind.Dictionary, CreateShape(candidate.GetGenericArguments()[1], depth + 1), type);
            }

            if (definition == typeof(IEnumerable<>))
            {
                itemType = candidate.GetGenericArguments()[0];
            }
        }

        if (typeof(IDictionary).IsAssignableFrom(type))
        {
            return new Shape(ShapeKind.Dictionary, Shape.None, type);
        }

        return new Shape(ShapeKind.Collection, itemType == null ? Shape.None : CreateShape(itemType, depth + 1));
    }

    private enum ShapeKind
    {
        None,
        Array,
        Collection,
        Dictionary
    }

    /// <summary>
    /// What a declared type is for this rewriting, with the shape of its items or dictionary values, and the
    /// declared type of a dictionary.
    /// </summary>
    private sealed class Shape
    {
        public static readonly Shape None = new(ShapeKind.None, null!); // This shape has no type

        public Shape(ShapeKind kind, Shape item, Type? type = null)
        {
            Kind = kind;
            Item = item ?? this;
            Type = type;
        }

        public ShapeKind Kind { get; }
        public Shape Item { get; }
        public Type? Type { get; }
    }
}

/// <summary>
/// Gives each collection or dictionary member of an object a converter that reads it as
/// <see cref="LegacyCollectionJson"/> describes. It hands everything to System.Text.Json's own converter for the
/// member's type, so plain JSON is read as without it. Members that already have a converter (on the member, or in
/// the options for that type), members System.Text.Json is told to populate, and members with their own number
/// handling are left alone. It is only added to options used for reading, and does nothing with a
/// <see cref="ReferenceHandler"/>: the nested calls would start a new reference scope, and the handler's own
/// <c>"$values"</c> are System.Text.Json's to read.
/// </summary>
internal static class JsonLegacyCollectionModifier
{
    public static void Apply(JsonTypeInfo typeInfo)
    {
        if (typeInfo.Kind != JsonTypeInfoKind.Object || typeInfo.Options.ReferenceHandler != null)
        {
            return;
        }

        foreach (JsonPropertyInfo property in typeInfo.Properties)
        {
            Type type = property.PropertyType;
            if (property.CustomConverter != null || property.NumberHandling != null || property.IsExtensionData)
            {
                continue;
            }

            if (!LegacyCollectionJson.MayHoldWrapped(type))
            {
                continue;
            }

            JsonObjectCreationHandling creationHandling = property.ObjectCreationHandling ??
                                   typeInfo.PreferredPropertyObjectCreationHandling ??
                                   typeInfo.Options.PreferredObjectCreationHandling;
            if (creationHandling == JsonObjectCreationHandling.Populate || HasConverterInOptions(typeInfo.Options, type))
            {
                continue;
            }

            property.CustomConverter = (JsonConverter?)Activator.CreateInstance(typeof(Converter<>).MakeGenericType(type));
        }
    }

    private static bool HasConverterInOptions(JsonSerializerOptions options, Type type)
    {
        foreach (JsonConverter converter in options.Converters)
        {
            if (converter.CanConvert(type))
            {
                return true;
            }
        }

        return false;
    }

    private sealed class Converter<T> : JsonConverter<T>
    {
        public override T? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (!LegacyCollectionJson.MayBeWrapped(ref reader, typeof(T), options))
            {
                return JsonSerializer.Deserialize<T>(ref reader, options);
            }

            using JsonDocument document = JsonDocument.ParseValue(ref reader);
            JsonElement root = document.RootElement;

            return LegacyCollectionJson.IsWrapped(root, typeof(T), options)
                ? JsonSerializer.Deserialize<T>(LegacyCollectionJson.Unwrap(root, typeof(T), options), options)
                : root.Deserialize<T>(options);
        }

        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
        {
            JsonSerializer.Serialize(writer, value, options);
        }
    }
}
/// <summary>
/// Reads a struct whose members are set only by its one public constructor, as the previous serializer did:
/// <c>readonly struct Money { public Money(decimal amount, string currency) … public decimal Amount { get; } }</c>.
/// System.Text.Json creates a struct with its implicit parameterless constructor unless a constructor is marked
/// <see cref="JsonConstructorAttribute"/>, so every such member would come back with its default value. It applies
/// to a struct with no <see cref="JsonConstructorAttribute"/> and exactly one public constructor, which takes
/// parameters that each match, by name (ignoring case) and type, a member that can't be set. The members found in
/// the JSON are collected, the constructor is called with them (a missing one gets the parameter's default), and
/// the other members found in the JSON are set afterwards; a collection member without a setter gets its items
/// added to the collection the constructor created. Only reading changes.
/// </summary>
internal static class JsonConstructorOnlyStructModifier
{
    private static readonly ConditionalWeakTable<object, object[]> Pending = [];
    private static readonly object Missing = new();

    public static void Apply(JsonTypeInfo typeInfo)
    {
        Type type = typeInfo.Type;
        if (typeInfo.Kind != JsonTypeInfoKind.Object || !type.IsValueType || typeInfo.CreateObject == null)
        {
            return;
        }

        ConstructorInfo? constructor = GetConstructor(type);
        if (constructor == null)
        {
            return;
        }

        ParameterInfo[] parameters = constructor.GetParameters();
        IList<JsonPropertyInfo> properties = typeInfo.Properties;
        int[] parameterProperties = new int[parameters.Length];

        for (int i = 0; i < parameters.Length; i++)
        {
            int index = FindProperty(properties, parameters[i]);
            if (index < 0 || properties[index].Set != null)
            {
                return;
            }

            parameterProperties[i] = index;
        }

        int count = properties.Count;
        Action<object, object>[] setters = new Action<object, object>[count];
        for (int i = 0; i < count; i++)
        {
            int index = i;
            JsonPropertyInfo property = properties[i];
            if (property.IsExtensionData)
            {
                continue;
            }

            bool parameter = Array.IndexOf(parameterProperties, i) >= 0;

            // A collection member without a setter gets its items added to the collection the constructor
            // created, so it waits for the constructor like the members that can be set
            setters[i] = property.Set ?? (parameter ? null : JsonGetOnlyCollectionModifier.CreateSetter(typeInfo, property))!; // Filtered on use: a parameter has no setter
            if (setters[i] == null && !parameter)
            {
                continue;
            }

            property.Set = (instance, value) => Pending.GetValue(instance, _ => NewValues(count))[index] = value!; // A null value stays null
        }

        Action<object>? previous = typeInfo.OnDeserialized;
        typeInfo.OnDeserialized = instance =>
        {
            Construct(instance, constructor, parameters, parameterProperties, setters);
            previous?.Invoke(instance);
        };
    }

    private static ConstructorInfo? GetConstructor(Type type)
    {
        foreach (ConstructorInfo candidate in type.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            if (candidate.IsDefined(typeof(JsonConstructorAttribute), inherit: false))
            {
                return null;
            }
        }

        ConstructorInfo[] constructors = type.GetConstructors(BindingFlags.Instance | BindingFlags.Public);
        return constructors is [var only] && only.GetParameters().Length > 0 ? only : null;
    }

    private static int FindProperty(IList<JsonPropertyInfo> properties, ParameterInfo parameter)
    {
        int found = -1;
        for (int i = 0; i < properties.Count; i++)
        {
            MemberInfo? member = properties[i].AttributeProvider as MemberInfo;
            if (member == null || properties[i].PropertyType != parameter.ParameterType)
            {
                continue;
            }

            // An exact match wins over one that differs only by case, as it did with the previous serializer
            if (string.Equals(member.Name, parameter.Name, StringComparison.Ordinal))
            {
                return i;
            }

            if (found < 0 && string.Equals(member.Name, parameter.Name, StringComparison.OrdinalIgnoreCase))
            {
                found = i;
            }
        }

        return found;
    }

    private static object[] NewValues(int count)
    {
        object[] values = new object[count];
        for (int i = 0; i < count; i++)
        {
            values[i] = Missing;
        }

        return values;
    }

    private static void Construct(
        object instance,
        ConstructorInfo constructor,
        ParameterInfo[] parameters,
        int[] parameterProperties,
        Action<object, object>[] setters)
    {
        if (!Pending.TryGetValue(instance, out object[]? values))
        {
            values = NewValues(setters.Length);
        }

        Pending.Remove(instance);

        object[] arguments = new object[parameters.Length];
        for (int i = 0; i < parameters.Length; i++)
        {
            object value = values[parameterProperties[i]];
            arguments[i] = !ReferenceEquals(value, Missing) ? value : DefaultOf(parameters[i])!; // A null argument stays null
        }

        object constructed;
        try
        {
            constructed = constructor.Invoke(arguments);
        }
        catch (TargetInvocationException ex) when (ex.InnerException != null)
        {
            ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }

        // The members that can be set, and the items of collection members without a setter, which the JSON had,
        // are set after the constructor ran
        for (int i = 0; i < setters.Length; i++)
        {
            if (setters[i] != null && !ReferenceEquals(values[i], Missing))
            {
                setters[i](constructed, values[i]);
            }
        }

        // System.Text.Json unboxes the instance it created, so the constructed value is copied into it
        foreach (FieldInfo field in instance.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            field.SetValue(instance, field.GetValue(constructed));
        }
    }

    private static object? DefaultOf(ParameterInfo parameter)
    {
        Type type = parameter.ParameterType;
        if (parameter.HasDefaultValue && parameter.DefaultValue != null && parameter.DefaultValue != DBNull.Value)
        {
            // An enum's default value may come back as its underlying number
            Type target = Nullable.GetUnderlyingType(type) ?? type;
            return target.IsEnum && !target.IsInstanceOfType(parameter.DefaultValue)
                ? Enum.ToObject(target, parameter.DefaultValue)
                : parameter.DefaultValue;
        }

        return type.IsValueType ? Activator.CreateInstance(type) : null;
    }
}

/// <summary>
/// Reads a collection member that has no setter into the collection the object already holds, as the previous
/// serializer did: <c>public List&lt;string&gt; Items { get; } = new List&lt;string&gt;();</c>. System.Text.Json skips
/// such a member, so its items would be lost. The items found in the JSON are added to the collection (a dictionary
/// sets its keys); nothing happens when the member is <c>null</c> or holds a read-only or fixed-size collection, such
/// as an array. Members System.Text.Json is told to populate itself (<see cref="JsonObjectCreationHandling.Populate"/>)
/// are left to it. Only reading changes.
/// </summary>
internal static class JsonGetOnlyCollectionModifier
{
    private static readonly MethodInfo? AddItemsMethod = typeof(JsonGetOnlyCollectionModifier)
        .GetMethod(nameof(AddItems), BindingFlags.NonPublic | BindingFlags.Static);

    private static readonly MethodInfo? SetItemsMethod = typeof(JsonGetOnlyCollectionModifier)
        .GetMethod(nameof(SetItems), BindingFlags.NonPublic | BindingFlags.Static);

    public static void Apply(JsonTypeInfo typeInfo)
    {
        if (typeInfo.Kind != JsonTypeInfoKind.Object)
        {
            return;
        }

        foreach (JsonPropertyInfo property in typeInfo.Properties)
        {
            Action<object, object>? set = CreateSetter(typeInfo, property);
            if (set != null)
            {
                property.Set = set!; // Checked above
            }
        }
    }

    /// <summary>
    /// Returns what adds the items read for a collection member without a setter to the collection the object
    /// holds, or <c>null</c> when this doesn't apply to the member.
    /// </summary>
    public static Action<object, object>? CreateSetter(JsonTypeInfo typeInfo, JsonPropertyInfo property)
    {
        if (property.Get == null || property.Set != null || property.IsExtensionData)
        {
            return null;
        }

        JsonObjectCreationHandling creationHandling = property.ObjectCreationHandling ??
                               typeInfo.PreferredPropertyObjectCreationHandling ??
                               typeInfo.Options.PreferredObjectCreationHandling;
        if (creationHandling == JsonObjectCreationHandling.Populate)
        {
            return null;
        }

        Action<object, object>? add = GetAdder(property.PropertyType);
        if (add == null)
        {
            return null;
        }

        Func<object, object?> get = property.Get;
        return (instance, items) =>
        {
            object? existing = get(instance);
            if (existing != null && items != null && !ReferenceEquals(existing, items))
            {
                add(existing, items);
            }
        };
    }

    /// <summary>
    /// Returns what adds the items of a deserialized collection to an existing one, or <c>null</c> when
    /// <paramref name="type"/> isn't a collection System.Text.Json creates on its own.
    /// </summary>
    private static Action<object, object>? GetAdder(Type type)
    {
        if (type.IsArray || type == typeof(string) || !typeof(IEnumerable).IsAssignableFrom(type))
        {
            return null;
        }

        if (!type.IsInterface && (type.IsAbstract || type.GetConstructor(Type.EmptyTypes) == null))
        {
            return null;
        }

        if (type.Namespace != null && type.Namespace.StartsWith("System.Text.Json", StringComparison.Ordinal))
        {
            return null;
        }

        if (type.IsDefined(typeof(JsonConverterAttribute), inherit: false))
        {
            return null;
        }

        Type[] interfaces = type.IsInterface ? [.. type.GetInterfaces(), .. new[] { type }] : type.GetInterfaces();

        foreach (Type candidate in interfaces)
        {
            if (!candidate.IsGenericType)
            {
                continue;
            }

            Type definition = candidate.GetGenericTypeDefinition();
            if (definition == typeof(IDictionary<,>) || definition == typeof(IReadOnlyDictionary<,>))
            {
                return SetItemsMethod!.MakeGenericMethod(candidate.GetGenericArguments()) // The method exists: found by name on this type
                    .CreateDelegate<Action<object, object>>();
            }
        }

        foreach (Type candidate in interfaces)
        {
            if (candidate.IsGenericType && candidate.GetGenericTypeDefinition() == typeof(IEnumerable<>))
            {
                return AddItemsMethod!.MakeGenericMethod(candidate.GetGenericArguments()) // The method exists: found by name on this type
                    .CreateDelegate<Action<object, object>>();
            }
        }

        if (typeof(IDictionary).IsAssignableFrom(type))
        {
            return SetEntries;
        }

        if (typeof(IList).IsAssignableFrom(type) || type == typeof(IEnumerable) || type == typeof(ICollection))
        {
            return AddEntries;
        }

        return null;
    }

    private static void AddItems<T>(object existing, object items)
    {
        if (existing is ICollection<T> collection && !collection.IsReadOnly && existing is not Array)
        {
            foreach (T? item in (IEnumerable<T>)items)
            {
                collection.Add(item);
            }
        }
        else
        {
            AddEntries(existing, items);
        }
    }

    private static void SetItems<TKey, TValue>(object existing, object items)
    {
        if (existing is IDictionary<TKey, TValue> dictionary && !dictionary.IsReadOnly)
        {
            foreach (KeyValuePair<TKey, TValue> item in (IEnumerable<KeyValuePair<TKey, TValue>>)items)
            {
                dictionary[item.Key] = item.Value;
            }
        }
        else
        {
            SetEntries(existing, items);
        }
    }

    private static void AddEntries(object existing, object items)
    {
        if (existing is IList list && !list.IsReadOnly && !list.IsFixedSize)
        {
            foreach (object? item in (IEnumerable)items)
            {
                list.Add(item);
            }
        }
    }

    private static void SetEntries(object existing, object items)
    {
        if (existing is IDictionary dictionary && !dictionary.IsReadOnly && !dictionary.IsFixedSize && items is IDictionary entries)
        {
            foreach (DictionaryEntry entry in entries)
            {
                dictionary[entry.Key] = entry.Value;
            }
        }
    }
}

/// <summary>
/// Handles <see cref="DefaultValueAttribute"/> like the previous serializer's <c>IgnoreAndPopulate</c> did, without
/// changing user data. When reading, a member missing from the JSON is set to its default value, so data written
/// with such members left out reads back as it was written. When writing, only a member declared by a type of
/// Masterly.NightShift itself (the <c>Order</c> of filter attributes, for example) is left out when it equals its
/// default value, as those members always were; members of other types (job arguments, return values) are always
/// written, so they keep exactly the values they had.
/// </summary>
internal static class JsonDefaultValueModifier
{
    private static readonly Assembly OwnAssembly = typeof(JsonDefaultValueModifier).Assembly;

    public static void Apply(JsonTypeInfo typeInfo)
    {
        if (typeInfo.Kind != JsonTypeInfoKind.Object)
        {
            return;
        }

        List<KeyValuePair<JsonPropertyInfo, object>>? populated = null;
        HashSet<string>? constructorParameters = null;

        foreach (JsonPropertyInfo property in typeInfo.Properties)
        {
            if (!TryGetDefaultValue(property, out object? expected))
            {
                continue;
            }

            if (property.AttributeProvider is MemberInfo member && member.DeclaringType?.Assembly == OwnAssembly)
            {
                property.ShouldSerialize = (_, value) => !Equals(value, expected);
            }

            // A member bound to a constructor parameter gets that parameter's default from System.Text.Json,
            // and setting it before the JSON is applied would overwrite what the constructor stored
            constructorParameters ??= GetConstructorParameters(typeInfo);
            if (property.Set == null || property.IsExtensionData ||
                (property.AttributeProvider is MemberInfo bound && constructorParameters.Contains(bound.Name)))
            {
                continue;
            }

            (populated ??= []).Add(
                new KeyValuePair<JsonPropertyInfo, object>(property, expected));
        }

        if (populated == null)
        {
            return;
        }

        // Runs once the object is created, before the members found in the JSON are set
        Action<object>? previous = typeInfo.OnDeserializing;
        typeInfo.OnDeserializing = instance =>
        {
            foreach (KeyValuePair<JsonPropertyInfo, object> member in populated)
            {
                member.Key.Set!(instance, member.Value); // Only members with a setter are collected
            }

            previous?.Invoke(instance);
        };
    }

    private static bool TryGetDefaultValue(JsonPropertyInfo property, out object value)
    {
        value = null!; // Unused when this returns false

        object[]? attributes = property.AttributeProvider?.GetCustomAttributes(typeof(DefaultValueAttribute), inherit: true);
        if (attributes is not { Length: > 0 } || attributes[0] is not DefaultValueAttribute defaultValue)
        {
            return false;
        }

        try
        {
            value = ConvertDefault(defaultValue.Value, property.PropertyType)!; // A null default stays null
            return true;
        }
        catch (Exception ex) when (ex is InvalidCastException or FormatException or OverflowException or ArgumentException)
        {
            // A default value that doesn't fit the member's type is ignored, as if it wasn't there
            return false;
        }
    }

    private static object? ConvertDefault(object? value, Type type)
    {
        if (value == null || type.IsInstanceOfType(value))
        {
            return value;
        }

        Type target = Nullable.GetUnderlyingType(type) ?? type;
        return target.IsEnum
            ? Enum.ToObject(target, value)
            : Convert.ChangeType(value, target, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// The names of the constructor parameters System.Text.Json may bind members to. Without a parameterless
    /// way to create the object, every public constructor and any marked <see cref="JsonConstructorAttribute"/>
    /// count, which can only leave out more members than needed.
    /// </summary>
    private static HashSet<string> GetConstructorParameters(JsonTypeInfo typeInfo)
    {
        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
        if (typeInfo.CreateObject != null)
        {
            return names;
        }

        foreach (ConstructorInfo constructor in typeInfo.Type.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            if (!constructor.IsPublic && !constructor.IsDefined(typeof(JsonConstructorAttribute), inherit: false))
            {
                continue;
            }

            foreach (ParameterInfo parameter in constructor.GetParameters())
            {
                if (parameter.Name != null)
                {
                    names.Add(parameter.Name);
                }
            }
        }

        return names;
    }
}

/// <summary>Type names as stored in a <c>"$type"</c> property: without version, culture or public key.</summary>
internal static partial class JsonTypeNames
{
    [GeneratedRegex(@",\s*(Version|Culture|PublicKeyToken)=[^,\]]*", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 5000)]
    private static partial Regex AssemblyDetailsRegex();

    public static string? Simple(string typeName)
    {
        return typeName == null ? null : AssemblyDetailsRegex().Replace(typeName, string.Empty);
    }
}

/// <summary>Writes a culture as its name, for example <c>"en-US"</c>, and reads it back.</summary>
internal sealed class CultureInfoJsonConverter : JsonConverter<CultureInfo>
{
    public override CultureInfo? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return null;
        }

        return CultureInfo.GetCultureInfo(reader.GetString() ?? string.Empty);
    }

    public override void Write(Utf8JsonWriter writer, CultureInfo value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.Name);
    }
}

/// <summary>
/// Writes an XML element as its XML text, and reads either XML text or the JSON form older data uses, where
/// <c>{"name": "text"}</c> is the element <c>&lt;name&gt;text&lt;/name&gt;</c>, <c>@attribute</c> keys are
/// attributes, <c>#text</c> is the text and nested objects and arrays are child elements.
/// </summary>
internal sealed class XElementJsonConverter : JsonConverter<XElement>
{
    public override XElement? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return null;
        }

        if (reader.TokenType == JsonTokenType.String)
        {
            return XElement.Parse(reader.GetString() ?? string.Empty);
        }

        using JsonDocument document = JsonDocument.ParseValue(ref reader);
        JsonElement root = document.RootElement;
        List<JsonProperty>? elements = root.ValueKind == JsonValueKind.Object
            ? [.. root.EnumerateObject().Where(static p => !p.Name.StartsWith('?'))]
            : null;

        if (elements == null || elements.Count != 1)
        {
            throw new JsonException("An XML element must be XML text or a JSON object with one root property.");
        }

        return ToElement(elements[0].Name, elements[0].Value).Single();
    }

    public override void Write(Utf8JsonWriter writer, XElement value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.ToString(SaveOptions.DisableFormatting));
    }

    private static System.Collections.Generic.IEnumerable<XElement> ToElement(string name, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Array:
                foreach (JsonElement item in value.EnumerateArray())
                {
                    foreach (XElement element in ToElement(name, item))
                    {
                        yield return element;
                    }
                }
                break;
            case JsonValueKind.Object:
                XElement result = new(name);
                foreach (JsonProperty property in value.EnumerateObject())
                {
                    if (property.Name.StartsWith('@'))
                    {
                        result.SetAttributeValue(property.Name[1..], ScalarText(property.Value));
                    }
                    else if (property.Name == "#text")
                    {
                        result.Add(new XText(ScalarText(property.Value) ?? string.Empty));
                    }
                    else
                    {
                        result.Add(ToElement(property.Name, property.Value));
                    }
                }
                yield return result;
                break;
            case JsonValueKind.Null:
                yield return new XElement(name);
                break;
            default:
                yield return new XElement(name, ScalarText(value));
                break;
        }
    }

    private static string? ScalarText(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Null => null,
            _ => value.GetRawText()
        };
    }
}

/// <summary>
/// Writes a <see cref="BigInteger"/> as a JSON number, as the previous serializer did, and reads a number or a
/// string. Without it, the value would be written as its read-only members and read back as zero.
/// </summary>
internal sealed class BigIntegerJsonConverter : JsonConverter<BigInteger>
{
    public override BigInteger Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return reader.TokenType switch
        {
            JsonTokenType.Number => Parse(Encoding.UTF8.GetString(reader.HasValueSequence ? reader.ValueSequence.ToArray() : reader.ValueSpan)),
            JsonTokenType.String => Parse(reader.GetString()!), // The token is a string
            _ => throw new JsonException($"Can't convert {reader.TokenType} to {nameof(BigInteger)}."),
        };
    }

    public override void Write(Utf8JsonWriter writer, BigInteger value, JsonSerializerOptions options)
    {
        writer.WriteRawValue(value.ToString("D", CultureInfo.InvariantCulture), skipInputValidation: true);
    }

    public override BigInteger ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return Parse(reader.GetString()!); // A property name is a string
    }

    public override void WriteAsPropertyName(Utf8JsonWriter writer, BigInteger value, JsonSerializerOptions options)
    {
        writer.WritePropertyName(value.ToString("D", CultureInfo.InvariantCulture));
    }

    private static BigInteger Parse(string text)
    {
        if (BigInteger.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out BigInteger value))
        {
            return value;
        }

        throw new JsonException($"'{text}' isn't a whole number.");
    }
}

/// <summary>Writes a <see cref="Rune"/> as its text, and reads text, its scalar value, or its members.</summary>
internal sealed class RuneJsonConverter : JsonConverter<Rune>
{
    public override Rune Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.String:
                string text = reader.GetString()!; // The token is a string
                if (Rune.DecodeFromUtf16(text, out Rune rune, out int length) == OperationStatus.Done && length == text.Length)
                {
                    return rune;
                }

                throw new JsonException($"'{text}' isn't a single Unicode scalar value.");
            case JsonTokenType.Number:
                return FromScalar(reader.GetInt32());
            case JsonTokenType.StartObject:
                // Written as its members ("Value" is the scalar) before this converter
                using (JsonDocument document = JsonDocument.ParseValue(ref reader))
                {
                    return JsonMembers.TryGet(document.RootElement, "Value", out JsonElement value) && value.ValueKind == JsonValueKind.Number
                        ? FromScalar(value.GetInt32())
                        : throw new JsonException($"A {nameof(Rune)} written as an object needs its \"Value\".");
                }
            default:
                throw new JsonException($"Can't convert {reader.TokenType} to {nameof(Rune)}.");
        }
    }

    public override void Write(Utf8JsonWriter writer, Rune value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.ToString());
    }

    private static Rune FromScalar(int value)
    {
        return Rune.TryCreate(value, out Rune rune) ? rune : throw new JsonException($"{value} isn't a Unicode scalar value.");
    }
}

/// <summary>
/// Writes an <see cref="Index"/> as its members, <c>{"Value": 1, "IsFromEnd": true}</c>, the form it was written in
/// before, and reads them back, or text such as <c>"^1"</c>. Without it, it would be read back as <c>0</c>.
/// </summary>
internal sealed class IndexJsonConverter : JsonConverter<Index>
{
    public override Index Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using JsonDocument document = JsonDocument.ParseValue(ref reader);
        return FromElement(document.RootElement);
    }

    public override void Write(Utf8JsonWriter writer, Index value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteNumber(JsonMembers.Name("Value", options), value.Value);
        writer.WriteBoolean(JsonMembers.Name("IsFromEnd", options), value.IsFromEnd);
        writer.WriteEndObject();
    }

    public static Index FromElement(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Number:
                return FromValue(element.GetInt32(), fromEnd: false);
            case JsonValueKind.String:
                return Parse(element.GetString()!); // The value is a string
            case JsonValueKind.Object when JsonMembers.TryGet(element, "Value", out JsonElement value) && value.ValueKind == JsonValueKind.Number:
                bool fromEnd = JsonMembers.TryGet(element, "IsFromEnd", out JsonElement end) && end.ValueKind == JsonValueKind.True;
                return FromValue(value.GetInt32(), fromEnd);
            default:
                throw new JsonException($"Can't convert {element.ValueKind} to {nameof(Index)}.");
        }
    }

    public static Index Parse(string text)
    {
        ReadOnlySpan<char> span = text.AsSpan().Trim();
        bool fromEnd = span.StartsWith("^", StringComparison.Ordinal);
        if (int.TryParse(fromEnd ? span[1..] : span, NumberStyles.None, CultureInfo.InvariantCulture, out int value))
        {
            return FromValue(value, fromEnd);
        }

        throw new JsonException($"'{text}' isn't an index.");
    }

    private static Index FromValue(int value, bool fromEnd)
    {
        return value >= 0 ? new Index(value, fromEnd) : throw new JsonException($"An index can't be negative: {value}.");
    }
}

/// <summary>
/// Writes a <see cref="Range"/> as its members, <c>{"Start": …, "End": …}</c>, the form it was written in before,
/// and reads them back, or text such as <c>"1..^2"</c>.
/// </summary>
internal sealed class RangeJsonConverter : JsonConverter<Range>
{
    public override Range Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using JsonDocument document = JsonDocument.ParseValue(ref reader);
        JsonElement root = document.RootElement;

        switch (root.ValueKind)
        {
            case JsonValueKind.String:
                string text = root.GetString()!; // The value is a string
                int separator = text.IndexOf("..", StringComparison.Ordinal);
                if (separator < 0)
                {
                    throw new JsonException($"'{text}' isn't a range.");
                }

                string start = text[..separator].Trim();
                string end = text[(separator + 2)..].Trim();
                return new Range(
                    start.Length == 0 ? Index.Start : IndexJsonConverter.Parse(start),
                    end.Length == 0 ? Index.End : IndexJsonConverter.Parse(end));
            case JsonValueKind.Object:
                return new Range(
                    JsonMembers.TryGet(root, "Start", out JsonElement startElement) ? IndexJsonConverter.FromElement(startElement) : Index.Start,
                    JsonMembers.TryGet(root, "End", out JsonElement endElement) ? IndexJsonConverter.FromElement(endElement) : Index.End);
            default:
                throw new JsonException($"Can't convert {root.ValueKind} to {nameof(Range)}.");
        }
    }

    public override void Write(Utf8JsonWriter writer, Range value, JsonSerializerOptions options)
    {
        IndexJsonConverter index = new();

        writer.WriteStartObject();
        writer.WritePropertyName(JsonMembers.Name("Start", options));
        index.Write(writer, value.Start, options);
        writer.WritePropertyName(JsonMembers.Name("End", options));
        index.Write(writer, value.End, options);
        writer.WriteEndObject();
    }
}

/// <summary>
/// Writes a <see cref="TimeZoneInfo"/> as its id when it is a time zone of the system with the same rules, and
/// otherwise as <see cref="TimeZoneInfo.ToSerializedString"/>. Reads either, or the members it was written as
/// before (its <c>"Id"</c>, or a custom zone from its <c>"BaseUtcOffset"</c>). Without it, a time zone was written
/// but could never be read back.
/// </summary>
internal sealed class TimeZoneInfoJsonConverter : JsonConverter<TimeZoneInfo>
{
    public override TimeZoneInfo? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Null:
                return null;
            case JsonTokenType.String:
                return FromText(reader.GetString()!); // The token is a string
            case JsonTokenType.StartObject:
                using (JsonDocument document = JsonDocument.ParseValue(ref reader))
                {
                    return FromMembers(document.RootElement);
                }
            default:
                throw new JsonException($"Can't convert {reader.TokenType} to {nameof(TimeZoneInfo)}.");
        }
    }

    public override void Write(Utf8JsonWriter writer, TimeZoneInfo value, JsonSerializerOptions options)
    {
        bool system = TimeZoneInfo.TryFindSystemTimeZoneById(value.Id, out TimeZoneInfo? found) && found.HasSameRules(value);
        writer.WriteStringValue(system ? value.Id : value.ToSerializedString());
    }

    private static TimeZoneInfo FromText(string text)
    {
        if (TimeZoneInfo.TryFindSystemTimeZoneById(text, out TimeZoneInfo? zone))
        {
            return zone;
        }

        try
        {
            return TimeZoneInfo.FromSerializedString(text);
        }
        catch (Exception ex) when (ex is ArgumentException or SerializationException)
        {
            throw new JsonException($"'{text}' is neither the id of a time zone of this system nor a serialized time zone.", ex);
        }
    }

    private static TimeZoneInfo FromMembers(JsonElement root)
    {
        if (!JsonMembers.TryGet(root, "Id", out JsonElement id) || id.ValueKind != JsonValueKind.String)
        {
            throw new JsonException($"A {nameof(TimeZoneInfo)} written as an object needs its \"Id\".");
        }

        string zoneId = id.GetString()!; // The value is a string
        if (TimeZoneInfo.TryFindSystemTimeZoneById(zoneId, out TimeZoneInfo? zone))
        {
            return zone;
        }

        if (JsonMembers.TryGet(root, "BaseUtcOffset", out JsonElement offset) && offset.ValueKind == JsonValueKind.String &&
            TimeSpan.TryParse(offset.GetString(), CultureInfo.InvariantCulture, out TimeSpan baseUtcOffset))
        {
            return TimeZoneInfo.CreateCustomTimeZone(
                zoneId,
                baseUtcOffset,
                JsonMembers.TryGet(root, "DisplayName", out JsonElement displayName) ? displayName.GetString() : zoneId,
                JsonMembers.TryGet(root, "StandardName", out JsonElement standardName) ? standardName.GetString() : zoneId);
        }

        throw new JsonException($"'{zoneId}' isn't the id of a time zone of this system.");
    }
}

/// <summary>
/// Writes a type as text when a <see cref="TypeConverter"/> converts it to and from text, as the previous serializer
/// did (<see cref="System.Drawing.Color"/>, for example), but only a type that System.Text.Json would write as its
/// members and couldn't read back (<see cref="JsonRoundTrip.CannotReadBack"/>), so a type that reads back today
/// keeps its form. Converters that can't be relied on (the base <see cref="TypeConverter"/>,
/// <see cref="ReferenceConverter"/> and its kind) are left out, as the previous serializer left them out.
/// </summary>
internal sealed class StringTypeConverterJsonConverterFactory : JsonConverterFactory
{
    private static readonly ConcurrentDictionary<Type, TypeConverter?> Converters = new();

    public override bool CanConvert(Type typeToConvert)
    {
        return Converters.GetOrAdd(typeToConvert, FindConverter) != null;
    }

    public override JsonConverter? CreateConverter(Type typeToConvert, JsonSerializerOptions options)
    {
        TypeConverter converter = Converters.GetOrAdd(typeToConvert, FindConverter)!; // Asked for only after CanConvert
        return (JsonConverter?)Activator.CreateInstance(typeof(Converter<>).MakeGenericType(typeToConvert), converter);
    }

    private static TypeConverter? FindConverter(Type type)
    {
        if (type.IsPrimitive || type.IsEnum || type.IsInterface || type.IsAbstract || type.IsArray || type.IsPointer ||
            type.IsByRef || type.ContainsGenericParameters || type == typeof(string) || type == typeof(object) ||
            Nullable.GetUnderlyingType(type) != null || typeof(IEnumerable).IsAssignableFrom(type) ||
            type.IsDefined(typeof(JsonConverterAttribute), inherit: false))
        {
            return null;
        }

        TypeConverter converter;
        try
        {
            converter = TypeDescriptor.GetConverter(type);
        }
        catch (Exception ex) when (ex.IsCatchableExceptionType())
        {
            return null;
        }

        Type converterType = converter.GetType();
        if (converterType == typeof(TypeConverter) || converter is ReferenceConverter ||
            converterType.FullName == "System.Windows.Forms.Design.DataSourceConverter")
        {
            return null;
        }

        try
        {
            return converter.CanConvertTo(typeof(string)) && converter.CanConvertFrom(typeof(string)) && JsonRoundTrip.CannotReadBack(type)
                ? converter
                : null;
        }
        catch (Exception ex) when (ex.IsCatchableExceptionType())
        {
            return null;
        }
    }

    private sealed class Converter<T>(TypeConverter converter) : JsonConverter<T>
    {
        public override T? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.String)
            {
                throw new JsonException($"Can't convert {reader.TokenType} to {typeof(T).Name}: it is written as text.");
            }

            string text = reader.GetString()!; // The token is a string
            try
            {
                return (T?)converter.ConvertFromInvariantString(text);
            }
            catch (Exception ex) when (ex.IsCatchableExceptionType())
            {
                throw new JsonException($"'{text}' can't be read as {typeof(T).Name}.", ex);
            }
        }

        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
        {
            writer.WriteStringValue(converter.ConvertToInvariantString(value));
        }
    }
}

/// <summary>
/// Tells whether System.Text.Json reads a type back from what it writes: a value type written as its members
/// none of which can be set (no setter, no constructor that takes them) comes back as its default value.
/// </summary>
internal static class JsonRoundTrip
{
    // System.Text.Json's own converters, public fields, and what reads structs set by their constructor and
    // collection members without a setter: what every option reads with
    private static readonly JsonSerializerOptions Probe = new()
    {
        IncludeFields = true,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver()
            .WithAddedModifier(JsonConstructorOnlyStructModifier.Apply)
            .WithAddedModifier(JsonGetOnlyCollectionModifier.Apply)
    };

    /// <summary>
    /// Tells whether System.Text.Json alone writes the type as its members and couldn't read it back: a value
    /// type whose written members can't be set, or a class it has no way to create.
    /// </summary>
    public static bool CannotReadBack(Type type)
    {
        JsonTypeInfo info;
        try
        {
            info = Probe.GetTypeInfo(type);
        }
        catch (Exception ex) when (ex.IsCatchableExceptionType())
        {
            return false;
        }

        return type.IsValueType
            ? IsReadAsDefault(info)
            : info.Kind == JsonTypeInfoKind.Object && info.CreateObject == null && !HasConstructorToBind(type);
    }

    /// <summary>
    /// Tells whether a value type is written as members that can't be read back: created with its parameterless
    /// constructor, and none of the members it writes has a setter.
    /// </summary>
    public static bool IsReadAsDefault(JsonTypeInfo info)
    {
        if (info.Kind != JsonTypeInfoKind.Object || !info.Type.IsValueType || info.CreateObject == null || info.Properties.Count == 0)
        {
            return false;
        }

        foreach (JsonPropertyInfo property in info.Properties)
        {
            if (property.Set != null || property.IsExtensionData)
            {
                return false;
            }
        }

        return true;
    }

    private static bool HasConstructorToBind(Type type)
    {
        foreach (ConstructorInfo constructor in type.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            if (constructor.IsPublic || constructor.IsDefined(typeof(JsonConstructorAttribute), inherit: false))
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>Reads and names the members of the objects the compatibility converters write.</summary>
internal static class JsonMembers
{
    /// <summary>Finds a member by name, ignoring case, as values written with any naming policy are read.</summary>
    public static bool TryGet(JsonElement element, string name, out JsonElement value)
    {
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    /// <summary>The name of a member as the options write it.</summary>
    public static string Name(string name, JsonSerializerOptions options)
    {
        return options.PropertyNamingPolicy?.ConvertName(name) ?? name;
    }
}
