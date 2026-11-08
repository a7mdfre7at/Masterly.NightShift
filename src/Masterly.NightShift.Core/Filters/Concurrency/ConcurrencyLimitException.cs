#nullable enable

using System;

namespace Masterly.NightShift
{
    /// <summary>
    /// Thrown when a job configured with <see cref="ConcurrencyAction.Wait"/> doesn't get a slot before
    /// the wait timeout expires. The job fails and the usual retry rules apply.
    /// </summary>
    public class ConcurrencyLimitException : Exception
    {
        /// <summary>Initializes the exception.</summary>
        /// <param name="resource">The throttled resource.</param>
        /// <param name="message">The message.</param>
        public ConcurrencyLimitException(string resource, string message)
            : base(message)
        {
            Resource = resource;
        }

        /// <summary>Gets the resource whose limit was reached.</summary>
        public string Resource { get; }
    }
}
