using System.Linq.Expressions;

namespace Masterly.NightShift;

public partial class BackgroundJob
{
    /// <summary>Creates a fire-and-forget job with options. See <see cref="BackgroundJobClientScheduleOptionsExtensions"/>.</summary>
    public static string? Enqueue([NotNull, InstantHandle] Expression<Action> methodCall, [NotNull] ScheduleOptions options)
    {
        return ClientFactory().Enqueue(methodCall, options);
    }

    /// <summary>Creates a fire-and-forget job with options. See <see cref="BackgroundJobClientScheduleOptionsExtensions"/>.</summary>
    public static string? Enqueue([NotNull, InstantHandle] Expression<Func<Task>> methodCall, [NotNull] ScheduleOptions options)
    {
        return ClientFactory().Enqueue(methodCall, options);
    }

    /// <summary>Creates a fire-and-forget job with options. See <see cref="BackgroundJobClientScheduleOptionsExtensions"/>.</summary>
    public static string? Enqueue<T>([NotNull, InstantHandle] Expression<Action<T>> methodCall, [NotNull] ScheduleOptions options)
    {
        return ClientFactory().Enqueue(methodCall, options);
    }

    /// <summary>Creates a fire-and-forget job with options. See <see cref="BackgroundJobClientScheduleOptionsExtensions"/>.</summary>
    public static string? Enqueue<T>([NotNull, InstantHandle] Expression<Func<T, Task>> methodCall, [NotNull] ScheduleOptions options)
    {
        return ClientFactory().Enqueue(methodCall, options);
    }

    /// <summary>Creates a job that runs after a delay, with options. See <see cref="BackgroundJobClientScheduleOptionsExtensions"/>.</summary>
    public static string? Schedule([NotNull, InstantHandle] Expression<Action> methodCall, TimeSpan delay, [NotNull] ScheduleOptions options)
    {
        return ClientFactory().Schedule(methodCall, delay, options);
    }

    /// <summary>Creates a job that runs after a delay, with options. See <see cref="BackgroundJobClientScheduleOptionsExtensions"/>.</summary>
    public static string? Schedule([NotNull, InstantHandle] Expression<Func<Task>> methodCall, TimeSpan delay, [NotNull] ScheduleOptions options)
    {
        return ClientFactory().Schedule(methodCall, delay, options);
    }

    /// <summary>Creates a job that runs after a delay, with options. See <see cref="BackgroundJobClientScheduleOptionsExtensions"/>.</summary>
    public static string? Schedule<T>([NotNull, InstantHandle] Expression<Action<T>> methodCall, TimeSpan delay, [NotNull] ScheduleOptions options)
    {
        return ClientFactory().Schedule(methodCall, delay, options);
    }

    /// <summary>Creates a job that runs after a delay, with options. See <see cref="BackgroundJobClientScheduleOptionsExtensions"/>.</summary>
    public static string? Schedule<T>([NotNull, InstantHandle] Expression<Func<T, Task>> methodCall, TimeSpan delay, [NotNull] ScheduleOptions options)
    {
        return ClientFactory().Schedule(methodCall, delay, options);
    }

    /// <summary>Creates a job due at a given moment, with options. See <see cref="BackgroundJobClientScheduleOptionsExtensions"/>.</summary>
    public static string? Schedule([NotNull, InstantHandle] Expression<Action> methodCall, DateTimeOffset enqueueAt, [NotNull] ScheduleOptions options)
    {
        return ClientFactory().Schedule(methodCall, enqueueAt, options);
    }

    /// <summary>Creates a job due at a given moment, with options. See <see cref="BackgroundJobClientScheduleOptionsExtensions"/>.</summary>
    public static string? Schedule([NotNull, InstantHandle] Expression<Func<Task>> methodCall, DateTimeOffset enqueueAt, [NotNull] ScheduleOptions options)
    {
        return ClientFactory().Schedule(methodCall, enqueueAt, options);
    }

    /// <summary>Creates a job due at a given moment, with options. See <see cref="BackgroundJobClientScheduleOptionsExtensions"/>.</summary>
    public static string? Schedule<T>([NotNull, InstantHandle] Expression<Action<T>> methodCall, DateTimeOffset enqueueAt, [NotNull] ScheduleOptions options)
    {
        return ClientFactory().Schedule(methodCall, enqueueAt, options);
    }

    /// <summary>Creates a job due at a given moment, with options. See <see cref="BackgroundJobClientScheduleOptionsExtensions"/>.</summary>
    public static string? Schedule<T>([NotNull, InstantHandle] Expression<Func<T, Task>> methodCall, DateTimeOffset enqueueAt, [NotNull] ScheduleOptions options)
    {
        return ClientFactory().Schedule(methodCall, enqueueAt, options);
    }
}
