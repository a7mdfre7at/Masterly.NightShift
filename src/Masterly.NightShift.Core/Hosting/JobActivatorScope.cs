// This file is part of Hangfire. Copyright © 2015 Hangfire OÜ.
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
using System.Threading;
using Masterly.NightShift.Annotations;

namespace Masterly.NightShift
{
    /// <summary>
    /// The scope a job's instance is resolved from, begun by <see cref="JobActivator.BeginScope(JobActivatorContext)"/>
    /// before the job runs and disposed after it, so per-job services can be released.
    /// </summary>
    public abstract class JobActivatorScope : IDisposable
    {
        // ReSharper disable once InconsistentNaming
        private static readonly ThreadLocal<JobActivatorScope?> _current
            = new ThreadLocal<JobActivatorScope?>(trackAllValues: false);

        /// <summary>
        /// Initializes the scope and makes it <see cref="Current"/> on this thread.
        /// </summary>
        protected JobActivatorScope()
        {
            _current.Value = this;
        }

        /// <summary>
        /// Gets the scope most recently created on this thread and not yet disposed, or <c>null</c>.
        /// </summary>
        public static JobActivatorScope? Current => _current.Value;

        /// <summary>
        /// Not used.
        /// </summary>
        [Obsolete("This property was never implemented and has no effect. Kept for code ported from Hangfire.")]
        public object? InnerScope { get; set; }

        /// <summary>
        /// Returns an instance of <paramref name="type"/> for the job to run on.
        /// </summary>
        /// <param name="type">The type that declares the job method.</param>
        /// <returns>The instance; the server fails the job when it is <c>null</c>.</returns>
        public abstract object? Resolve([NotNull] Type type);

        /// <summary>
        /// Releases what the scope created. Called by <see cref="Dispose"/>; the default implementation does nothing.
        /// </summary>
        public virtual void DisposeScope()
        {
        }

        /// <summary>
        /// Calls <see cref="DisposeScope"/> and clears <see cref="Current"/> on this thread.
        /// </summary>
        public void Dispose()
        {
            try
            {
                DisposeScope();
            }
            finally
            {
                _current.Value = null;
            }

            GC.SuppressFinalize(this);
        }
    }
}