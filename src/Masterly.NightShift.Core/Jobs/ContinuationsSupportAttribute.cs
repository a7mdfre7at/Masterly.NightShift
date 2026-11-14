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

using System.Diagnostics;
using System.Globalization;
using Masterly.NightShift.Logging;
using Masterly.NightShift.Server;

namespace Masterly.NightShift;

/// <summary>
/// Starts a job's continuations when it reaches a final state, and makes a continuation created after its parent
/// finished start at once. Added to the global filters by default.
/// </summary>
public class ContinuationsSupportAttribute : JobFilterAttribute, IElectStateFilter, IApplyStateFilter
{
    internal static readonly HashSet<string> KnownFinalStates = [DeletedState.StateName, SucceededState.StateName];

    /// <summary>The job parameter that lists the continuations of a job.</summary>
    internal const string ContinuationsParameter = "Continuations";

    private static readonly TimeSpan AddJobLockTimeout = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan ContinuationStateFetchTimeout = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan ContinuationInvalidTimeout = TimeSpan.FromMinutes(15);

    private readonly ILog _logger = LogProvider.For<ContinuationsSupportAttribute>();
    private readonly HashSet<string> _knownFinalStates;
    private readonly IBackgroundJobStateChanger _stateChanger;

    // Whether the parent's result is passed to its continuations, see UseResultsInContinuations
    internal bool PushResults { get; }

    public ContinuationsSupportAttribute()
        : this(false)
    {
    }

    public ContinuationsSupportAttribute(bool pushResults)
        : this(pushResults, KnownFinalStates)
    {
    }

    public ContinuationsSupportAttribute(HashSet<string> knownFinalStates)
        : this(false, knownFinalStates)
    {
    }

    public ContinuationsSupportAttribute(bool pushResults, HashSet<string> knownFinalStates)
        : this(pushResults, knownFinalStates, new BackgroundJobStateChanger())
    {
    }

    public ContinuationsSupportAttribute(
        [NotNull] HashSet<string> knownFinalStates,
        [NotNull] IBackgroundJobStateChanger stateChanger)
        : this(false, knownFinalStates, stateChanger)
    {
    }

    public ContinuationsSupportAttribute(
        bool pushResults,
        [NotNull] HashSet<string> knownFinalStates,
        [NotNull] IBackgroundJobStateChanger stateChanger)
    {
        ArgumentNullException.ThrowIfNull(knownFinalStates);
        ArgumentNullException.ThrowIfNull(stateChanger);

        PushResults = pushResults;
        _knownFinalStates = knownFinalStates;
        _stateChanger = stateChanger;

        // Ensure this filter is the last filter in the chain to start
        // continuations on the last candidate state only.
        Order = 1000;
    }

    public void OnStateElection(ElectStateContext context)
    {
        if (context.CandidateState is AwaitingState awaitingState)
        {
            // Branch for a child background job.
            AddContinuation(context, awaitingState);
        }
        else if (_knownFinalStates.Contains(context.CandidateState.Name) || IsIntermediateTrigger(context.CandidateState.Name))
        {
            // Branch for a parent background job: a final state, or a final failure or the start of processing,
            // which start only the continuations waiting for them
            ExecuteContinuationsIfExist(context);
        }
    }

    // Failed (once no retry is left: this filter runs after AutomaticRetryAttribute) and Processing start the
    // continuations that ask for them, and leave the others waiting
    private static bool IsIntermediateTrigger(string stateName)
    {
        return FailedState.StateName.Equals(stateName, StringComparison.OrdinalIgnoreCase) ||
               ProcessingState.StateName.Equals(stateName, StringComparison.OrdinalIgnoreCase);
    }

    public void OnStateApplied(ApplyStateContext context, IWriteOnlyTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(context);

        // A continuation or fan-in continuation leaves the Awaiting state: it becomes due now, or at the time its
        // next state schedules it for. Its lateness counts from there, not from its creation, which would add the
        // time its parents waited and ran. A deleted continuation never runs.
        if (!AwaitingState.StateName.Equals(context.OldStateName, StringComparison.OrdinalIgnoreCase) ||
            context.NewState is DeletedState)
        {
            return;
        }

        DateTime dueAt = context.NewState is ScheduledState scheduled ? scheduled.EnqueueAt : JobClock.UtcNow;
        RecordDueAt(context, transaction, dueAt);
    }

    private void RecordDueAt(ApplyStateContext context, IWriteOnlyTransaction transaction, DateTime dueAt)
    {
        string jobId = context.BackgroundJob.Id;
        string value = JobHelper.ToMillisecondTimestamp(dueAt).ToString(CultureInfo.InvariantCulture);

        if (transaction is JobStorageTransaction storageTransaction &&
            context.Storage.HasFeature(JobStorageFeatures.Transaction.SetJobParameter))
        {
            storageTransaction.SetJobParameter(jobId, PerformContext.DueAtParameter, value);
            return;
        }

        // Before the commit, so a worker never fetches the job without it. Should the transaction fail, the value
        // stays on a job still Awaiting, and the next move out of Awaiting writes it again.
        try
        {
            context.Connection.SetJobParameter(jobId, PerformContext.DueAtParameter, value);
        }
        catch (Exception ex) when (ex.IsCatchableExceptionType())
        {
            // Only bookkeeping: without it the continuation's lateness counts from its creation
            _logger.WarnException($"Could not record when continuation '{jobId}' became due.", ex);
        }
    }

    internal static List<Continuation> DeserializeContinuations(string? serialized)
    {
        List<Continuation>? continuations = SerializationHelper.Deserialize<List<Continuation>>(serialized);

        if (continuations != null && continuations.TrueForAll(static x => x.JobId == null))
        {
            continuations = SerializationHelper.Deserialize<List<Continuation>>(serialized, SerializationOption.User);
        }

        return continuations ?? [];
    }

    private void AddContinuation(ElectStateContext context, AwaitingState awaitingState)
    {
        IStorageConnection connection = context.Connection;
        string parentId = awaitingState.ParentId;

        // We store continuations as a json array in a job parameter. Since there 
        // is no way to add a continuation in an atomic way, we are placing a 
        // distributed lock on parent job to prevent race conditions, when
        // multiple threads add continuation to the same parent job.
        using (connection.AcquireDistributedJobLock(parentId, AddJobLockTimeout))
        {
            // When we try to add a continuation for a removed job,
            // the system should throw an exception instead of creating
            // corrupted state.
            JobData jobData = connection.GetJobData(parentId) ?? throw new InvalidOperationException(
                $"Can not add a continuation: parent background job '{parentId}' does not exist.");
            List<Continuation> continuations = GetContinuations(context, parentId);

            // Continuation may be already added. This may happen, when outer transaction
            // was failed after adding a continuation last time, since the addition is
            // performed outside of an outer transaction.
            if (!continuations.Exists(x => x.JobId == context.BackgroundJob.Id))
            {
                continuations.Add(new Continuation { JobId = context.BackgroundJob.Id, Options = awaitingState.Options });

                // Set continuation only after ensuring that parent job still
                // exists. Otherwise we could create add non-expiring (garbage)
                // parameter for the parent job.
                SetContinuations(connection, parentId, continuations);
            }

            StateData? currentState = connection.GetStateData(parentId);

            // A continuation for a failed or processing parent starts at once when it asks for that state, and
            // otherwise waits: neither state is final
            if (currentState != null && !_knownFinalStates.Contains(currentState.Name) &&
                ShouldStartContinuation(currentState.Name, awaitingState.Options, addedLate: true))
            {
                if (PushResults)
                {
                    HandOver(context.Connection, context.BackgroundJob.Id, currentState);
                }

                context.CandidateState = awaitingState.NextState;
            }
            else if (currentState != null && _knownFinalStates.Contains(currentState.Name))
            {
                bool startImmediately = ShouldStartContinuation(currentState.Name, awaitingState.Options, addedLate: true);

                if (PushResults && startImmediately)
                {
                    HandOver(context.Connection, context.BackgroundJob.Id, currentState);
                }

                context.CandidateState = startImmediately
                    ? awaitingState.NextState
                    : new DeletedState { Reason = "Continuation condition was not met" };
            }
        }
    }

    private void ExecuteContinuationsIfExist(ElectStateContext context)
    {
        // The following lines are executed inside a distributed job lock,
        // so it is safe to get continuation list here.
        List<Continuation> continuations = GetContinuations(context, null);

        // Most jobs have none: nothing else to read or allocate
        if (continuations.Count == 0)
        {
            return;
        }

        Dictionary<string, IState> nextStates = [];

        // Getting continuation data for all continuations – state they are waiting 
        // for and their next state.
        foreach (Continuation continuation in continuations)
        {
            if (string.IsNullOrWhiteSpace(continuation.JobId))
            {
                continue;
            }

            // A failure or the start of processing leaves the continuations that wait for another outcome waiting.
            // Decided from the options alone, before anything is read: a parent that starts processing or fails
            // doesn't read the continuations it leaves alone, nor wait for one that has no state yet
            FanInContinuationsSupportAttribute.Decision decision = Decide(context.CandidateState.Name, continuation.Options, _knownFinalStates);
            if (decision == FanInContinuationsSupportAttribute.Decision.Undecided)
            {
                continue;
            }

            StateData? currentState = GetContinuationState(context, continuation.JobId, ContinuationStateFetchTimeout);
            if (currentState == null)
            {
                continue;
            }

            // All continuations should be in the awaiting state. If someone changed 
            // the state of a continuation, we should simply skip it.
            if (currentState.Name != AwaitingState.StateName)
            {
                continue;
            }

            IState? nextState;

            if (decision == FanInContinuationsSupportAttribute.Decision.Skip)
            {
                nextState = new DeletedState { Reason = "Continuation condition was not met" };
            }
            else
            {
                try
                {
                    nextState = SerializationHelper.Deserialize<IState>(currentState.Data["NextState"], SerializationOption.TypedInternal);
                }
                catch (Exception ex) when (ex.IsCatchableExceptionType())
                {
                    nextState = new FailedState(ex)
                    {
                        Reason = "An error occurred while deserializing the continuation"
                    };
                }
            }

            // Duplicate continuations are possible when they were added by Hangfire before version 1.6.10, see the
            // check for existing continuations in AddContinuation; the first one wins.
            nextStates.TryAdd(continuation.JobId, nextState!); // NextState was written from a real state
        }

        string? antecedentResult = null;
        string? antecedentException = null;

        if (PushResults)
        {
            if (context.CandidateState is SucceededState)
            {
                Dictionary<string, string> serializedData = context.CandidateState.SerializeData();
                serializedData.TryGetValue("Result", out antecedentResult);
            }
            else if (context.CandidateState is DeletedState)
            {
                Dictionary<string, string> serializedData = context.CandidateState.SerializeData();
                if (!serializedData.TryGetValue("Exception", out antecedentException))
                {
                    antecedentException = JobParameterInjectionFilter.DefaultException;
                }
            }
            else if (context.CandidateState is FailedState { Exception: not null } failedState)
            {
                antecedentException = SerializationHelper.Serialize(new ExceptionInfo(failedState.Exception), SerializationOption.User);
            }
        }

        foreach (KeyValuePair<string, IState> tuple in nextStates)
        {
            if (antecedentResult != null)
            {
                context.Connection.SetJobParameter(tuple.Key, "AntecedentResult", antecedentResult);
            }

            if (antecedentException != null)
            {
                context.Connection.SetJobParameter(tuple.Key, "AntecedentException", antecedentException);
            }

            _stateChanger.ChangeState(new StateChangeContext(
                context.Storage,
                context.Connection,
                tuple.Key,
                tuple.Value,
                AwaitingState.StateName));
        }
    }

    private StateData? GetContinuationState(ElectStateContext context, string continuationJobId, TimeSpan timeout)
    {
        StateData? currentState = null;

        Stopwatch started = Stopwatch.StartNew();
        bool firstAttempt = true;

        while (true)
        {
            JobData? continuationData = context.Connection.GetJobData(continuationJobId);
            if (continuationData == null)
            {
                _logger.Warn(
                    $"Can not start continuation '{continuationJobId}' for background job '{context.BackgroundJob.Id}': continuation does not exist.");

                break;
            }

            currentState = context.Connection.GetStateData(continuationJobId);
            if (currentState != null)
            {
                break;
            }

            if (JobClock.UtcNow - continuationData.CreatedAt > ContinuationInvalidTimeout)
            {
                _logger.Warn(
                    $"Continuation '{continuationJobId}' has been ignored: it was deemed to be aborted, because its state is still non-initialized.");

                break;
            }

            if (started.Elapsed >= timeout)
            {
                _logger.Warn(
                    $"Can not start continuation '{continuationJobId}' for background job '{context.BackgroundJob.Id}': timeout expired while trying to fetch continuation state.");

                break;
            }

            Thread.Sleep(firstAttempt ? 0 : 100);
            firstAttempt = false;
        }

        return currentState;
    }

    /// <summary>
    /// Hands the outcome of a parent to a continuation that is about to start, from the parent's stored state: the
    /// result of a parent that succeeded (<c>AntecedentResult</c>), the exception of one that was deleted or failed
    /// (<c>AntecedentException</c>). For a continuation started after its parent finished: one added late, or one
    /// that was never told to start and is started by a server later.
    /// </summary>
    internal static void HandOver(IStorageConnection connection, string continuationId, StateData parent)
    {
        if (SucceededState.StateName.Equals(parent.Name, StringComparison.OrdinalIgnoreCase))
        {
            if (parent.Data.TryGetValue("Result", out string? antecedentResult))
            {
                connection.SetJobParameter(continuationId, "AntecedentResult", antecedentResult);
            }
        }
        else if (DeletedState.StateName.Equals(parent.Name, StringComparison.OrdinalIgnoreCase))
        {
            if (!parent.Data.TryGetValue("Exception", out string? antecedentException))
            {
                antecedentException = JobParameterInjectionFilter.DefaultException;
            }

            connection.SetJobParameter(continuationId, "AntecedentException", antecedentException);
        }
        else if (FailedState.StateName.Equals(parent.Name, StringComparison.OrdinalIgnoreCase) &&
                 TryGetFailedException(parent.Data, out string? failedException))
        {
            connection.SetJobParameter(continuationId, "AntecedentException", failedException);
        }
    }

    // A failed parent's exception, as [FromException] reads it
    private static bool TryGetFailedException(IDictionary<string, string> data, [NotNullWhen(true)] out string? serialized)
    {
        serialized = null;
        if (!data.TryGetValue("ExceptionType", out string? type) || string.IsNullOrEmpty(type))
        {
            return false;
        }

        data.TryGetValue("ExceptionMessage", out string? message);
        serialized = SerializationHelper.Serialize(new ExceptionInfo(type, message, null), SerializationOption.User);
        return true;
    }

    /// <summary>Whether the continuations are handed the results of their parents (<c>UseResultsInContinuations</c>).</summary>
    internal static bool PushesResults()
    {
        return GlobalJobFilters.Filters.Any(static x => x.Instance is ContinuationsSupportAttribute { PushResults: true });
    }

    /// <summary>
    /// What a parent that reaches a final state, fails for good or starts processing decides for a continuation
    /// that waits for it with these options: it starts, it is skipped (deleted, its condition not met), or it keeps
    /// waiting. The decision this filter takes then, and what tells whether a waiting step of a chain is about to
    /// start.
    /// </summary>
    internal static FanInContinuationsSupportAttribute.Decision Decide(string? parentStateName, JobContinuationOptions options, HashSet<string> knownFinalStates)
    {
        if (parentStateName == null)
        {
            return FanInContinuationsSupportAttribute.Decision.Undecided;
        }

        if (ShouldStartContinuation(parentStateName, options, addedLate: false, knownFinalStates))
        {
            return FanInContinuationsSupportAttribute.Decision.Start;
        }

        // A failure or the start of processing leaves the continuations that wait for another outcome waiting
        return !IsIntermediateTrigger(parentStateName) && knownFinalStates.Contains(parentStateName)
            ? FanInContinuationsSupportAttribute.Decision.Skip
            : FanInContinuationsSupportAttribute.Decision.Undecided;
    }

    private bool ShouldStartContinuation(string antecedentStateName, JobContinuationOptions options, bool addedLate)
    {
        return ShouldStartContinuation(antecedentStateName, options, addedLate, _knownFinalStates);
    }

    // addedLate: the continuation is added after the parent reached this state, so a processing continuation
    // also starts for a parent that already processed (succeeded or failed)
    private static bool ShouldStartContinuation(string antecedentStateName, JobContinuationOptions options, bool addedLate, HashSet<string> knownFinalStates)
    {
        if (options.HasFlag(JobContinuationOptions.OnlyOnFailedState) &&
            FailedState.StateName.Equals(antecedentStateName, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (options.HasFlag(JobContinuationOptions.OnlyOnProcessingState) &&
            (ProcessingState.StateName.Equals(antecedentStateName, StringComparison.OrdinalIgnoreCase) ||
             (addedLate && (SucceededState.StateName.Equals(antecedentStateName, StringComparison.OrdinalIgnoreCase) ||
                            FailedState.StateName.Equals(antecedentStateName, StringComparison.OrdinalIgnoreCase)))))
        {
            return true;
        }

        if (options == JobContinuationOptions.OnAnyFinishedState)
        {
            return knownFinalStates.Contains(antecedentStateName);
        }

        if (options.HasFlag(JobContinuationOptions.OnlyOnSucceededState) &&
            SucceededState.StateName.Equals(antecedentStateName, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (options.HasFlag(JobContinuationOptions.OnlyOnDeletedState) &&
            DeletedState.StateName.Equals(antecedentStateName, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    private static void SetContinuations(
        IStorageConnection connection, string jobId, List<Continuation> continuations)
    {
        connection.SetJobParameter(jobId, "Continuations", SerializationHelper.Serialize(continuations));
    }

    private static List<Continuation> GetContinuations(ElectStateContext context, string? jobId)
    {
        // We are altering continuation list only when its background job is locked,
        // and parameter snapshot is obtained only when background job is locked, so
        // it's safe to use cached list when possible.
        string? serialized;

        if (string.IsNullOrEmpty(jobId) && context.BackgroundJob.ParametersSnapshot != null)
        {
            // A snapshot without the parameter means the job has no continuations: no storage read
            if (!context.BackgroundJob.ParametersSnapshot.TryGetValue("Continuations", out serialized))
            {
                return [];
            }
        }
        else
        {
            serialized = context.Connection.GetJobParameter(jobId ?? context.BackgroundJob.Id, "Continuations");
        }

        return DeserializeContinuations(serialized);
    }

    void IApplyStateFilter.OnStateUnapplied(ApplyStateContext context, IWriteOnlyTransaction transaction)
    {
    }

    internal struct Continuation
    {
        public string JobId { get; set; }
        public JobContinuationOptions Options { get; set; }
    }
}
