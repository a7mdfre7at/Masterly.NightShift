// This file is part of Hangfire. Copyright © 2016 Hangfire OÜ.
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

namespace Masterly.NightShift;

/// <summary>
/// Options of a recurring job: its time zone, queue, misfire handling and start and end dates.
/// </summary>
public class RecurringJobOptions
{
    public RecurringJobOptions()
    {
        TimeZone = DefaultTimeZone;
        QueueName = EnqueuedState.DefaultQueue;
        MisfireHandling = MisfireHandlingMode.Relaxed;
    }

    /// <summary>
    /// Gets or sets the time zone new options start with, so recurring jobs registered without a time zone get it:
    /// <see cref="TimeZoneInfo.Utc"/> unless changed with <c>UseDefaultRecurringJobTimeZone</c>. Jobs already
    /// stored keep the time zone they were registered with.
    /// </summary>
    /// <exception cref="ArgumentNullException">The value is <c>null</c>.</exception>
    [NotNull]
    public static TimeZoneInfo DefaultTimeZone
    {
        get;
        set => field = value ?? throw new ArgumentNullException(nameof(value));
    } = TimeZoneInfo.Utc;

    /// <summary>
    /// Gets or sets the time zone the CRON expression is read in. Defaults to <see cref="DefaultTimeZone"/>.
    /// </summary>
    [NotNull]
    public TimeZoneInfo TimeZone
    {
        get;
        set
        {
            ArgumentNullException.ThrowIfNull(value);

            field = value;
        }
    } = TimeZoneInfo.Utc;

    /// <summary>
    /// Gets or sets how late a run may start, or <c>null</c> (the default) to run however late. A run that starts
    /// more than this after its occurrence is deleted as skipped, like a job with <see cref="SkipIfLateAttribute"/>,
    /// and <see cref="MisfireHandlingMode.RunLatest"/> leaves out a missed occurrence older than this. Must be
    /// positive.
    /// </summary>
    /// <remarks>
    /// Stored with the recurring job, with a precision of one second, and copied to every run as the
    /// <c>SkipAfter</c> job parameter. It needs a storage that loads the parameters with the job, as SQL Server
    /// does; on another storage the parameter is ignored and only <see cref="SkipIfLateAttribute"/> skips runs.
    /// </remarks>
    public TimeSpan? SkipAfter { get; set; }

    /// <summary>
    /// Gets or sets the name of the stored <see cref="SchedulePolicy"/> the job follows, or <c>null</c>: occurrences
    /// the policy blocks don't run, and the next run is the next occurrence it allows. The policy must be stored
    /// (<c>SaveSchedulePolicy</c>) before the job is registered.
    /// </summary>
    /// <remarks>
    /// The policy is checked when an occurrence is due, not when its run starts. Changing the policy reschedules the
    /// jobs that follow it. Servers of earlier versions ignore it and run every occurrence.
    /// </remarks>
    [CanBeNull]
    public string? Policy
    {
        get;
        set => field = value == null ? null : SchedulePolicy.NormalizeName(value, nameof(value));
    }

    /// <summary>
    /// Gets or sets the queue the recurring job's runs are enqueued to. Default is <c>default</c>. Prefer the
    /// <c>queue</c> parameter of <c>AddOrUpdate</c>, which stores the queue with the job; this property is for
    /// storages without <see cref="Storage.JobStorageFeatures.JobQueueProperty"/>, where the queue can only be kept with
    /// the recurring job.
    /// </summary>
    [NotNull]
    public string QueueName
    {
        get;
        set
        {
            EnqueuedState.ValidateQueueName(nameof(value), value);
            field = value;
        }
    } = EnqueuedState.DefaultQueue;

    public MisfireHandlingMode MisfireHandling { get; set; }

    /// <summary>
    /// Gets or sets the instant before which the job doesn't run, or <c>null</c> (the default) for no start.
    /// Occurrences of the CRON expression earlier than this instant are skipped, and misfire handling never
    /// catches up on them. An occurrence exactly at this instant runs.
    /// </summary>
    /// <remarks>
    /// The value is an absolute instant, stored in UTC with a precision of one millisecond; <see cref="TimeZone"/>
    /// only decides when the CRON occurrences fall. To start at midnight of a local date, give the offset of
    /// that zone, for example <c>new DateTimeOffset(date, timeZone.GetUtcOffset(date))</c>. Only servers of
    /// this version or later respect it.
    /// </remarks>
    public DateTimeOffset? StartAt { get; set; }

    /// <summary>
    /// Gets or sets the instant after which the job doesn't run, or <c>null</c> (the default) for no end.
    /// Once the next occurrence of the CRON expression is later than this instant, the job leaves the
    /// schedule (its next execution is cleared) but stays registered, so it can still be triggered, updated
    /// or removed. An occurrence exactly at this instant runs. Must be later than <see cref="StartAt"/>.
    /// </summary>
    /// <remarks>
    /// The value is an absolute instant, stored in UTC with a precision of one millisecond, like
    /// <see cref="StartAt"/>. Only servers of this version or later respect it.
    /// </remarks>
    public DateTimeOffset? EndAt { get; set; }
}
