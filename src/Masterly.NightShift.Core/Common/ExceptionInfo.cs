// This file is part of Hangfire.
// Copyright © 2020 Hangfire OÜ.
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
using System.Text.Json.Serialization;
using System.Text;
using Masterly.NightShift.Annotations;
using Masterly.NightShift.Common;

namespace Masterly.NightShift
{
    /// <summary>
    /// The type, message and inner exception of an exception, as passed to a continuation with <see
    /// cref="FromExceptionAttribute"/>.
    /// </summary>
    public sealed class ExceptionInfo
    {
        public ExceptionInfo([NotNull] Exception exception)
        {
            if (exception == null) throw new ArgumentNullException(nameof(exception));

            Message = exception.Message;
            Type = TypeHelper.CurrentTypeSerializer(exception.GetType());

            if (exception.InnerException != null)
            {
                InnerException = new ExceptionInfo(exception.InnerException);
            }
        }

        [JsonConstructor]
        public ExceptionInfo([NotNull] string type, [CanBeNull] string message, [CanBeNull] ExceptionInfo innerException)
        {
            Type = type ?? throw new ArgumentNullException(nameof(type));
            Message = message;
            InnerException = innerException;
        }

        [NotNull]
        [JsonInclude, JsonPropertyName("e")]
        public string Type { get; }

        [CanBeNull]
        [JsonInclude, JsonPropertyName("m"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string Message { get; }

        [CanBeNull]
        [JsonInclude, JsonPropertyName("i"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public ExceptionInfo InnerException { get; }

        public override string ToString()
        {
            var sb = new StringBuilder();
            var commaIndex = Type.IndexOf(',');
            sb.Append(commaIndex >= 0 ? Type.Substring(0, commaIndex) : Type);
            sb.Append(": ");
            sb.Append(Message);

            if (InnerException != null)
            {
                sb.Append(" ---> ");
                sb.AppendLine(InnerException.ToString());
            }

            return sb.ToString();
        }
    }
}
