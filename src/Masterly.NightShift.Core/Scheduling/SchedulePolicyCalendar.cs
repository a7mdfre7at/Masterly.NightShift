using System.Globalization;
using Masterly.NightShift.Logging;

namespace Masterly.NightShift;

/// <summary>Whether a <see cref="SchedulePolicy"/> allows an instant, and why not when it doesn't.</summary>
/// <param name="Policy">The policy asked.</param>
/// <param name="Instant">The instant checked, UTC.</param>
/// <param name="Allowed">Whether a job may start then.</param>
/// <param name="Reason">Why it may not, such as <c>policy 'business-hours': Saturday isn't an allowed day</c>; <c>null</c> when allowed.</param>
public sealed record SchedulePolicyDecision(string Policy, DateTime Instant, bool Allowed, string? Reason);

/// <summary>
/// A policy with every policy it requires or makes an exception for, read once and evaluated many times: by the
/// filter that applies policies to jobs, the recurring job scheduler and the previews.
/// </summary>
internal sealed class SchedulePolicyCalendar
{
    /// <summary>How far <see cref="FindNextAllowed"/> looks ahead: about ten years.</summary>
    public static readonly TimeSpan SearchHorizon = TimeSpan.FromDays(3653);

    // Each step of the search moves to the next boundary of a policy (a window's start or end, or midnight), so
    // ten years take a few thousand steps per policy; this only stops a broken time zone from looping forever
    private const int MaxSearchSteps = 1_000_000;

    private static readonly ILog Logger = LogProvider.GetLogger(typeof(SchedulePolicyCalendar));

    private readonly Dictionary<string, Entry> _entries;
    private int _providerErrorLogged;

    private SchedulePolicyCalendar(string name, Dictionary<string, Entry> entries)
    {
        Name = name;
        _entries = entries;
    }

    /// <summary>The policy evaluated.</summary>
    public string Name { get; }

    /// <summary>The policy evaluated, as stored.</summary>
    public SchedulePolicy? Policy => _entries.TryGetValue(Name, out Entry? entry) ? entry.Policy : null;

    /// <summary>
    /// Reads a policy and the policies it refers to. Returns <c>null</c> when there is no policy of this name. A
    /// policy it refers to that doesn't exist (any more) adds no rule.
    /// </summary>
    public static SchedulePolicyCalendar? Load(
        [NotNull] IStorageConnection connection,
        [NotNull] string name,
        [NotNull] ITimeZoneResolver timeZoneResolver)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(timeZoneResolver);

        Dictionary<string, Entry> entries = new(StringComparer.Ordinal);
        Queue<string> pending = new([name]);

        while (pending.Count > 0)
        {
            string next = pending.Dequeue();
            if (entries.ContainsKey(next)) continue;

            Entry? entry = Entry.Read(connection, next, timeZoneResolver);
            if (entry == null)
            {
                if (next == name) return null;
                continue;
            }

            entries.Add(next, entry);
            if (entry.Policy == null) continue;

            foreach (string reference in entry.Policy.Required.Concat(entry.Policy.Excepted))
            {
                pending.Enqueue(reference);
            }
        }

        return new SchedulePolicyCalendar(name, entries);
    }

    /// <summary>Evaluates policies that aren't stored, such as the ones a declaration is about to save.</summary>
    public static SchedulePolicyCalendar FromPolicies(
        [NotNull] string name,
        [NotNull] IEnumerable<SchedulePolicy> policies,
        [NotNull] ITimeZoneResolver timeZoneResolver)
    {
        Dictionary<string, Entry> entries = new(StringComparer.Ordinal);
        foreach (SchedulePolicy policy in policies)
        {
            entries[policy.Name] = Entry.Create(policy, timeZoneResolver);
        }

        return new SchedulePolicyCalendar(name, entries);
    }

    /// <summary>Whether the policy allows a job to start at this instant, and why not.</summary>
    public SchedulePolicyDecision Evaluate(DateTime instantUtc)
    {
        DateTime instant = DateTime.SpecifyKind(instantUtc, DateTimeKind.Utc);
        string? reason = GetBlockReason(Name, instant, [], explain: true);
        return new SchedulePolicyDecision(Name, instant, reason == null, reason);
    }

    /// <summary>Whether the policy allows a job to start at this instant.</summary>
    public bool IsAllowed(DateTime instantUtc)
    {
        return GetBlockReason(Name, DateTime.SpecifyKind(instantUtc, DateTimeKind.Utc), [], explain: false) == null;
    }

    /// <summary>
    /// The first instant at or after <paramref name="fromUtc"/> that the policy allows, or <c>null</c> when it
    /// allows none within <see cref="SearchHorizon"/>.
    /// </summary>
    public DateTime? FindNextAllowed(DateTime fromUtc)
    {
        DateTime instant = DateTime.SpecifyKind(fromUtc, DateTimeKind.Utc);
        DateTime limit = instant + SearchHorizon;

        for (int step = 0; step < MaxSearchSteps && instant <= limit; step++)
        {
            if (IsAllowed(instant)) return instant;

            DateTime? next = null;
            foreach (Entry entry in _entries.Values)
            {
                DateTime? boundary = entry.NextBoundary(instant);
                if (boundary.HasValue && (!next.HasValue || boundary < next)) next = boundary;
            }

            // No policy has a rule that changes (each one fails to read): nothing is ever allowed
            if (!next.HasValue) return null;

            instant = next.Value;
        }

        return null;
    }

    private string? GetBlockReason(string name, DateTime instant, HashSet<string> visiting, bool explain)
    {
        // A policy that doesn't exist (any more) adds no rule
        if (!_entries.TryGetValue(name, out Entry? entry)) return null;

        if (!visiting.Add(name))
        {
            return explain ? $"policy '{name}' refers back to itself" : string.Empty;
        }

        try
        {
            string? reason = entry.GetOwnBlockReason(instant, explain, this);
            if (reason != null) return reason;

            SchedulePolicy policy = entry.Policy!; // An entry that failed to read returned above

            foreach (string required in policy.Required)
            {
                string? requiredReason = GetBlockReason(required, instant, visiting, explain);
                if (requiredReason != null)
                {
                    return explain ? $"policy '{name}' requires '{required}', and {requiredReason}" : string.Empty;
                }
            }

            foreach (string excepted in policy.Excepted)
            {
                if (_entries.ContainsKey(excepted) && GetBlockReason(excepted, instant, visiting, explain: false) == null)
                {
                    return explain ? $"policy '{name}' blocks the times policy '{excepted}' allows" : string.Empty;
                }
            }

            return null;
        }
        finally
        {
            visiting.Remove(name);
        }
    }

    private void LogProviderError(string provider, Exception exception)
    {
        // Once per calendar: a search can ask the provider thousands of times
        if (Interlocked.Exchange(ref _providerErrorLogged, 1) == 0)
        {
            Logger.WarnException($"Schedule date provider '{provider}' failed, so policy '{Name}' blocks the dates it checks.", exception);
        }
    }

    private sealed class Entry
    {
        private Entry(string name, SchedulePolicy? policy, TimeZoneInfo? timeZone, string? error)
        {
            Name = name;
            Policy = policy;
            TimeZone = timeZone;
            Error = error;

            if (policy != null)
            {
                ExcludedDates = [.. policy.ExcludedDates];
                // Midnight is a boundary of every day, as days and dates change then
                Boundaries = [.. policy.Windows.SelectMany(static x => new[] { x.Start, x.End }).Append(TimeSpan.Zero).Distinct().Order()];
            }
        }

        public string Name { get; }
        public SchedulePolicy? Policy { get; }
        public TimeZoneInfo? TimeZone { get; }
        public string? Error { get; }
        private HashSet<DateOnly> ExcludedDates { get; } = [];
        private TimeSpan[] Boundaries { get; } = [];

        public static Entry? Read(IStorageConnection connection, string name, ITimeZoneResolver timeZoneResolver)
        {
            Dictionary<string, string>? fields = connection.GetAllEntriesFromHash(SchedulePolicies.GetKey(name));
            if (fields == null || fields.Count == 0) return null;

            try
            {
                return Create(SchedulePolicy.FromFields(name, fields), timeZoneResolver);
            }
            catch (FormatException ex)
            {
                // Blocks everything rather than let jobs start at times the policy may forbid
                return new Entry(name, null, null, ex.Message);
            }
        }

        public static Entry Create(SchedulePolicy policy, ITimeZoneResolver timeZoneResolver)
        {
            try
            {
                return new Entry(policy.Name, policy, timeZoneResolver.GetTimeZoneById(policy.TimeZoneId), null);
            }
            catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
            {
                return new Entry(policy.Name, null, null, $"time zone '{policy.TimeZoneId}' of policy '{policy.Name}' isn't known on this server");
            }
        }

        public string? GetOwnBlockReason(DateTime instant, bool explain, SchedulePolicyCalendar calendar)
        {
            if (Policy == null || TimeZone == null)
            {
                return explain ? $"policy '{Name}' can't be read: {Error}" : string.Empty;
            }

            DateTime local = TimeZoneInfo.ConvertTimeFromUtc(instant, TimeZone);

            if (Policy.Days.Count > 0 && !Policy.Days.Contains(local.DayOfWeek))
            {
                return explain ? $"policy '{Name}': {local.DayOfWeek} isn't an allowed day" : string.Empty;
            }

            DateOnly date = DateOnly.FromDateTime(local);
            if (ExcludedDates.Contains(date))
            {
                return explain ? $"policy '{Name}': {Format(date)} is excluded" : string.Empty;
            }

            foreach (AnnualDate annualDate in Policy.ExcludedAnnualDates)
            {
                if (annualDate.Matches(date))
                {
                    return explain ? $"policy '{Name}': {annualDate} is excluded every year" : string.Empty;
                }
            }

            foreach (string providerName in Policy.DateProviders)
            {
                if (!ScheduleDateProviders.TryGet(providerName, out IScheduleDateProvider? provider))
                {
                    return explain ? $"policy '{Name}': date provider '{providerName}' isn't registered on this server" : string.Empty;
                }

                try
                {
                    if (provider.Contains(date))
                    {
                        return explain ? $"policy '{Name}': {Format(date)} is excluded by date provider '{providerName}'" : string.Empty;
                    }
                }
                catch (Exception ex) when (ex.IsCatchableExceptionType())
                {
                    calendar.LogProviderError(providerName, ex);
                    return explain ? $"policy '{Name}': date provider '{providerName}' failed: {ex.Message}" : string.Empty;
                }
            }

            if (Policy.Windows.Count > 0 && !Policy.Windows.Any(x => x.Contains(local.TimeOfDay)))
            {
                return explain
                    ? $"policy '{Name}': {local.ToString("HH:mm", CultureInfo.InvariantCulture)} is outside {string.Join(", ", Policy.Windows)} ({TimeZone.Id})"
                    : string.Empty;
            }

            return null;
        }

        /// <summary>The first instant after <paramref name="instant"/> at which this policy's rules may change.</summary>
        public DateTime? NextBoundary(DateTime instant)
        {
            if (TimeZone is not { } timeZone) return null;

            DateTime local = TimeZoneInfo.ConvertTimeFromUtc(instant, timeZone);

            // Today's boundaries after now, then the next days' (two more cover a day lost to daylight saving)
            for (int days = 0; days <= 2; days++)
            {
                DateTime date = local.Date.AddDays(days);
                foreach (TimeSpan time in Boundaries)
                {
                    DateTime candidate = date + time;
                    if (candidate <= local) continue;

                    DateTime? utc = ToUtcAfter(timeZone, candidate, instant);
                    if (utc.HasValue) return utc;
                }
            }

            return instant.AddDays(1);
        }

        // A local time inside the hour skipped when clocks go forward doesn't exist: the first that does is the end
        // of the gap. A local time inside the hour repeated when clocks go back happens twice: the first one after
        // the instant counts.
        private static DateTime? ToUtcAfter(TimeZoneInfo timeZone, DateTime local, DateTime after)
        {
            DateTime unspecified = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
            for (int minutes = 0; minutes < 24 * 60 && timeZone.IsInvalidTime(unspecified); minutes++)
            {
                unspecified = unspecified.AddMinutes(1);
            }

            if (timeZone.IsAmbiguousTime(unspecified))
            {
                // The larger offset gives the earlier instant
                foreach (TimeSpan offset in timeZone.GetAmbiguousTimeOffsets(unspecified).OrderByDescending(static x => x))
                {
                    DateTime utc = DateTime.SpecifyKind(unspecified - offset, DateTimeKind.Utc);
                    if (utc > after) return utc;
                }

                return null;
            }

            DateTime converted = TimeZoneInfo.ConvertTimeToUtc(unspecified, timeZone);
            return converted > after ? converted : null;
        }

        private static string Format(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }
}
