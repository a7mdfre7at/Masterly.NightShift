// This file is part of Hangfire. Copyright © 2021 Hangfire OÜ.
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

namespace Masterly.NightShift
{
    /// <summary>
    /// Creates background job clients that also implement <see cref="IBackgroundJobClientV2"/>.
    /// </summary>
    public interface IBackgroundJobClientFactoryV2 : IBackgroundJobClientFactory
    {
        /// <summary>Creates a background job client for the given storage.</summary>
        /// <param name="storage">The storage the client creates jobs in.</param>
        IBackgroundJobClientV2 GetClientV2(JobStorage storage);
    }

    /// <summary>
    /// Creates background job clients for a storage, with the job factory and state changer registered in the
    /// container. <c>AddNightShift</c> registers it; use it to get a client for a storage other than the default one.
    /// </summary>
    public interface IBackgroundJobClientFactory
    {
        /// <summary>Creates a background job client for the given storage.</summary>
        /// <param name="storage">The storage the client creates jobs in.</param>
        IBackgroundJobClient GetClient(JobStorage storage);
    }
}
