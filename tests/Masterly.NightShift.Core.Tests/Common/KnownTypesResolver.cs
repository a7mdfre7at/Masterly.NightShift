using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Masterly.NightShift.Core.Tests;

/// <summary>
/// A resolver that knows only the given types, like a source-generated context: System.Text.Json throws
/// <see cref="NotSupportedException"/> for any other type.
/// </summary>
internal sealed class KnownTypesResolver(params Type[] types) : IJsonTypeInfoResolver
{
    private readonly DefaultJsonTypeInfoResolver _inner = new();
    private readonly HashSet<Type> _types = [.. types];

    public JsonTypeInfo GetTypeInfo(Type type, JsonSerializerOptions options)
    {
        return _types.Contains(type) ? _inner.GetTypeInfo(type, options) : null;
    }
}
