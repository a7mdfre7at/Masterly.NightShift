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
    /// Creates recurring job managers that can also pause, resume and reschedule recurring jobs.
    /// </summary>
    public interface IRecurringJobManagerFactoryV3 : IRecurringJobManagerFactoryV2
    {
        /// <summary>Creates a recurring job manager for the given storage.</summary>
        IRecurringJobManagerV3 GetManagerV3(JobStorage storage);
    }

    /// <summary>
    /// Creates recurring job managers that also implement <see cref="IRecurringJobManagerV2"/>.
    /// </summary>
    public interface IRecurringJobManagerFactoryV2 : IRecurringJobManagerFactory
    {
        /// <summary>Creates a recurring job manager for the given storage.</summary>
        /// <param name="storage">The storage the manager works on.</param>
        IRecurringJobManagerV2 GetManagerV2(JobStorage storage);
    }

    /// <summary>
    /// Creates recurring job managers for a storage, with the job factory and state changer registered in the
    /// container. <c>AddNightShift</c> registers it; use it to manage the recurring jobs of a storage other than
    /// the default one.
    /// </summary>
    public interface IRecurringJobManagerFactory
    {
        /// <summary>Creates a recurring job manager for the given storage.</summary>
        /// <param name="storage">The storage the manager works on.</param>
        IRecurringJobManager GetManager(JobStorage storage);
    }
}
