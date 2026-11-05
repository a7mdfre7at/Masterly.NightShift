using System;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
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
        private const string TypeProperty = "$type";

        public override IState Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            using var document = JsonDocument.ParseValue(ref reader);
            var root = document.RootElement;

            if (root.ValueKind == JsonValueKind.Null) return null;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(TypeProperty, out var typeName) || typeName.ValueKind != JsonValueKind.String)
            {
                throw new JsonException($"A state must be a JSON object with a \"{TypeProperty}\" property.");
            }

            var type = TypeHelper.CurrentTypeResolver(typeName.GetString());
            if (!typeof(IState).IsAssignableFrom(type) || type.IsInterface || type.IsAbstract)
            {
                throw new JsonException($"The type '{typeName.GetString()}' isn't a concrete state type.");
            }

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

            return (IState)JsonSerializer.Deserialize(stream.ToArray(), type, options);
        }

        public override void Write(Utf8JsonWriter writer, IState value, JsonSerializerOptions options)
        {
            var type = value.GetType();
            var element = JsonSerializer.SerializeToElement(value, type, options);

            writer.WriteStartObject();
            writer.WriteString(TypeProperty, JsonTypeNames.Simple(TypeHelper.CurrentTypeSerializer(type)));
            foreach (var property in element.EnumerateObject())
            {
                property.WriteTo(writer);
            }
            writer.WriteEndObject();
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
    /// With <c>typeNames</c>, an object value is written with a leading <c>"$type"</c> property and read back as that
    /// type, the way arguments of type <see cref="object"/> were stored with the recommended settings before.
    /// </remarks>
    internal sealed class InferredObjectJsonConverter : JsonConverter<object>
    {
        private const string TypeProperty = "$type";
        private const string ValuesProperty = "$values";

        private readonly bool _typeNames;

        public InferredObjectJsonConverter(bool typeNames)
        {
            _typeNames = typeNames;
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

            if (_typeNames && root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty(TypeProperty, out var typeName) && typeName.ValueKind == JsonValueKind.String)
            {
                var type = TypeHelper.CurrentTypeResolver(typeName.GetString());
                var payload = root.TryGetProperty(ValuesProperty, out var values) ? values.GetRawText() : WithoutTypeProperty(root);
                return JsonSerializer.Deserialize(payload, type, options);
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

        private static string WithoutTypeProperty(JsonElement root)
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

            return System.Text.Encoding.UTF8.GetString(stream.ToArray());
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
    /// Leaves out a member whose value equals its <see cref="DefaultValueAttribute"/>, keeping stored data short.
    /// </summary>
    internal static class JsonDefaultValueModifier
    {
        public static void Apply(JsonTypeInfo typeInfo)
        {
            if (typeInfo.Kind != JsonTypeInfoKind.Object) return;

            foreach (var property in typeInfo.Properties)
            {
                var attribute = property.AttributeProvider?
                    .GetCustomAttributes(typeof(DefaultValueAttribute), inherit: true);

                if (attribute is not { Length: > 0 } || attribute[0] is not DefaultValueAttribute defaultValue) continue;

                var expected = ConvertDefault(defaultValue.Value, property.PropertyType);
                property.ShouldSerialize = (_, value) => !Equals(value, expected);
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
