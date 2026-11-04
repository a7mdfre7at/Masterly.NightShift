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
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using System.Reflection;
using Hangfire.Common;

namespace Hangfire.Server
{
    /// <summary>
    /// Finds the interceptors for a job: the global ones, then the attributes on its type and method,
    /// ordered by <see cref="JobInterceptorAttribute.Order"/>. Attribute lookups are cached per method.
    /// </summary>
    internal static class JobInterceptorPipeline
    {
        private static readonly IJobInterceptor[] Empty = [];

        private static readonly ConcurrentDictionary<(Type Type, MethodInfo Method), IJobInterceptor[]> AttributeCache = new();

        public static IReadOnlyList<IJobInterceptor> GetInterceptors(Job job)
        {
            if (job == null) throw new ArgumentNullException(nameof(job));

            var declared = AttributeCache.GetOrAdd((job.Type, job.Method), static key => GetDeclaredInterceptors(key.Type, key.Method));
            var global = GlobalJobInterceptors.Interceptors;

            if (global.Count == 0) return declared;
            if (declared.Length == 0) return global;

            var combined = new IJobInterceptor[global.Count + declared.Length];

            for (var i = 0; i < global.Count; i++)
            {
                combined[i] = global[i];
            }

            Array.Copy(declared, 0, combined, global.Count, declared.Length);
            return combined;
        }

        private static IJobInterceptor[] GetDeclaredInterceptors(Type type, MethodInfo method)
        {
            var typeAttributes = type.GetTypeInfo().GetCustomAttributes<JobInterceptorAttribute>(inherit: true);
            var methodAttributes = method.GetCustomAttributes<JobInterceptorAttribute>(inherit: true);

            var all = typeAttributes
                .Select(static (attribute, index) => (Attribute: attribute, Scope: 0, Index: index))
                .Concat(methodAttributes.Select(static (attribute, index) => (Attribute: attribute, Scope: 1, Index: index)))
                .OrderBy(static x => x.Attribute.Order)
                .ThenBy(static x => x.Scope)
                .ThenBy(static x => x.Index)
                .Select(static x => (IJobInterceptor)x.Attribute)
                .ToArray();

            return all.Length == 0 ? Empty : all;
        }
    }
}
