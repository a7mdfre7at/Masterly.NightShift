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
using System.Reflection;
using System.Runtime.ExceptionServices;

namespace Masterly.NightShift.Common
{
    /// <summary>
    /// The last-resort <see cref="JobInvoker"/>: <see cref="MethodBase.Invoke(object, object[])"/>, with
    /// the <see cref="TargetInvocationException"/> unwrapped so callers see the job's own exception.
    /// </summary>
    internal sealed class ReflectionJobInvoker : JobInvoker
    {
        public ReflectionJobInvoker(MethodInfo method)
            : base(method, JobInvokerKind.Reflection)
        {
        }

        public override object? Invoke(object? instance, object?[] arguments)
        {
            if (arguments == null) throw new ArgumentNullException(nameof(arguments));

            try
            {
                return Method.Invoke(instance, arguments);
            }
            catch (TargetInvocationException ex) when (ex.InnerException != null)
            {
                ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
                throw;
            }
        }
    }
}
