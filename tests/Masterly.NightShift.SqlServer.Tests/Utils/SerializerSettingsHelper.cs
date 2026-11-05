using System.Text.Json;
using System.Text.Json.Serialization;

namespace Masterly.NightShift.SqlServer.Tests
{
    public static class SerializerSettingsHelper
    {
        /// <summary>
        /// User options as different from the defaults as possible, to prove that internal data doesn't depend on them.
        /// </summary>
        public static readonly JsonSerializerOptions DangerousSettings = new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
            NumberHandling = JsonNumberHandling.WriteAsString | JsonNumberHandling.AllowReadingFromString,
            IncludeFields = true,
            Converters = { new JsonStringEnumConverter() }
        };
    }
}
