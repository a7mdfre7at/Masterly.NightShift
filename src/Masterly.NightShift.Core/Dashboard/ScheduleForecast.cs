using Cronos;

namespace Masterly.NightShift.Dashboard;

/// <summary>
/// Projects every recurring job's upcoming runs over a window, with the expected duration of each run taken
/// from <see cref="JobInsightsAttribute"/>, and finds the slots where several jobs start together.
/// </summary>
internal sealed class ScheduleForecast
{
    /// <summary>The default expected duration when a method has no recorded runs.</summary>
    public static readonly TimeSpan DefaultDuration = TimeSpan.FromSeconds(30);

    /// <summary>The slot width used to find hot spots.</summary>
    public static readonly TimeSpan SlotSize = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How many occurrences of one job are projected at most. A job that runs more often in the window is marked
    /// <see cref="Row.Truncated"/>, and its runs are known to go on until <see cref="Row.RunsUntil"/>.
    /// </summary>
    public const int MaxOccurrencesPerJob = 2000;

    private ScheduleForecast(DateTime from, DateTime to, IReadOnlyList<Row> rows, IReadOnlyList<HotSpot> hotSpots)
    {
        From = from;
        To = to;
        Rows = rows;
        HotSpots = hotSpots;
    }

    /// <summary>The window start, UTC.</summary>
    public DateTime From { get; }

    /// <summary>The window end, UTC.</summary>
    public DateTime To { get; }

    /// <summary>One row per recurring job, busiest first.</summary>
    public IReadOnlyList<Row> Rows { get; }

    /// <summary>Slots where at least two jobs start, busiest first. Jobs that run more often than once per slot are left out.</summary>
    public IReadOnlyList<HotSpot> HotSpots { get; }

    /// <summary>All projected runs in the window; more run when <see cref="Truncated"/>.</summary>
    public int TotalRuns => Rows.Sum(static r => r.Occurrences.Count);

    /// <summary>Whether a job runs more than <see cref="MaxOccurrencesPerJob"/> times in the window.</summary>
    public bool Truncated => Rows.Any(static r => r.Truncated);

    /// <summary>The sum of the expected durations of all runs.</summary>
    public TimeSpan TotalBusyTime => TimeSpan.FromTicks(Rows.Sum(static r => r.Occurrences.Count * r.ExpectedDuration.Ticks));

    /// <summary>The hour (UTC, start) with the most runs, or <c>null</c> without runs.</summary>
    public DateTime? BusiestHour
    {
        get
        {
            Dictionary<DateTime, int> hours = [];
            foreach (Row row in Rows)
            {
                foreach (DateTime occurrence in row.Occurrences)
                {
                    DateTime hour = new(occurrence.Year, occurrence.Month, occurrence.Day, occurrence.Hour, 0, 0, DateTimeKind.Utc);
                    hours[hour] = hours.TryGetValue(hour, out int count) ? count + 1 : 1;
                }
            }

            return hours.Count == 0 ? null : hours.OrderByDescending(static x => x.Value).ThenBy(static x => x.Key).First().Key;
        }
    }

    /// <summary>Builds the forecast for the given window from the storage.</summary>
    public static ScheduleForecast Build([NotNull] IStorageConnection connection, DateTime fromUtc, TimeSpan window, [CanBeNull] ITimeZoneResolver? timeZoneResolver = null)
    {
        ArgumentNullException.ThrowIfNull(connection);

        List<RecurringJobDto> recurringJobs = connection.GetRecurringJobs();
        ITimeZoneResolver resolver = timeZoneResolver ?? GlobalTimeZoneResolver.Current;

        // Each policy is read once per forecast, however many jobs follow it
        SchedulePolicyCalendars calendars = new(connection);

        return Build(
            recurringJobs,
            fromUtc,
            window,
            key =>
            {
                JobInsight? insight = connection is JobStorageConnection storageConnection ? JobInsightsAttribute.Read(storageConnection, key) : null;
                return insight != null && insight.Durations.Length >= 3 ? TimeSpan.FromMilliseconds(insight.MedianMs) : (TimeSpan?)null;
            },
            resolver,
            name => calendars.Get(name, resolver));
    }

    /// <summary>
    /// Builds the forecast from recurring job definitions; <paramref name="expectedDuration"/> maps an insights key to
    /// a duration, and <paramref name="policies"/> a policy name to its calendar (<c>null</c> for a policy that isn't
    /// stored, which no longer applies). Without <paramref name="policies"/> no policy is applied.
    /// </summary>
    public static ScheduleForecast Build(
        [NotNull] IEnumerable<RecurringJobDto> recurringJobs,
        DateTime fromUtc,
        TimeSpan window,
        [NotNull] Func<string, TimeSpan?> expectedDuration,
        [CanBeNull] ITimeZoneResolver? timeZoneResolver = null,
        [CanBeNull] Func<string, SchedulePolicyCalendar?>? policies = null)
    {
        ArgumentNullException.ThrowIfNull(recurringJobs);
        ArgumentNullException.ThrowIfNull(expectedDuration);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(window, TimeSpan.Zero);

        ITimeZoneResolver resolver = timeZoneResolver ?? GlobalTimeZoneResolver.Current;
        DateTime from = DateTime.SpecifyKind(fromUtc, DateTimeKind.Utc);
        DateTime to = from.Add(window);
        List<Row> rows = [];

        foreach (RecurringJobDto dto in recurringJobs)
        {
            if (dto.Removed)
            {
                continue;
            }

            string? insightsKey = dto.Job != null ? JobInsightsAttribute.GetKey(dto.Job) : null;
            TimeSpan? duration = insightsKey != null ? expectedDuration(insightsKey) : null;
            List<DateTime> occurrences = [];
            bool truncated = false;
            DateTime? runsUntil = null;
            string? error = dto.Error;

            if (!dto.Paused && !string.IsNullOrEmpty(dto.Cron))
            {
                try
                {
                    CronExpression expression = RecurringJobEntity.ParseCronExpression(dto.Cron);
                    TimeZoneInfo timeZone = string.IsNullOrEmpty(dto.TimeZoneId) ? TimeZoneInfo.Utc : resolver.GetTimeZoneById(dto.TimeZoneId);

                    // Without a next execution the scheduler doesn't run the job: it ended, or its policy allows
                    // none of its occurrences
                    PolicySkipper policy = dto.NextExecution.HasValue
                        ? new(expression, timeZone, dto.Policy != null ? policies?.Invoke(dto.Policy) : null, to)
                        : PolicySkipper.Never;

                    // Like the scheduler: nothing before the job's start (an occurrence at the start runs),
                    // and nothing after its end (an occurrence at the end runs)
                    DateTime? startAt = ToUtc(dto.StartAt);
                    DateTime? endAt = ToUtc(dto.EndAt);

                    DateTime? next = policy.SkipBlocked(startAt.HasValue && startAt.Value > from
                        ? expression.GetNextOccurrence(startAt.Value, timeZone, inclusive: true)
                        : expression.GetNextOccurrence(from, timeZone, inclusive: false));

                    while (occurrences.Count < MaxOccurrencesPerJob &&
                           next.HasValue &&
                           next.Value < to &&
                           !(endAt.HasValue && next.Value > endAt.Value))
                    {
                        occurrences.Add(next.Value);
                        next = policy.SkipBlocked(expression.GetNextOccurrence(next.Value, timeZone, inclusive: false));
                    }

                    // Stopped by the cap with runs still to come: the job keeps running until its end or the
                    // window's, which the page shows instead of a band that stops after the first runs
                    if (occurrences.Count == MaxOccurrencesPerJob && next.HasValue && next.Value < to && !(endAt.HasValue && next.Value > endAt.Value))
                    {
                        truncated = true;
                        runsUntil = endAt.HasValue && endAt.Value < to ? endAt.Value : to;
                    }
                }
                catch (Exception ex) when (ex.IsCatchableExceptionType())
                {
                    error = ex.Message;
                }
            }

            rows.Add(new Row(dto, occurrences, duration ?? DefaultDuration, duration.HasValue, error) { Truncated = truncated, RunsUntil = runsUntil });
        }

        rows.Sort(static (x, y) =>
        {
            int byCount = y.Occurrences.Count.CompareTo(x.Occurrences.Count);
            return byCount != 0 ? byCount : string.CompareOrdinal(x.Id, y.Id);
        });

        return new ScheduleForecast(from, to, rows, FindHotSpots(rows, window));
    }

    internal static DateTime? ToUtc(DateTime? value)
    {
        if (!value.HasValue)
        {
            return null;
        }

        return value.Value.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(value.Value, DateTimeKind.Utc)
            : value.Value.ToUniversalTime();
    }

    private static IReadOnlyList<HotSpot> FindHotSpots(IReadOnlyList<Row> rows, TimeSpan window)
    {
        Dictionary<DateTime, HashSet<string>> slots = [];

        // Jobs that run more than once per slot would mark every slot hot and hide the real collisions
        long maxOccurrences = window.Ticks / SlotSize.Ticks;

        foreach (Row row in rows)
        {
            // A truncated job runs more often than the cap, which is about one per slot over a week
            if (row.Truncated || row.Occurrences.Count > maxOccurrences)
            {
                continue;
            }

            foreach (DateTime occurrence in row.Occurrences)
            {
                DateTime slot = new(occurrence.Ticks - occurrence.Ticks % SlotSize.Ticks, DateTimeKind.Utc);
                if (!slots.TryGetValue(slot, out HashSet<string>? jobs))
                {
                    jobs = new HashSet<string>(StringComparer.Ordinal);
                    slots[slot] = jobs;
                }

                jobs.Add(row.Id);
            }
        }

        return [.. slots
            .Where(static x => x.Value.Count >= 2)
            .Select(static x => new HotSpot(x.Key, [.. x.Value.OrderBy(static id => id, StringComparer.Ordinal)]))
            .OrderByDescending(static x => x.JobIds.Count)
            .ThenBy(static x => x.Slot)
            .Take(10)];
    }

    // Leaves out the occurrences a policy blocks, as the scheduler does: a blocked stretch is skipped whole, to the
    // next instant the policy allows. An occurrence the policy can't evaluate isn't shown, nor the ones after it,
    // since the scheduler doesn't run them while it waits for the policy
    private sealed class PolicySkipper(CronExpression? cron, TimeZoneInfo timeZone, SchedulePolicyCalendar? calendar, DateTime to)
    {
        // Enough for any policy over the longest window the page shows, without an endless walk
        private const int MaxJumps = 1000;

        /// <summary>Allows no occurrence.</summary>
        public static PolicySkipper Never { get; } = new(null, TimeZoneInfo.Utc, null, DateTime.MinValue);

        public DateTime? SkipBlocked(DateTime? occurrence)
        {
            if (cron == null)
            {
                return null;
            }

            if (calendar == null)
            {
                return occurrence;
            }

            for (int jump = 0; jump < MaxJumps && occurrence.HasValue && occurrence.Value < to; jump++)
            {
                SchedulePolicySearchResult found = calendar.Search(occurrence.Value);
                if (found.Failure != null || !found.Allowed.HasValue)
                {
                    return null;
                }

                if (found.Allowed.Value == occurrence.Value)
                {
                    return occurrence;
                }

                occurrence = cron.GetNextOccurrence(found.Allowed.Value, timeZone, inclusive: true);
            }

            return occurrence.HasValue && occurrence.Value < to ? null : occurrence;
        }
    }

    /// <summary>The forecast of one recurring job.</summary>
    public sealed class Row
    {
        internal Row(RecurringJobDto dto, IReadOnlyList<DateTime> occurrences, TimeSpan expectedDuration, bool durationKnown, string? error)
        {
            Dto = dto;
            Occurrences = occurrences;
            ExpectedDuration = expectedDuration;
            DurationKnown = durationKnown;
            Error = error;
        }

        /// <summary>The definition.</summary>
        public RecurringJobDto Dto { get; }

        /// <summary>The recurring job identifier.</summary>
        public string Id => Dto.Id;

        /// <summary>Projected start times in the window, UTC, ascending.</summary>
        public IReadOnlyList<DateTime> Occurrences { get; }

        /// <summary>The expected duration of one run.</summary>
        public TimeSpan ExpectedDuration { get; }

        /// <summary>Whether the duration comes from recorded runs rather than the default.</summary>
        public bool DurationKnown { get; }

        /// <summary>The job's stored error or the cron parsing error, if any.</summary>
        public string? Error { get; }

        /// <summary>
        /// Whether the job runs more than <see cref="MaxOccurrencesPerJob"/> times in the window, so only the first
        /// ones are in <see cref="Occurrences"/>.
        /// </summary>
        public bool Truncated { get; internal set; }

        /// <summary>For a <see cref="Truncated"/> row, when its runs stop: the window's end or the job's end date.</summary>
        public DateTime? RunsUntil { get; internal set; }
    }

    /// <summary>A slot where several jobs start.</summary>
    public sealed class HotSpot
    {
        internal HotSpot(DateTime slot, IReadOnlyList<string> jobIds)
        {
            Slot = slot;
            JobIds = jobIds;
        }

        /// <summary>The slot start, UTC.</summary>
        public DateTime Slot { get; }

        /// <summary>The recurring jobs starting in the slot.</summary>
        public IReadOnlyList<string> JobIds { get; }
    }
}
