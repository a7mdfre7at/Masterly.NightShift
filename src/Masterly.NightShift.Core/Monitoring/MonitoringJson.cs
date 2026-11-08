#nullable enable

using System;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Masterly.NightShift.Common;

namespace Masterly.NightShift
{
    /// <summary>
    /// The JSON of the entries the monitoring features keep in storage: <see cref="ActivityEntry"/>,
    /// <see cref="AuditEntry"/> and the <see cref="JobNote"/>s of a job. They are written with options of their own, never with the serializer settings an
    /// application configures for job arguments: a source-generated resolver set with <c>UseSerializerSettings</c>
    /// doesn't know these types and would fail every state change. Older entries, written with the application's
    /// settings, are still read.
    /// </summary>
    internal static class MonitoringJson
    {
        private static readonly JsonSerializerOptions Options = new JsonSerializerOptions
        {
            // Non-ASCII text stays readable in the storage, like the rest of the internal data
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            PropertyNameCaseInsensitive = true,
            NumberHandling = JsonNumberHandling.AllowReadingFromString,
            TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
        };

        public static string Serialize<T>(T value)
        {
            return JsonSerializer.Serialize(value, Options);
        }

        /// <summary>Reads an entry, trying the application's serializer settings when these options can't read it.</summary>
        public static T? Deserialize<T>(string value)
            where T : class
        {
            try
            {
                return JsonSerializer.Deserialize<T>(value, Options);
            }
            catch (Exception ex) when (ex is JsonException || ex is NotSupportedException)
            {
                // Entries written before were serialized with the application's settings
                return SerializationHelper.Deserialize<T>(value, SerializationOption.User);
            }
        }
    }
}
