using System.Collections.Concurrent;

namespace Masterly.NightShift;

/// <summary>
/// Dates a <see cref="SchedulePolicy"/> excludes, computed in code: public holidays, a trading calendar, closing
/// days read from a database. Register it by name on every server and client with
/// <c>GlobalConfiguration.Configuration.UseScheduleDateProvider("public-holidays", provider)</c> (or
/// <c>AddNightShiftScheduleDateProvider</c> in a container), and name it in a policy with
/// <see cref="SchedulePolicy.ExcludeDatesFrom"/>.
/// </summary>
/// <remarks>
/// <see cref="Contains"/> is called often, from several threads, when servers look for the next allowed instant:
/// keep it fast, for example with the dates of each year computed once. A provider that throws blocks the instant
/// checked, and the reason names the error.
/// </remarks>
public interface IScheduleDateProvider
{
    /// <summary>Whether the day is excluded. The day is a date in the time zone of the policy that asks.</summary>
    bool Contains(DateOnly day);
}

/// <summary>The date providers registered in this process, by name.</summary>
internal static class ScheduleDateProviders
{
    private static readonly ConcurrentDictionary<string, IScheduleDateProvider> Providers = new(StringComparer.Ordinal);

    public static void Set(string name, IScheduleDateProvider provider)
    {
        Providers[SchedulePolicy.NormalizeName(name, nameof(name))] = provider ?? throw new ArgumentNullException(nameof(provider));
    }

    public static bool TryGet(string name, [NotNullWhen(true)] out IScheduleDateProvider? provider)
    {
        return Providers.TryGetValue(name, out provider);
    }

    public static bool Remove(string name) => Providers.TryRemove(name, out _);

    // For tests
    internal static void Clear() => Providers.Clear();
}
