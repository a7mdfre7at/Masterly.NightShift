// This file is part of Hangfire. Copyright © 2019 Hangfire OÜ.
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
using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using System.Text.Encodings.Web;
using System.Text.Json.Serialization.Metadata;
using System.Text.Json.Serialization;
using System.Text.Json;
using System.Threading;
using Masterly.NightShift.Annotations;
using Masterly.NightShift.States;

namespace Masterly.NightShift.Common
{
    /// <summary>
    /// Says which serializer settings <see cref="SerializationHelper"/> uses: the isolated internal settings, or the
    /// settings set with <c>UseSerializerSettings</c>.
    /// </summary>
    public enum SerializationOption
    {
        /// <summary>
        /// For internal data using isolated settings that can't be changed from user code.
        /// </summary>
        Internal,

        /// <summary>
        /// For internal data using isolated settings that also store the type of a state (a <c>"$type"</c>
        /// property), so it can be read back as <see cref="IState"/>. Can't be changed from user code.
        /// </summary>
        TypedInternal,

        /// <summary>
        /// For user data like arguments and parameters, configurable via
        /// <see cref="GlobalConfigurationExtensions.UseSerializerSettings"/>.
        /// </summary>
        User
    }

    /// <summary>
    /// Provides methods to serialize/deserialize data with Masterly.NightShift default settings, using System.Text.Json.
    /// Internal data uses isolated options that user code can't change.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The options read JSON as leniently as the previous serializer did, so data stored before keeps working:
    /// property names match case-insensitively, numbers may be quoted, <c>"NaN"</c> and <c>"Infinity"</c> are read
    /// as numbers, enums may be names or numbers, public fields are included, and a value read into
    /// <see cref="object"/> becomes a primitive where possible. User data is read this way whatever options are
    /// configured, while it is written exactly as the configured options say.
    /// </para>
    /// <para>
    /// The built-in options write every member of user data, also one equal to its
    /// <see cref="System.ComponentModel.DefaultValueAttribute"/>, and set a member that is missing from the JSON to
    /// its <see cref="System.ComponentModel.DefaultValueAttribute"/> value when reading, so data the previous
    /// serializer wrote with such members left out reads back as it was written.
    /// </para>
    /// <para>
    /// A value declared as an interface or abstract class, which can't be read back from its members alone, is
    /// written with a leading <c>"$type"</c> property naming its actual type, and read back as that type.
    /// </para>
    /// <para>
    /// Whatever options are configured, reading also accepts what the previous serializer read back: collections
    /// wrapped as <c>{"$type": …, "$values": […]}</c> and dictionaries with a leading <c>"$type"</c> (read as the
    /// declared type), structs set only by their one public constructor, and collection members without a setter.
    /// A dictionary's leading <c>"$type"</c> is taken for a type name only when it names a dictionary type that fits
    /// the declared one; otherwise it is an ordinary key. With a <see cref="ReferenceHandler"/> in the options,
    /// collections aren't unwrapped: System.Text.Json reads its own <c>"$id"</c>, <c>"$ref"</c> and
    /// <c>"$values"</c>. None of this changes how data is written.
    /// </para>
    /// <para>
    /// Library data that the configured user options can't write (a resolver that doesn't know the type throws
    /// <see cref="NotSupportedException"/>) is written with the internal options instead.
    /// </para>
    /// </remarks>
    public static class SerializationHelper
    {
        private static readonly Lazy<JsonSerializerOptions> InternalSerializerOptions =
            new Lazy<JsonSerializerOptions>(CreateInternalOptions, LazyThreadSafetyMode.PublicationOnly);

        // The internal options with what reads data the previous serializer wrote; only for reading
        private static readonly Lazy<JsonSerializerOptions> InternalReadingOptions =
            new Lazy<JsonSerializerOptions>(static () => ForReading(InternalSerializerOptions.Value), LazyThreadSafetyMode.PublicationOnly);

        private static readonly Lazy<UserSerializerOptions> DefaultUserSerializerOptions =
            new Lazy<UserSerializerOptions>(static () => new UserSerializerOptions(CreateDefaultUserOptions()), LazyThreadSafetyMode.PublicationOnly);

        private static UserSerializerOptions _userSerializerOptions;

        // Quoted numbers, and NaN and infinities as "NaN", "Infinity" and "-Infinity", which the previous serializer wrote
        private const JsonNumberHandling LenientNumberHandling =
            JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.AllowNamedFloatingPointLiterals;

        /// <summary>
        /// Serializes data with <see cref="SerializationOption.Internal"/> option.
        /// Use this method to serialize internal data. Using isolated settings that can't be changed from user code.
        /// </summary>
        public static string Serialize<T>([CanBeNull] T value)
        {
            return Serialize(value, SerializationOption.Internal);
        }

        /// <summary>
        /// Serializes data with specified option.
        /// Use <see cref="SerializationOption.Internal"/> option to serialize internal data.
        /// Use <see cref="SerializationOption.TypedInternal"/> option if you need to store type information.
        /// Use <see cref="SerializationOption.User"/> option to serialize user data like arguments and parameters.
        /// </summary>
        public static string Serialize<T>([CanBeNull] T value, SerializationOption option)
        {
            return Serialize(value, typeof(T), option);
        }

        /// <summary>
        /// Serializes data with specified option.
        /// Use <see cref="SerializationOption.Internal"/> option to serialize internal data.
        /// Use <see cref="SerializationOption.TypedInternal"/> option if you need to store type information.
        /// Use <see cref="SerializationOption.User"/> option to serialize user data like arguments and parameters.
        /// </summary>
        public static string Serialize([CanBeNull] object value, [CanBeNull] Type type, SerializationOption option)
        {
            if (value == null) return null;

            var options = GlobalConfiguration.HasCompatibilityLevel(CompatibilityLevel.Version_170)
                ? GetSerializerOptions(option)
                // Previously almost all the data was serialized with the user settings, except
                // when the type information had to be persisted.
                : option == SerializationOption.TypedInternal ? InternalSerializerOptions.Value : GetUserSerializerOptions().Writing;

            if (ReferenceEquals(options, InternalSerializerOptions.Value) || !IsLibraryData(value, type, option))
            {
                return SerializeWith(value, type, option, options);
            }

            try
            {
                return SerializeWith(value, type, option, options);
            }
            catch (NotSupportedException ex)
            {
                // The application's options can't write it, typically a resolver that only knows the application's
                // own types (a source-generated context). Library data is then written with the internal options,
                // which Deserialize tries first, instead of failing every job and state change.
                try
                {
                    return SerializeWith(value, type, option, InternalSerializerOptions.Value);
                }
                catch (Exception internalEx) when (internalEx.IsCatchableExceptionType())
                {
                    ExceptionDispatchInfo.Capture(ex).Throw();
                    throw;
                }
            }
        }

        /// <summary>
        /// Tells whether a value is the library's own data written with the application's options: internal data
        /// below <see cref="CompatibilityLevel.Version_170"/>, or a type of Masterly.NightShift itself, such as the
        /// exception of a deleted job.
        /// </summary>
        private static bool IsLibraryData(object value, Type type, SerializationOption option)
        {
            return option != SerializationOption.User || (type ?? value.GetType()).Assembly == typeof(SerializationHelper).Assembly;
        }

        /// <summary>
        /// Serializes library data with the internal options at every compatibility level, for data the application's
        /// options must not change or break (a naming policy that renames members, a resolver that doesn't know the
        /// type). Use it only for data that older servers read the same way whatever options wrote it, or that only
        /// Masterly.NightShift reads. The value is written as its actual type, without a <c>"$type"</c>.
        /// <see cref="Deserialize(string, Type)"/> reads it back, and still reads data written with the user options
        /// before.
        /// </summary>
        internal static string SerializeInternal([CanBeNull] object value)
        {
            if (value == null) return null;
            return SerializeWith(value, value.GetType(), SerializationOption.Internal, InternalSerializerOptions.Value);
        }

        private static string SerializeWith(object value, Type type, SerializationOption option, JsonSerializerOptions options)
        {
            var abstractTypeInfo = GetAbstractTypeInfo(type, options);
            if (NeedsTypeName(abstractTypeInfo))
            {
                return JsonSerializer.Serialize(new TypeNamedValue(value), options);
            }

            return JsonSerializer.Serialize(value, GetSerializedType(value, type, option, abstractTypeInfo), options);
        }

        /// <summary>
        /// Deserializes data with <see cref="SerializationOption.Internal"/> option.
        /// Use this method to deserialize internal data. Using isolated settings that can't be changed from user code.
        /// </summary>
        public static object Deserialize([CanBeNull] string value, [NotNull] Type type)
        {
            return Deserialize(value, type, SerializationOption.Internal);
        }

        /// <summary>
        /// Deserializes data with specified option.
        /// Use <see cref="SerializationOption.Internal"/> to deserialize internal data.
        /// Use <see cref="SerializationOption.TypedInternal"/> if deserializable internal data has type names information.
        /// Use <see cref="SerializationOption.User"/> to deserialize user data like arguments and parameters.
        /// </summary>
        public static object Deserialize([CanBeNull] string value, [NotNull] Type type, SerializationOption option)
        {
            if (type == null) throw new ArgumentNullException(nameof(type));

            // An empty value means no value, as it did before
            if (String.IsNullOrWhiteSpace(value)) return null;

            Exception exception = null;

            if (option != SerializationOption.User)
            {
                try
                {
                    return DeserializeWith(value, type, InternalReadingOptions.Value);
                }
                catch (Exception ex) when (ex.IsCatchableExceptionType())
                {
                    // Older data was written with the user settings, so try those before giving up. If they
                    // can't read the value either, the original exception is the one worth seeing.
                    exception = ex;
                }
            }

            try
            {
                return GetUserSerializerOptions().Deserialize(value, type);
            }
            catch (Exception ex) when (exception != null && ex.IsCatchableExceptionType())
            {
                ExceptionDispatchInfo.Capture(exception).Throw();
                throw;
            }
        }

        /// <summary>
        /// Deserializes data with <see cref="SerializationOption.Internal"/> option.
        /// Use this method to deserialize internal data. Using isolated settings that can't be changed from user code.
        /// </summary>
        public static T Deserialize<T>([CanBeNull] string value)
        {
            if (value == null) return default(T);
            return Deserialize<T>(value, SerializationOption.Internal);
        }

        /// <summary>
        /// Deserializes data with specified option.
        /// Use <see cref="SerializationOption.Internal"/> to deserialize internal data.
        /// Use <see cref="SerializationOption.TypedInternal"/> if deserializable internal data has type names information.
        /// Use <see cref="SerializationOption.User"/> to deserialize user data like arguments and parameters.
        /// </summary>
        public static T Deserialize<T>([CanBeNull] string value, SerializationOption option)
        {
            if (value == null) return default(T);
            var result = Deserialize(value, typeof(T), option);
            return result == null ? default(T) : (T)result;
        }

        /// <summary>
        /// Reads text an operator typed, in the dashboard's job forms, as a value of <paramref name="type"/>. It is
        /// read like user data, except that a value declared as <see cref="object"/> (the argument itself or a member
        /// of it) is read as plain JSON: a <c>"$type"</c> in it is refused instead of resolved, so the text can't make
        /// Masterly.NightShift create a type it names. Interface and abstract types still take a <c>"$type"</c>, which
        /// must name a type assignable to them. A <see cref="Type"/> value (the argument itself or a member of it) is
        /// refused, because resolving it would load any type the text names. Stored data is read by
        /// <see cref="Deserialize(string, Type, SerializationOption)"/> as before.
        /// </summary>
        internal static object DeserializeInput([CanBeNull] string value, [NotNull] Type type)
        {
            if (type == null) throw new ArgumentNullException(nameof(type));
            if (String.IsNullOrWhiteSpace(value)) return null;

            return GetUserSerializerOptions().DeserializeInput(value, type);
        }

        /// <summary>
        /// Writes a value of <paramref name="type"/> as text <see cref="DeserializeInput"/> reads back, to pre-fill the
        /// dashboard's job forms: like user data, but without type names in values declared as <see cref="object"/>.
        /// </summary>
        internal static string SerializeInput([CanBeNull] object value, [NotNull] Type type)
        {
            if (type == null) throw new ArgumentNullException(nameof(type));
            if (value == null) return null;

            return SerializeWith(value, type, SerializationOption.User, GetUserSerializerOptions().InputWriting);
        }

        /// <summary>
        /// Returns new, modifiable options with the settings of the internal ones but without their converters, the
        /// starting point of <see cref="GlobalConfigurationExtensions.UseRecommendedSerializerSettings(IGlobalConfiguration, Action{JsonSerializerOptions})"/>.
        /// </summary>
        internal static JsonSerializerOptions GetInternalSettings()
        {
            var options = CreateBaseOptions();
            options.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
            return options;
        }

        internal static void SetUserSerializerSettings([CanBeNull] JsonSerializerOptions options)
        {
            Volatile.Write(
                ref _userSerializerOptions,
                options == null ? null : new UserSerializerOptions(WithCompatibilityConverters(options, typeNames: false)));
        }

        /// <summary>
        /// Uses a copy of the internal options for user data, changed by <paramref name="configure"/>. The converters
        /// it adds come before the built-in ones, so they win for the same type.
        /// </summary>
        internal static void SetRecommendedSerializerSettings([CanBeNull] Action<JsonSerializerOptions> configure)
        {
            var options = GetInternalSettings();
            configure?.Invoke(options);

            Volatile.Write(ref _userSerializerOptions, new UserSerializerOptions(WithCompatibilityConverters(options, typeNames: true)));
        }

        /// <summary>
        /// Copies user options and adds the converters that keep stored data readable (types, states, cultures, XML,
        /// arrays with type names, primitives in object values). The user's own converters come first, so they win.
        /// With a <see cref="ReferenceHandler"/>, arrays are left to System.Text.Json: the array converter writes and
        /// reads the items through nested calls, which would each start a new reference scope.
        /// </summary>
        private static JsonSerializerOptions WithCompatibilityConverters(JsonSerializerOptions options, bool typeNames)
        {
            var copy = new JsonSerializerOptions(options);

            foreach (var converter in CreateCompatibilityConverters(typeNames))
            {
                if (options.ReferenceHandler != null && converter is LegacyTypedArrayConverterFactory) continue;

                var exists = false;
                foreach (var existing in copy.Converters)
                {
                    if (existing.GetType() == converter.GetType()) { exists = true; break; }
                }

                if (!exists) copy.Converters.Add(converter);
            }

            return copy;
        }

        /// <summary>
        /// Returns options that read what other options wrote before: property names in any case, quoted numbers,
        /// <c>"NaN"</c> and <c>"Infinity"</c>, public fields, trailing commas and comments. Only reading differs, so the
        /// result is used only to read.
        /// </summary>
        private static JsonSerializerOptions WithLenientReading(JsonSerializerOptions options)
        {
            var numberHandling = options.NumberHandling | LenientNumberHandling;

            if (options.PropertyNameCaseInsensitive && options.NumberHandling == numberHandling && options.IncludeFields &&
                options.AllowTrailingCommas && options.ReadCommentHandling == JsonCommentHandling.Skip)
            {
                return options;
            }

            return new JsonSerializerOptions(options)
            {
                PropertyNameCaseInsensitive = true,
                NumberHandling = numberHandling,
                IncludeFields = true,
                AllowTrailingCommas = true,
                ReadCommentHandling = JsonCommentHandling.Skip
            };
        }

        /// <summary>
        /// Returns a read-only copy of <paramref name="options"/> that also reads what the previous serializer could
        /// read, and System.Text.Json alone can't: structs set only by their constructor, collection members without a
        /// setter, and collections stored with their type name. The copy is used only to read, so data is written
        /// exactly as the options say (with one reference scope under a <see cref="ReferenceHandler"/>, for example).
        /// </summary>
        private static JsonSerializerOptions ForReading(JsonSerializerOptions options)
        {
            var copy = new JsonSerializerOptions(options)
            {
                TypeInfoResolver = (options.TypeInfoResolver ?? new DefaultJsonTypeInfoResolver())
                    .WithAddedModifier(JsonConstructorOnlyStructModifier.Apply)
                    .WithAddedModifier(JsonGetOnlyCollectionModifier.Apply)
                    .WithAddedModifier(JsonLegacyCollectionModifier.Apply)
            };

            copy.MakeReadOnly(populateMissingResolver: true);
            return copy;
        }

        private static JsonConverter[] CreateCompatibilityConverters(bool typeNames)
        {
            return new JsonConverter[]
            {
                new LenientEnumConverterFactory(),
                new TypeNameJsonConverter(),
                new InferredObjectJsonConverter(typeNames),
                new StateJsonConverter(),
                new LegacyTypedArrayConverterFactory(),
                new CultureInfoJsonConverter(),
                new XElementJsonConverter(),
            };
        }

        private static Type GetSerializedType(object value, Type type, SerializationOption option, JsonTypeInfo abstractTypeInfo)
        {
            // A state is written with its "$type", so it can be read back as IState
            if (type == typeof(IState) || (option == SerializationOption.TypedInternal && value is IState))
            {
                return typeof(IState);
            }

            // Declared as object: the object converter decides, writing "$type" when the options keep type names
            if (type == typeof(object)) return type;

            // Polymorphism configured for the declared type ([JsonPolymorphic], [JsonDerivedType]): System.Text.Json
            // writes its own type discriminator, but only when the value is written as the declared type
            if (abstractTypeInfo?.PolymorphismOptions != null) return type;

            // Like the previous serializer, write every member of the actual object, not only the declared ones
            return value.GetType();
        }

        private static object DeserializeWith(string value, Type type, JsonSerializerOptions options)
        {
            if (NeedsTypeName(GetAbstractTypeInfo(type, options))) return DeserializeTypeNamed(value, type, options);

            // A collection the previous serializer stored with its type name, in the value or in its items. With a
            // reference handler, "$id", "$ref" and "$values" are System.Text.Json's own, and it reads them itself
            if (options.ReferenceHandler == null &&
                value.IndexOf("\"$", StringComparison.Ordinal) >= 0 &&
                LegacyCollectionJson.MayHoldWrapped(type))
            {
                return LegacyCollectionJson.Deserialize(value, type, options);
            }

            return JsonSerializer.Deserialize(value, type, options);
        }

        private static object DeserializeTypeNamed(string value, Type type, JsonSerializerOptions options)
        {
            var documentOptions = new JsonDocumentOptions
            {
                AllowTrailingCommas = options.AllowTrailingCommas,
                CommentHandling = options.ReadCommentHandling,
                MaxDepth = options.MaxDepth
            };

            using var document = JsonDocument.Parse(value, documentOptions);
            var root = document.RootElement;

            if (root.ValueKind == JsonValueKind.Null) return null;
            if (!TypeNamedJson.TryGetTypeName(root, out var typeName))
            {
                throw new JsonException(
                    $"A value of the interface or abstract type '{type}' must be a JSON object with a \"{TypeNamedJson.TypeProperty}\" property naming its actual type.");
            }

            return TypeNamedJson.Read(root, TypeNamedJson.ResolveType(typeName, type), options);
        }

        /// <summary>
        /// Returns the contract of a declared interface or abstract class, or <c>null</c> for any other type.
        /// </summary>
        private static JsonTypeInfo GetAbstractTypeInfo(Type type, JsonSerializerOptions options)
        {
            if (type == null || !(type.IsInterface || type.IsAbstract)) return null;

            try
            {
                return options.GetTypeInfo(type);
            }
            catch (NotSupportedException)
            {
                // The configured resolver doesn't know the type: leave it to System.Text.Json, as before
                return null;
            }
        }

        /// <summary>
        /// Tells whether a value of the declared type can only be read back with its actual type stored next to it:
        /// an interface or abstract class read as an object (not a collection), that no converter handles, that the
        /// options can't create, and that has no polymorphism configured.
        /// </summary>
        private static bool NeedsTypeName(JsonTypeInfo abstractTypeInfo)
        {
            return abstractTypeInfo != null &&
                   abstractTypeInfo.Kind == JsonTypeInfoKind.Object &&
                   abstractTypeInfo.CreateObject == null &&
                   abstractTypeInfo.PolymorphismOptions == null;
        }

        private static JsonSerializerOptions CreateInternalOptions()
        {
            // Values declared as object keep their type (what TypeNameHandling.Auto did before)
            var options = GetInternalSettings();
            AddCompatibilityConverters(options, typeNames: true);
            options.MakeReadOnly();
            return options;
        }

        private static JsonSerializerOptions CreateDefaultUserOptions()
        {
            var options = CreateBaseOptions();
            AddCompatibilityConverters(options, typeNames: false);
            return options;
        }

        private static JsonSerializerOptions CreateBaseOptions()
        {
            var resolver = new DefaultJsonTypeInfoResolver();
            resolver.Modifiers.Add(JsonDefaultValueModifier.Apply);

            return new JsonSerializerOptions
            {
                // Non-ASCII text stays readable in the storage, as before
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                PropertyNameCaseInsensitive = true,
                // Also writes NaN and infinities, as text, instead of throwing
                NumberHandling = LenientNumberHandling,
                IncludeFields = true,
                AllowTrailingCommas = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
                MaxDepth = 128,
                TypeInfoResolver = resolver
            };
        }

        private static void AddCompatibilityConverters(JsonSerializerOptions options, bool typeNames)
        {
            foreach (var converter in CreateCompatibilityConverters(typeNames))
            {
                options.Converters.Add(converter);
            }
        }

        private static JsonSerializerOptions GetSerializerOptions(SerializationOption serializationOption)
        {
            switch (serializationOption)
            {
                case SerializationOption.Internal:
                case SerializationOption.TypedInternal: return InternalSerializerOptions.Value;
                case SerializationOption.User: return GetUserSerializerOptions().Writing;
                default: throw new ArgumentOutOfRangeException(nameof(serializationOption), serializationOption, null);
            }
        }

        private static UserSerializerOptions GetUserSerializerOptions()
        {
            return Volatile.Read(ref _userSerializerOptions) ?? DefaultUserSerializerOptions.Value;
        }

        /// <summary>
        /// The options user data is written with, and the lenient copy of them it is read with, so data written
        /// before with other options (another naming policy, quoted numbers, public fields) stays readable.
        /// </summary>
        private sealed class UserSerializerOptions
        {
            public UserSerializerOptions(JsonSerializerOptions writing)
            {
                // Read-only options cache the contracts GetTypeInfo returns
                writing.MakeReadOnly(populateMissingResolver: true);

                Writing = writing;
                StrictReading = ForReading(writing);
                Reading = WithLenientReading(StrictReading);
                Reading.MakeReadOnly(populateMissingResolver: true);
            }

            public JsonSerializerOptions Writing { get; }

            // The writing options plus what reads the previous serializer's data, for types the lenient copy can't
            // describe
            public JsonSerializerOptions StrictReading { get; }
            public JsonSerializerOptions Reading { get; }

            // The same options for text an operator types: values declared as object never resolve a "$type"
            private JsonSerializerOptions _inputWriting;
            private JsonSerializerOptions _inputStrictReading;
            private JsonSerializerOptions _inputReading;

            public JsonSerializerOptions InputWriting => LazyInitializer.EnsureInitialized(ref _inputWriting, () => ForInput(Writing));
            public JsonSerializerOptions InputStrictReading => LazyInitializer.EnsureInitialized(ref _inputStrictReading, () => ForInput(StrictReading));
            public JsonSerializerOptions InputReading => LazyInitializer.EnsureInitialized(ref _inputReading, () => ForInput(Reading));

            // Types the lenient copy can't describe, for example members whose names differ only by case, which
            // collide once names are case-insensitive. They are read with the options they were written with.
            private readonly ConcurrentDictionary<Type, bool> _strictTypes = new ConcurrentDictionary<Type, bool>();

            public object Deserialize(string value, Type type)
            {
                if (ReferenceEquals(Reading, StrictReading) || _strictTypes.ContainsKey(type))
                {
                    return DeserializeWith(value, type, StrictReading);
                }

                try
                {
                    return DeserializeWith(value, type, Reading);
                }
                catch (InvalidOperationException)
                {
                    // System.Text.Json reports a contract it can't build with InvalidOperationException, and bad
                    // data with JsonException, so this is about the type, not the value
                    _strictTypes.TryAdd(type, true);
                    return DeserializeWith(value, type, StrictReading);
                }
            }

            public object DeserializeInput(string value, Type type)
            {
                if (ReferenceEquals(Reading, StrictReading) || _strictTypes.ContainsKey(type))
                {
                    return DeserializeWith(value, type, InputStrictReading);
                }

                try
                {
                    return DeserializeWith(value, type, InputReading);
                }
                catch (InvalidOperationException)
                {
                    _strictTypes.TryAdd(type, true);
                    return DeserializeWith(value, type, InputStrictReading);
                }
            }

            private static JsonSerializerOptions ForInput(JsonSerializerOptions options)
            {
                var copy = new JsonSerializerOptions(options);
                var converter = new InferredObjectJsonConverter(typeNames: false, rejectTypeNames: true);
                var replaced = false;

                for (var i = 0; i < copy.Converters.Count; i++)
                {
                    if (copy.Converters[i] is InferredObjectJsonConverter)
                    {
                        copy.Converters[i] = converter;
                        replaced = true;
                    }
                }

                if (!replaced) copy.Converters.Add(converter);

                // First, so that it wins over any converter for Type, the user's own included
                copy.Converters.Insert(0, new RefusedTypeInputJsonConverter());

                copy.MakeReadOnly(populateMissingResolver: true);
                return copy;
            }
        }
    }
}
