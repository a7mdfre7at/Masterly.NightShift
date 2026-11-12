// This file is part of Hangfire. Copyright © 2014 Hangfire OÜ.
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

using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;

namespace Masterly.NightShift.Common;

internal static partial class TypeExtensions
{
    [GeneratedRegex(@"`[1-9]\d*", RegexOptions.None, matchTimeoutMilliseconds: 5000)]
    private static partial Regex GenericArgumentsRegex();

    extension(Type type)
    {
        public string ToGenericTypeString()
        {
            if (!type.GetTypeInfo().IsGenericType)
            {
                return type.GetFullNameWithoutNamespace()! // Generic and array types have these
                        .ReplacePlusWithDotInNestedTypeName();
            }

            return type.GetGenericTypeDefinition()
                    .GetFullNameWithoutNamespace()! // Generic and array types have these
                    .ReplacePlusWithDotInNestedTypeName()
                    .ReplaceGenericParametersInGenericTypeName(type);
        }
    }

    extension([NotNull] Type type)
    {
        public MethodInfo? GetNonOpenMatchingMethod(
            [NotNull] string name,
            [CanBeNull] Type[]? parameterTypes)
        {
            ArgumentNullException.ThrowIfNull(type);
            ArgumentNullException.ThrowIfNull(name);

            parameterTypes ??= Type.EmptyTypes;

            List<MethodInfo> methodCandidates = [.. type.GetRuntimeMethods()];

            if (type.GetTypeInfo().IsInterface)
            {
                methodCandidates.AddRange(type.GetTypeInfo()
                    .ImplementedInterfaces.SelectMany(static x => x.GetRuntimeMethods()));
            }

            // Prefer the overload whose parameter types are exactly the given ones: a job created for
            // Handle(Derived) must not run Handle(Base) just because that overload is declared first.
            // A non-generic exact match wins at once, like C# prefers it over a generic method; failing
            // that, a generic method whose inferred parameter types are exact; and failing that, the first
            // candidate whose parameter types accept the given ones, as before.
            MethodInfo? exactGenericMatch = null;
            MethodInfo? firstMatch = null;

            foreach (MethodInfo methodCandidate in methodCandidates)
            {
                if (!methodCandidate.GetNormalizedName().Equals(name, StringComparison.Ordinal))
                {
                    continue;
                }

                ParameterInfo[] parameters = methodCandidate.GetParameters();
                if (parameters.Length != parameterTypes.Length)
                {
                    continue;
                }

                bool parameterTypesMatched = true;

                Type[]? genericArguments = methodCandidate.ContainsGenericParameters
                    ? new Type[methodCandidate.GetGenericArguments().Length]
                    : null;

                // Determining whether we can use this method candidate with
                // current parameter types.
                for (int i = 0; i < parameters.Length; i++)
                {
                    TypeInfo parameterType = parameters[i].ParameterType.GetTypeInfo();
                    TypeInfo actualType = parameterTypes[i].GetTypeInfo();

                    if (!TypesMatchRecursive(parameterType, actualType, genericArguments!)) // Generic methods have generic arguments here
                    {
                        parameterTypesMatched = false;
                        break;
                    }
                }

                if (parameterTypesMatched)
                {
                    if (genericArguments != null)
                    {
                        bool genericArgumentsResolved = true;

                        foreach (Type genericArgument in genericArguments)
                        {
                            if (genericArgument == null)
                            {
                                genericArgumentsResolved = false;
                            }
                        }

                        if (genericArgumentsResolved)
                        {
                            MethodInfo genericMethod = methodCandidate.MakeGenericMethod(genericArguments);

                            if (exactGenericMatch == null && HasParameterTypes(genericMethod, parameterTypes))
                            {
                                exactGenericMatch = genericMethod;
                            }

                            firstMatch ??= genericMethod;
                        }
                    }
                    else
                    {
                        if (HasParameterTypes(methodCandidate, parameterTypes))
                        {
                            return methodCandidate;
                        }

                        firstMatch ??= methodCandidate;
                    }
                }
            }

            return exactGenericMatch ?? firstMatch;
        }
    }

    private static bool HasParameterTypes(MethodInfo method, Type[] parameterTypes)
    {
        ParameterInfo[] parameters = method.GetParameters();

        for (int i = 0; i < parameters.Length; i++)
        {
            if (parameters[i].ParameterType != parameterTypes[i])
            {
                return false;
            }
        }

        return true;
    }

    extension(TypeInfo type)
    {
        public Type[] GetAllGenericArguments()
        {
            return type.GenericTypeArguments.Length > 0 ? type.GenericTypeArguments : type.GenericTypeParameters;
        }
    }

    private static bool TypesMatchRecursive(TypeInfo parameterType, TypeInfo actualType, IList<Type> genericArguments)
    {
        if (parameterType.IsGenericParameter)
        {
            int position = parameterType.GenericParameterPosition;

            // Return false if this generic parameter has been identified and it's not the same as actual type
            if (genericArguments[position] != null && genericArguments[position].GetTypeInfo() != actualType)
            {
                return false;
            }

            genericArguments[position] = actualType.AsType();
            return true;
        }

        if (parameterType.ContainsGenericParameters)
        {
            if (parameterType.IsArray)
            {
                // Return false if parameterType is array whereas actualType isn't
                if (!actualType.IsArray)
                {
                    return false;
                }

                Type? parameterElementType = parameterType.GetElementType();
                Type? actualElementType = actualType.GetElementType();

                return TypesMatchRecursive(parameterElementType!.GetTypeInfo(), actualElementType!.GetTypeInfo(), genericArguments); // Generic and array types have these
            }

            if (!actualType.IsGenericType || parameterType.GetGenericTypeDefinition() != actualType.GetGenericTypeDefinition())
            {
                return false;
            }

            for (int i = 0; i < parameterType.GenericTypeArguments.Length; i++)
            {
                Type parameterGenericArgument = parameterType.GenericTypeArguments[i];
                Type actualGenericArgument = actualType.GenericTypeArguments[i];

                if (!TypesMatchRecursive(parameterGenericArgument.GetTypeInfo(), actualGenericArgument.GetTypeInfo(), genericArguments))
                {
                    return false;
                }
            }

            return true;
        }

        return parameterType != typeof(object).GetTypeInfo()
            ? parameterType.IsAssignableFrom(actualType)
            : parameterType == actualType;
    }

    extension(Type type)
    {
        private string? GetFullNameWithoutNamespace()
        {
            if (type.IsGenericParameter)
            {
                return type.Name;
            }

            const int dotLength = 1;
            // ReSharper disable once PossibleNullReferenceException
            return !string.IsNullOrEmpty(type.Namespace)
                ? type.FullName![(type.Namespace.Length + dotLength)..] // Only named types reach this
                : type.FullName;
        }
    }

    extension(string typeName)
    {
        private string ReplacePlusWithDotInNestedTypeName()
        {
            return typeName.Replace('+', '.');
        }

        private string ReplaceGenericParametersInGenericTypeName(Type type)
        {
            Type[] genericArguments = type.GetTypeInfo().GetAllGenericArguments();

            typeName = GenericArgumentsRegex().Replace(typeName, match =>
            {
                int currentGenericArgumentNumbers = int.Parse(match.Value.AsSpan(1), CultureInfo.InvariantCulture);
                string currentArguments = string.Join(",", genericArguments.Take(currentGenericArgumentNumbers).Select(ToGenericTypeString));
                genericArguments = [.. genericArguments.Skip(currentGenericArgumentNumbers)];
                return string.Concat("<", currentArguments, ">");
            });

            return typeName;
        }
    }
}
