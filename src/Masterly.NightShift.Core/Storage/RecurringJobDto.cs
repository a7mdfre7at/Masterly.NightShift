// This file is part of Hangfire. Copyright © 2013-2014 Hangfire OÜ.
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

namespace Masterly.NightShift.Storage;

/// <summary>
/// A recurring job as read from the storage: its schedule, job, queue, window, and its last and next executions.
/// </summary>
public class RecurringJobDto
{
    public string Id { get; set; } = null!; // Always set: a job that was removed still has its id
    public string? Cron { get; set; }
    public string? Queue { get; set; }
    public Job? Job { get; set; }
    public JobLoadException? LoadException { get; set; }
    public DateTime? NextExecution { get; set; }
    public string? LastJobId { get; set; }
    public string? LastJobState { get; set; }
    public DateTime? LastExecution { get; set; }
    public DateTime? CreatedAt { get; set; }
    public bool Removed { get; set; }
    public string? TimeZoneId { get; set; }
    public string? Error { get; set; }
    public int RetryAttempt { get; set; }

    /// <summary>Gets or sets whether the job is paused from the dashboard or by <c>IRecurringJobManagerV3.Pause</c>.</summary>
    public bool Paused { get; set; }

    /// <summary>Gets or sets how missed occurrences are handled.</summary>
    public MisfireHandlingMode MisfireHandling { get; set; }

    /// <summary>
    /// Gets or sets the UTC instant before which the job doesn't run (<see cref="RecurringJobOptions.StartAt"/>),
    /// or <c>null</c> when it has no start.
    /// </summary>
    public DateTime? StartAt { get; set; }

    /// <summary>
    /// Gets or sets the UTC instant after which the job doesn't run (<see cref="RecurringJobOptions.EndAt"/>),
    /// or <c>null</c> when it has no end. A job past its end has no <see cref="NextExecution"/>.
    /// </summary>
    public DateTime? EndAt { get; set; }

    /// <summary>
    /// Gets or sets how late a run may start (<see cref="RecurringJobOptions.SkipAfter"/>), or <c>null</c> when runs
    /// start however late.
    /// </summary>
    public TimeSpan? SkipAfter { get; set; }

    /// <summary>
    /// Gets or sets the name of the schedule policy the job follows (<see cref="RecurringJobOptions.Policy"/>), or
    /// <c>null</c>.
    /// </summary>
    [CanBeNull]
    public string? Policy { get; set; }

    /// <summary>
    /// Gets or sets whether the job was registered by a <see cref="RecurringJobSet"/> (declared in code), so a change
    /// made elsewhere, on the dashboard for example, is replaced by the declaration the next time the set is applied.
    /// </summary>
    public bool Declared { get; set; }

    /// <summary>
    /// Gets or sets the name of the <see cref="RecurringJobSet"/> that declared the job last
    /// (<see cref="RecurringJobSet.Name"/>), or <c>null</c> when no set did, or when an earlier build's set did
    /// without recording its name (<see cref="Declared"/> is then <c>true</c>).
    /// </summary>
    [CanBeNull]
    public string? DeclaredBy { get; set; }

    /// <summary>
    /// Gets or sets whether the job was paused because the <see cref="RecurringJobSet"/> that declared it no longer does
    /// (<see cref="RecurringJobSet.RetireUndeclared"/>). Declaring it again resumes it.
    /// </summary>
    public bool Retired { get; set; }
}
