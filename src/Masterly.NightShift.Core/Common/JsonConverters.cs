using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Xml.Linq;
using System.IO;
using System.Text.Json.Serialization.Metadata;
using System.Text.Json.Serialization;
using System.Text.Json;
using Masterly.NightShift.States;

namespace Masterly.NightShift.Common
{
    /// <summary>
    /// Writes a state with a leading <c>"$type"</c> property naming its type, and reads it back as that type.
    /// It's the format stored data already uses, so states saved before can still be read.
    /// </summary>
    internal sealed class StateJsonConverter : JsonConverter<IState>
    {
        public override IState Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            using var document = JsonDocument.ParseValue(ref reader);
            var root = document.RootElement;

            if (root.ValueKind == JsonValueKind.Null) return null;
            if (!TypeNamedJson.TryGetTypeName(root, out var typeName))
            {
                throw new JsonException($"A state must be a JSON object with a \"{TypeNamedJson.TypeProperty}\" property.");
            }

            return (IState)TypeNamedJson.Read(root, TypeNamedJson.ResolveType(typeName, typeof(IState)), options);
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
    internal static class TypeNamedJson
    {
        public const string TypeProperty = "$type";

        /// <summary>
        /// Writes every member of the value's actual type, after a <c>"$type"</c> property naming that type. A value
        /// that isn't written as a JSON object (a number, a string, an array) is written as it is, without a type.
        /// </summary>
        public static void Write(Utf8JsonWriter writer, object value, JsonSerializerOptions options)
        {
            var type = value.GetType();
            var element = JsonSerializer.SerializeToElement(value, type, options);
            if (element.ValueKind != JsonValueKind.Object)
            {
                element.WriteTo(writer);
                return;
            }

            writer.WriteStartObject();
            writer.WriteString(TypeProperty, JsonTypeNames.Simple(TypeHelper.CurrentTypeSerializer(type)));
            foreach (var property in element.EnumerateObject())
            {
                property.WriteTo(writer);
            }
            writer.WriteEndObject();
        }

        public static bool TryGetTypeName(JsonElement root, out string typeName)
        {
            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty(TypeProperty, out var property) && property.ValueKind == JsonValueKind.String)
            {
                typeName = property.GetString();
                return true;
            }

            typeName = null;
            return false;
        }

        /// <summary>
        /// Resolves a <c>"$type"</c> name through the configured type resolver, and checks that it names a concrete
        /// type assignable to <paramref name="expectedType"/>, so stored data can't make a server create anything else.
        /// </summary>
        public static Type ResolveType(string typeName, Type expectedType)
        {
            var type = TypeHelper.CurrentTypeResolver(typeName);
            if (!expectedType.IsAssignableFrom(type) || type.IsInterface || type.IsAbstract)
            {
                throw new JsonException($"The type '{typeName}' in \"{TypeProperty}\" isn't a concrete type assignable to '{expectedType}'.");
            }

            return type;
        }

        /// <summary>Reads an object written by <see cref="Write"/> as <paramref name="type"/>, leaving out <c>"$type"</c>.</summary>
        public static object Read(JsonElement root, Type type, JsonSerializerOptions options)
        {
            return JsonSerializer.Deserialize(WithoutTypeProperty(root), type, options);
        }

        public static byte[] WithoutTypeProperty(JsonElement root)
        {
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();
                foreach (var property in root.EnumerateObject())
                {
                    if (!property.NameEquals(TypeProperty)) property.WriteTo(writer);
                }
                writer.WriteEndObject();
            }

            return stream.ToArray();
        }
    }

    /// <summary>
    /// Holds a value that <see cref="SerializationHelper"/> writes with <see cref="TypeNamedJson"/>, because its
    /// declared type is an interface or abstract class that couldn't be read back without the actual type.
    /// </summary>
    [JsonConverter(typeof(Converter))]
    internal sealed class TypeNamedValue
    {
        public TypeNamedValue(object value)
        {
            Value = value;
        }

        public object Value { get; }

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

        public override Type Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Null) return null;
            var name = reader.GetString();
            return String.IsNullOrEmpty(name) ? null : TypeHelper.CurrentTypeResolver(name);
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
        public override bool CanConvert(Type typeToConvert) => typeof(Type).IsAssignableFrom(typeToConvert);

        public override Type Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Null) return null;
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

        public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
        {
            return (JsonConverter)Activator.CreateInstance(typeof(LenientEnumConverter<>).MakeGenericType(typeToConvert));
        }

        private sealed class LenientEnumConverter<TEnum> : JsonConverter<TEnum> where TEnum : struct, Enum
        {
            private static readonly bool IsUnsigned = Type.GetTypeCode(Enum.GetUnderlyingType(typeof(TEnum))) is
                TypeCode.Byte or TypeCode.UInt16 or TypeCode.UInt32 or TypeCode.UInt64;

            public override TEnum Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            {
                switch (reader.TokenType)
                {
                    case JsonTokenType.Number:
                        return IsUnsigned
                            ? (TEnum)Enum.ToObject(typeof(TEnum), reader.GetUInt64())
                            : (TEnum)Enum.ToObject(typeof(TEnum), reader.GetInt64());
                    case JsonTokenType.String:
                        return Parse(reader.GetString());
                    default:
                        throw new JsonException($"Can't convert {reader.TokenType} to {typeof(TEnum).Name}.");
                }
            }

            public override void Write(Utf8JsonWriter writer, TEnum value, JsonSerializerOptions options)
            {
                if (IsUnsigned) writer.WriteNumberValue(Convert.ToUInt64(value, CultureInfo.InvariantCulture));
                else writer.WriteNumberValue(Convert.ToInt64(value, CultureInfo.InvariantCulture));
            }

            public override TEnum ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            {
                return Parse(reader.GetString());
            }

            public override void WriteAsPropertyName(Utf8JsonWriter writer, TEnum value, JsonSerializerOptions options)
            {
                writer.WritePropertyName(value.ToString());
            }

            private static TEnum Parse(string text)
            {
                if (Enum.TryParse<TEnum>(text, ignoreCase: true, out var result)) return result;
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
    internal sealed class InferredObjectJsonConverter : JsonConverter<object>
    {
        private const string ValuesProperty = "$values";

        private readonly bool _typeNames;
        private readonly bool _rejectTypeNames;

        public InferredObjectJsonConverter(bool typeNames)
            : this(typeNames, rejectTypeNames: false)
        {
        }

        public InferredObjectJsonConverter(bool typeNames, bool rejectTypeNames)
        {
            _typeNames = typeNames && !rejectTypeNames;
            _rejectTypeNames = rejectTypeNames;
        }

        public override object Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.True: return true;
                case JsonTokenType.False: return false;
                case JsonTokenType.Null: return null;
                case JsonTokenType.Number:
                    return reader.TryGetInt64(out var whole) ? (object)whole : reader.GetDouble();
                case JsonTokenType.String:
                    return reader.TryGetDateTime(out var date) ? date : reader.GetString();
            }

            using var document = JsonDocument.ParseValue(ref reader);
            var root = document.RootElement;

            if (_rejectTypeNames && TypeNamedJson.TryGetTypeName(root, out _))
            {
                throw new JsonException(
                    $"A \"{TypeNamedJson.TypeProperty}\" property can't be used here: a value of type object is read as plain JSON.");
            }

            if (_typeNames && TypeNamedJson.TryGetTypeName(root, out var typeName))
            {
                var type = TypeHelper.CurrentTypeResolver(typeName);
                return root.TryGetProperty(ValuesProperty, out var values)
                    ? JsonSerializer.Deserialize(values.GetRawText(), type, options)
                    : TypeNamedJson.Read(root, type, options);
            }

            return root.Clone();
        }

        public override void Write(Utf8JsonWriter writer, object value, JsonSerializerOptions options)
        {
            var type = value.GetType();
            if (type == typeof(object))
            {
                writer.WriteStartObject();
                writer.WriteEndObject();
                return;
            }

            if (!_typeNames)
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

        public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
        {
            return (JsonConverter)Activator.CreateInstance(
                typeof(ArrayConverter<>).MakeGenericType(typeToConvert.GetElementType()!));
        }

        private sealed class ArrayConverter<TElement> : JsonConverter<TElement[]>
        {
            public override TElement[] Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            {
                if (reader.TokenType == JsonTokenType.StartObject)
                {
                    using var document = JsonDocument.ParseValue(ref reader);
                    if (!document.RootElement.TryGetProperty("$values", out var values))
                    {
                        throw new JsonException($"Expected an array or a \"$values\" property for {typeToConvert.Name}.");
                    }

                    return JsonSerializer.Deserialize<TElement[]>(values.GetRawText(), options);
                }

                if (reader.TokenType != JsonTokenType.StartArray)
                {
                    throw new JsonException($"Can't convert {reader.TokenType} to {typeToConvert.Name}.");
                }

                var items = new System.Collections.Generic.List<TElement>();
                while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                {
                    items.Add(JsonSerializer.Deserialize<TElement>(ref reader, options));
                }

                return items.ToArray();
            }

            public override void Write(Utf8JsonWriter writer, TElement[] value, JsonSerializerOptions options)
            {
                writer.WriteStartArray();
                foreach (var item in value)
                {
                    // Every member of the actual item, as for other values; object and IState have their own converters
                    var declared = typeof(TElement) == typeof(object) || typeof(TElement) == typeof(IState) || item == null;
                    JsonSerializer.Serialize(writer, item, declared ? typeof(TElement) : item!.GetType(), options);
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
    /// nested in arrays, collections and dictionaries.
    /// </summary>
    internal static class LegacyCollectionJson
    {
        private const string ValuesProperty = "$values";

        private static readonly ConcurrentDictionary<Type, Shape> Shapes = new ConcurrentDictionary<Type, Shape>();

        /// <summary>
        /// Tells whether JSON for <paramref name="type"/> may hold such a wrapped collection or dictionary somewhere
        /// this class rewrites: the value itself, or the items of an array, collection or dictionary.
        /// </summary>
        public static bool MayHoldWrapped(Type type)
        {
            var shape = GetShape(type);
            return shape.Kind == ShapeKind.Collection || shape.Kind == ShapeKind.Dictionary ||
                   (shape.Kind == ShapeKind.Array && shape.Item.Kind != ShapeKind.None);
        }

        /// <summary>
        /// Tells, from the first token of a value and without reading it, whether it may need to be rewritten: an
        /// object for a collection, a dictionary with a leading <c>"$type"</c>, or items that may be wrapped themselves.
        /// </summary>
        public static bool MayBeWrapped(ref Utf8JsonReader reader, Type type)
        {
            var shape = GetShape(type);
            var collection = shape.Kind == ShapeKind.Collection || shape.Kind == ShapeKind.Array;

            switch (reader.TokenType)
            {
                case JsonTokenType.StartArray:
                    return collection && shape.Item.Kind != ShapeKind.None;
                case JsonTokenType.StartObject when collection:
                    return true;
                case JsonTokenType.StartObject when shape.Kind == ShapeKind.Dictionary:
                    if (shape.Item.Kind != ShapeKind.None) return true;
                    var copy = reader;
                    return copy.Read() && copy.TokenType == JsonTokenType.PropertyName && copy.ValueTextEquals(TypeNamedJson.TypeProperty) &&
                           copy.Read() && copy.TokenType == JsonTokenType.String && IsLegacyTypeName(copy.GetString(), shape.Type);
                default:
                    return false;
            }
        }

        /// <summary>Tells whether a value with this shape needs to be rewritten to be read.</summary>
        public static bool IsWrapped(JsonElement element, Type type)
        {
            return IsWrapped(element, GetShape(type));
        }

        /// <summary>Returns the JSON without the type names the previous serializer added to collections and dictionaries.</summary>
        public static byte[] Unwrap(JsonElement element, Type type)
        {
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                Write(element, GetShape(type), writer);
            }

            return stream.ToArray();
        }

        /// <summary>Reads <paramref name="json"/> as <paramref name="type"/>, rewriting it first when it holds wrapped collections.</summary>
        public static object Deserialize(string json, Type type, JsonSerializerOptions options)
        {
            var documentOptions = new JsonDocumentOptions
            {
                AllowTrailingCommas = options.AllowTrailingCommas,
                CommentHandling = options.ReadCommentHandling,
                MaxDepth = options.MaxDepth
            };

            using var document = JsonDocument.Parse(json, documentOptions);
            var root = document.RootElement;

            return IsWrapped(root, type)
                ? JsonSerializer.Deserialize(Unwrap(root, type), type, options)
                : root.Deserialize(type, options);
        }

        private static bool IsWrapped(JsonElement element, Shape shape)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object when shape.Kind == ShapeKind.Collection || shape.Kind == ShapeKind.Array:
                    return true;
                case JsonValueKind.Object when shape.Kind == ShapeKind.Dictionary:
                    if (StartsWithTypeName(element, shape.Type)) return true;
                    if (shape.Item.Kind == ShapeKind.None) return false;
                    foreach (var property in element.EnumerateObject())
                    {
                        if (IsWrapped(property.Value, shape.Item)) return true;
                    }
                    return false;
                case JsonValueKind.Array when (shape.Kind == ShapeKind.Collection || shape.Kind == ShapeKind.Array) && shape.Item.Kind != ShapeKind.None:
                    foreach (var item in element.EnumerateArray())
                    {
                        if (IsWrapped(item, shape.Item)) return true;
                    }
                    return false;
                default:
                    return false;
            }
        }

        private static void Write(JsonElement element, Shape shape, Utf8JsonWriter writer)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object when shape.Kind == ShapeKind.Collection || shape.Kind == ShapeKind.Array:
                    // A collection that isn't wrapped is left to fail as it would have
                    if (element.TryGetProperty(ValuesProperty, out var values)) Write(values, shape, writer);
                    else element.WriteTo(writer);
                    return;
                case JsonValueKind.Object when shape.Kind == ShapeKind.Dictionary:
                    writer.WriteStartObject();
                    var skipFirst = StartsWithTypeName(element, shape.Type);
                    foreach (var property in element.EnumerateObject())
                    {
                        if (skipFirst)
                        {
                            skipFirst = false;
                            continue;
                        }

                        writer.WritePropertyName(property.Name);
                        Write(property.Value, shape.Item, writer);
                    }
                    writer.WriteEndObject();
                    return;
                case JsonValueKind.Array when (shape.Kind == ShapeKind.Collection || shape.Kind == ShapeKind.Array) && shape.Item.Kind != ShapeKind.None:
                    writer.WriteStartArray();
                    foreach (var item in element.EnumerateArray())
                    {
                        Write(item, shape.Item, writer);
                    }
                    writer.WriteEndArray();
                    return;
                default:
                    element.WriteTo(writer);
                    return;
            }
        }

        // A dictionary whose first entry is a "$type" naming its type: what the previous serializer wrote
        private static bool StartsWithTypeName(JsonElement element, Type declaredType)
        {
            using var properties = element.EnumerateObject();
            return properties.MoveNext() &&
                   properties.Current.NameEquals(TypeNamedJson.TypeProperty) &&
                   properties.Current.Value.ValueKind == JsonValueKind.String &&
                   IsLegacyTypeName(properties.Current.Value.GetString(), declaredType);
        }

        /// <summary>
        /// Tells whether the value of a dictionary's leading <c>"$type"</c> entry is the type name the previous
        /// serializer wrote there, rather than an ordinary key: a name the configured type resolver resolves to a
        /// dictionary type assignable to the declared type, or of the same generic type definition.
        /// </summary>
        private static bool IsLegacyTypeName(string value, Type declaredType)
        {
            // A type name always names an assembly or a namespace
            if (String.IsNullOrWhiteSpace(value) || declaredType == null || value.IndexOfAny(TypeNameSeparators) < 0) return false;

            Type type;
            try
            {
                type = TypeHelper.CurrentTypeResolver(value);
            }
            catch (Exception ex) when (ex.IsCatchableExceptionType())
            {
                return false;
            }

            if (type == null || !IsDictionaryType(type)) return false;
            if (declaredType.IsAssignableFrom(type)) return true;

            return type.IsGenericType && declaredType.IsGenericType &&
                   type.GetGenericTypeDefinition() == declaredType.GetGenericTypeDefinition();
        }

        private static readonly char[] TypeNameSeparators = { '.', ',' };

        private static bool IsDictionaryType(Type type)
        {
            if (typeof(IDictionary).IsAssignableFrom(type)) return true;

            foreach (var candidate in type.IsInterface ? type.GetInterfaces().Concat(new[] { type }) : type.GetInterfaces())
            {
                if (!candidate.IsGenericType) continue;

                var definition = candidate.GetGenericTypeDefinition();
                if (definition == typeof(IDictionary<,>) || definition == typeof(IReadOnlyDictionary<,>)) return true;
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
            if (depth > 8) return Shape.None;

            if (type.IsArray)
            {
                return type.GetArrayRank() == 1 && type != typeof(byte[])
                    ? new Shape(ShapeKind.Array, CreateShape(type.GetElementType()!, depth + 1))
                    : Shape.None;
            }

            if (type == typeof(string) || type.IsPrimitive || !typeof(IEnumerable).IsAssignableFrom(type)) return Shape.None;

            // JsonArray, JsonObject and other types System.Text.Json reads its own way, and types with a converter of their own
            if (type.Namespace != null && type.Namespace.StartsWith("System.Text.Json", StringComparison.Ordinal)) return Shape.None;
            if (type.IsDefined(typeof(JsonConverterAttribute), inherit: false)) return Shape.None;

            var interfaces = type.IsInterface ? type.GetInterfaces().Concat(new[] { type }).ToArray() : type.GetInterfaces();
            Type itemType = null;

            foreach (var candidate in interfaces)
            {
                if (!candidate.IsGenericType) continue;

                var definition = candidate.GetGenericTypeDefinition();
                if (definition == typeof(IDictionary<,>) || definition == typeof(IReadOnlyDictionary<,>))
                {
                    return new Shape(ShapeKind.Dictionary, CreateShape(candidate.GetGenericArguments()[1], depth + 1), type);
                }

                if (definition == typeof(IEnumerable<>)) itemType = candidate.GetGenericArguments()[0];
            }

            if (typeof(IDictionary).IsAssignableFrom(type)) return new Shape(ShapeKind.Dictionary, Shape.None, type);

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
            public static readonly Shape None = new Shape(ShapeKind.None, null);

            public Shape(ShapeKind kind, Shape item, Type type = null)
            {
                Kind = kind;
                Item = item ?? this;
                Type = type;
            }

            public ShapeKind Kind { get; }
            public Shape Item { get; }
            public Type Type { get; }
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
            if (typeInfo.Kind != JsonTypeInfoKind.Object || typeInfo.Options.ReferenceHandler != null) return;

            foreach (var property in typeInfo.Properties)
            {
                var type = property.PropertyType;
                if (property.CustomConverter != null || property.NumberHandling != null || property.IsExtensionData) continue;
                if (!LegacyCollectionJson.MayHoldWrapped(type)) continue;

                var creationHandling = property.ObjectCreationHandling ??
                                       typeInfo.PreferredPropertyObjectCreationHandling ??
                                       typeInfo.Options.PreferredObjectCreationHandling;
                if (creationHandling == JsonObjectCreationHandling.Populate || HasConverterInOptions(typeInfo.Options, type)) continue;

                property.CustomConverter = (JsonConverter)Activator.CreateInstance(typeof(Converter<>).MakeGenericType(type));
            }
        }

        private static bool HasConverterInOptions(JsonSerializerOptions options, Type type)
        {
            foreach (var converter in options.Converters)
            {
                if (converter.CanConvert(type)) return true;
            }

            return false;
        }

        private sealed class Converter<T> : JsonConverter<T>
        {
            public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            {
                if (!LegacyCollectionJson.MayBeWrapped(ref reader, typeof(T)))
                {
                    return JsonSerializer.Deserialize<T>(ref reader, options);
                }

                using var document = JsonDocument.ParseValue(ref reader);
                var root = document.RootElement;

                return LegacyCollectionJson.IsWrapped(root, typeof(T))
                    ? JsonSerializer.Deserialize<T>(LegacyCollectionJson.Unwrap(root, typeof(T)), options)
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
        private static readonly ConditionalWeakTable<object, object[]> Pending = new ConditionalWeakTable<object, object[]>();
        private static readonly object Missing = new object();

        public static void Apply(JsonTypeInfo typeInfo)
        {
            var type = typeInfo.Type;
            if (typeInfo.Kind != JsonTypeInfoKind.Object || !type.IsValueType || typeInfo.CreateObject == null) return;

            var constructor = GetConstructor(type);
            if (constructor == null) return;

            var parameters = constructor.GetParameters();
            var properties = typeInfo.Properties;
            var parameterProperties = new int[parameters.Length];

            for (var i = 0; i < parameters.Length; i++)
            {
                var index = FindProperty(properties, parameters[i]);
                if (index < 0 || properties[index].Set != null) return;
                parameterProperties[i] = index;
            }

            var count = properties.Count;
            var setters = new Action<object, object>[count];
            for (var i = 0; i < count; i++)
            {
                var index = i;
                var property = properties[i];
                if (property.IsExtensionData) continue;

                var parameter = Array.IndexOf(parameterProperties, i) >= 0;

                // A collection member without a setter gets its items added to the collection the constructor
                // created, so it waits for the constructor like the members that can be set
                setters[i] = property.Set ?? (parameter ? null : JsonGetOnlyCollectionModifier.CreateSetter(typeInfo, property));
                if (setters[i] == null && !parameter) continue;

                property.Set = (instance, value) => Pending.GetValue(instance, _ => NewValues(count))[index] = value;
            }

            var previous = typeInfo.OnDeserialized;
            typeInfo.OnDeserialized = instance =>
            {
                Construct(instance, constructor, parameters, parameterProperties, setters);
                previous?.Invoke(instance);
            };
        }

        private static ConstructorInfo GetConstructor(Type type)
        {
            foreach (var candidate in type.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (candidate.IsDefined(typeof(JsonConstructorAttribute), inherit: false)) return null;
            }

            var constructors = type.GetConstructors(BindingFlags.Instance | BindingFlags.Public);
            return constructors.Length == 1 && constructors[0].GetParameters().Length > 0 ? constructors[0] : null;
        }

        private static int FindProperty(IList<JsonPropertyInfo> properties, ParameterInfo parameter)
        {
            var found = -1;
            for (var i = 0; i < properties.Count; i++)
            {
                var member = properties[i].AttributeProvider as MemberInfo;
                if (member == null || properties[i].PropertyType != parameter.ParameterType) continue;

                // An exact match wins over one that differs only by case, as it did with the previous serializer
                if (String.Equals(member.Name, parameter.Name, StringComparison.Ordinal)) return i;
                if (found < 0 && String.Equals(member.Name, parameter.Name, StringComparison.OrdinalIgnoreCase)) found = i;
            }

            return found;
        }

        private static object[] NewValues(int count)
        {
            var values = new object[count];
            for (var i = 0; i < count; i++) values[i] = Missing;
            return values;
        }

        private static void Construct(
            object instance,
            ConstructorInfo constructor,
            ParameterInfo[] parameters,
            int[] parameterProperties,
            Action<object, object>[] setters)
        {
            if (!Pending.TryGetValue(instance, out var values)) values = NewValues(setters.Length);
            Pending.Remove(instance);

            var arguments = new object[parameters.Length];
            for (var i = 0; i < parameters.Length; i++)
            {
                var value = values[parameterProperties[i]];
                arguments[i] = !ReferenceEquals(value, Missing) ? value : DefaultOf(parameters[i]);
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
            for (var i = 0; i < setters.Length; i++)
            {
                if (setters[i] != null && !ReferenceEquals(values[i], Missing)) setters[i](constructed, values[i]);
            }

            // System.Text.Json unboxes the instance it created, so the constructed value is copied into it
            foreach (var field in instance.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                field.SetValue(instance, field.GetValue(constructed));
            }
        }

        private static object DefaultOf(ParameterInfo parameter)
        {
            var type = parameter.ParameterType;
            if (parameter.HasDefaultValue && parameter.DefaultValue != null && parameter.DefaultValue != DBNull.Value)
            {
                // An enum's default value may come back as its underlying number
                var target = Nullable.GetUnderlyingType(type) ?? type;
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
        private static readonly MethodInfo AddItemsMethod = typeof(JsonGetOnlyCollectionModifier)
            .GetMethod(nameof(AddItems), BindingFlags.NonPublic | BindingFlags.Static);

        private static readonly MethodInfo SetItemsMethod = typeof(JsonGetOnlyCollectionModifier)
            .GetMethod(nameof(SetItems), BindingFlags.NonPublic | BindingFlags.Static);

        public static void Apply(JsonTypeInfo typeInfo)
        {
            if (typeInfo.Kind != JsonTypeInfoKind.Object) return;

            foreach (var property in typeInfo.Properties)
            {
                var set = CreateSetter(typeInfo, property);
                if (set != null) property.Set = set;
            }
        }

        /// <summary>
        /// Returns what adds the items read for a collection member without a setter to the collection the object
        /// holds, or <c>null</c> when this doesn't apply to the member.
        /// </summary>
        public static Action<object, object> CreateSetter(JsonTypeInfo typeInfo, JsonPropertyInfo property)
        {
            if (property.Get == null || property.Set != null || property.IsExtensionData) return null;

            var creationHandling = property.ObjectCreationHandling ??
                                   typeInfo.PreferredPropertyObjectCreationHandling ??
                                   typeInfo.Options.PreferredObjectCreationHandling;
            if (creationHandling == JsonObjectCreationHandling.Populate) return null;

            var add = GetAdder(property.PropertyType);
            if (add == null) return null;

            var get = property.Get;
            return (instance, items) =>
            {
                var existing = get(instance);
                if (existing != null && items != null && !ReferenceEquals(existing, items)) add(existing, items);
            };
        }

        /// <summary>
        /// Returns what adds the items of a deserialized collection to an existing one, or <c>null</c> when
        /// <paramref name="type"/> isn't a collection System.Text.Json creates on its own.
        /// </summary>
        private static Action<object, object> GetAdder(Type type)
        {
            if (type.IsArray || type == typeof(string) || !typeof(IEnumerable).IsAssignableFrom(type)) return null;
            if (!type.IsInterface && (type.IsAbstract || type.GetConstructor(Type.EmptyTypes) == null)) return null;
            if (type.Namespace != null && type.Namespace.StartsWith("System.Text.Json", StringComparison.Ordinal)) return null;
            if (type.IsDefined(typeof(JsonConverterAttribute), inherit: false)) return null;

            var interfaces = type.IsInterface ? type.GetInterfaces().Concat(new[] { type }).ToArray() : type.GetInterfaces();

            foreach (var candidate in interfaces)
            {
                if (!candidate.IsGenericType) continue;

                var definition = candidate.GetGenericTypeDefinition();
                if (definition == typeof(IDictionary<,>) || definition == typeof(IReadOnlyDictionary<,>))
                {
                    return (Action<object, object>)SetItemsMethod.MakeGenericMethod(candidate.GetGenericArguments())
                        .CreateDelegate(typeof(Action<object, object>));
                }
            }

            foreach (var candidate in interfaces)
            {
                if (candidate.IsGenericType && candidate.GetGenericTypeDefinition() == typeof(IEnumerable<>))
                {
                    return (Action<object, object>)AddItemsMethod.MakeGenericMethod(candidate.GetGenericArguments())
                        .CreateDelegate(typeof(Action<object, object>));
                }
            }

            if (typeof(IDictionary).IsAssignableFrom(type)) return SetEntries;
            if (typeof(IList).IsAssignableFrom(type) || type == typeof(IEnumerable) || type == typeof(ICollection)) return AddEntries;
            return null;
        }

        private static void AddItems<T>(object existing, object items)
        {
            if (existing is ICollection<T> collection && !collection.IsReadOnly && !(existing is Array))
            {
                foreach (var item in (IEnumerable<T>)items) collection.Add(item);
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
                foreach (var item in (IEnumerable<KeyValuePair<TKey, TValue>>)items) dictionary[item.Key] = item.Value;
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
                foreach (var item in (IEnumerable)items) list.Add(item);
            }
        }

        private static void SetEntries(object existing, object items)
        {
            if (existing is IDictionary dictionary && !dictionary.IsReadOnly && !dictionary.IsFixedSize && items is IDictionary entries)
            {
                foreach (DictionaryEntry entry in entries) dictionary[entry.Key] = entry.Value;
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
            if (typeInfo.Kind != JsonTypeInfoKind.Object) return;

            List<KeyValuePair<JsonPropertyInfo, object>> populated = null;
            HashSet<string> constructorParameters = null;

            foreach (var property in typeInfo.Properties)
            {
                if (!TryGetDefaultValue(property, out var expected)) continue;

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

                (populated ??= new List<KeyValuePair<JsonPropertyInfo, object>>()).Add(
                    new KeyValuePair<JsonPropertyInfo, object>(property, expected));
            }

            if (populated == null) return;

            // Runs once the object is created, before the members found in the JSON are set
            var previous = typeInfo.OnDeserializing;
            typeInfo.OnDeserializing = instance =>
            {
                foreach (var member in populated)
                {
                    member.Key.Set!(instance, member.Value);
                }

                previous?.Invoke(instance);
            };
        }

        private static bool TryGetDefaultValue(JsonPropertyInfo property, out object value)
        {
            value = null;

            var attributes = property.AttributeProvider?.GetCustomAttributes(typeof(DefaultValueAttribute), inherit: true);
            if (attributes is not { Length: > 0 } || attributes[0] is not DefaultValueAttribute defaultValue) return false;

            try
            {
                value = ConvertDefault(defaultValue.Value, property.PropertyType);
                return true;
            }
            catch (Exception ex) when (ex is InvalidCastException || ex is FormatException || ex is OverflowException || ex is ArgumentException)
            {
                // A default value that doesn't fit the member's type is ignored, as if it wasn't there
                return false;
            }
        }

        private static object ConvertDefault(object value, Type type)
        {
            if (value == null || type.IsInstanceOfType(value)) return value;

            var target = Nullable.GetUnderlyingType(type) ?? type;
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
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (typeInfo.CreateObject != null) return names;

            foreach (var constructor in typeInfo.Type.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (!constructor.IsPublic && !constructor.IsDefined(typeof(JsonConstructorAttribute), inherit: false)) continue;

                foreach (var parameter in constructor.GetParameters())
                {
                    if (parameter.Name != null) names.Add(parameter.Name);
                }
            }

            return names;
        }
    }

    /// <summary>Type names as stored in a <c>"$type"</c> property: without version, culture or public key.</summary>
    internal static class JsonTypeNames
    {
        private static readonly Regex AssemblyDetails = new Regex(
            @",\s*(Version|Culture|PublicKeyToken)=[^,\]]*", RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(5));

        public static string Simple(string typeName)
        {
            return typeName == null ? null : AssemblyDetails.Replace(typeName, String.Empty);
        }
    }

    /// <summary>Writes a culture as its name, for example <c>"en-US"</c>, and reads it back.</summary>
    internal sealed class CultureInfoJsonConverter : JsonConverter<CultureInfo>
    {
        public override CultureInfo Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Null) return null;
            return CultureInfo.GetCultureInfo(reader.GetString() ?? String.Empty);
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
        public override XElement Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Null) return null;
            if (reader.TokenType == JsonTokenType.String) return XElement.Parse(reader.GetString() ?? String.Empty);

            using var document = JsonDocument.ParseValue(ref reader);
            var root = document.RootElement;
            var elements = root.ValueKind == JsonValueKind.Object
                ? root.EnumerateObject().Where(static p => !p.Name.StartsWith("?", StringComparison.Ordinal)).ToList()
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
                    foreach (var item in value.EnumerateArray())
                    {
                        foreach (var element in ToElement(name, item)) yield return element;
                    }
                    break;
                case JsonValueKind.Object:
                    var result = new XElement(name);
                    foreach (var property in value.EnumerateObject())
                    {
                        if (property.Name.StartsWith("@", StringComparison.Ordinal))
                        {
                            result.SetAttributeValue(property.Name.Substring(1), ScalarText(property.Value));
                        }
                        else if (property.Name == "#text")
                        {
                            result.Add(new XText(ScalarText(property.Value) ?? String.Empty));
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

        private static string ScalarText(JsonElement value)
        {
            return value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Null => null,
                _ => value.GetRawText()
            };
        }
    }
}
