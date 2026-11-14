
using System.Text.Json.Serialization;

namespace Masterly.NightShift.States;

/// <summary>
/// How a fan-in continuation waits for its parents.
/// </summary>
public enum FanInMode
{
    /// <summary>The continuation starts once every parent has finished (and satisfied the options).</summary>
    All,

    /// <summary>The continuation starts as soon as one parent has finished and satisfied the options.</summary>
    Any,

    /// <summary>
    /// The continuation starts as soon as one parent fails for good (no retry left), for alerting or compensating
    /// work. It is skipped once every parent finished without failing. The options are ignored.
    /// </summary>
    AnyFailed,

    /// <summary>
    /// The continuation starts as soon as one parent fails for good or is deleted. It is skipped once every parent
    /// succeeded. The options are ignored.
    /// </summary>
    AnyFailedOrCanceled,

    /// <summary>
    /// The continuation starts once every parent has finished, whatever the outcome: succeeded, deleted or failed for
    /// good. Never skipped. The options are ignored.
    /// </summary>
    AllCompleted,
}

/// <summary>
/// The state of a job that waits for a <em>set</em> of parent jobs: fan-out/fan-in without a batch package.
/// Shares the <c>Awaiting</c> state name with <see cref="AwaitingState"/>, so these jobs appear on the same
/// dashboard page and use the same state handler, but they are driven by
/// <see cref="FanInContinuationsSupportAttribute"/> instead of <see cref="ContinuationsSupportAttribute"/>.
/// </summary>
public sealed class AwaitingJobsState : IState
{
    /// <summary>The state name, identical to <see cref="AwaitingState.StateName"/>.</summary>
    public static readonly string StateName = AwaitingState.StateName;

    /// <summary>Creates the state for jobs waiting for all the given parents to succeed.</summary>
    public AwaitingJobsState([NotNull] IEnumerable<string> parentIds)
        : this(parentIds, new EnqueuedState(), JobContinuationOptions.OnlyOnSucceededState, FanInMode.All)
    {
    }

    /// <summary>Creates the state.</summary>
    /// <param name="parentIds">Identifiers of the parent jobs. Duplicates are removed.</param>
    /// <param name="nextState">The state the job moves to when the condition is met.</param>
    /// <param name="options">Which parent outcomes count as satisfying the condition.</param>
    /// <param name="mode">Whether every parent or any parent has to satisfy the condition.</param>
    [JsonConstructor]
    public AwaitingJobsState(
        [NotNull] IEnumerable<string> parentIds,
        [NotNull] IState nextState,
        JobContinuationOptions options,
        FanInMode mode)
    {
        ArgumentNullException.ThrowIfNull(parentIds);
        ArgumentNullException.ThrowIfNull(nextState);

        ParentIds = [.. parentIds.Where(static x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.Ordinal)];
        if (ParentIds.Count == 0)
        {
            throw new ArgumentException("At least one parent job identifier is required.", nameof(parentIds));
        }

        NextState = nextState;
        Options = options;
        Mode = mode;
    }

    /// <summary>The parent job identifiers.</summary>
    [NotNull]
    public IReadOnlyList<string> ParentIds { get; }

    /// <summary>The state applied once the condition is met.</summary>
    [NotNull]
    public IState NextState { get; }

    /// <summary>Which parent outcomes satisfy the condition.</summary>
    public JobContinuationOptions Options { get; }

    /// <summary>Whether all parents or any parent has to finish.</summary>
    public FanInMode Mode { get; }

    /// <inheritdoc />
    [JsonIgnore]
    public string Name => StateName;

    /// <inheritdoc />
    public string? Reason { get; set; }

    /// <summary>
    /// What parents whose jobs may be gone finished as, by parent: a step of a chain created again by a retry waits
    /// for the steps beside the retried one, which the chain kept. A parent whose job doesn't exist when the state is
    /// elected counts as this outcome rather than refusing the state. Never stored: it is written, as each parent
    /// writes what it finished as, for the job while it waits (<see cref="FanInContinuationsSupportAttribute"/>).
    /// </summary>
    internal IReadOnlyDictionary<string, string>? KnownOutcomes { get; init; }

    /// <inheritdoc />
    [JsonIgnore]
    public bool IsFinal => false;

    /// <inheritdoc />
    [JsonIgnore]
    public bool IgnoreJobLoadException => false;

    /// <inheritdoc />
    public Dictionary<string, string> SerializeData()
    {
        return new Dictionary<string, string>
        {
            { "ParentId", ParentIds[0] },
            { "ParentIds", string.Join(",", ParentIds) },
            { "Mode", Mode.ToString("G") },
            { "NextState", SerializationHelper.Serialize(NextState, SerializationOption.TypedInternal) },
            { "Options", Options.ToString("D") },
        };
    }

    /// <summary>
    /// Rebuilds the waiting condition from serialized state data, or returns <c>null</c> when the data belongs
    /// to an ordinary <see cref="AwaitingState"/>.
    /// </summary>
    public static AwaitingJobsState? FromStateData([NotNull] IDictionary<string, string> data)
    {
        ArgumentNullException.ThrowIfNull(data);

        if (!data.TryGetValue("ParentIds", out string? parentIds) || string.IsNullOrEmpty(parentIds))
        {
            return null;
        }

        FanInMode mode = data.TryGetValue("Mode", out string? modeValue) && Enum.TryParse(modeValue, out FanInMode parsedMode)
            ? parsedMode
            : FanInMode.All;

        JobContinuationOptions options = data.TryGetValue("Options", out string? optionsValue) && Enum.TryParse(optionsValue, out JobContinuationOptions parsedOptions)
            ? parsedOptions
            : JobContinuationOptions.OnlyOnSucceededState;

        IState? nextState = null;
        if (data.TryGetValue("NextState", out string? serializedNextState))
        {
            nextState = SerializationHelper.Deserialize<IState>(serializedNextState, SerializationOption.TypedInternal);
        }

        return new AwaitingJobsState(parentIds.Split(','), nextState ?? new EnqueuedState(), options, mode);
    }
}
