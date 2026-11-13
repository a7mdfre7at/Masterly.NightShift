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

using Masterly.NightShift.Profiling;

namespace Masterly.NightShift.States;

/// <summary>
/// The arguments of a state change: the job, the new state, the states the job is expected to be in, and the
/// storage and connection to use.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1068:CancellationToken parameters must come last", Justification = "Cancellation tokens in this class are used only as a part of a general context and don't have usual meaning.")]
public class StateChangeContext
{
    public StateChangeContext(
        [NotNull] JobStorage storage,
        [NotNull] IStorageConnection connection,
        [NotNull] string backgroundJobId,
        [NotNull] IState newState)
        : this(storage, connection, backgroundJobId, newState, null)
    {
    }

    public StateChangeContext(
        [NotNull] JobStorage storage,
        [NotNull] IStorageConnection connection,
        [NotNull] string backgroundJobId,
        [NotNull] IState newState,
        [CanBeNull] params string[]? expectedStates)
        : this(storage, connection, backgroundJobId, newState, expectedStates, CancellationToken.None)
    {
    }

    public StateChangeContext(
        [NotNull] JobStorage storage,
        [NotNull] IStorageConnection connection,
        [NotNull] string backgroundJobId,
        [NotNull] IState newState,
        [CanBeNull] IEnumerable<string>? expectedStates,
        CancellationToken cancellationToken)
    : this(storage, connection, null, backgroundJobId, newState, expectedStates, false, null, cancellationToken, EmptyProfiler.Instance, null)
    {
    }

    public StateChangeContext(
        [NotNull] JobStorage storage,
        [NotNull] IStorageConnection connection,
        [CanBeNull] JobStorageTransaction? transaction,
        [NotNull] string backgroundJobId,
        [NotNull] IState newState,
        [CanBeNull] IEnumerable<string>? expectedStates,
        CancellationToken cancellationToken)
        : this(storage, connection, transaction, backgroundJobId, newState, expectedStates, false, null, cancellationToken, EmptyProfiler.Instance, null)
    {
    }

    internal StateChangeContext(
        [NotNull] JobStorage storage,
        [NotNull] IStorageConnection connection,
        [NotNull] string backgroundJobId,
        [NotNull] IState newState,
        [CanBeNull] IEnumerable<string>? expectedStates,
        bool disableFilters,
        CancellationToken cancellationToken,
        [NotNull] IProfiler profiler,
        [CanBeNull] string? serverId,
        [CanBeNull] IReadOnlyDictionary<string, object>? customData = null)
        : this(storage, connection, null, backgroundJobId, newState, expectedStates, disableFilters, null, cancellationToken, profiler, serverId, customData)
    {
    }

    internal StateChangeContext(
        [NotNull] JobStorage storage,
        [NotNull] IStorageConnection connection,
        [CanBeNull] JobStorageTransaction? transaction,
        [NotNull] string backgroundJobId,
        [NotNull] IState newState,
        [CanBeNull] IEnumerable<string>? expectedStates,
        bool disableFilters,
        [CanBeNull] IFetchedJob? completeJob,
        CancellationToken cancellationToken,
        [NotNull] IProfiler profiler,
        [CanBeNull] string? serverId,
        [CanBeNull] IReadOnlyDictionary<string, object>? customData = null)
    {
        Storage = storage ?? throw new ArgumentNullException(nameof(storage));
        Connection = connection ?? throw new ArgumentNullException(nameof(connection));
        Transaction = transaction;
        BackgroundJobId = backgroundJobId ?? throw new ArgumentNullException(nameof(backgroundJobId));
        NewState = newState ?? throw new ArgumentNullException(nameof(newState));
        ExpectedStates = expectedStates;
        DisableFilters = disableFilters;
        CancellationToken = cancellationToken;
        Profiler = profiler ?? throw new ArgumentNullException(nameof(profiler));
        ServerId = serverId;
        CustomData = customData;
        CompleteJob = completeJob;
    }

    [NotNull]
    public JobStorage Storage { get; }

    [NotNull]
    public IStorageConnection Connection { get; }

    [CanBeNull]
    public JobStorageTransaction? Transaction { get; }

    [NotNull]
    public string BackgroundJobId { get; }

    [NotNull]
    public IState NewState { get; }

    [CanBeNull]
    public IEnumerable<string>? ExpectedStates { get; }

    public bool DisableFilters { get; }
    public CancellationToken CancellationToken { get; }

    [NotNull]
    internal IProfiler Profiler { get; }

    [CanBeNull]
    public IReadOnlyDictionary<string, object>? CustomData { get; }

    [CanBeNull]
    public IFetchedJob? CompleteJob { get; }

    [CanBeNull]
    public BackgroundJob? ProcessedJob { get; set; }

    [CanBeNull]
    public string? ServerId { get; set; }

    /// <summary>
    /// The worker moving the job out of Processing, or <c>null</c>. The change is refused when another worker picked
    /// the job up since (see <see cref="StaleJobFilter"/>).
    /// </summary>
    internal string? LeaseHolder { get; init; }

    /// <summary>
    /// The queue entry a worker fetched the job from, for the state change that moves the job to Processing (and
    /// the Failed state of last resort that replaces it), when the storage tells it
    /// (<see cref="JobStorageConnection.GetQueueEntryId"/>). The change is refused when the job is Processing
    /// under another entry: another worker runs it.
    /// </summary>
    internal string? QueueEntry { get; init; }

    /// <summary>
    /// Set by a worker for the state change that moves a fetched job to Processing: lets an election filter have
    /// the worker look at the job again in a moment instead, with nothing stored (see <see cref="JobLookAgain"/>).
    /// <c>null</c> for every other state change, and when the worker doesn't offer it. Not honoured with a
    /// transaction of the caller's or with the filters disabled.
    /// </summary>
    internal JobLookAgain? LookAgain { get; init; }
}