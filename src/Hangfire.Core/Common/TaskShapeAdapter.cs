// This file is part of Hangfire. Copyright © 2026 Hangfire OÜ.
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

#nullable enable

using System;
using System.Collections.Concurrent;
using System.Reflection;
using System.Threading.Tasks;

namespace Hangfire.Common
{
    /// <summary>
    /// Turns the <see cref="Task{TResult}"/> of <see cref="object"/> an asynchronous interceptor produces
    /// into a value of the job method's return type, so the performer can treat it like the method's own result.
    /// </summary>
    internal static class TaskShapeAdapter
    {
        private static readonly ConcurrentDictionary<Type, Func<Task<object?>, object>> Adapters = new();

        private static readonly MethodInfo ToTaskOfMethod = typeof(TaskShapeAdapter)
            .GetTypeInfo().GetDeclaredMethod(nameof(ToTaskOf))!;

        public static object? Adapt(Task<object?> source, Type returnType)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (returnType == null) throw new ArgumentNullException(nameof(returnType));

            if (returnType == typeof(void) || !IsTaskLikeType(returnType))
            {
                // A synchronous method: the caller expects the value itself.
                return source.GetAwaiter().GetResult();
            }

            return Adapters.GetOrAdd(returnType, static type => CreateAdapter(type))(source);
        }

        private static bool IsTaskLikeType(Type type)
        {
            if (typeof(Task).GetTypeInfo().IsAssignableFrom(type.GetTypeInfo())) return true;

            return type.FullName != null && type.FullName.StartsWith("System.Threading.Tasks.ValueTask", StringComparison.Ordinal);
        }

        private static Func<Task<object?>, object> CreateAdapter(Type returnType)
        {
            var typeInfo = returnType.GetTypeInfo();

            if (returnType == typeof(Task) || returnType == typeof(Task<object?>))
            {
                return static source => source;
            }

            if (typeInfo.IsGenericType && typeInfo.GetGenericTypeDefinition() == typeof(Task<>))
            {
                var resultType = typeInfo.GenericTypeArguments[0];
                var toTaskOf = ToTaskOfMethod.MakeGenericMethod(resultType);
                var converter = (Func<Task<object?>, Task>)toTaskOf.CreateDelegate(typeof(Func<Task<object?>, Task>));

                return source => converter(source);
            }

            // ValueTask and ValueTask<T>: both have a constructor taking the corresponding Task.
            if (typeInfo.IsGenericType)
            {
                var resultType = typeInfo.GenericTypeArguments[0];
                var taskOfResult = typeof(Task<>).MakeGenericType(resultType);
                var constructor = typeInfo.GetConstructor(new[] { taskOfResult });

                if (constructor != null)
                {
                    var toTaskOf = ToTaskOfMethod.MakeGenericMethod(resultType);
                    var converter = (Func<Task<object?>, Task>)toTaskOf.CreateDelegate(typeof(Func<Task<object?>, Task>));

                    return source => constructor.Invoke(new object[] { converter(source) });
                }
            }
            else
            {
                var constructor = typeInfo.GetConstructor(new[] { typeof(Task) });
                if (constructor != null)
                {
                    return source => constructor.Invoke(new object[] { source });
                }
            }

            throw new NotSupportedException($"Asynchronous interceptors can't adapt their result to the `{returnType}` return type.");
        }

        private static async Task<T> ToTaskOf<T>(Task<object?> source)
        {
            var result = await source.ConfigureAwait(false);
            return result == null ? default! : (T)result;
        }
    }
}
