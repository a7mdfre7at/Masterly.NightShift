namespace Masterly.NightShift;

/// <summary>
/// How long finished jobs are kept, per state, for <c>UseJobRetention</c>. A state left unset keeps the default
/// (a day for succeeded and deleted jobs; failed jobs are kept until deleted).
/// </summary>
public sealed class JobRetentionOptions
{
    private readonly Dictionary<string, TimeSpan> _states = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>How long a succeeded job is kept, or <c>null</c> for the default.</summary>
    public TimeSpan? Succeeded
    {
        get => Get(SucceededState.StateName);
        set => Set(SucceededState.StateName, value);
    }

    /// <summary>How long a deleted job (skipped and canceled ones included) is kept, or <c>null</c> for the default.</summary>
    public TimeSpan? Deleted
    {
        get => Get(DeletedState.StateName);
        set => Set(DeletedState.StateName, value);
    }

    /// <summary>
    /// How long a job that failed for good is kept, or <c>null</c> to keep it until it is deleted or requeued. The
    /// same as <c>UseFailedJobRetention</c>: positive, and at most <see cref="ExpireAfterAttribute.MaxDays"/> days.
    /// </summary>
    public TimeSpan? Failed
    {
        get;
        set
        {
            if (value.HasValue) FailedJobRetentionFilter.Validate(value.Value, nameof(value));
            field = value;
        }
    }

    /// <summary>
    /// Sets how long jobs are kept in a custom final state, or clears it with <c>null</c>.
    /// </summary>
    /// <param name="stateName">The state's name.</param>
    /// <param name="retention">How long, at most <see cref="ExpireAfterAttribute.MaxDays"/> days.</param>
    /// <returns>The same options, for chaining.</returns>
    public JobRetentionOptions Set([NotNull] string stateName, TimeSpan? retention)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateName);

        if (FailedState.StateName.Equals(stateName, StringComparison.OrdinalIgnoreCase))
        {
            Failed = retention;
            return this;
        }

        if (retention.HasValue)
        {
            FailedJobRetentionFilter.Validate(retention.Value, nameof(retention));
            _states[stateName] = retention.Value;
        }
        else
        {
            _states.Remove(stateName);
        }

        return this;
    }

    internal IReadOnlyDictionary<string, TimeSpan> States => _states;

    private TimeSpan? Get(string stateName) => _states.TryGetValue(stateName, out TimeSpan value) ? value : null;
}

/// <summary>
/// Sets how long a job is kept once it reaches a final state, per state. Added to the global filters by
/// <c>UseJobRetention</c>. A per-method <see cref="ExpireAfterAttribute"/> runs later and wins.
/// </summary>
internal sealed class JobRetentionFilter(IReadOnlyDictionary<string, TimeSpan> retention) : IApplyStateFilter
{
    private volatile Dictionary<string, TimeSpan> _retention = new(retention, StringComparer.OrdinalIgnoreCase);

    /// <summary>Replaces the retention, when <c>UseJobRetention</c> is called again.</summary>
    public void Update(IReadOnlyDictionary<string, TimeSpan> retention)
    {
        _retention = new Dictionary<string, TimeSpan>(retention, StringComparer.OrdinalIgnoreCase);
    }

    internal IReadOnlyDictionary<string, TimeSpan> Retention => _retention;

    /// <summary>
    /// Whether the failed-job retention in the global filters was set by <c>UseJobRetention</c>, which then removes it
    /// when called again without <see cref="JobRetentionOptions.Failed"/>.
    /// </summary>
    internal bool SetsFailedRetention { get; set; }

    /// <inheritdoc />
    public void OnStateUnapplied(ApplyStateContext context, IWriteOnlyTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(context);

        // Before every OnStateApplied, like ExpireAfterAttribute, so filters that read the timeout see it
        if (context.NewState.IsFinal && _retention.TryGetValue(context.NewState.Name, out TimeSpan value))
        {
            context.JobExpirationTimeout = value;
        }
    }

    /// <inheritdoc />
    public void OnStateApplied(ApplyStateContext context, IWriteOnlyTransaction transaction)
    {
    }
}
