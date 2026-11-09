namespace Masterly.NightShift.Common;

/// <summary>
/// The clock behind the timestamps the library writes, such as when a state was entered or when a job expires.
/// Replace it with <c>UseTimeProvider</c> to control time in tests. Waits between polls use real time.
/// </summary>
public static class JobClock
{
    /// <summary>Gets or sets the time source. Defaults to <see cref="TimeProvider.System"/>.</summary>
    /// <exception cref="ArgumentNullException">The value is <c>null</c>.</exception>
    public static TimeProvider TimeProvider
    {
        get;
        set => field = value ?? throw new ArgumentNullException(nameof(value));
    } = TimeProvider.System;

    /// <summary>Gets the current time, in UTC.</summary>
    public static DateTime UtcNow => TimeProvider.GetUtcNow().UtcDateTime;
}
