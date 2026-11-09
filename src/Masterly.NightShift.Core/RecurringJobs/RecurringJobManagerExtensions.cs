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

using System.Linq.Expressions;

namespace Masterly.NightShift;

/// <summary>
/// Overloads of <see cref="IRecurringJobManager"/> that create or update a recurring job from a method call
/// expression.
/// </summary>
#pragma warning disable CA1708 // False positive: the analyzer compares the types the compiler generates for extension blocks
public static class RecurringJobManagerExtensions
#pragma warning restore CA1708
{
    extension([NotNull] IRecurringJobManager manager)
    {
        /// <summary>
        /// Pauses a recurring job, see <see cref="IRecurringJobManagerV3.Pause"/>.
        /// </summary>
        /// <exception cref="NotSupportedException">The manager doesn't implement <see cref="IRecurringJobManagerV3"/>.</exception>
        public bool Pause([NotNull] string recurringJobId)
        {
            return AsV3(manager).Pause(recurringJobId);
        }

        /// <summary>
        /// Resumes a paused recurring job, see <see cref="IRecurringJobManagerV3.Resume"/>.
        /// </summary>
        /// <exception cref="NotSupportedException">The manager doesn't implement <see cref="IRecurringJobManagerV3"/>.</exception>
        public bool Resume([NotNull] string recurringJobId)
        {
            return AsV3(manager).Resume(recurringJobId);
        }

        /// <summary>
        /// Changes a recurring job's schedule, see <see cref="IRecurringJobManagerV3.UpdateSchedule"/>.
        /// </summary>
        /// <exception cref="NotSupportedException">The manager doesn't implement <see cref="IRecurringJobManagerV3"/>.</exception>
        public bool UpdateSchedule(
            [NotNull] string recurringJobId,
            [CanBeNull] string? cronExpression,
            [CanBeNull] TimeZoneInfo? timeZone = null,
            [CanBeNull] string? queue = null,
            MisfireHandlingMode? misfireHandling = null)
        {
            return AsV3(manager).UpdateSchedule(recurringJobId, cronExpression, timeZone, queue, misfireHandling);
        }
    }

    private static IRecurringJobManagerV3 AsV3(IRecurringJobManager manager)
    {
        ArgumentNullException.ThrowIfNull(manager);

        return manager as IRecurringJobManagerV3
            ?? throw new NotSupportedException($"The recurring job manager '{manager.GetType()}' doesn't implement {nameof(IRecurringJobManagerV3)}, so recurring jobs can't be paused, resumed or rescheduled through it.");
    }

    extension([NotNull] IRecurringJobManager manager)
    {
        public void AddOrUpdate(
            [NotNull] string recurringJobId,
            [NotNull] Job job,
            [NotNull] string cronExpression)
        {
            ArgumentNullException.ThrowIfNull(manager);

            manager.AddOrUpdate(recurringJobId, job, cronExpression, new RecurringJobOptions
            {
                TimeZone = TimeZoneInfo.Utc
            });
        }

        public void AddOrUpdate(
            [NotNull] string recurringJobId,
            [NotNull] Expression<Action> methodCall,
            [NotNull] Func<string> cronExpression)
        {
            AddOrUpdate(manager, recurringJobId, methodCall, cronExpression, new RecurringJobOptions());
        }

        public void AddOrUpdate(
            [NotNull] string recurringJobId,
            [NotNull] Expression<Action> methodCall,
            [NotNull] Func<string> cronExpression,
            [NotNull] RecurringJobOptions options)
        {
            ArgumentNullException.ThrowIfNull(cronExpression);
            AddOrUpdate(manager, recurringJobId, methodCall, cronExpression(), options);
        }

        public void AddOrUpdate(
            [NotNull] string recurringJobId,
            [NotNull] string queue,
            [NotNull] Expression<Action> methodCall,
            [NotNull] Func<string> cronExpression)
        {
            AddOrUpdate(manager, recurringJobId, queue, methodCall, cronExpression, new RecurringJobOptions());
        }

        public void AddOrUpdate(
            [NotNull] string recurringJobId,
            [NotNull] string queue,
            [NotNull] Expression<Action> methodCall,
            [NotNull] Func<string> cronExpression,
            [NotNull] RecurringJobOptions options)
        {
            ArgumentNullException.ThrowIfNull(queue);
            ArgumentNullException.ThrowIfNull(cronExpression);

            AddOrUpdate(manager, recurringJobId, queue, methodCall, cronExpression(), options);
        }

        public void AddOrUpdate<T>(
            [NotNull] string recurringJobId,
            [NotNull] Expression<Action<T>> methodCall,
            [NotNull] Func<string> cronExpression)
        {
            AddOrUpdate(manager, recurringJobId, methodCall, cronExpression, new RecurringJobOptions());
        }

        public void AddOrUpdate<T>(
            [NotNull] string recurringJobId,
            [NotNull] Expression<Action<T>> methodCall,
            [NotNull] Func<string> cronExpression,
            [NotNull] RecurringJobOptions options)
        {
            ArgumentNullException.ThrowIfNull(cronExpression);
            AddOrUpdate(manager, recurringJobId, methodCall, cronExpression(), options);
        }

        public void AddOrUpdate<T>(
            [NotNull] string recurringJobId,
            [NotNull] string queue,
            [NotNull] Expression<Action<T>> methodCall,
            [NotNull] Func<string> cronExpression)
        {
            AddOrUpdate(manager, recurringJobId, queue, methodCall, cronExpression, new RecurringJobOptions());
        }

        public void AddOrUpdate<T>(
            [NotNull] string recurringJobId,
            [NotNull] string queue,
            [NotNull] Expression<Action<T>> methodCall,
            [NotNull] Func<string> cronExpression,
            [NotNull] RecurringJobOptions options)
        {
            ArgumentNullException.ThrowIfNull(queue);
            ArgumentNullException.ThrowIfNull(cronExpression);

            AddOrUpdate(manager, recurringJobId, queue, methodCall, cronExpression(), options);
        }

        public void AddOrUpdate(
            [NotNull] string recurringJobId,
            [NotNull] Expression<Action> methodCall,
            [NotNull] string cronExpression)
        {
            AddOrUpdate(manager, recurringJobId, methodCall, cronExpression, new RecurringJobOptions());
        }

        public void AddOrUpdate(
            [NotNull] string recurringJobId,
            [NotNull] Expression<Action> methodCall,
            [NotNull] string cronExpression,
            [NotNull] RecurringJobOptions options)
        {
            ArgumentNullException.ThrowIfNull(manager);

            var job = Job.FromExpression(methodCall);
            manager.AddOrUpdate(recurringJobId, job, cronExpression, options);
        }

        public void AddOrUpdate(
            [NotNull] string recurringJobId,
            [NotNull] string queue,
            [NotNull] Expression<Action> methodCall,
            [NotNull] string cronExpression)
        {
            AddOrUpdate(manager, recurringJobId, queue, methodCall, cronExpression, new RecurringJobOptions());
        }

        public void AddOrUpdate(
            [NotNull] string recurringJobId,
            [NotNull] string queue,
            [NotNull] Expression<Action> methodCall,
            [NotNull] string cronExpression,
            [NotNull] RecurringJobOptions options)
        {
            ArgumentNullException.ThrowIfNull(manager);
            ArgumentNullException.ThrowIfNull(queue);

            var job = Job.FromExpression(methodCall, queue);
            manager.AddOrUpdate(recurringJobId, job, cronExpression, options);
        }

        public void AddOrUpdate<T>(
            [NotNull] string recurringJobId,
            [NotNull] Expression<Action<T>> methodCall,
            [NotNull] string cronExpression)
        {
            AddOrUpdate(manager, recurringJobId, methodCall, cronExpression, new RecurringJobOptions());
        }

        public void AddOrUpdate<T>(
            [NotNull] string recurringJobId,
            [NotNull] Expression<Action<T>> methodCall,
            [NotNull] string cronExpression,
            [NotNull] RecurringJobOptions options)
        {
            ArgumentNullException.ThrowIfNull(manager);

            var job = Job.FromExpression(methodCall);
            manager.AddOrUpdate(recurringJobId, job, cronExpression, options);
        }

        public void AddOrUpdate<T>(
            [NotNull] string recurringJobId,
            [NotNull] string queue,
            [NotNull] Expression<Action<T>> methodCall,
            [NotNull] string cronExpression)
        {
            AddOrUpdate(manager, recurringJobId, queue, methodCall, cronExpression, new RecurringJobOptions());
        }

        public void AddOrUpdate<T>(
            [NotNull] string recurringJobId,
            [NotNull] string queue,
            [NotNull] Expression<Action<T>> methodCall,
            [NotNull] string cronExpression,
            [NotNull] RecurringJobOptions options)
        {
            ArgumentNullException.ThrowIfNull(manager);
            ArgumentNullException.ThrowIfNull(queue);

            var job = Job.FromExpression(methodCall, queue);
            manager.AddOrUpdate(recurringJobId, job, cronExpression, options);
        }

        public void AddOrUpdate(
            [NotNull] string recurringJobId,
            [NotNull] Expression<Func<Task>> methodCall,
            [NotNull] Func<string> cronExpression)
        {
            AddOrUpdate(manager, recurringJobId, methodCall, cronExpression, new RecurringJobOptions());
        }

        public void AddOrUpdate(
            [NotNull] string recurringJobId,
            [NotNull] Expression<Func<Task>> methodCall,
            [NotNull] Func<string> cronExpression,
            [NotNull] RecurringJobOptions options)
        {
            ArgumentNullException.ThrowIfNull(cronExpression);
            AddOrUpdate(manager, recurringJobId, methodCall, cronExpression(), options);
        }

        public void AddOrUpdate(
            [NotNull] string recurringJobId,
            [NotNull] string queue,
            [NotNull] Expression<Func<Task>> methodCall,
            [NotNull] Func<string> cronExpression)
        {
            AddOrUpdate(manager, recurringJobId, queue, methodCall, cronExpression, new RecurringJobOptions());
        }

        public void AddOrUpdate(
            [NotNull] string recurringJobId,
            [NotNull] string queue,
            [NotNull] Expression<Func<Task>> methodCall,
            [NotNull] Func<string> cronExpression,
            [NotNull] RecurringJobOptions options)
        {
            ArgumentNullException.ThrowIfNull(queue);
            ArgumentNullException.ThrowIfNull(cronExpression);

            AddOrUpdate(manager, recurringJobId, queue, methodCall, cronExpression(), options);
        }

        public void AddOrUpdate<T>(
            [NotNull] string recurringJobId,
            [NotNull] Expression<Func<T, Task>> methodCall,
            [NotNull] Func<string> cronExpression)
        {
            AddOrUpdate(manager, recurringJobId, methodCall, cronExpression, new RecurringJobOptions());
        }

        public void AddOrUpdate<T>(
            [NotNull] string recurringJobId,
            [NotNull] Expression<Func<T, Task>> methodCall,
            [NotNull] Func<string> cronExpression,
            [NotNull] RecurringJobOptions options)
        {
            ArgumentNullException.ThrowIfNull(cronExpression);
            AddOrUpdate(manager, recurringJobId, methodCall, cronExpression(), options);
        }

        public void AddOrUpdate<T>(
            [NotNull] string recurringJobId,
            [NotNull] string queue,
            [NotNull] Expression<Func<T, Task>> methodCall,
            [NotNull] Func<string> cronExpression)
        {
            AddOrUpdate(manager, recurringJobId, queue, methodCall, cronExpression, new RecurringJobOptions());
        }

        public void AddOrUpdate<T>(
            [NotNull] string recurringJobId,
            [NotNull] string queue,
            [NotNull] Expression<Func<T, Task>> methodCall,
            [NotNull] Func<string> cronExpression,
            [NotNull] RecurringJobOptions options)
        {
            ArgumentNullException.ThrowIfNull(queue);
            ArgumentNullException.ThrowIfNull(cronExpression);

            AddOrUpdate(manager, recurringJobId, queue, methodCall, cronExpression(), options);
        }

        public void AddOrUpdate(
            [NotNull] string recurringJobId,
            [NotNull] Expression<Func<Task>> methodCall,
            [NotNull] string cronExpression)
        {
            AddOrUpdate(manager, recurringJobId, methodCall, cronExpression, new RecurringJobOptions());
        }

        public void AddOrUpdate(
            [NotNull] string recurringJobId,
            [NotNull] Expression<Func<Task>> methodCall,
            [NotNull] string cronExpression,
            [NotNull] RecurringJobOptions options)
        {
            ArgumentNullException.ThrowIfNull(manager);

            var job = Job.FromExpression(methodCall);
            manager.AddOrUpdate(recurringJobId, job, cronExpression, options);
        }

        public void AddOrUpdate(
            [NotNull] string recurringJobId,
            [NotNull] string queue,
            [NotNull] Expression<Func<Task>> methodCall,
            [NotNull] string cronExpression)
        {
            AddOrUpdate(manager, recurringJobId, queue, methodCall, cronExpression, new RecurringJobOptions());
        }

        public void AddOrUpdate(
            [NotNull] string recurringJobId,
            [NotNull] string queue,
            [NotNull] Expression<Func<Task>> methodCall,
            [NotNull] string cronExpression,
            [NotNull] RecurringJobOptions options)
        {
            ArgumentNullException.ThrowIfNull(manager);
            ArgumentNullException.ThrowIfNull(queue);

            var job = Job.FromExpression(methodCall, queue);
            manager.AddOrUpdate(recurringJobId, job, cronExpression, options);
        }

        public void AddOrUpdate<T>(
            [NotNull] string recurringJobId,
            [NotNull] Expression<Func<T, Task>> methodCall,
            [NotNull] string cronExpression)
        {
            AddOrUpdate(manager, recurringJobId, methodCall, cronExpression, new RecurringJobOptions());
        }

        public void AddOrUpdate<T>(
            [NotNull] string recurringJobId,
            [NotNull] Expression<Func<T, Task>> methodCall,
            [NotNull] string cronExpression,
            [NotNull] RecurringJobOptions options)
        {
            ArgumentNullException.ThrowIfNull(manager);

            var job = Job.FromExpression(methodCall);
            manager.AddOrUpdate(recurringJobId, job, cronExpression, options);
        }

        public void AddOrUpdate<T>(
            [NotNull] string recurringJobId,
            [NotNull] string queue,
            [NotNull] Expression<Func<T, Task>> methodCall,
            [NotNull] string cronExpression)
        {
            AddOrUpdate(manager, recurringJobId, queue, methodCall, cronExpression, new RecurringJobOptions());
        }

        public void AddOrUpdate<T>(
            [NotNull] string recurringJobId,
            [NotNull] string queue,
            [NotNull] Expression<Func<T, Task>> methodCall,
            [NotNull] string cronExpression,
            [NotNull] RecurringJobOptions options)
        {
            ArgumentNullException.ThrowIfNull(manager);
            ArgumentNullException.ThrowIfNull(queue);

            var job = Job.FromExpression(methodCall, queue);
            manager.AddOrUpdate(recurringJobId, job, cronExpression, options);
        }
    }
}