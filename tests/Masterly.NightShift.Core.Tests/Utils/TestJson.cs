namespace Masterly.NightShift.Core.Tests;

/// <summary>
/// JSON with the user serializer options, as job arguments are written. Builds the expected payloads in tests.
/// </summary>
internal static class TestJson
{
    public static string ToJson(object value)
    {
        return SerializationHelper.Serialize(value, null, SerializationOption.User);
    }

    public static T FromJson<T>(string value)
    {
        return SerializationHelper.Deserialize<T>(value, SerializationOption.User);
    }

    public static object FromJson(string value, Type type)
    {
        return SerializationHelper.Deserialize(value, type, SerializationOption.User);
    }
}
