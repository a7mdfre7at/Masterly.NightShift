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

namespace Masterly.NightShift.Server;

/// <summary>
/// Wraps an exception thrown while a job was performed, with the job's identifier.
/// </summary>
[Serializable]
public class JobPerformanceException(string message, Exception innerException, string jobId) : Exception(message, innerException)
{
    public JobPerformanceException(string message, Exception innerException)
        : this(message, innerException, null)
    {
    }

    /// <summary>
    /// The Background Job Id of the Job instance this exception has been raised for
    /// </summary>
    public string JobId { get; private set; } = jobId;
}
