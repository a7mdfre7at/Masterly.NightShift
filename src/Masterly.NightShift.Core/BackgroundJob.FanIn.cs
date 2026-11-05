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
using System.Linq.Expressions;
using System.Threading.Tasks;
using Masterly.NightShift.Annotations;

namespace Masterly.NightShift
{
    public partial class BackgroundJob
    {
        /// <summary>Creates a job that starts once <em>every</em> parent has finished. See <see cref="BackgroundJobClientFanInExtensions"/>.</summary>
        public static string? ContinueJobWithAll(
            [NotNull] IEnumerable<string?> parentIds,
            [NotNull, InstantHandle] Expression<Action> methodCall,
            JobContinuationOptions options = JobContinuationOptions.OnlyOnSucceededState)
        {
            return ClientFactory().ContinueJobWithAll(parentIds, methodCall, options);
        }

        /// <summary>Creates a job that starts once <em>every</em> parent has finished. See <see cref="BackgroundJobClientFanInExtensions"/>.</summary>
        public static string? ContinueJobWithAll(
            [NotNull] IEnumerable<string?> parentIds,
            [NotNull, InstantHandle] Expression<Func<Task>> methodCall,
            JobContinuationOptions options = JobContinuationOptions.OnlyOnSucceededState)
        {
            return ClientFactory().ContinueJobWithAll(parentIds, methodCall, options);
        }

        /// <summary>Creates a job that starts once <em>every</em> parent has finished. See <see cref="BackgroundJobClientFanInExtensions"/>.</summary>
        public static string? ContinueJobWithAll<T>(
            [NotNull] IEnumerable<string?> parentIds,
            [NotNull, InstantHandle] Expression<Action<T>> methodCall,
            JobContinuationOptions options = JobContinuationOptions.OnlyOnSucceededState)
        {
            return ClientFactory().ContinueJobWithAll(parentIds, methodCall, options);
        }

        /// <summary>Creates a job that starts once <em>every</em> parent has finished. See <see cref="BackgroundJobClientFanInExtensions"/>.</summary>
        public static string? ContinueJobWithAll<T>(
            [NotNull] IEnumerable<string?> parentIds,
            [NotNull, InstantHandle] Expression<Func<T, Task>> methodCall,
            JobContinuationOptions options = JobContinuationOptions.OnlyOnSucceededState)
        {
            return ClientFactory().ContinueJobWithAll(parentIds, methodCall, options);
        }

        /// <summary>Creates a job that starts as soon as <em>one</em> parent has finished. See <see cref="BackgroundJobClientFanInExtensions"/>.</summary>
        public static string? ContinueJobWithAny(
            [NotNull] IEnumerable<string?> parentIds,
            [NotNull, InstantHandle] Expression<Action> methodCall,
            JobContinuationOptions options = JobContinuationOptions.OnlyOnSucceededState)
        {
            return ClientFactory().ContinueJobWithAny(parentIds, methodCall, options);
        }

        /// <summary>Creates a job that starts as soon as <em>one</em> parent has finished. See <see cref="BackgroundJobClientFanInExtensions"/>.</summary>
        public static string? ContinueJobWithAny(
            [NotNull] IEnumerable<string?> parentIds,
            [NotNull, InstantHandle] Expression<Func<Task>> methodCall,
            JobContinuationOptions options = JobContinuationOptions.OnlyOnSucceededState)
        {
            return ClientFactory().ContinueJobWithAny(parentIds, methodCall, options);
        }

        /// <summary>Creates a job that starts as soon as <em>one</em> parent has finished. See <see cref="BackgroundJobClientFanInExtensions"/>.</summary>
        public static string? ContinueJobWithAny<T>(
            [NotNull] IEnumerable<string?> parentIds,
            [NotNull, InstantHandle] Expression<Action<T>> methodCall,
            JobContinuationOptions options = JobContinuationOptions.OnlyOnSucceededState)
        {
            return ClientFactory().ContinueJobWithAny(parentIds, methodCall, options);
        }

        /// <summary>Creates a job that starts as soon as <em>one</em> parent has finished. See <see cref="BackgroundJobClientFanInExtensions"/>.</summary>
        public static string? ContinueJobWithAny<T>(
            [NotNull] IEnumerable<string?> parentIds,
            [NotNull, InstantHandle] Expression<Func<T, Task>> methodCall,
            JobContinuationOptions options = JobContinuationOptions.OnlyOnSucceededState)
        {
            return ClientFactory().ContinueJobWithAny(parentIds, methodCall, options);
        }
    }
}
