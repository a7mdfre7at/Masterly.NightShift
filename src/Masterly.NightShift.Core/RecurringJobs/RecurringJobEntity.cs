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

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Cronos;
using Masterly.NightShift.Annotations;
using Masterly.NightShift.Common;

namespace Masterly.NightShift
{
    internal sealed class RecurringJobEntity
    {
        private static readonly char[] SeparatorCharacters = new[] { ' ', '\t' };

        private readonly IDictionary<string, string> _recurringJob;

        public RecurringJobEntity(
            [NotNull] string recurringJobId,
            [NotNull] IDictionary<string, string> recurringJob)
        {
            _recurringJob = recurringJob ?? throw new ArgumentNullException(nameof(recurringJob));

            RecurringJobId = recurringJobId ?? throw new ArgumentNullException(nameof(recurringJobId));

            if (recurringJob.TryGetValue("Queue", out var queue) && !String.IsNullOrWhiteSpace(queue))
            {
                Queue = queue;
            }

            if (recurringJob.TryGetValue("TimeZoneId", out var timeZoneId) && !String.IsNullOrWhiteSpace(timeZoneId))
            {
                TimeZoneId = timeZoneId;
            }
            else
            {
                TimeZoneId = TimeZoneInfo.Utc.Id;
            }

            if (recurringJob.TryGetValue("Cron", out var cron) && !String.IsNullOrWhiteSpace(cron))
            {
                Cron = cron;
            }
            
            if (recurringJob.TryGetValue("Job", out var job) && !String.IsNullOrWhiteSpace(job))
            {
                Job = job;
            }

            if (recurringJob.TryGetValue("LastJobId", out var lastJobId) && !String.IsNullOrWhiteSpace(lastJobId))
            {
                LastJobId = lastJobId;
            }

            if (recurringJob.TryGetValue("LastExecution", out var lastExecution) && !String.IsNullOrWhiteSpace(lastExecution))
            {
                LastExecution = JobHelper.DeserializeDateTime(lastExecution);
            }

            if (recurringJob.TryGetValue("NextExecution", out var nextExecution) && !String.IsNullOrWhiteSpace(nextExecution))
            {
                NextExecution = JobHelper.DeserializeDateTime(nextExecution);
            }

            if (recurringJob.TryGetValue("CreatedAt", out var createdAt) && !String.IsNullOrWhiteSpace(createdAt))
            {
                CreatedAt = JobHelper.DeserializeDateTime(createdAt);
            }

            if (recurringJob.TryGetValue("Misfire", out var misfireStr))
            {
                MisfireHandling = (MisfireHandlingMode)Enum.Parse(typeof(MisfireHandlingMode), misfireStr);
                if (!Enum.IsDefined(typeof(MisfireHandlingMode), MisfireHandling))
                {
                    throw new NotSupportedException(String.Format(CultureInfo.CurrentCulture, "Misfire option '{0}' is not supported.", (int)MisfireHandling));
                }
            }
            else
            {
                MisfireHandling = MisfireHandlingMode.Relaxed;
            }

            if (recurringJob.TryGetValue("V", out var version) && !String.IsNullOrWhiteSpace(version))
            {
                Version = int.Parse(version, CultureInfo.InvariantCulture);
            }

            if (recurringJob.TryGetValue("RetryAttempt", out var attemptString) &&
                int.TryParse(attemptString, out var retryAttempt))
            {
                RetryAttempt = retryAttempt;
            }

            if (recurringJob.TryGetValue("Error", out var error) && !String.IsNullOrWhiteSpace(error))
            {
                Error = error;
            }

            if (recurringJob.TryGetValue("Paused", out var paused) && paused == "1")
            {
                Paused = true;
            }

            if (recurringJob.TryGetValue("ResumedAt", out var resumedAt) && !String.IsNullOrWhiteSpace(resumedAt))
            {
                ResumedAt = JobHelper.DeserializeDateTime(resumedAt);
            }

            if (recurringJob.TryGetValue("ScheduleChangedAt", out var scheduleChangedAt) && !String.IsNullOrWhiteSpace(scheduleChangedAt))
            {
                ScheduleChangedAt = JobHelper.DeserializeDateTime(scheduleChangedAt);
            }

            if (recurringJob.TryGetValue(StartAtField, out var startAt) && !String.IsNullOrWhiteSpace(startAt))
            {
                StartAt = JobHelper.DeserializeDateTime(startAt);
            }

            if (recurringJob.TryGetValue(EndAtField, out var endAt) && !String.IsNullOrWhiteSpace(endAt))
            {
                EndAt = JobHelper.DeserializeDateTime(endAt);
            }
        }

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

        public string Queue { get; set; }
        public string Cron { get; set; }
        public string TimeZoneId { get; set; }
        public string Job { get; set; }
        public MisfireHandlingMode MisfireHandling { get; set; }

        public DateTime? CreatedAt { get; }
        public DateTime? NextExecution { get; private set; }

        public DateTime? LastExecution { get; set; }
        public string LastJobId { get; set; }
        public int? Version { get; private set; }
        public int RetryAttempt { get; set; }
        public string Error { get; set; }

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
            var newStartAt = Normalize(startAt);
            var newEndAt = Normalize(endAt);

            if (newStartAt == StartAt && newEndAt == EndAt) return false;

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
            if (first.HasValue && second.HasValue) return first > second ? first : second;
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
            if (timeZoneResolver == null) throw new ArgumentNullException(nameof(timeZoneResolver));

            var result = new List<DateTime>();

            if (Paused)
            {
                NextExecution = null;
                Error = null;
                return result;
            }

            var cron = ParseCronExpression(Cron);
            var timeZone = timeZoneResolver.GetTimeZoneById(TimeZoneId);

            // Before the window opens, the first occurrence is the first one at or after its start, so no
            // misfire handling mode catches up on the occurrences before it
            var next = StartAt.HasValue && StartAt.Value > from
                ? cron.GetNextOccurrence(StartAt.Value, timeZone, inclusive: true)
                : cron.GetNextOccurrence(from, timeZone, inclusive: false);

            // Occurrences after the window's end never run, whatever the misfire handling mode
            for (; next <= now && !IsAfterEnd(next); next = cron.GetNextOccurrence(next.Value, timeZone, inclusive: false))
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
                            next = now;
                            result.Add(next.Value);
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
                    }
                }
            }

            // After the window's end the job leaves the schedule, but stays registered
            NextExecution = IsAfterEnd(next) ? null : next;
            Error = null;
            return result;
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
            var result = new Dictionary<string, string>();

            if ((_recurringJob.TryGetValue("Queue", out var queue) ? queue : null) != Queue)
            {
                result.Add("Queue", Queue);
            }

            if ((_recurringJob.TryGetValue("Cron", out var cron) ? cron : null) != Cron)
            {
                result.Add("Cron", Cron);
            }

            if ((_recurringJob.TryGetValue("TimeZoneId", out var timeZoneId) ? timeZoneId : null) != TimeZoneId)
            {
                result.Add("TimeZoneId", TimeZoneId);
            }

            if ((_recurringJob.TryGetValue("Job", out var job) ? job : null) != Job)
            {
                result.Add("Job", Job);
            }

            if (!_recurringJob.ContainsKey("CreatedAt"))
            {
                result.Add("CreatedAt", JobHelper.SerializeDateTime(now));
            }

            var serializedLastExecution = LastExecution.HasValue ? JobHelper.SerializeDateTime(LastExecution.Value) : null;

            if ((_recurringJob.TryGetValue("LastExecution", out var lastExecution) ? lastExecution : null) !=
                serializedLastExecution)
            {
                result.Add("LastExecution", serializedLastExecution ?? String.Empty);
            }

            var serializedNextExecution = NextExecution.HasValue ? JobHelper.SerializeDateTime(NextExecution.Value) : null;
            if ((_recurringJob.TryGetValue("NextExecution", out var next) ? next : null) !=
                serializedNextExecution)
            {
                result.Add("NextExecution", serializedNextExecution ?? String.Empty);
            }

            if ((_recurringJob.TryGetValue("LastJobId", out var last) && !String.IsNullOrWhiteSpace(last) ? last : null) != LastJobId)
            {
                result.Add("LastJobId", LastJobId ?? String.Empty);
            }

            var misfireHandlingValue = MisfireHandling.ToString("D");
            if ((!_recurringJob.ContainsKey("Misfire") && MisfireHandling != MisfireHandlingMode.Relaxed) ||
                (_recurringJob.TryGetValue("Misfire", out var misfire) && misfire != misfireHandlingValue))
            {
                result.Add("Misfire", misfireHandlingValue);
            }

            if (!_recurringJob.ContainsKey("V"))
            {
                result.Add("V", "2");
            }

            if ((_recurringJob.TryGetValue("Error", out var error) && !String.IsNullOrWhiteSpace(error) ? error : null) != Error)
            {
                result.Add("Error", Error ?? String.Empty);
            }

            var serializedResumedAt = ResumedAt.HasValue ? JobHelper.SerializeDateTime(ResumedAt.Value) : null;
            if (serializedResumedAt != null &&
                (_recurringJob.TryGetValue("ResumedAt", out var resumedAt) ? resumedAt : null) != serializedResumedAt)
            {
                result.Add("ResumedAt", serializedResumedAt);
            }

            var serializedScheduleChangedAt = ScheduleChangedAt.HasValue ? JobHelper.SerializeDateTime(ScheduleChangedAt.Value) : null;
            if (serializedScheduleChangedAt != null &&
                (_recurringJob.TryGetValue("ScheduleChangedAt", out var scheduleChangedAt) ? scheduleChangedAt : null) != serializedScheduleChangedAt)
            {
                result.Add("ScheduleChangedAt", serializedScheduleChangedAt);
            }

            AddWindowFieldIfChanged(result, StartAtField, StartAt);
            AddWindowFieldIfChanged(result, EndAtField, EndAt);

            var pausedValue = Paused ? "1" : "0";
            if ((!_recurringJob.ContainsKey("Paused") && Paused) ||
                (_recurringJob.TryGetValue("Paused", out var pausedStored) && pausedStored != pausedValue))
            {
                result.Add("Paused", pausedValue);
            }

            var retryAttemptValue = RetryAttempt.ToString(CultureInfo.InvariantCulture);
            if ((_recurringJob.TryGetValue("RetryAttempt", out var retryAttempt) ? retryAttempt : null) != retryAttemptValue)
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
            var serialized = value.HasValue ? JobHelper.SerializeDateTime(value.Value) : null;
            var stored = _recurringJob.TryGetValue(field, out var storedValue) && !String.IsNullOrWhiteSpace(storedValue)
                ? storedValue
                : null;

            if (serialized == null)
            {
                if (stored != null) result.Add(field, String.Empty);
                return;
            }

            if (stored == null || !value.Equals(JobHelper.DeserializeDateTime(stored)))
            {
                result.Add(field, serialized);
            }
        }

        public override string ToString()
        {
            return String.Join(";", _recurringJob.Select(static x => $"{x.Key}:{x.Value}"));
        }

        public static CronExpression ParseCronExpression([NotNull] string cronExpression)
        {
            if (cronExpression == null) throw new ArgumentNullException(nameof(cronExpression));

            var format = CronFormat.Standard;

            if (!cronExpression.StartsWith("@", StringComparison.OrdinalIgnoreCase))
            {
                var parts = cronExpression.Split(SeparatorCharacters, StringSplitOptions.RemoveEmptyEntries);
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
}