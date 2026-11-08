using System;
using System.Text.Encodings.Web;
using System.Text.Json.Serialization;
using System.Text.Json;
using System.Threading.Tasks;

namespace Masterly.NightShift.Dashboard
{
    /// <summary>
    /// JSON responses of the dashboard's own endpoints: camelCase names, enums as strings, ISO dates.
    /// </summary>
    internal static class DashboardJson
    {
        /// <summary>camelCase names and dictionary keys, enums as camelCase strings, UTC dates, no nulls.</summary>
        public static readonly JsonSerializerOptions Options = Create(camelCaseDictionaryKeys: true, ignoreNulls: true, utcDates: true, indented: false);

        public static string Serialize(object value)
        {
            return JsonSerializer.Serialize(value, value?.GetType() ?? typeof(object), Options);
        }

        public static JsonSerializerOptions Create(bool camelCaseDictionaryKeys, bool ignoreNulls, bool utcDates, bool indented)
        {
            var options = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                DictionaryKeyPolicy = camelCaseDictionaryKeys ? JsonNamingPolicy.CamelCase : null,
                DefaultIgnoreCondition = ignoreNulls ? JsonIgnoreCondition.WhenWritingNull : JsonIgnoreCondition.Never,
                WriteIndented = indented,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            };

            options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
            if (utcDates) options.Converters.Add(new UtcDateTimeJsonConverter());

            return options;
        }

        public static Task WriteAsync(DashboardContext context, int statusCode, object value)
        {
            context.Response.StatusCode = statusCode;
            context.Response.ContentType = "application/json";
            return context.Response.WriteAsync(Serialize(value));
        }
    }

    /// <summary>
    /// Writes every date as UTC with a trailing <c>Z</c>, treating dates of unspecified kind as UTC, so browsers
    /// never read them as local time.
    /// </summary>
    internal sealed class UtcDateTimeJsonConverter : JsonConverter<DateTime>
    {
        public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            return reader.GetDateTime().ToUniversalTime();
        }

        public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options)
        {
            writer.WriteStringValue(value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : DateTime.SpecifyKind(value, DateTimeKind.Utc));
        }
    }
}
