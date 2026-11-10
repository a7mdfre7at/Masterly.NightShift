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

using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;

namespace Masterly.NightShift.Common;

/// <summary>
/// Turns types into the names stored with jobs and back, and maps the type names stored by earlier versions to the
/// current types. The resolver and serializer can be replaced.
/// </summary>
public partial class TypeHelper
{
    private static readonly ConcurrentDictionary<Type, string> DefaultTypeSerializerCache = new();
    private static readonly ConcurrentDictionary<Type, string> SimpleAssemblyTypeSerializerCache = new();

    private static readonly ConcurrentDictionary<string, Type> DefaultTypeResolverCache = new();
    private static readonly ConcurrentDictionary<string, Type> IgnoredAssemblyVersionTypeResolverCache = new();

    private static readonly Assembly CoreLibrary = typeof(int).GetTypeInfo().Assembly;
    private static readonly AssemblyName MscorlibAssemblyName = new("mscorlib, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089");
    private static readonly ConcurrentDictionary<string, Assembly?> AssemblyCache = new();

    [GeneratedRegex(@", Version=\d+.\d+.\d+.\d+", RegexOptions.None, matchTimeoutMilliseconds: 5000)]
    private static partial Regex VersionRegex();
    [GeneratedRegex(@", Culture=\w+", RegexOptions.None, matchTimeoutMilliseconds: 5000)]
    private static partial Regex CultureRegex();
    [GeneratedRegex(@", PublicKeyToken=\w+", RegexOptions.None, matchTimeoutMilliseconds: 5000)]
    private static partial Regex PublicKeyTokenRegex();

    public static Func<string, Type> CurrentTypeResolver
    {
        get => Volatile.Read(ref field) ?? DefaultTypeResolver;
        set => Volatile.Write(ref field, value);
    }

    public static Func<Type, string> CurrentTypeSerializer
    {
        get => Volatile.Read(ref field) ?? DefaultTypeSerializer;
        set => Volatile.Write(ref field, value);
    }

    public static string DefaultTypeSerializer(Type type)
    {
        return DefaultTypeSerializerCache.GetOrAdd(type, static t => t.AssemblyQualifiedName!); // Serialized types are named
    }

    public static string SimpleAssemblyTypeSerializer(Type type)
    {
        return SimpleAssemblyTypeSerializerCache.GetOrAdd(type, static value =>
        {
            StringBuilder builder = new();
            SerializeType(value, true, builder);

            return builder.ToString();
        });
    }

    public static Type DefaultTypeResolver(string typeName)
    {
        return DefaultTypeResolverCache.GetOrAdd(typeName, static name =>
        {
            return Type.GetType(
                name,
                typeResolver: TypeResolver,
                assemblyResolver: CachedAssemblyResolver,
                throwOnError: true)!; // throwOnError: true, so never null
        });
    }

    public static Type IgnoredAssemblyVersionTypeResolver(string typeName)
    {
        return IgnoredAssemblyVersionTypeResolverCache.GetOrAdd(typeName, static value =>
        {
            value = VersionRegex().Replace(value, string.Empty);
            value = CultureRegex().Replace(value, string.Empty);
            value = PublicKeyTokenRegex().Replace(value, string.Empty);

            return DefaultTypeResolver(value);
        });
    }

    private static void SerializeType(Type type, bool withAssemblyName, StringBuilder typeNameBuilder)
    {
        if (type == typeof(System.Console))
        {
            typeNameBuilder.Append("System.Console, mscorlib");
            return;
        }

        if (type == typeof(System.Threading.Thread))
        {
            typeNameBuilder.Append("System.Threading.Thread, mscorlib");
            return;
        }

        if (type.IsArray && type.HasElementType)
        {
            Type? elementType = type.GetElementType();
            SerializeType(elementType!, false, typeNameBuilder); // An array type has an element type
            typeNameBuilder.Append("[]");
        }
        else
        {
            if (type.DeclaringType != null)
            {
                SerializeType(type.DeclaringType, false, typeNameBuilder);
                typeNameBuilder.Append('+');
            }
            else if (type.Namespace != null)
            {
                typeNameBuilder.Append(type.Namespace).Append('.');
            }

            typeNameBuilder.Append(type.Name);
        }

        if (type.GenericTypeArguments.Length > 0)
        {
            SerializeTypes(type.GenericTypeArguments, typeNameBuilder);
        }

        if (!withAssemblyName) return;

        TypeInfo typeInfo = type.GetTypeInfo();

        if (type != typeof(object) && type != typeof(string) && !typeInfo.IsPrimitive)
        {
            string? assemblyName;

            TypeForwardedFromAttribute? typeForwardedFrom = typeInfo.GetCustomAttribute<TypeForwardedFromAttribute>();
            if (typeForwardedFrom != null)
            {
                assemblyName = typeForwardedFrom.AssemblyFullName;

                int delimiterIndex = assemblyName.IndexOf(",", StringComparison.OrdinalIgnoreCase);

                assemblyName = delimiterIndex >= 0 ? assemblyName[..delimiterIndex] : assemblyName;
            }
            else
            {
                assemblyName = typeInfo.Assembly.GetName().Name;
            }

            if (assemblyName!.Equals("System.Private.CoreLib", StringComparison.OrdinalIgnoreCase)) // Loaded assemblies have names
            {
                assemblyName = "mscorlib";
            }

            typeNameBuilder.Append(", ").Append(assemblyName);
        }
    }

    private static void SerializeTypes(Type[] types, StringBuilder typeNamesBuilder)
    {
        if (types == null) return;

        typeNamesBuilder.Append('[');

        for (int i = 0; i < types.Length; i++)
        {
            typeNamesBuilder.Append('[');
            SerializeType(types[i], true, typeNamesBuilder);
            typeNamesBuilder.Append(']');

            if (i != types.Length - 1) typeNamesBuilder.Append(',');
        }

        typeNamesBuilder.Append(']');
    }

    private static Assembly? CachedAssemblyResolver(AssemblyName assemblyName)
    {
        return AssemblyCache.GetOrAdd(assemblyName.FullName, AssemblyResolver);
    }

    // Jobs stored before the rename refer to types such as "Hangfire.Server.PerformContext, Hangfire.Core".
    // Only these assemblies are mapped, so a user's own assembly with a similar name is left alone.
    private const string LegacyPrefix = "Hangfire.";
    private const string CurrentPrefix = "Masterly.NightShift.";
    private static readonly string[] LegacyAssemblies =
    [
        "Hangfire.Core", "Hangfire.SqlServer", "Hangfire.NetCore", "Hangfire.AspNetCore"
    ];

    private static bool IsLegacyAssembly(string name)
    {
        return Array.Exists(LegacyAssemblies, x => x.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsCurrentAssembly(Assembly assembly)
    {
        return assembly.GetName().Name?.StartsWith(CurrentPrefix, StringComparison.OrdinalIgnoreCase) == true;
    }

    private static string ToCurrentName(string name)
    {
        return name.StartsWith(LegacyPrefix, StringComparison.OrdinalIgnoreCase)
            ? string.Concat(CurrentPrefix, name.AsSpan(LegacyPrefix.Length))
            : name;
    }

    private static Assembly? AssemblyResolver(string assemblyString)
    {
        AssemblyName assemblyName = new(assemblyString);

        if (IsLegacyAssembly(assemblyName.Name!)) // Loaded assemblies have names
        {
            // Its version and public key belong to the old package, so only the name is kept
            assemblyName = new AssemblyName(ToCurrentName(assemblyName.Name!)); // Loaded assemblies have names
        }

        if (assemblyName.Name!.Equals("System.Console", StringComparison.OrdinalIgnoreCase) || // Loaded assemblies have names
            assemblyName.Name.Equals("System.Private.CoreLib", StringComparison.OrdinalIgnoreCase) ||
            assemblyName.Name.Equals("mscorlib", StringComparison.OrdinalIgnoreCase))
        {
            assemblyName = MscorlibAssemblyName;
        }
        else if (assemblyName.Name.Equals("System.Private.Xml.Linq", StringComparison.OrdinalIgnoreCase))
        {
            assemblyName = new AssemblyName("System.Xml.Linq, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089");
        }

        byte[]? publicKeyToken = assemblyName.GetPublicKeyToken();

        if (assemblyName.Version == null && assemblyName.CultureInfo == null && publicKeyToken == null)
        {
            // A partial name, as Assembly.LoadWithPartialName (obsolete) loads it on .NET
            try
            {
                return Assembly.Load(new AssemblyName(assemblyName.Name!)); // Loaded assemblies have names
            }
            catch (FileNotFoundException)
            {
                return null;
            }
        }

        try
        {
            return Assembly.Load(assemblyName);
        }
        catch (Exception ex) when (ex.IsCatchableExceptionType())
        {
            AssemblyName shortName = new(assemblyName.Name!); // Loaded assemblies have names
            if (publicKeyToken != null)
            {
                shortName.SetPublicKeyToken(publicKeyToken);
            }

            return Assembly.Load(shortName);
        }
    }

    private static Type? TypeResolver(Assembly? assembly, string typeName, bool ignoreCase)
    {
        if (typeName.Equals("System.Diagnostics.Debug", StringComparison.Ordinal))
        {
            return typeof(System.Diagnostics.Debug);
        }

        assembly ??= CoreLibrary;

        if (IsCurrentAssembly(assembly))
        {
            typeName = ToCurrentName(typeName);
        }

        if (assembly != CoreLibrary &&
            assembly.GetName().Name!.Equals("mscorlib", StringComparison.OrdinalIgnoreCase)) // Loaded assemblies have names
        {
            // Everything defaults to `mscorlib` for interoperability reasons between
            // .NET Framework and .NET Core. Most of the types have the proper forwarding,
            // but newer types like DateOnly or TimeOnly don't. So for types from `mscorlib`
            // we perform the first search in the current core library.
            Type? type = CoreLibrary.GetType(typeName, false, ignoreCase);
            if (type != null) return type;
        }

        return assembly.GetType(typeName, true, ignoreCase);
    }
}
