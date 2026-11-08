// This file is part of Hangfire. Copyright © 2016 Hangfire OÜ.
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

using Masterly.NightShift.Annotations;
using Masterly.NightShift.Common;

namespace Masterly.NightShift
{
    /// <summary>
    /// A recurring job manager that also exposes its storage and can trigger a recurring job and return the identifier
    /// of the job it created.
    /// </summary>
    public interface IRecurringJobManagerV2 : IRecurringJobManager
    {
        [NotNull]
        JobStorage Storage { get; }

        [CanBeNull]
        string? TriggerJob([NotNull] string recurringJobId);
    }

    /// <summary>
    /// Creates, updates, triggers and removes recurring jobs in a storage. <see cref="RecurringJobManager"/> implements
    /// it.
    /// </summary>
    public interface IRecurringJobManager
    {
        void AddOrUpdate(
            [NotNull] string recurringJobId, 
            [NotNull] Job job, 
            [NotNull] string cronExpression, 
            [NotNull] RecurringJobOptions options);

        void Trigger([NotNull] string recurringJobId);
        void RemoveIfExists([NotNull] string recurringJobId);
    }
}