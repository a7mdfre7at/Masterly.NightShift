using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Masterly.NightShift.Core.Tests
{
    /// <summary>
    /// A resolver that knows only the given types, like a source-generated context: System.Text.Json throws
    /// <see cref="NotSupportedException"/> for any other type.
    /// </summary>
    internal sealed class KnownTypesResolver : IJsonTypeInfoResolver
    {
        private readonly DefaultJsonTypeInfoResolver _inner = new DefaultJsonTypeInfoResolver();
        private readonly HashSet<Type> _types;

        public KnownTypesResolver(params Type[] types)
        {
            _types = new HashSet<Type>(types);
        }

        public JsonTypeInfo GetTypeInfo(Type type, JsonSerializerOptions options)
        {
            return _types.Contains(type) ? _inner.GetTypeInfo(type, options) : null;
        }
    }
}
