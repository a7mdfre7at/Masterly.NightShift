// This file is part of Hangfire. Copyright © 2019 Hangfire OÜ.
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

using System.Globalization;
using Cronos;

namespace Masterly.NightShift;

internal sealed class RecurringJobEntity
{
    private static readonly char[] SeparatorCharacters = [' ', '\t'];

    private readonly IDictionary<string, string> _recurringJob;

    public RecurringJobEntity(
        [NotNull] string recurringJobId,
        [NotNull] IDictionary<string, string> recurringJob)
    {
        _recurringJob = recurringJob ?? throw new ArgumentNullException(nameof(recurringJob));

        RecurringJobId = recurringJobId ?? throw new ArgumentNullException(nameof(recurringJobId));

        if (recurringJob.TryGetValue("Queue", out string? queue) && !string.IsNullOrWhiteSpace(queue))
        {
            Queue = queue;
        }

        if (recurringJob.TryGetValue("TimeZoneId", out string? timeZoneId) && !string.IsNullOrWhiteSpace(timeZoneId))
        {
            TimeZoneId = timeZoneId;
        }
        else
        {
            TimeZoneId = TimeZoneInfo.Utc.Id;
        }

        if (recurringJob.TryGetValue("Cron", out string? cron) && !string.IsNullOrWhiteSpace(cron))
        {
            Cron = cron;
        }

        if (recurringJob.TryGetValue("Job", out string? job) && !string.IsNullOrWhiteSpace(job))
        {
            Job = job;
        }

        if (recurringJob.TryGetValue("LastJobId", out string? lastJobId) && !string.IsNullOrWhiteSpace(lastJobId))
        {
            LastJobId = lastJobId;
        }

        if (recurringJob.TryGetValue("LastExecution", out string? lastExecution) && !string.IsNullOrWhiteSpace(lastExecution))
        {
            LastExecution = JobHelper.DeserializeDateTime(lastExecution);
        }

        if (recurringJob.TryGetValue("NextExecution", out string? nextExecution) && !string.IsNullOrWhiteSpace(nextExecution))
        {
            NextExecution = JobHelper.DeserializeDateTime(nextExecution);
        }

        if (recurringJob.TryGetValue("CreatedAt", out string? createdAt) && !string.IsNullOrWhiteSpace(createdAt))
        {
            CreatedAt = JobHelper.DeserializeDateTime(createdAt);
        }

        if (recurringJob.TryGetValue("Misfire", out string? misfireStr))
        {
            MisfireHandling = Enum.Parse<MisfireHandlingMode>(misfireStr);
            if (!Enum.IsDefined(MisfireHandling))
            {
                throw new NotSupportedException(string.Format(CultureInfo.CurrentCulture, "Misfire option '{0}' is not supported.", (int)MisfireHandling));
            }
        }
        else
        {
            MisfireHandling = MisfireHandlingMode.Relaxed;
        }

        if (recurringJob.TryGetValue("V", out string? version) && !string.IsNullOrWhiteSpace(version))
        {
            Version = int.Parse(version, CultureInfo.InvariantCulture);
        }

        if (recurringJob.TryGetValue("RetryAttempt", out string? attemptString) &&
            int.TryParse(attemptString, out int retryAttempt))
        {
            RetryAttempt = retryAttempt;
        }

        if (recurringJob.TryGetValue("Error", out string? error) && !string.IsNullOrWhiteSpace(error))
        {
            Error = error;
        }

        if (recurringJob.TryGetValue("Paused", out string? paused) && paused == "1")
        {
            Paused = true;
        }

        if (recurringJob.TryGetValue("ResumedAt", out string? resumedAt) && !string.IsNullOrWhiteSpace(resumedAt))
        {
            ResumedAt = JobHelper.DeserializeDateTime(resumedAt);
        }

        if (recurringJob.TryGetValue("ScheduleChangedAt", out string? scheduleChangedAt) && !string.IsNullOrWhiteSpace(scheduleChangedAt))
        {
            ScheduleChangedAt = JobHelper.DeserializeDateTime(scheduleChangedAt);
        }

        if (recurringJob.TryGetValue(StartAtField, out string? startAt) && !string.IsNullOrWhiteSpace(startAt))
        {
            StartAt = JobHelper.DeserializeDateTime(startAt);
        }

        if (recurringJob.TryGetValue(EndAtField, out string? endAt) && !string.IsNullOrWhiteSpace(endAt))
        {
            EndAt = JobHelper.DeserializeDateTime(endAt);
        }

        if (recurringJob.TryGetValue(SkipAfterField, out string? skipAfter) &&
            long.TryParse(skipAfter, NumberStyles.Integer, CultureInfo.InvariantCulture, out long skipAfterSeconds) &&
            skipAfterSeconds > 0)
        {
            SkipAfter = TimeSpan.FromSeconds(skipAfterSeconds);
        }

        if (recurringJob.TryGetValue(PolicyField, out string? policy) && !string.IsNullOrWhiteSpace(policy))
        {
            Policy = policy;
        }
    }

    /// <summary>
    /// Hash field with the name of the <see cref="SchedulePolicy"/> the job follows. Servers of earlier versions
    /// ignore it and run every occurrence.
    /// </summary>
    internal const string PolicyField = "Policy";

    /// <summary>
    /// The name of the schedule policy whose blocked occurrences don't run, or <c>null</c>. A policy that doesn't
    /// exist (any more) blocks nothing.
    /// </summary>
    public string? Policy { get; set; }

    /// <summary>
    /// Where <see cref="ScheduleNext(ITimeZoneResolver, DateTime, DateTime, TimeSpan)"/> reads <see cref="Policy"/>
    /// from: the calendars of a whole scheduler pass, or of this job alone. Without it the policy is ignored.
    /// </summary>
    public SchedulePolicyCalendars? PolicyCalendars { get; set; }

    /// <summary>
    /// How long a job waits when its policy can't be evaluated (<see cref="PolicyFailure"/>) before it is looked at
    /// again. The scheduler sets its polling interval.
    /// </summary>
    public TimeSpan PolicyRetryDelay { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Why the last call to <see cref="ScheduleNext(ITimeZoneResolver, DateTime, DateTime, TimeSpan)"/> couldn't
    /// evaluate <see cref="Policy"/>, or <c>null</c>. The job then stays on the schedule and is looked at again.
    /// </summary>
    public SchedulePolicyFailure? PolicyFailure { get; private set; }

    /// <summary>
    /// Whether the stored job waits because its policy couldn't be evaluated: its <see cref="Error"/> says so, and
    /// <see cref="NextExecution"/> is when the scheduler looks at it again, counting the occurrences missed
    /// meanwhile from the last execution. Computing the next occurrence from now instead would put them off
    /// until the next occurrence still to come.
    /// </summary>
    public bool WaitsForPolicy
    {
        get => Policy != null && NextExecution.HasValue && Error != null &&
               Error.StartsWith(GetPolicyFailureErrorStart(Policy), StringComparison.Ordinal);
    }

    // How the Error of a job that waits for its policy starts: WaitsForPolicy tells it from other errors by it
    private static string GetPolicyFailureErrorStart(string? policy) => $"Schedule policy '{policy}' can't be evaluated";

    // A blocked occurrence makes the search jump to the next instant the policy allows; this bounds the jumps for
    // a schedule whose occurrences keep missing the allowed times
    private const int MaxBlockedJumps = 1000;

    /// <summary>
    /// Hash field with <see cref="SkipAfter"/>, in whole seconds. Servers of earlier versions ignore it and run
    /// late occurrences.
    /// </summary>
    internal const string SkipAfterField = "SkipAfter";

    /// <summary>
    /// How late a run may start, or <c>null</c>. Copied to each run as a job parameter, and the limit of
    /// <see cref="MisfireHandlingMode.RunLatest"/>.
    /// </summary>
    public TimeSpan? SkipAfter { get; set; }

    // Hash fields of the run window. Servers of earlier versions don't know them and schedule as if there
    // were no window, so the recurring job's version stays 2 and these servers keep scheduling it.
    internal const string StartAtField = "StartAt";
    internal const string EndAtField = "EndAt";

    /// <summary>
    /// The most runs one call to <see cref="ScheduleNext(ITimeZoneResolver, DateTime, DateTime, TimeSpan)"/>
    /// returns. When more occurrences are due (<see cref="MisfireHandlingMode.Strict"/> after a long downtime),
    /// <see cref="NextExecution"/> is left on the first one not returned, so the job is still due and the
    /// scheduler's next pass continues from the last run created. No run is lost.
    /// </summary>
    public const int MaxExecutionsPerPass = 100;

    public string RecurringJobId { get; }

    public string? Queue { get; set; }
    public string? Cron { get; set; }
    public string TimeZoneId { get; set; }
    public string? Job { get; set; }
    public MisfireHandlingMode MisfireHandling { get; set; }

    public DateTime? CreatedAt { get; }
    public DateTime? NextExecution { get; private set; }

    public DateTime? LastExecution { get; set; }
    public string? LastJobId { get; set; }
    public int? Version { get; private set; }
    public int RetryAttempt { get; set; }
    public string? Error { get; set; }

    /// <summary>
    /// A paused job keeps its definition but leaves the schedule: <see cref="ScheduleNext(ITimeZoneResolver, DateTime)"/>
    /// yields nothing and <see cref="NextExecution"/> stays <c>null</c> until <see cref="Resume"/>.
    /// </summary>
    public bool Paused { get; private set; }

    /// <summary>
    /// When the job was last resumed. The scheduler never looks for missed occurrences before this
    /// instant, so a resumed job doesn't catch up on the time it was paused.
    /// </summary>
    public DateTime? ResumedAt { get; private set; }

    /// <summary>
    /// When the CRON expression or the time zone last changed. The scheduler never looks for missed
    /// occurrences before this instant, so a new schedule doesn't create the runs it would have had
    /// before it was set.
    /// </summary>
    public DateTime? ScheduleChangedAt { get; private set; }

    /// <summary>
    /// The UTC instant before which no occurrence runs, or <c>null</c>. The scheduler computes the first
    /// occurrence from it when it is later than the last execution, so misfire handling never catches up on
    /// occurrences before it.
    /// </summary>
    public DateTime? StartAt { get; private set; }

    /// <summary>
    /// The UTC instant after which no occurrence runs, or <c>null</c>. Once the next occurrence is later,
    /// <see cref="NextExecution"/> is cleared and the job leaves the schedule, but stays registered.
    /// </summary>
    public DateTime? EndAt { get; private set; }

    /// <summary>
    /// Sets the run window, as UTC instants. The values are kept as storage keeps them (to the millisecond),
    /// so a job registered again with the same window isn't seen as changed.
    /// </summary>
    /// <returns><c>true</c> when the window changed.</returns>
    public bool SetWindow(DateTime? startAt, DateTime? endAt)
    {
        DateTime? newStartAt = Normalize(startAt);
        DateTime? newEndAt = Normalize(endAt);

        if (newStartAt == StartAt && newEndAt == EndAt)
        {
            return false;
        }

        StartAt = newStartAt;
        EndAt = newEndAt;
        return true;
    }

    private static DateTime? Normalize(DateTime? value)
    {
        return value.HasValue ? JobHelper.DeserializeDateTime(JobHelper.SerializeDateTime(value.Value)) : (DateTime?)null;
    }

    /// <summary>
    /// The instant the scheduler computes the next occurrences from: the latest of the last execution,
    /// the resume time and the schedule change. <c>null</c> for a job that never ran, was never resumed
    /// and never had its schedule changed.
    /// </summary>
    public DateTime? ScheduleFrom
    {
        // A second before the change, like the next execution computed when the schedule changed,
        // so an occurrence that falls in the second of the change isn't skipped
        get => Latest(Latest(LastExecution, ResumedAt), ScheduleChangedAt?.AddSeconds(-1));
    }

    private static DateTime? Latest(DateTime? first, DateTime? second)
    {
        if (first.HasValue && second.HasValue)
        {
            return first > second ? first : second;
        }

        return first ?? second;
    }

    /// <summary>
    /// Records that the CRON expression or the time zone changed at <paramref name="now"/>, so missed
    /// occurrences are counted from then and not from the last execution under the old schedule.
    /// </summary>
    public void RecordScheduleChange(DateTime now)
    {
        ScheduleChangedAt = now;
    }

    public void Pause()
    {
        Paused = true;
        NextExecution = null;
        Error = null;
        RetryAttempt = 0;
    }

    /// <summary>
    /// Resumes a paused job from <paramref name="now"/>: occurrences missed while paused are not caught up,
    /// whatever the misfire handling mode. The execution history is kept.
    /// </summary>
    public void Resume(ITimeZoneResolver timeZoneResolver, DateTime now)
    {
        Paused = false;
        ResumedAt = now;
        ScheduleNext(timeZoneResolver, now);
    }

    public void ScheduleNext(ITimeZoneResolver timeZoneResolver, DateTime from)
    {
        ScheduleNext(timeZoneResolver, from, from, TimeSpan.Zero);
    }

    public IEnumerable<DateTime> ScheduleNext(
        ITimeZoneResolver timeZoneResolver,
        DateTime from,
        DateTime now,
        TimeSpan precision)
    {
        ArgumentNullException.ThrowIfNull(timeZoneResolver);

        List<DateTime> result = [];
        PolicyFailure = null;

        if (Paused)
        {
            NextExecution = null;
            Error = null;
            return result;
        }

        CronExpression cron = ParseCronExpression(Cron!); // An entry without a cron returned above
        TimeZoneInfo timeZone = timeZoneResolver.GetTimeZoneById(TimeZoneId);
        PolicySearch search = new(cron, timeZone, LoadPolicy(timeZoneResolver));

        if (MisfireHandling == MisfireHandlingMode.RunLatest && SkipAfter.HasValue)
        {
            // Only the occurrences inside the window can run, so the older ones aren't even looked at: a job
            // that runs every second doesn't walk through a long outage occurrence by occurrence
            DateTime windowStart = now - SkipAfter.Value;
            if (windowStart.AddSeconds(-1) > from)
            {
                from = windowStart.AddSeconds(-1);
            }
        }

        // Before the window opens, the first occurrence is the first one at or after its start, so no
        // misfire handling mode catches up on the occurrences before it
        DateTime? next = search.SkipBlocked(
            StartAt.HasValue && StartAt.Value > from
                ? cron.GetNextOccurrence(StartAt.Value, timeZone, inclusive: true)
                : cron.GetNextOccurrence(from, timeZone, inclusive: false));

        DateTime? latestMissed = null;
        DateTime? deferredTo = null;

        // Occurrences after the window's end never run, whatever the misfire handling mode
        for (; next <= now && !search.Stopped && !IsAfterEnd(next); next = search.SkipBlocked(cron.GetNextOccurrence(next.Value, timeZone, inclusive: false)))
        {
            if (result.Count >= MaxExecutionsPerPass)
            {
                // The rest is left for the next pass: next is due, so NextExecution keeps the job due
                break;
            }

            if (next == now)
            {
                result.Add(next.Value);
            }
            else
            {
                switch (MisfireHandling)
                {
                    case MisfireHandlingMode.Relaxed:
                        // The missed occurrence was allowed at its time, and runs now only when the policy allows now
                        // too. Otherwise it waits for the next time the policy allows, still counted from the last
                        // execution, so it isn't lost.
                        DateTime? runAt = search.NextAllowed(now);
                        if (runAt == now)
                        {
                            next = now;
                            result.Add(now);
                        }
                        else
                        {
                            deferredTo = runAt;
                        }

                        break;
                    case MisfireHandlingMode.Strict:
                        result.Add(next.Value);
                        break;
                    case MisfireHandlingMode.Ignorable:
                        if (now.Add(precision.Negate()) <= next && next <= now)
                        {
                            result.Add(next.Value);
                        }

                        break;
                    case MisfireHandlingMode.RunLatest:
                        // Each one replaces the one before; the last is added after the loop
                        latestMissed = next;
                        break;
                }
            }

            if (deferredTo.HasValue || search.Stopped)
            {
                break;
            }
        }

        if (search.Failure is { } failure)
        {
            ScheduleAfterPolicyFailure(cron, timeZone, failure, now);
            return result;
        }

        if (latestMissed.HasValue && !result.Contains(latestMissed.Value) &&
            (!SkipAfter.HasValue || now - latestMissed.Value <= SkipAfter.Value))
        {
            result.Add(latestMissed.Value);
            result.Sort();
        }

        // After the window's end the job leaves the schedule, but stays registered
        NextExecution = deferredTo ?? (IsAfterEnd(next) || search.NeverAllowed ? null : next);
        Error = null;

        if (search.NeverAllowed && !IsAfterEnd(next))
        {
            // Only a search that evaluated every instant it met ends here. Registering the job again or saving the
            // policy schedules it again, as for any error.
            Error = $"Schedule policy '{Policy}' allows none of its next occurrences in the next ten years.";
        }

        return result;
    }

    // The policy can't tell whether the next occurrences may run, for example because a date provider failed or isn't
    // registered on this server: they don't run, but the job stays on the schedule and is looked at again, from its
    // last execution, so a missed occurrence is handled by the misfire mode once the policy can be evaluated.
    // A RunLatest occurrence found before the failure waits too, so that a later one doesn't run as well.
    private void ScheduleAfterPolicyFailure(CronExpression cron, TimeZoneInfo timeZone, SchedulePolicyFailure failure, DateTime now)
    {
        PolicyFailure = failure;

        // Every occurrence that may run is after the window's end
        if (IsAfterEnd(failure.Instant))
        {
            NextExecution = null;
            Error = null;
            return;
        }

        // The occurrences before the failing instant were evaluated and are blocked
        DateTime? candidate = failure.Instant > now ? cron.GetNextOccurrence(failure.Instant, timeZone, inclusive: true) : null;
        NextExecution = candidate ?? now.Add(PolicyRetryDelay);
        Error = $"{GetPolicyFailureErrorStart(Policy)}, so the job waits and tries again: {failure.Reason}.";
    }

    /// <summary>The occurrences of the next runs after <paramref name="from"/>, with the policy applied.</summary>
    internal IEnumerable<DateTime> GetNextOccurrences(ITimeZoneResolver timeZoneResolver, DateTime from)
    {
        CronExpression cron = ParseCronExpression(Cron!); // Checked by the caller
        TimeZoneInfo timeZone = timeZoneResolver.GetTimeZoneById(TimeZoneId);
        PolicySearch search = new(cron, timeZone, LoadPolicy(timeZoneResolver));

        // An occurrence the policy can't evaluate isn't listed, nor the ones after it
        for (DateTime? next = search.SkipBlocked(cron.GetNextOccurrence(from, timeZone, inclusive: true));
             next.HasValue && !search.Stopped && !IsAfterEnd(next);
             next = search.SkipBlocked(cron.GetNextOccurrence(next.Value, timeZone, inclusive: false)))
        {
            yield return next.Value;
        }
    }

    private SchedulePolicyCalendar? LoadPolicy(ITimeZoneResolver timeZoneResolver)
    {
        return Policy != null && PolicyCalendars != null
            ? PolicyCalendars.Get(Policy, timeZoneResolver)
            : null;
    }

    // Applies a policy to the occurrences of a schedule, and remembers why it stopped
    private sealed class PolicySearch(CronExpression cron, TimeZoneInfo timeZone, SchedulePolicyCalendar? calendar)
    {
        /// <summary>A whole search, without an instant it couldn't evaluate, found no occurrence allowed.</summary>
        public bool NeverAllowed { get; private set; }

        /// <summary>The first instant the policy couldn't evaluate, or <c>null</c>.</summary>
        public SchedulePolicyFailure? Failure { get; private set; }

        public bool Stopped => NeverAllowed || Failure != null;

        // The first occurrence at or after the given one that the policy allows. A blocked stretch is skipped
        // whole: the search moves to the next instant the policy allows and takes the first occurrence from there.
        public DateTime? SkipBlocked(DateTime? occurrence)
        {
            if (calendar == null || !occurrence.HasValue)
            {
                return occurrence;
            }

            DateTime limit = occurrence.Value + SchedulePolicyCalendar.SearchHorizon;

            for (int jump = 0; jump < MaxBlockedJumps && occurrence.HasValue && occurrence.Value <= limit; jump++)
            {
                SchedulePolicySearchResult found = calendar.Search(occurrence.Value);
                if (found.Failure != null)
                {
                    Failure = found.Failure;
                    return occurrence;
                }

                if (!found.Allowed.HasValue)
                {
                    break;
                }

                if (found.Allowed.Value == occurrence.Value)
                {
                    return occurrence;
                }

                occurrence = cron.GetNextOccurrence(found.Allowed.Value, timeZone, inclusive: true);
            }

            // No occurrence the policy allows in sight. A schedule that has no more occurrences just ends.
            if (occurrence.HasValue)
            {
                NeverAllowed = true;
            }

            return occurrence;
        }

        // The first instant at or after the given one that the policy allows
        public DateTime? NextAllowed(DateTime instant)
        {
            if (calendar == null)
            {
                return instant;
            }

            SchedulePolicySearchResult found = calendar.Search(instant);
            if (found.Failure != null)
            {
                Failure = found.Failure;
            }
            else if (!found.Allowed.HasValue)
            {
                NeverAllowed = true;
            }

            return found.Allowed;
        }
    }

    private bool IsAfterEnd(DateTime? occurrence)
    {
        return EndAt.HasValue && occurrence > EndAt.Value;
    }

    public bool IsChanged(DateTime now, out IReadOnlyDictionary<string, string> changedFields)
    {
        changedFields = GetChangedFields(now);
        return changedFields.Count > 0;
    }

    public void ScheduleRetry(DateTime nextAttempt, string error)
    {
        RetryAttempt++;
        Error = error;
        NextExecution = nextAttempt;
    }

    public void Disable(string error)
    {
        NextExecution = null;
        Error = error;
    }

    private IReadOnlyDictionary<string, string> GetChangedFields(DateTime now)
    {
        Dictionary<string, string> result = [];

        if ((_recurringJob.TryGetValue("Queue", out string? queue) ? queue : null) != Queue)
        {
            result.Add("Queue", Queue!); // A null value stays null in the hash
        }

        if ((_recurringJob.TryGetValue("Cron", out string? cron) ? cron : null) != Cron)
        {
            result.Add("Cron", Cron!); // A null value stays null in the hash
        }

        if ((_recurringJob.TryGetValue("TimeZoneId", out string? timeZoneId) ? timeZoneId : null) != TimeZoneId)
        {
            result.Add("TimeZoneId", TimeZoneId);
        }

        if ((_recurringJob.TryGetValue("Job", out string? job) ? job : null) != Job)
        {
            result.Add("Job", Job!); // A null value stays null in the hash
        }

        if (!_recurringJob.ContainsKey("CreatedAt"))
        {
            result.Add("CreatedAt", JobHelper.SerializeDateTime(now));
        }

        string? serializedLastExecution = LastExecution.HasValue ? JobHelper.SerializeDateTime(LastExecution.Value) : null;

        if ((_recurringJob.TryGetValue("LastExecution", out string? lastExecution) ? lastExecution : null) !=
            serializedLastExecution)
        {
            result.Add("LastExecution", serializedLastExecution ?? string.Empty);
        }

        string? serializedNextExecution = NextExecution.HasValue ? JobHelper.SerializeDateTime(NextExecution.Value) : null;
        if ((_recurringJob.TryGetValue("NextExecution", out string? next) ? next : null) !=
            serializedNextExecution)
        {
            result.Add("NextExecution", serializedNextExecution ?? string.Empty);
        }

        if ((_recurringJob.TryGetValue("LastJobId", out string? last) && !string.IsNullOrWhiteSpace(last) ? last : null) != LastJobId)
        {
            result.Add("LastJobId", LastJobId ?? string.Empty);
        }

        string misfireHandlingValue = MisfireHandling.ToString("D");
        if ((!_recurringJob.ContainsKey("Misfire") && MisfireHandling != MisfireHandlingMode.Relaxed) ||
            (_recurringJob.TryGetValue("Misfire", out string? misfire) && misfire != misfireHandlingValue))
        {
            result.Add("Misfire", misfireHandlingValue);
        }

        if (!_recurringJob.ContainsKey("V"))
        {
            result.Add("V", "2");
        }

        if ((_recurringJob.TryGetValue("Error", out string? error) && !string.IsNullOrWhiteSpace(error) ? error : null) != Error)
        {
            result.Add("Error", Error ?? string.Empty);
        }

        string? serializedResumedAt = ResumedAt.HasValue ? JobHelper.SerializeDateTime(ResumedAt.Value) : null;
        if (serializedResumedAt != null &&
            (_recurringJob.TryGetValue("ResumedAt", out string? resumedAt) ? resumedAt : null) != serializedResumedAt)
        {
            result.Add("ResumedAt", serializedResumedAt);
        }

        string? serializedScheduleChangedAt = ScheduleChangedAt.HasValue ? JobHelper.SerializeDateTime(ScheduleChangedAt.Value) : null;
        if (serializedScheduleChangedAt != null &&
            (_recurringJob.TryGetValue("ScheduleChangedAt", out string? scheduleChangedAt) ? scheduleChangedAt : null) != serializedScheduleChangedAt)
        {
            result.Add("ScheduleChangedAt", serializedScheduleChangedAt);
        }

        AddWindowFieldIfChanged(result, StartAtField, StartAt);
        AddWindowFieldIfChanged(result, EndAtField, EndAt);

        string? serializedSkipAfter = SkipAfter.HasValue
            ? ((long)SkipAfter.Value.TotalSeconds).ToString(CultureInfo.InvariantCulture)
            : null;
        string? storedSkipAfter = _recurringJob.TryGetValue(SkipAfterField, out string? skipAfterValue) && !string.IsNullOrWhiteSpace(skipAfterValue)
            ? skipAfterValue
            : null;
        if (serializedSkipAfter != storedSkipAfter)
        {
            result.Add(SkipAfterField, serializedSkipAfter ?? string.Empty);
        }

        string? storedPolicy = _recurringJob.TryGetValue(PolicyField, out string? policyValue) && !string.IsNullOrWhiteSpace(policyValue)
            ? policyValue
            : null;
        if (Policy != storedPolicy)
        {
            result.Add(PolicyField, Policy ?? string.Empty);
        }

        string pausedValue = Paused ? "1" : "0";
        if ((!_recurringJob.ContainsKey("Paused") && Paused) ||
            (_recurringJob.TryGetValue("Paused", out string? pausedStored) && pausedStored != pausedValue))
        {
            result.Add("Paused", pausedValue);
        }

        string retryAttemptValue = RetryAttempt.ToString(CultureInfo.InvariantCulture);
        if ((_recurringJob.TryGetValue("RetryAttempt", out string? retryAttempt) ? retryAttempt : null) != retryAttemptValue)
        {
            if (_recurringJob.ContainsKey("RetryAttempt") || retryAttemptValue != "0")
            {
                result.Add("RetryAttempt", retryAttemptValue);
            }
        }

        return result;
    }

    // A window that was never set writes nothing; a cleared one writes an empty value, which reads as no value
    private void AddWindowFieldIfChanged(Dictionary<string, string> result, string field, DateTime? value)
    {
        string? serialized = value.HasValue ? JobHelper.SerializeDateTime(value.Value) : null;
        string? stored = _recurringJob.TryGetValue(field, out string? storedValue) && !string.IsNullOrWhiteSpace(storedValue)
            ? storedValue
            : null;

        if (serialized == null)
        {
            if (stored != null)
            {
                result.Add(field, string.Empty);
            }

            return;
        }

        if (stored == null || !value.Equals(JobHelper.DeserializeDateTime(stored)))
        {
            result.Add(field, serialized);
        }
    }

    public override string ToString()
    {
        return string.Join(";", _recurringJob.Select(static x => $"{x.Key}:{x.Value}"));
    }

    public static CronExpression ParseCronExpression([NotNull] string cronExpression)
    {
        ArgumentNullException.ThrowIfNull(cronExpression);

        CronFormat format = CronFormat.Standard;

        if (!cronExpression.StartsWith("@", StringComparison.OrdinalIgnoreCase))
        {
            string[] parts = cronExpression.Split(SeparatorCharacters, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 6)
            {
                format |= CronFormat.IncludeSeconds;
            }
            else if (parts.Length != 5)
            {
                throw new CronFormatException(
                    $"Wrong number of parts in the `{cronExpression}` cron expression, you can only use 5 or 6 (with seconds) part-based expressions.");
            }
        }

        return CronExpression.Parse(cronExpression, format);
    }
}