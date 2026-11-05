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
using System.Runtime.Serialization;

namespace Masterly.NightShift.Server
{
    [Serializable]
    public class JobPerformanceException : Exception
    {
        public JobPerformanceException(string message, Exception innerException)
            : this(message, innerException, null)
        {
        }
    
        public JobPerformanceException(string message, Exception innerException, string jobId)
            : base(message, innerException)
        {
            JobId = jobId;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="JobPerformanceException"/> class
        /// with serialized data.
        /// </summary>
        /// <param name="info">The <see cref="SerializationInfo"/> that holds the serialized object data about the exception being thrown.</param>
        /// <param name="context">The <see cref="StreamingContext"/> that contains contextual information about the source or destination.</param>
#if NET8_0_OR_GREATER
        [Obsolete("Formatter-based serialization is obsolete; this constructor only exists for compatibility.", DiagnosticId = "SYSLIB0051")]
#endif
        protected JobPerformanceException(SerializationInfo info, StreamingContext context)
            : base(info, context)
        {
        }

        /// <summary>
        /// The Background Job Id of the Job instance this exception has been raised for
        /// </summary>
        public string JobId { get; private set; }
    }
}
