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
using System.Linq;
using System.Reflection;
using System.Threading;
using Hangfire.Annotations;

namespace Hangfire.Common
{
    /// <summary>
    /// Tells how a <see cref="JobInvoker"/> calls its method.
    /// </summary>
    public enum JobInvokerKind
    {
        /// <summary>The call was compiled into the consumer's assembly by the Hangfire source generator.</summary>
        Generated,

        /// <summary>The call is a delegate compiled from an expression tree at runtime, once per method.</summary>
        Compiled,

        /// <summary>The call goes through <see cref="MethodBase.Invoke(object, object[])"/>.</summary>
        Reflection,
    }

    /// <summary>
    /// Calls one background job method without reflection. Instances are registered in the
    /// <see cref="JobInvokerRegistry"/>, usually by code the Hangfire source generator emits into the
    /// assembly that defines the job, and resolved by <see cref="Server.IBackgroundJobPerformer"/>
    /// implementations when performing a job.
    /// </summary>
    /// <remarks>
    /// <para>An invoker is identified by the method's declaring type, name, parameter types and generic
    /// arguments, so a job created from an interface, a base class or a derived instance resolves to the
    /// same invoker.</para>
    /// <para><see cref="Invoke"/> throws the job's own exception, never a
    /// <see cref="TargetInvocationException"/>.</para>
    /// </remarks>
    public abstract class JobInvoker
    {
        private MethodInfo? _method;

        /// <summary>
        /// Initializes an invoker for a method identified by its signature. The <see cref="Method"/> is
        /// resolved on first access. This constructor is used by generated code.
        /// </summary>
        /// <param name="type">Type that declares the method.</param>
        /// <param name="methodName">Method name.</param>
        /// <param name="parameterTypes">Closed parameter types, in order.</param>
        /// <param name="genericArguments">Generic arguments of a constructed generic method, or an empty array.</param>
        protected JobInvoker(
            [NotNull] Type type,
            [NotNull] string methodName,
            [NotNull] Type[] parameterTypes,
            [NotNull] Type[] genericArguments)
        {
            Type = type ?? throw new ArgumentNullException(nameof(type));
            MethodName = methodName ?? throw new ArgumentNullException(nameof(methodName));
            ParameterTypes = parameterTypes ?? throw new ArgumentNullException(nameof(parameterTypes));
            GenericArguments = genericArguments ?? throw new ArgumentNullException(nameof(genericArguments));
            Kind = JobInvokerKind.Generated;
        }

        internal JobInvoker([NotNull] MethodInfo method, JobInvokerKind kind)
        {
            if (method == null) throw new ArgumentNullException(nameof(method));
            if (method.DeclaringType == null) throw new ArgumentException("Global methods are not supported.", nameof(method));

            _method = method;
            Type = method.DeclaringType;
            MethodName = method.Name;
            ParameterTypes = method.GetParameters().Select(static x => x.ParameterType).ToArray();
            GenericArguments = method.IsGenericMethod ? method.GetGenericArguments() : Type.EmptyTypes;
            Kind = kind;
        }

        /// <summary>Gets the type that declares the method. Instances passed to <see cref="Invoke"/> are cast to it.</summary>
        [NotNull]
        public Type Type { get; }

        /// <summary>Gets the method name.</summary>
        [NotNull]
        public string MethodName { get; }

        /// <summary>Gets the closed parameter types, in declaration order.</summary>
        [NotNull]
        public Type[] ParameterTypes { get; }

        /// <summary>Gets the generic arguments of a constructed generic method, or an empty array.</summary>
        [NotNull]
        public Type[] GenericArguments { get; }

        /// <summary>Gets how this invoker calls the method.</summary>
        public JobInvokerKind Kind { get; }

        /// <summary>
        /// Gets the method metadata. Filters still need it to read attributes; it is resolved once, lazily,
        /// for generated invokers.
        /// </summary>
        [NotNull]
        public MethodInfo Method
        {
            get
            {
                var method = Volatile.Read(ref _method);
                if (method != null) return method;

                method = ResolveMethod();
                Interlocked.CompareExchange(ref _method, method, null);
                return _method!;
            }
        }

        /// <summary>Gets the method's return type.</summary>
        [NotNull]
        public Type ReturnType => Method.ReturnType;

        /// <summary>Gets a value indicating whether the method is static, so <see cref="Invoke"/> ignores the instance.</summary>
        public bool IsStatic => Method.IsStatic;

        /// <summary>
        /// Calls the method.
        /// </summary>
        /// <param name="instance">Instance to call the method on, ignored for static methods.</param>
        /// <param name="arguments">Arguments, already converted to <see cref="ParameterTypes"/>. A <c>null</c>
        /// for a non-nullable value type parameter is passed as that type's default value.</param>
        /// <returns>The method's return value, or <c>null</c> for <c>void</c> methods.</returns>
        [CanBeNull]
        public abstract object? Invoke([CanBeNull] object? instance, [NotNull] object?[] arguments);

        /// <inheritdoc />
        public override string ToString()
        {
            return $"{Kind}: {Type.ToGenericTypeString()}.{MethodName}({String.Join(", ", ParameterTypes.Select(static x => x.Name))})";
        }

        private MethodInfo ResolveMethod()
        {
            var method = Type.GetNonOpenMatchingMethod(MethodName, ParameterTypes);

            if (method == null)
            {
                throw new InvalidOperationException(
                    $"The type `{Type.FullName}` does not contain a method with signature `{MethodName}({String.Join(", ", ParameterTypes.Select(static x => x.Name))})`.");
            }

            if (GenericArguments.Length > 0 && method.IsGenericMethodDefinition)
            {
                method = method.MakeGenericMethod(GenericArguments);
            }

            return method;
        }
    }
}
