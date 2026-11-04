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

namespace Hangfire
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
