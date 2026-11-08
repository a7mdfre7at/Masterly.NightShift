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

using System;
using System.Collections.Generic;
using System.Reflection;
using Masterly.NightShift.Annotations;
using Masterly.NightShift.Server;
using Masterly.NightShift.Storage;

// ReSharper disable once CheckNamespace
namespace Masterly.NightShift.Common
{
    partial class Job
    {
        [Obsolete("Use Job(Type, MethodInfo, object[]) ctor overload instead. Kept for code ported from Hangfire.")]
        public Job([NotNull] Type type, [NotNull] MethodInfo method, [NotNull] string[] arguments)
        {
            if (type == null) throw new ArgumentNullException(nameof(type));
            if (method == null) throw new ArgumentNullException(nameof(method));
            if (arguments == null) throw new ArgumentNullException(nameof(arguments));

            Validate(type, nameof(type), method, nameof(method), arguments.Length, nameof(arguments));

            Type = type;
            Method = method;
            Args = InvocationData.DeserializeArguments(method, arguments);
        }

        /// <exclude />
        [NotNull]
        [Obsolete("Use the `Args` property instead, which avoids needless serialization. Kept for code ported from Hangfire.")]
        public string[] Arguments => InvocationData.SerializeArguments(Method, Args);

        /// <exclude />
        [Obsolete("Use the `CoreBackgroundJobPerformer` or `BackgroundJobPerformer` classes instead. Kept for code ported from Hangfire.")]
        public object Perform(JobActivator activator, IJobCancellationToken cancellationToken)
        {
            if (activator == null) throw new ArgumentNullException(nameof(activator));
            if (cancellationToken == null) throw new ArgumentNullException(nameof(cancellationToken));

            object instance = null;

            object result;
            try
            {
                if (!Method.IsStatic)
                {
                    instance = Activate(activator);
                }

                var arguments = GetArguments(cancellationToken);
                result = InvokeMethod(instance, arguments, cancellationToken);
            }
            finally
            {
                Dispose(instance);
            }

            return result;
        }

        [Obsolete("Used only by the obsolete Perform method. Kept for code ported from Hangfire.")]
        private object Activate(JobActivator activator)
        {
            try
            {
                var instance = activator.ActivateJob(Type);

                if (instance == null)
                {
                    throw new InvalidOperationException($"JobActivator returned NULL instance of the '{Type}' type.");
                }

                return instance;
            }
            catch (Exception ex) when (ex.IsCatchableExceptionType())
            {
                throw new JobPerformanceException(
                    "An exception occurred during job activation.",
                    ex);
            }
        }

        [Obsolete("Used only by the obsolete Perform method. Kept for code ported from Hangfire.")]
        private object[] GetArguments(IJobCancellationToken cancellationToken)
        {
            try
            {
                var parameters = Method.GetParameters();
                var result = new List<object>(Args.Count);

                for (var i = 0; i < parameters.Length; i++)
                {
                    var parameter = parameters[i];
                    var argument = Args[i];

                    object value;

                    if (typeof(IJobCancellationToken).GetTypeInfo().IsAssignableFrom(parameter.ParameterType.GetTypeInfo()))
                    {
                        value = cancellationToken;
                    }
                    else
                    {
                        value = argument;
                    }

                    result.Add(value);
                }

                return result.ToArray();
            }
            catch (Exception ex) when (ex.IsCatchableExceptionType())
            {
                throw new JobPerformanceException(
                    "An exception occurred during arguments deserialization.",
                    ex);
            }
        }

        [Obsolete("Used only by the obsolete Perform method. Kept for code ported from Hangfire.")]
        private object InvokeMethod(object instance, object[] deserializedArguments, IJobCancellationToken cancellationToken)
        {
            try
            {
                return Method.Invoke(instance, deserializedArguments);
            }
            catch (TargetInvocationException ex)
            {
                CoreBackgroundJobPerformer.HandleJobPerformanceException(ex.InnerException, cancellationToken, null);
                throw;
            }
        }

        [Obsolete("Used only by the obsolete Perform method. Kept for code ported from Hangfire.")]
        private static void Dispose(object instance)
        {
            try
            {
                var disposable = instance as IDisposable;
                disposable?.Dispose();
            }
            catch (Exception ex) when (ex.IsCatchableExceptionType())
            {
                throw new JobPerformanceException(
                    "Job has been performed, but an exception occurred during disposal.",
                    ex);
            }
        }
    }
}