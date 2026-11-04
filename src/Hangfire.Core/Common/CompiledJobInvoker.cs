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
using System.Linq.Expressions;
using System.Reflection;
using Hangfire.Annotations;
using Hangfire.Logging;

namespace Hangfire.Common
{
    /// <summary>
    /// A <see cref="JobInvoker"/> whose call is a delegate compiled once from an expression tree:
    /// <c>(instance, arguments) => ((T)instance).Method((P0)arguments[0], ...)</c>.
    /// </summary>
    internal sealed class CompiledJobInvoker : JobInvoker
    {
        private static readonly ILog Logger = LogProvider.GetLogger(typeof(CompiledJobInvoker));

        private readonly Func<object?, object?[], object?> _call;

        private CompiledJobInvoker(MethodInfo method, Func<object?, object?[], object?> call)
            : base(method, JobInvokerKind.Compiled)
        {
            _call = call;
        }

        /// <summary>
        /// Gets whether this runtime can compile expression trees to delegates. Without it the interpreter
        /// would be slower than reflection, so the registry skips this tier.
        /// </summary>
        public static bool IsSupported
        {
            get
            {
#if NET8_0_OR_GREATER
                return System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeCompiled;
#else
                return true;
#endif
            }
        }

        /// <summary>
        /// Compiles an invoker for the method, or returns <c>null</c> when the method can't be compiled, for
        /// example because a type involved is not visible to dynamic code.
        /// </summary>
        [CanBeNull]
        public static CompiledJobInvoker? TryCreate([NotNull] MethodInfo method)
        {
            if (method == null) throw new ArgumentNullException(nameof(method));

            try
            {
                return new CompiledJobInvoker(method, Compile(method));
            }
            catch (Exception ex) when (ex.IsCatchableExceptionType())
            {
                Logger.DebugException($"Unable to compile an invoker for `{method.DeclaringType}.{method.Name}`, reflection will be used instead.", ex);
                return null;
            }
        }

        public override object? Invoke(object? instance, object?[] arguments)
        {
            if (arguments == null) throw new ArgumentNullException(nameof(arguments));
            return _call(instance, arguments);
        }

        private static Func<object?, object?[], object?> Compile(MethodInfo method)
        {
            if (method.DeclaringType == null) throw new NotSupportedException("Global methods are not supported.");
            if (method.ContainsGenericParameters) throw new NotSupportedException("Open generic methods are not supported.");

            var instance = Expression.Parameter(typeof(object), "instance");
            var arguments = Expression.Parameter(typeof(object[]), "arguments");

            var parameters = method.GetParameters();
            var callArguments = new Expression[parameters.Length];

            for (var i = 0; i < parameters.Length; i++)
            {
                var parameterType = parameters[i].ParameterType;

                if (parameterType.IsByRef || parameterType.IsPointer)
                {
                    throw new NotSupportedException("Parameters passed by reference or pointers are not supported.");
                }

                Expression argument = Expression.ArrayIndex(arguments, Expression.Constant(i));

                if (parameterType.GetTypeInfo().IsValueType && Nullable.GetUnderlyingType(parameterType) == null)
                {
                    // A null argument for a non-nullable value type is passed as the default value, like MethodInfo.Invoke does.
                    argument = Expression.Condition(
                        Expression.ReferenceEqual(argument, Expression.Constant(null, typeof(object))),
                        Expression.Default(parameterType),
                        Expression.Convert(argument, parameterType));
                }
                else
                {
                    argument = Expression.Convert(argument, parameterType);
                }

                callArguments[i] = argument;
            }

            Expression call = method.IsStatic
                ? Expression.Call(method, callArguments)
                : Expression.Call(Expression.Convert(instance, method.DeclaringType), method, callArguments);

            Expression body = method.ReturnType == typeof(void)
                ? Expression.Block(call, Expression.Constant(null, typeof(object)))
                : Expression.Convert(call, typeof(object));

            return Expression.Lambda<Func<object?, object?[], object?>>(body, instance, arguments).Compile();
        }
    }
}
