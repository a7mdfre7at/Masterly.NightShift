#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using Masterly.NightShift.Annotations;
using Masterly.NightShift.Server;

namespace Masterly.NightShift
{
    /// <summary>
    /// Interceptors applied to every background job, in registration order, before the ones declared with
    /// attributes on the job's type and method.
    /// </summary>
    public static class GlobalJobInterceptors
    {
        private static readonly object SyncRoot = new();
        private static IJobInterceptor[] _interceptors = [];

        /// <summary>Gets a snapshot of the registered interceptors.</summary>
        [NotNull]
        public static IReadOnlyList<IJobInterceptor> Interceptors => _interceptors;

        /// <summary>Adds an interceptor that runs around every job.</summary>
        /// <param name="interceptor">The interceptor.</param>
        public static void Add([NotNull] IJobInterceptor interceptor)
        {
            if (interceptor == null) throw new ArgumentNullException(nameof(interceptor));

            lock (SyncRoot)
            {
                var updated = new IJobInterceptor[_interceptors.Length + 1];
                Array.Copy(_interceptors, updated, _interceptors.Length);
                updated[_interceptors.Length] = interceptor;
                _interceptors = updated;
            }
        }

        /// <summary>Removes a previously added interceptor.</summary>
        /// <param name="interceptor">The interceptor.</param>
        /// <returns><c>true</c> when it was registered.</returns>
        public static bool Remove([NotNull] IJobInterceptor interceptor)
        {
            if (interceptor == null) throw new ArgumentNullException(nameof(interceptor));

            lock (SyncRoot)
            {
                if (!_interceptors.Contains(interceptor)) return false;

                _interceptors = _interceptors.Where(x => !ReferenceEquals(x, interceptor)).ToArray();
                return true;
            }
        }

        /// <summary>Removes every registered interceptor.</summary>
        public static void Clear()
        {
            lock (SyncRoot)
            {
                _interceptors = [];
            }
        }
    }
}
