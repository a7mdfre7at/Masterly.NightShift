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
    /// The options read JSON as leniently as the previous serializer did, so data stored before keeps working:
    /// property names match case-insensitively, numbers may be quoted, enums may be names or numbers, public fields
    /// are included, and a value read into <see cref="object"/> becomes a primitive where possible.
    /// </remarks>
    public static class SerializationHelper
    {
        private static readonly Lazy<JsonSerializerOptions> InternalSerializerOptions =
            new Lazy<JsonSerializerOptions>(CreateInternalOptions, LazyThreadSafetyMode.PublicationOnly);

        private static readonly Lazy<JsonSerializerOptions> DefaultUserSerializerOptions =
            new Lazy<JsonSerializerOptions>(CreateDefaultUserOptions, LazyThreadSafetyMode.PublicationOnly);

        private static JsonSerializerOptions _userSerializerOptions;

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
                : option == SerializationOption.TypedInternal ? InternalSerializerOptions.Value : GetUserSerializerOptions();

            return JsonSerializer.Serialize(value, GetSerializedType(value, type, option), options);
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
                    return JsonSerializer.Deserialize(value, type, GetSerializerOptions(option));
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
                return JsonSerializer.Deserialize(value, type, GetSerializerOptions(SerializationOption.User));
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
        /// Returns a new, modifiable copy of the options used for internal data, the starting point of
        /// <see cref="GlobalConfigurationExtensions.UseRecommendedSerializerSettings(IGlobalConfiguration, Action{JsonSerializerOptions})"/>.
        /// </summary>
        internal static JsonSerializerOptions GetInternalSettings()
        {
            return new JsonSerializerOptions(InternalSerializerOptions.Value);
        }

        internal static void SetUserSerializerSettings([CanBeNull] JsonSerializerOptions options)
        {
            Volatile.Write(ref _userSerializerOptions, options == null ? null : WithCompatibilityConverters(options));
        }

        /// <summary>
        /// Copies user options and adds the converters that keep stored data readable (types, states, cultures, XML,
        /// arrays with type names, primitives in object values). The user's own converters come first, so they win.
        /// </summary>
        private static JsonSerializerOptions WithCompatibilityConverters(JsonSerializerOptions options)
        {
            var copy = new JsonSerializerOptions(options);

            foreach (var converter in CreateCompatibilityConverters(typeNames: false))
            {
                var exists = false;
                foreach (var existing in copy.Converters)
                {
                    if (existing.GetType() == converter.GetType()) { exists = true; break; }
                }

                if (!exists) copy.Converters.Add(converter);
            }

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

        private static Type GetSerializedType(object value, Type type, SerializationOption option)
        {
            // A state is written with its "$type", so it can be read back as IState
            if (type == typeof(IState) || (option == SerializationOption.TypedInternal && value is IState))
            {
                return typeof(IState);
            }

            // Declared as object: the object converter decides, writing "$type" when the options keep type names
            if (type == typeof(object)) return type;

            // Like the previous serializer, write every member of the actual object, not only the declared ones
            return value.GetType();
        }

        private static JsonSerializerOptions CreateInternalOptions()
        {
            // Values declared as object keep their type (what TypeNameHandling.Auto did before)
            var options = CreateBaseOptions(typeNames: true);
            options.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
            return options;
        }

        private static JsonSerializerOptions CreateDefaultUserOptions()
        {
            return CreateBaseOptions(typeNames: false);
        }

        private static JsonSerializerOptions CreateBaseOptions(bool typeNames)
        {
            var resolver = new DefaultJsonTypeInfoResolver();
            resolver.Modifiers.Add(JsonDefaultValueModifier.Apply);

            var options = new JsonSerializerOptions
            {
                // Non-ASCII text stays readable in the storage, as before
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                PropertyNameCaseInsensitive = true,
                NumberHandling = JsonNumberHandling.AllowReadingFromString,
                IncludeFields = true,
                AllowTrailingCommas = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
                MaxDepth = 128,
                TypeInfoResolver = resolver
            };

            foreach (var converter in CreateCompatibilityConverters(typeNames))
            {
                options.Converters.Add(converter);
            }

            return options;
        }

        private static JsonSerializerOptions GetSerializerOptions(SerializationOption serializationOption)
        {
            switch (serializationOption)
            {
                case SerializationOption.Internal:
                case SerializationOption.TypedInternal: return InternalSerializerOptions.Value;
                case SerializationOption.User: return GetUserSerializerOptions();
                default: throw new ArgumentOutOfRangeException(nameof(serializationOption), serializationOption, null);
            }
        }

        private static JsonSerializerOptions GetUserSerializerOptions()
        {
            return Volatile.Read(ref _userSerializerOptions) ?? DefaultUserSerializerOptions.Value;
        }
    }
}
