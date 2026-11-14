using System.Globalization;

namespace Masterly.NightShift.Dashboard;

/// <summary>
/// Reads values of state data for display. State data can come from another writer or version, so a value
/// that can't be read is shown as missing: one bad row must not take down a list page or its export.
/// </summary>
internal static class StateDataValues
{
    private const long MaxMilliseconds = long.MaxValue / TimeSpan.TicksPerMillisecond;

    /// <summary>A time written by <see cref="JobHelper.SerializeDateTime"/>, or <c>null</c>.</summary>
    public static DateTime? ReadDateTime(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        try
        {
            return JobHelper.DeserializeDateTime(value);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentOutOfRangeException)
        {
            // Not a time: the page shows the row without it
            return null;
        }
    }

    /// <summary>A number of milliseconds (a duration or a latency), or <c>null</c>.</summary>
    public static TimeSpan? ReadMilliseconds(string? value)
    {
        return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long milliseconds)
            && milliseconds is >= -MaxMilliseconds and <= MaxMilliseconds
                ? TimeSpan.FromTicks(milliseconds * TimeSpan.TicksPerMillisecond)
                : null;
    }
}
