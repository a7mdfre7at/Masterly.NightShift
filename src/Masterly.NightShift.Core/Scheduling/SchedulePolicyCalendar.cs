using System.Collections.Concurrent;
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
/// Why a policy can't tell whether an instant is allowed: a policy that can't be read, a time zone or a date provider
/// unknown on this server, a date provider that failed. Such an instant is never allowed, but it isn't blocked for
/// good either: asked again later, on another server, the answer may differ.
/// </summary>
/// <param name="Instant">The first instant that couldn't be evaluated, UTC.</param>
/// <param name="Reason">Why, such as <c>policy 'p': date provider 'holidays' failed: database down</c>.</param>
internal sealed record SchedulePolicyFailure(DateTime Instant, string Reason);

/// <summary>What <see cref="SchedulePolicyCalendar.Search"/> found.</summary>
/// <param name="Allowed">The first instant allowed, or <c>null</c>.</param>
/// <param name="Failure">
/// Why the search stopped before finding one, or <c>null</c>. With neither, the whole search found nothing allowed.
/// </param>
internal readonly record struct SchedulePolicySearchResult(DateTime? Allowed, SchedulePolicyFailure? Failure);

/// <summary>
/// A policy with every policy it requires or makes an exception for, read once and evaluated many times: by the
/// filter that applies policies to jobs, the recurring job scheduler and the previews. Safe to share between threads.
/// </summary>
internal sealed class SchedulePolicyCalendar
{
    /// <summary>How far <see cref="FindNextAllowed"/> looks ahead: about ten years.</summary>
    public static readonly TimeSpan SearchHorizon = TimeSpan.FromDays(3653);

    // A failing date provider is reported at most this often, whatever the number of jobs and calendars asking it
    internal static readonly TimeSpan ProviderWarningInterval = TimeSpan.FromMinutes(1);

    // Each step of the search moves to the next boundary of a policy (a window's start or end, midnight or a change
    // of the UTC offset), so ten years take a few thousand steps per policy; this only stops a broken time zone from
    // looping forever
    private const int MaxSearchSteps = 1_000_000;

    private static readonly ILog Logger = LogProvider.GetLogger(typeof(SchedulePolicyCalendar));
    private static readonly ConcurrentDictionary<string, DateTime> ProviderWarnings = new(StringComparer.Ordinal);

    private readonly Dictionary<string, Entry> _entries;

    // The date providers that failed while this calendar was used, with their error: they aren't asked again, so a
    // search doesn't throw at every step, and a calendar shared by a scheduler pass doesn't throw for every job
    private readonly ConcurrentDictionary<string, string> _failedProviders = new(StringComparer.Ordinal);

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
            if (entries.ContainsKey(next))
            {
                continue;
            }

            Entry? entry = Entry.Read(connection, next, timeZoneResolver);
            if (entry == null)
            {
                if (next == name)
                {
                    return null;
                }

                continue;
            }

            entries.Add(next, entry);
            if (entry.Policy == null)
            {
                continue;
            }

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

    /// <summary>
    /// Whether the policy allows a job to start at this instant, and why not. An instant the policy can't evaluate
    /// isn't allowed.
    /// </summary>
    public SchedulePolicyDecision Evaluate(DateTime instantUtc)
    {
        return Evaluate(instantUtc, out _);
    }

    /// <summary>
    /// Whether the policy allows a job to start at this instant, and why not. An instant the policy can't evaluate
    /// isn't allowed either, but it isn't blocked: <paramref name="failure"/> then says why it couldn't tell, and is
    /// <c>null</c> for an instant the policy allows or blocks.
    /// </summary>
    public SchedulePolicyDecision Evaluate(DateTime instantUtc, out SchedulePolicyFailure? failure)
    {
        DateTime instant = DateTime.SpecifyKind(instantUtc, DateTimeKind.Utc);
        Verdict verdict = Check(Name, instant, [], explain: true);

        failure = verdict.Outcome == Outcome.Unknown
            ? new SchedulePolicyFailure(instant, verdict.Reason!) // Unknown always has a reason
            : null;
        return new SchedulePolicyDecision(Name, instant, verdict.Outcome == Outcome.Allowed, verdict.Reason);
    }

    /// <summary>Whether the policy allows a job to start at this instant; not when it can't evaluate it.</summary>
    public bool IsAllowed(DateTime instantUtc)
    {
        return Check(Name, DateTime.SpecifyKind(instantUtc, DateTimeKind.Utc), [], explain: false).Outcome == Outcome.Allowed;
    }

    /// <summary>
    /// The first instant at or after <paramref name="fromUtc"/> that the policy allows, or <c>null</c> when it
    /// allows none within <see cref="SearchHorizon"/> or can't evaluate an instant before the first allowed one.
    /// </summary>
    public DateTime? FindNextAllowed(DateTime fromUtc)
    {
        return Search(fromUtc).Allowed;
    }

    /// <summary>
    /// Looks for the first instant at or after <paramref name="fromUtc"/> that the policy allows, within
    /// <see cref="SearchHorizon"/>. Stops at the first instant it can't evaluate, so a policy that can't be read or a
    /// failing date provider costs one step, not ten years of them.
    /// </summary>
    public SchedulePolicySearchResult Search(DateTime fromUtc)
    {
        DateTime instant = DateTime.SpecifyKind(fromUtc, DateTimeKind.Utc);
        DateTime limit = instant + SearchHorizon;

        for (int step = 0; step < MaxSearchSteps && instant <= limit; step++)
        {
            Verdict verdict = Check(Name, instant, [], explain: false);
            if (verdict.Outcome == Outcome.Allowed)
            {
                return new SchedulePolicySearchResult(instant, null);
            }

            if (verdict.Outcome == Outcome.Unknown)
            {
                return new SchedulePolicySearchResult(null, new SchedulePolicyFailure(instant, verdict.Reason!)); // Unknown always has a reason
            }

            DateTime? next = null;
            foreach (Entry entry in _entries.Values)
            {
                DateTime? boundary = entry.NextBoundary(instant);
                if (boundary.HasValue && (!next.HasValue || boundary < next))
                {
                    next = boundary;
                }
            }

            // No policy has a rule that changes: nothing is ever allowed
            if (!next.HasValue)
            {
                break;
            }

            instant = next.Value;
        }

        return new SchedulePolicySearchResult(null, null);
    }

    /// <summary>
    /// Whether a warning about the failing date provider <paramref name="provider"/> is due at <paramref name="now"/>:
    /// once per <see cref="ProviderWarningInterval"/> for each provider name. Also limits the warnings about a
    /// policy that can't be evaluated, under the key <c>policy:{name}</c>, which no provider can be named.
    /// </summary>
    internal static bool TryStartProviderWarning(string provider, DateTime now)
    {
        DateTime last = ProviderWarnings.GetOrAdd(provider, DateTime.MinValue);
        if (last != DateTime.MinValue && now - last < ProviderWarningInterval)
        {
            return false;
        }

        // Another thread that warned meanwhile wins
        return ProviderWarnings.TryUpdate(provider, now, last);
    }

    // A rule that blocks the instant decides, whatever the rules that can't be evaluated say. Otherwise a rule that
    // can't be evaluated makes the instant unknown: not allowed, and not blocked for good either.
    private Verdict Check(string name, DateTime instant, HashSet<string> visiting, bool explain)
    {
        // A policy that doesn't exist (any more) adds no rule
        if (!_entries.TryGetValue(name, out Entry? entry))
        {
            return Verdict.Allowed;
        }

        if (!visiting.Add(name))
        {
            return Verdict.Unknown($"policy '{name}' refers back to itself");
        }

        try
        {
            // An entry that failed to read is unknown, and has no references to follow
            Verdict own = entry.CheckOwnRules(instant, explain, this);
            if (own.Outcome == Outcome.Blocked || entry.Policy is not { } policy)
            {
                return own;
            }

            Verdict? unknown = own.Outcome == Outcome.Unknown ? own : null;

            foreach (string required in policy.Required)
            {
                Verdict requiredVerdict = Check(required, instant, visiting, explain);
                if (requiredVerdict.Outcome == Outcome.Blocked)
                {
                    return Verdict.Blocked(explain ? $"policy '{name}' requires '{required}', and {requiredVerdict.Reason}" : null);
                }

                if (requiredVerdict.Outcome == Outcome.Unknown)
                {
                    unknown ??= Verdict.Unknown($"policy '{name}' requires '{required}', and {requiredVerdict.Reason}");
                }
            }

            foreach (string excepted in policy.Excepted)
            {
                if (!_entries.ContainsKey(excepted))
                {
                    continue;
                }

                Verdict exceptedVerdict = Check(excepted, instant, visiting, explain: false);
                if (exceptedVerdict.Outcome == Outcome.Allowed)
                {
                    return Verdict.Blocked(explain ? $"policy '{name}' blocks the times policy '{excepted}' allows" : null);
                }

                // Can't tell whether the exception applies, so the instant isn't allowed either
                if (exceptedVerdict.Outcome == Outcome.Unknown)
                {
                    unknown ??= Verdict.Unknown($"policy '{name}' can't tell whether policy '{excepted}' allows this time, as {exceptedVerdict.Reason}");
                }
            }

            return unknown ?? Verdict.Allowed;
        }
        finally
        {
            visiting.Remove(name);
        }
    }

    private Verdict AskProvider(string policyName, string providerName, DateOnly date, bool explain)
    {
        if (_failedProviders.TryGetValue(providerName, out string? error))
        {
            return Verdict.Unknown($"policy '{policyName}': date provider '{providerName}' failed: {error}");
        }

        if (!ScheduleDateProviders.TryGet(providerName, out IScheduleDateProvider? provider))
        {
            return Verdict.Unknown($"policy '{policyName}': date provider '{providerName}' isn't registered on this server");
        }

        try
        {
            return provider.Contains(date)
                ? Verdict.Blocked(explain ? $"policy '{policyName}': {Entry.Format(date)} is excluded by date provider '{providerName}'" : null)
                : Verdict.Allowed;
        }
        catch (Exception ex) when (ex.IsCatchableExceptionType())
        {
            _failedProviders.TryAdd(providerName, ex.Message);
            LogProviderError(providerName, ex);
            return Verdict.Unknown($"policy '{policyName}': date provider '{providerName}' failed: {ex.Message}");
        }
    }

    private void LogProviderError(string provider, Exception exception)
    {
        // A search or a scheduler pass can ask the provider for thousands of dates and jobs
        if (TryStartProviderWarning(provider, JobClock.UtcNow))
        {
            Logger.WarnException($"Schedule date provider '{provider}' failed, so policy '{Name}' can't tell which dates it excludes and allows none of them. Recurring jobs that follow it wait and try again.", exception);
        }
    }

    private enum Outcome
    {
        Allowed,
        Blocked,
        Unknown,
    }

    // The reason of a blocked instant is only built when explained; an unknown one always has it
    private readonly record struct Verdict(Outcome Outcome, string? Reason)
    {
        public static Verdict Allowed => new(Outcome.Allowed, null);

        public static Verdict Blocked(string? reason) => new(Outcome.Blocked, reason);

        public static Verdict Unknown(string reason) => new(Outcome.Unknown, reason);
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
            if (fields == null || fields.Count == 0)
            {
                return null;
            }

            try
            {
                return Create(SchedulePolicy.FromFields(name, fields), timeZoneResolver);
            }
            catch (FormatException ex)
            {
                // Allows nothing rather than let jobs start at times the policy may forbid
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

        // The rules that need nothing but the policy come first: when one of them blocks the instant, a date provider
        // that fails doesn't matter
        public Verdict CheckOwnRules(DateTime instant, bool explain, SchedulePolicyCalendar calendar)
        {
            if (Policy == null || TimeZone == null)
            {
                return Verdict.Unknown($"policy '{Name}' can't be read: {Error}");
            }

            DateTime local = TimeZoneInfo.ConvertTimeFromUtc(instant, TimeZone);

            if (Policy.Days.Count > 0 && !Policy.Days.Contains(local.DayOfWeek))
            {
                return Verdict.Blocked(explain ? $"policy '{Name}': {local.DayOfWeek} isn't an allowed day" : null);
            }

            DateOnly date = DateOnly.FromDateTime(local);
            if (ExcludedDates.Contains(date))
            {
                return Verdict.Blocked(explain ? $"policy '{Name}': {Format(date)} is excluded" : null);
            }

            foreach (AnnualDate annualDate in Policy.ExcludedAnnualDates)
            {
                if (annualDate.Matches(date))
                {
                    return Verdict.Blocked(explain ? $"policy '{Name}': {annualDate} is excluded every year" : null);
                }
            }

            if (Policy.Windows.Count > 0 && !Policy.Windows.Any(x => x.Contains(local.TimeOfDay)))
            {
                return Verdict.Blocked(explain
                    ? $"policy '{Name}': {local.ToString("HH:mm", CultureInfo.InvariantCulture)} is outside {string.Join(", ", Policy.Windows)} ({TimeZone.Id})"
                    : null);
            }

            Verdict result = Verdict.Allowed;
            foreach (string providerName in Policy.DateProviders)
            {
                Verdict provided = calendar.AskProvider(Name, providerName, date, explain);
                if (provided.Outcome == Outcome.Blocked)
                {
                    return provided;
                }

                if (provided.Outcome == Outcome.Unknown && result.Outcome == Outcome.Allowed)
                {
                    result = provided;
                }
            }

            return result;
        }

        /// <summary>The first instant after <paramref name="instant"/> at which this policy's rules may change.</summary>
        public DateTime? NextBoundary(DateTime instant)
        {
            if (TimeZone is not { } timeZone)
            {
                return null;
            }

            DateTime local = TimeZoneInfo.ConvertTimeFromUtc(instant, timeZone);

            // Today's boundaries after now, then the next days' (two more cover a day lost to daylight saving)
            for (int days = 0; days <= 2; days++)
            {
                DateTime date = local.Date.AddDays(days);
                foreach (TimeSpan time in Boundaries)
                {
                    DateTime candidate = date + time;
                    if (candidate <= local)
                    {
                        continue;
                    }

                    DateTime? utc = ToUtcAfter(timeZone, candidate, instant);
                    if (utc.HasValue)
                    {
                        return FirstOffsetChange(timeZone, instant, utc.Value);
                    }
                }
            }

            return FirstOffsetChange(timeZone, instant, instant.AddDays(1));
        }

        // When the clocks change before the boundary, the local times after the change are new ones: going back, the
        // hour before the change happens a second time, and a window inside it opens again. The change is a boundary.
        private static DateTime FirstOffsetChange(TimeZoneInfo timeZone, DateTime after, DateTime boundary)
        {
            TimeSpan offset = timeZone.GetUtcOffset(after);
            if (timeZone.GetUtcOffset(boundary) == offset)
            {
                return boundary;
            }

            // The offset at "low" is the one at "after", the one at "high" isn't: the change is in between
            long low = after.Ticks;
            long high = boundary.Ticks;
            while (high - low > 1)
            {
                long middle = low + ((high - low) / 2);
                if (timeZone.GetUtcOffset(new DateTime(middle, DateTimeKind.Utc)) == offset)
                {
                    low = middle;
                }
                else
                {
                    high = middle;
                }
            }

            return new DateTime(high, DateTimeKind.Utc);
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
                    if (utc > after)
                    {
                        return utc;
                    }
                }

                return null;
            }

            DateTime converted = TimeZoneInfo.ConvertTimeToUtc(unspecified, timeZone);
            return converted > after ? converted : null;
        }

        internal static string Format(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }
}

/// <summary>
/// The calendars of the policies read during one pass of the recurring job scheduler, or one call that reschedules
/// many recurring jobs: each policy is read once, however many jobs follow it. Safe to share between threads; each
/// <see cref="WithConnection"/> view reads with its own connection.
/// </summary>
internal sealed class SchedulePolicyCalendars
{
    private readonly ConcurrentDictionary<(ITimeZoneResolver Resolver, string Name), SchedulePolicyCalendar?> _calendars;
    private readonly IStorageConnection _connection;

    /// <summary>Reads policies with <paramref name="connection"/> and keeps them.</summary>
    public SchedulePolicyCalendars([NotNull] IStorageConnection connection)
        : this(connection, new ConcurrentDictionary<(ITimeZoneResolver Resolver, string Name), SchedulePolicyCalendar?>())
    {
    }

    private SchedulePolicyCalendars(
        IStorageConnection connection,
        ConcurrentDictionary<(ITimeZoneResolver Resolver, string Name), SchedulePolicyCalendar?> calendars)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _calendars = calendars;
    }

    /// <summary>The same calendars, reading the ones not read yet with another connection.</summary>
    public SchedulePolicyCalendars WithConnection([NotNull] IStorageConnection connection)
    {
        return ReferenceEquals(connection, _connection) ? this : new SchedulePolicyCalendars(connection, _calendars);
    }

    /// <summary>The calendar of a policy, read once; <c>null</c> when there is no policy of this name.</summary>
    public SchedulePolicyCalendar? Get([NotNull] string name, [NotNull] ITimeZoneResolver timeZoneResolver)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(timeZoneResolver);

        // Two threads may read the same policy at once; either result is the same
        return _calendars.GetOrAdd((timeZoneResolver, name), static (key, connection) => SchedulePolicyCalendar.Load(connection, key.Name, key.Resolver), _connection);
    }
}
