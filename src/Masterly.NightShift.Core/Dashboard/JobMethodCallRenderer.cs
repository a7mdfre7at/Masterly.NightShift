// This file is part of Hangfire. Copyright © 2013-2014 Hangfire OÜ.
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

using System.Collections;
using System.Globalization;
using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using Masterly.NightShift.Dashboard.Resources;
using Masterly.NightShift.Processing;

namespace Masterly.NightShift.Dashboard;

internal static class JobMethodCallRenderer
{
    // Should not be converted to "readonly" to support https://github.com/HangfireIO/Hangfire/issues/1295
    internal static int MaxArgumentToRenderSize = 4096;

    public static NonEscapedString Render(Job job)
    {
        if (job == null) { return new NonEscapedString($"<em>{Encode(Strings.Common_CannotFindTargetMethod)}</em>"); }

        StringBuilder builder = new();

        builder.Append(WrapKeyword("using"));
        builder.Append(' ');
        builder.Append(Encode(job.Type.Namespace));
        builder.Append(';');
        builder.AppendLine();
        builder.AppendLine();

        string? serviceName = null;

        if (!job.Method.IsStatic)
        {
            serviceName = GetNameWithoutGenericArity(job.Type);

            if (job.Type.GetTypeInfo().IsInterface && serviceName[0] == 'I' && char.IsUpper(serviceName[1]))
            {
                serviceName = serviceName[1..];
            }

            serviceName = char.ToLower(serviceName[0]
                , CultureInfo.InvariantCulture
                ) + serviceName[1..];

            builder.Append(WrapKeyword("var"));
            builder.Append(
                FormattableString.Invariant($" {Encode(serviceName)} = Activate&lt;{WrapType(Encode(job.Type.ToGenericTypeString()))}&gt;();"));

            builder.AppendLine();
        }

        if (job.Method.GetCustomAttribute<AsyncStateMachineAttribute>() != null ||
            job.Method.ReturnType.IsTaskLike(out _))
        {
            builder.Append(FormattableString.Invariant($"{WrapKeyword("await")} "));
        }

        builder.Append(!job.Method.IsStatic ? Encode(serviceName) : WrapType(Encode(job.Type.ToGenericTypeString())));

        builder.Append('.');
        builder.Append(Encode(job.Method.Name));

        if (job.Method.IsGenericMethod)
        {
            string[] genericArgumentTypes = [.. job.Method.GetGenericArguments().Select(static x => WrapType(x.Name))];

            builder.Append(FormattableString.Invariant($"&lt;{string.Join(", ", genericArgumentTypes)}&gt;"));
        }

        builder.Append('(');

        ParameterInfo[] parameters = job.Method.GetParameters();
        // Only shown, never stored: a job is shown as it is
        string[] arguments = InvocationData.SerializeArguments(job.Method, job.Args, ensureReadsBack: false);
        List<string> renderedArguments = new(parameters.Length);
        int renderedArgumentsTotalLength = 0;

        const int splitStringMinLength = 100;

        for (int i = 0; i < parameters.Length; i++)
        {
            ParameterInfo parameter = parameters[i];

            if (RedactedAttribute.IsRedacted(parameter))
            {
                string redacted = $"<span class=\"hf-redacted\" title=\"{Encode(Strings.Common_Redacted)}\">{WrapString(Encode("\"" + RedactedAttribute.Mask + "\""))}</span>";
                renderedArguments.Add(redacted);
                renderedArgumentsTotalLength += redacted.Length;
                continue;
            }

            if (i < arguments.Length)
            {
                string? argument = arguments[i];

                if (argument != null && argument.Length > MaxArgumentToRenderSize)
                {
                    renderedArguments.Add(Encode("<VALUE IS TOO BIG>"));
                    continue;
                }

                string renderedArgument;

                Type? enumerableArgument = GetIEnumerableGenericArgument(parameter.ParameterType);

                object? argumentValue;
                bool isJson = true;

                try
                {
                    argumentValue = SerializationHelper.Deserialize(argument, parameter.ParameterType, SerializationOption.User);
                }
                catch (Exception ex) when (ex.IsCatchableExceptionType())
                {
                    // If argument value is not encoded as JSON (an old
                    // way using TypeConverter), we should display it as is.
                    argumentValue = argument;
                    isJson = false;
                }

                if (enumerableArgument == null || argumentValue == null)
                {
                    ArgumentRenderer argumentRenderer = ArgumentRenderer.GetRenderer(parameter.ParameterType);
                    renderedArgument = argumentRenderer.Render(isJson, argumentValue?.ToString(), argument);
                }
                else
                {
                    List<string> renderedItems = [];

                    // ReSharper disable once LoopCanBeConvertedToQuery
                    foreach (object? item in (IEnumerable)argumentValue) // Checked to be enumerable above
                    {
                        ArgumentRenderer argumentRenderer = ArgumentRenderer.GetRenderer(enumerableArgument);
                        renderedItems.Add(argumentRenderer.Render(isJson, item?.ToString(),
                            SerializationHelper.Serialize(item, SerializationOption.User)));
                    }

                    // ReSharper disable once UseStringInterpolation
                    renderedArgument = string.Format(
                        CultureInfo.CurrentCulture,
                        "{0}{1} {{ {2} }}",
                        WrapKeyword("new"),
                        parameter.ParameterType.IsArray ? " []" : "",
                        string.Join(", ", renderedItems));
                }

                renderedArguments.Add(renderedArgument);
                renderedArgumentsTotalLength += renderedArgument.Length;
            }
            else
            {
                renderedArguments.Add(Encode("<NO VALUE>"));
            }
        }

        for (int i = 0; i < renderedArguments.Count; i++)
        {
            // TODO: be aware of out of range
            ParameterInfo parameter = parameters[i];
            string tooltipPosition = "top";

            string renderedArgument = renderedArguments[i];
            if (renderedArgumentsTotalLength > splitStringMinLength)
            {
                builder.AppendLine();
                builder.Append("    ");

                tooltipPosition = "left";
            }
            else if (i > 0)
            {
                builder.Append(' ');
            }

            builder.Append(FormattableString.Invariant($"<span title=\"{parameter.Name}\" data-bs-placement=\"{tooltipPosition}\">"));
            builder.Append(renderedArgument);
            builder.Append("</span>");

            if (i < renderedArguments.Count - 1)
            {
                builder.Append(',');
            }
        }

        builder.Append(");");

        return new NonEscapedString(builder.ToString());
    }

    private static string WrapIdentifier(string value)
    {
        return value;
    }

    private static string WrapKeyword(string value)
    {
        return Span("keyword", value);
    }

    private static string WrapType(string value)
    {
        return Span("type", value);
    }

    private static string WrapString(string value)
    {
        return Span("string", value);
    }

    private static string Span(string @class, string value)
    {
        return $"<span class=\"{@class}\">{value}</span>";
    }

    private static string Encode(string? value)
    {
        return WebUtility.HtmlEncode(value) ?? string.Empty;
    }

    private static Type? GetIEnumerableGenericArgument(Type type)
    {
        if (type == typeof(string))
        {
            return null;
        }

        return type.GetTypeInfo().ImplementedInterfaces
            .Where(static x => x.GetTypeInfo().IsGenericType
                        && x.GetTypeInfo().GetGenericTypeDefinition() == typeof(IEnumerable<>))
            .Select(static x => x.GetTypeInfo().GetAllGenericArguments()[0])
            .FirstOrDefault();
    }

    public static string GetNameWithoutGenericArity(Type t)
    {
        string name = t.Name;
        int index = name.IndexOf('`');
        return index == -1 ? name : name[..index];
    }

    private sealed class ArgumentRenderer
    {
        private string _enclosingString;
        private Type? _deserializationType;
        private Func<string, string> _valueRenderer;

        private ArgumentRenderer()
        {
            _enclosingString = "\"";
            _valueRenderer = static value => value == null ? WrapKeyword("null") : WrapString(value);
        }

        public string Render(bool isJson, string? deserializedValue, string? rawValue)
        {
            StringBuilder builder = new();

            if (rawValue == null)
            {
                return WrapKeyword("null");
            }

            if (_deserializationType != null)
            {
                builder.Append(WrapIdentifier(
                    isJson ? "FromJson" : "Deserialize"));

                builder.Append("&lt;")
                    .Append(WrapType(Encode(_deserializationType.ToGenericTypeString())))
                    .Append(WrapIdentifier("&gt;"))
                    .Append('(');

                builder.Append(WrapString(Encode("\"" + rawValue.Replace("\"", "\\\"") + "\"")));
            }
            else
            {
                if (deserializedValue != null)
                {
                    builder.Append(_enclosingString);
                }

                builder.Append(_valueRenderer(Encode(deserializedValue)));

                if (deserializedValue != null)
                {
                    builder.Append(_enclosingString);
                }
            }

            if (_deserializationType != null)
            {
                builder.Append(')');
            }

            return builder.ToString();
        }

        public static ArgumentRenderer GetRenderer(Type type)
        {
            if (type.GetTypeInfo().IsEnum)
            {
                return new ArgumentRenderer
                {
                    _enclosingString = string.Empty,
                    _valueRenderer = value => $"{WrapType(type.Name)}.{value}"
                };
            }

            if (IsNumericType(type))
            {
                return new ArgumentRenderer
                {
                    _enclosingString = string.Empty,
                    _valueRenderer = WrapIdentifier
                };
            }

            if (type == typeof(bool))
            {
                return new ArgumentRenderer
                {
                    _valueRenderer = static value => WrapKeyword(value.ToLowerInvariant()),
                    _enclosingString = string.Empty,
                };
            }

            if (type == typeof(char))
            {
                return new ArgumentRenderer
                {
                    _enclosingString = "'",
                };
            }

            if (type == typeof(string) || type == typeof(object))
            {
                return new ArgumentRenderer
                {
                    _enclosingString = "\""
                };
            }

            if (type == typeof(TimeSpan) || type == typeof(DateTime))
            {
                return new ArgumentRenderer
                {
                    _enclosingString = string.Empty,
                    _valueRenderer = value => $"{WrapType(type.Name)}.Parse({WrapString($"\"{value}\"")})"
                };
            }

            if (type == typeof(CancellationToken))
            {
                return new ArgumentRenderer
                {
                    _enclosingString = string.Empty,
                    _valueRenderer = static _ => $"{WrapType(nameof(CancellationToken))}.None"
                };
            }

            return new ArgumentRenderer
            {
                _deserializationType = type,
            };
        }

        private static bool IsNumericType(Type? type)
        {
            if (type == null)
            {
                return false;
            }

            switch (type.GetTypeCode())
            {
                case TypeCode.Byte:
                case TypeCode.Decimal:
                case TypeCode.Double:
                case TypeCode.Int16:
                case TypeCode.Int32:
                case TypeCode.Int64:
                case TypeCode.SByte:
                case TypeCode.Single:
                case TypeCode.UInt16:
                case TypeCode.UInt32:
                case TypeCode.UInt64:
                    return true;

                case TypeCode.Object:
                    if (IsNullableType(type))
                    {
                        return IsNumericType(Nullable.GetUnderlyingType(type));
                    }
                    return false;
            }
            return false;
        }

        private static bool IsNullableType(Type type)
        {
            return type.GetTypeInfo().IsGenericType && type.GetTypeInfo().GetGenericTypeDefinition() == typeof(Nullable<>);
        }
    }
}

internal enum TypeCode
{
    Empty = 0,          // Null reference
    Object = 1,         // Instance that isn't a value
    DBNull = 2,         // Database null value
    Boolean = 3,        // Boolean
    Char = 4,           // Unicode character
    SByte = 5,          // Signed 8-bit integer
    Byte = 6,           // Unsigned 8-bit integer
    Int16 = 7,          // Signed 16-bit integer
    UInt16 = 8,         // Unsigned 16-bit integer
    Int32 = 9,          // Signed 32-bit integer
    UInt32 = 10,        // Unsigned 32-bit integer
    Int64 = 11,         // Signed 64-bit integer
    UInt64 = 12,        // Unsigned 64-bit integer
    Single = 13,        // IEEE 32-bit float
    Double = 14,        // IEEE 64-bit double
    Decimal = 15,       // Decimal
    DateTime = 16,      // DateTime
    String = 18,        // Unicode character string
}

internal static class TypeExtensionMethods
{
    extension(Type type)
    {
        public TypeCode GetTypeCode()
        {
            if (type == null)
            {
                return TypeCode.Empty;
            }
            else if (type == typeof(bool))
            {
                return TypeCode.Boolean;
            }
            else if (type == typeof(char))
            {
                return TypeCode.Char;
            }
            else if (type == typeof(sbyte))
            {
                return TypeCode.SByte;
            }
            else if (type == typeof(byte))
            {
                return TypeCode.Byte;
            }
            else if (type == typeof(short))
            {
                return TypeCode.Int16;
            }
            else if (type == typeof(ushort))
            {
                return TypeCode.UInt16;
            }
            else if (type == typeof(int))
            {
                return TypeCode.Int32;
            }
            else if (type == typeof(uint))
            {
                return TypeCode.UInt32;
            }
            else if (type == typeof(long))
            {
                return TypeCode.Int64;
            }
            else if (type == typeof(ulong))
            {
                return TypeCode.UInt64;
            }
            else if (type == typeof(float))
            {
                return TypeCode.Single;
            }
            else if (type == typeof(double))
            {
                return TypeCode.Double;
            }
            else if (type == typeof(decimal))
            {
                return TypeCode.Decimal;
            }
            else if (type == typeof(DateTime))
            {
                return TypeCode.DateTime;
            }
            else if (type == typeof(string))
            {
                return TypeCode.String;
            }
            else
            {
                return TypeCode.Object;
            }
        }
    }
}
