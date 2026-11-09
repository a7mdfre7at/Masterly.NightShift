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

using System.Linq.Expressions;

namespace Masterly.NightShift;

/// <summary>
/// Static methods that create, update, trigger and remove recurring jobs in <see cref="JobStorage.Current"/>. In
/// applications with dependency injection, prefer <see cref="IRecurringJobManager"/>.
/// </summary>
public static class RecurringJob
{
    private static readonly Func<RecurringJobManager> DefaultFactory = static () => new RecurringJobManager();
    private static readonly NightShiftLock ManagerFactoryLock = new();

    internal static Func<RecurringJobManager> ManagerFactory
    {
        get
        {
            lock (ManagerFactoryLock)
            {
                return field ?? DefaultFactory;
            }
        }
        set
        {
            lock (ManagerFactoryLock)
            {
                field = value;
            }
        }
    }

    /// <summary>Pauses a recurring job, see <see cref="IRecurringJobManagerV3.Pause"/>.</summary>
    public static bool Pause([NotNull] string recurringJobId)
    {
        return ManagerFactory().Pause(recurringJobId);
    }

    /// <summary>Resumes a paused recurring job, see <see cref="IRecurringJobManagerV3.Resume"/>.</summary>
    public static bool Resume([NotNull] string recurringJobId)
    {
        return ManagerFactory().Resume(recurringJobId);
    }

    /// <summary>Changes a recurring job's schedule, see <see cref="IRecurringJobManagerV3.UpdateSchedule"/>.</summary>
    public static bool UpdateSchedule(
        [NotNull] string recurringJobId,
        [CanBeNull] string? cronExpression,
        [CanBeNull] TimeZoneInfo? timeZone = null,
        [CanBeNull] string? queue = null,
        MisfireHandlingMode? misfireHandling = null)
    {
        return ManagerFactory().UpdateSchedule(recurringJobId, cronExpression, timeZone, queue, misfireHandling);
    }

    public static void AddOrUpdate(
        [NotNull] string recurringJobId,
        [NotNull, InstantHandle] Expression<Action> methodCall,
        [NotNull] Func<string> cronExpression)
    {
        AddOrUpdate(recurringJobId, methodCall, cronExpression, new RecurringJobOptions());
    }

    public static void AddOrUpdate(
        [NotNull] string recurringJobId,
        [NotNull, InstantHandle] Expression<Action> methodCall,
        [NotNull] Func<string> cronExpression,
        [NotNull] RecurringJobOptions options)
    {
        ArgumentNullException.ThrowIfNull(cronExpression);
        AddOrUpdate(recurringJobId, methodCall, cronExpression(), options);
    }

    public static void AddOrUpdate(
        [NotNull] string recurringJobId,
        [NotNull] string queue,
        [NotNull, InstantHandle] Expression<Action> methodCall,
        [NotNull] Func<string> cronExpression)
    {
        AddOrUpdate(recurringJobId, queue, methodCall, cronExpression, new RecurringJobOptions());
    }

    public static void AddOrUpdate(
        [NotNull] string recurringJobId,
        [NotNull] string queue,
        [NotNull, InstantHandle] Expression<Action> methodCall,
        [NotNull] Func<string> cronExpression,
        [NotNull] RecurringJobOptions options)
    {
        ArgumentNullException.ThrowIfNull(cronExpression);
        AddOrUpdate(recurringJobId, queue, methodCall, cronExpression(), options);
    }

    public static void AddOrUpdate<T>(
        [NotNull] string recurringJobId,
        [NotNull, InstantHandle] Expression<Action<T>> methodCall,
        [NotNull] Func<string> cronExpression)
    {
        AddOrUpdate(recurringJobId, methodCall, cronExpression, new RecurringJobOptions());
    }

    public static void AddOrUpdate<T>(
        [NotNull] string recurringJobId,
        [NotNull, InstantHandle] Expression<Action<T>> methodCall,
        [NotNull] Func<string> cronExpression,
        [NotNull] RecurringJobOptions options)
    {
        ArgumentNullException.ThrowIfNull(cronExpression);
        AddOrUpdate(recurringJobId, methodCall, cronExpression(), options);
    }

    public static void AddOrUpdate<T>(
        [NotNull] string recurringJobId,
        [NotNull] string queue,
        [NotNull, InstantHandle] Expression<Action<T>> methodCall,
        [NotNull] Func<string> cronExpression)
    {
        AddOrUpdate(recurringJobId, queue, methodCall, cronExpression, new RecurringJobOptions());
    }

    public static void AddOrUpdate<T>(
        [NotNull] string recurringJobId,
        [NotNull] string queue,
        [NotNull, InstantHandle] Expression<Action<T>> methodCall,
        [NotNull] Func<string> cronExpression,
        [NotNull] RecurringJobOptions options)
    {
        ArgumentNullException.ThrowIfNull(cronExpression);
        AddOrUpdate(recurringJobId, queue, methodCall, cronExpression(), options);
    }

    public static void AddOrUpdate(
        [NotNull] string recurringJobId,
        [NotNull, InstantHandle] Expression<Action> methodCall,
        [NotNull] string cronExpression)
    {
        AddOrUpdate(recurringJobId, methodCall, cronExpression, new RecurringJobOptions());
    }

    public static void AddOrUpdate(
        [NotNull] string recurringJobId,
        [NotNull, InstantHandle] Expression<Action> methodCall,
        [NotNull] string cronExpression,
        [NotNull] RecurringJobOptions options)
    {
        var job = Job.FromExpression(methodCall);
        ManagerFactory().AddOrUpdate(recurringJobId, job, cronExpression, options);
    }

    public static void AddOrUpdate(
        [NotNull] string recurringJobId,
        [NotNull] string queue,
        [NotNull, InstantHandle] Expression<Action> methodCall,
        [NotNull] string cronExpression)
    {
        AddOrUpdate(recurringJobId, queue, methodCall, cronExpression, new RecurringJobOptions());
    }

    public static void AddOrUpdate(
        [NotNull] string recurringJobId,
        [NotNull] string queue,
        [NotNull, InstantHandle] Expression<Action> methodCall,
        [NotNull] string cronExpression,
        [NotNull] RecurringJobOptions options)
    {
        ArgumentNullException.ThrowIfNull(queue);

        var job = Job.FromExpression(methodCall, queue);
        ManagerFactory().AddOrUpdate(recurringJobId, job, cronExpression, options);
    }

    public static void AddOrUpdate<T>(
        [NotNull] string recurringJobId,
        [NotNull, InstantHandle] Expression<Action<T>> methodCall,
        [NotNull] string cronExpression)
    {
        AddOrUpdate(recurringJobId, methodCall, cronExpression, new RecurringJobOptions());
    }

    public static void AddOrUpdate<T>(
        [NotNull] string recurringJobId,
        [NotNull, InstantHandle] Expression<Action<T>> methodCall,
        [NotNull] string cronExpression,
        [NotNull] RecurringJobOptions options)
    {
        var job = Job.FromExpression(methodCall);
        ManagerFactory().AddOrUpdate(recurringJobId, job, cronExpression, options);
    }

    public static void AddOrUpdate<T>(
        [NotNull] string recurringJobId,
        [NotNull] string queue,
        [NotNull, InstantHandle] Expression<Action<T>> methodCall,
        [NotNull] string cronExpression)
    {
        AddOrUpdate(recurringJobId, queue, methodCall, cronExpression, new RecurringJobOptions());
    }

    public static void AddOrUpdate<T>(
        [NotNull] string recurringJobId,
        [NotNull] string queue,
        [NotNull, InstantHandle] Expression<Action<T>> methodCall,
        [NotNull] string cronExpression,
        [NotNull] RecurringJobOptions options)
    {
        ArgumentNullException.ThrowIfNull(queue);

        var job = Job.FromExpression(methodCall, queue);
        ManagerFactory().AddOrUpdate(recurringJobId, job, cronExpression, options);
    }

    public static void AddOrUpdate(
        [NotNull] string recurringJobId,
        [NotNull, InstantHandle] Expression<Func<Task>> methodCall,
        [NotNull] Func<string> cronExpression)
    {
        AddOrUpdate(recurringJobId, methodCall, cronExpression, new RecurringJobOptions());
    }

    public static void AddOrUpdate(
        [NotNull] string recurringJobId,
        [NotNull, InstantHandle] Expression<Func<Task>> methodCall,
        [NotNull] Func<string> cronExpression,
        [NotNull] RecurringJobOptions options)
    {
        ArgumentNullException.ThrowIfNull(cronExpression);
        AddOrUpdate(recurringJobId, methodCall, cronExpression(), options);
    }

    public static void AddOrUpdate(
        [NotNull] string recurringJobId,
        [NotNull] string queue,
        [NotNull, InstantHandle] Expression<Func<Task>> methodCall,
        [NotNull] Func<string> cronExpression)
    {
        AddOrUpdate(recurringJobId, queue, methodCall, cronExpression, new RecurringJobOptions());
    }

    public static void AddOrUpdate(
        [NotNull] string recurringJobId,
        [NotNull] string queue,
        [NotNull, InstantHandle] Expression<Func<Task>> methodCall,
        [NotNull] Func<string> cronExpression,
        [NotNull] RecurringJobOptions options)
    {
        ArgumentNullException.ThrowIfNull(cronExpression);
        AddOrUpdate(recurringJobId, queue, methodCall, cronExpression(), options);
    }

    public static void AddOrUpdate<T>(
        [NotNull] string recurringJobId,
        [NotNull, InstantHandle] Expression<Func<T, Task>> methodCall,
        [NotNull] Func<string> cronExpression)
    {
        AddOrUpdate(recurringJobId, methodCall, cronExpression, new RecurringJobOptions());
    }

    public static void AddOrUpdate<T>(
        [NotNull] string recurringJobId,
        [NotNull, InstantHandle] Expression<Func<T, Task>> methodCall,
        [NotNull] Func<string> cronExpression,
        [NotNull] RecurringJobOptions options)
    {
        ArgumentNullException.ThrowIfNull(cronExpression);
        AddOrUpdate(recurringJobId, methodCall, cronExpression(), options);
    }

    public static void AddOrUpdate<T>(
        [NotNull] string recurringJobId,
        [NotNull] string queue,
        [NotNull, InstantHandle] Expression<Func<T, Task>> methodCall,
        [NotNull] Func<string> cronExpression)
    {
        AddOrUpdate(recurringJobId, queue, methodCall, cronExpression, new RecurringJobOptions());
    }

    public static void AddOrUpdate<T>(
        [NotNull] string recurringJobId,
        [NotNull] string queue,
        [NotNull, InstantHandle] Expression<Func<T, Task>> methodCall,
        [NotNull] Func<string> cronExpression,
        [NotNull] RecurringJobOptions options)
    {
        ArgumentNullException.ThrowIfNull(cronExpression);
        AddOrUpdate(recurringJobId, queue, methodCall, cronExpression(), options);
    }

    public static void AddOrUpdate(
        [NotNull] string recurringJobId,
        [NotNull, InstantHandle] Expression<Func<Task>> methodCall,
        [NotNull] string cronExpression)
    {
        AddOrUpdate(recurringJobId, methodCall, cronExpression, new RecurringJobOptions());
    }

    public static void AddOrUpdate(
        [NotNull] string recurringJobId,
        [NotNull, InstantHandle] Expression<Func<Task>> methodCall,
        [NotNull] string cronExpression,
        [NotNull] RecurringJobOptions options)
    {
        var job = Job.FromExpression(methodCall);
        ManagerFactory().AddOrUpdate(recurringJobId, job, cronExpression, options);
    }

    public static void AddOrUpdate(
        [NotNull] string recurringJobId,
        [NotNull] string queue,
        [NotNull, InstantHandle] Expression<Func<Task>> methodCall,
        [NotNull] string cronExpression)
    {
        AddOrUpdate(recurringJobId, queue, methodCall, cronExpression, new RecurringJobOptions());
    }

    public static void AddOrUpdate(
        [NotNull] string recurringJobId,
        [NotNull] string queue,
        [NotNull, InstantHandle] Expression<Func<Task>> methodCall,
        [NotNull] string cronExpression,
        [NotNull] RecurringJobOptions options)
    {
        ArgumentNullException.ThrowIfNull(queue);

        var job = Job.FromExpression(methodCall, queue);
        ManagerFactory().AddOrUpdate(recurringJobId, job, cronExpression, options);
    }

    public static void AddOrUpdate<T>(
        [NotNull] string recurringJobId,
        [NotNull, InstantHandle] Expression<Func<T, Task>> methodCall,
        [NotNull] string cronExpression)
    {
        AddOrUpdate(recurringJobId, methodCall, cronExpression, new RecurringJobOptions());
    }

    public static void AddOrUpdate<T>(
        [NotNull] string recurringJobId,
        [NotNull, InstantHandle] Expression<Func<T, Task>> methodCall,
        [NotNull] string cronExpression,
        [NotNull] RecurringJobOptions options)
    {
        var job = Job.FromExpression(methodCall);
        ManagerFactory().AddOrUpdate(recurringJobId, job, cronExpression, options);
    }

    public static void AddOrUpdate<T>(
        [NotNull] string recurringJobId,
        [NotNull] string queue,
        [NotNull, InstantHandle] Expression<Func<T, Task>> methodCall,
        [NotNull] string cronExpression)
    {
        AddOrUpdate(recurringJobId, queue, methodCall, cronExpression, new RecurringJobOptions());
    }

    public static void AddOrUpdate<T>(
        [NotNull] string recurringJobId,
        [NotNull] string queue,
        [NotNull, InstantHandle] Expression<Func<T, Task>> methodCall,
        [NotNull] string cronExpression,
        [NotNull] RecurringJobOptions options)
    {
        ArgumentNullException.ThrowIfNull(queue);

        var job = Job.FromExpression(methodCall, queue);
        ManagerFactory().AddOrUpdate(recurringJobId, job, cronExpression, options);
    }

    public static void RemoveIfExists([NotNull] string recurringJobId)
    {
        ManagerFactory().RemoveIfExists(recurringJobId);
    }

    public static string? TriggerJob([NotNull] string recurringJobId)
    {
        return ManagerFactory().TriggerJob(recurringJobId);
    }
}
