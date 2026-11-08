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

#nullable enable

using System;

namespace Masterly.NightShift.Storage
{
    /// <summary>
    /// A job a worker took from a queue with <see cref="IStorageConnection.FetchNextJob"/>. Other workers don't
    /// get it until it is requeued. When the job is done, call <see cref="RemoveFromQueue"/>; to give it back,
    /// call <see cref="Requeue"/>. Disposing it without calling either gives it back as well in the SQL Server storage.
    /// </summary>
    public interface IFetchedJob : IDisposable
    {
        /// <summary>Gets the identifier of the fetched job.</summary>
        string JobId { get; }

        /// <summary>Removes the job from its queue for good, once it has been processed.</summary>
        void RemoveFromQueue();

        /// <summary>Puts the job back in its queue, so a worker can fetch it again.</summary>
        void Requeue();
    }
}