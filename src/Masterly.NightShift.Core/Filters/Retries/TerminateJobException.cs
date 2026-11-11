namespace Masterly.NightShift;

/// <summary>How a job that throws <see cref="TerminateJobException"/> ends.</summary>
public enum TerminateJobOutcome
{
    /// <summary>
    /// The job is deleted with the reason <c>Skipped: ...</c>: it had nothing to do, or shouldn't run now. It isn't
    /// retried, and continuations waiting for the <c>Deleted</c> state start.
    /// </summary>
    Skipped = 0,

    /// <summary>
    /// The job is deleted with the reason <c>Canceled: ...</c>: it stopped on its own decision. It isn't retried,
    /// and continuations waiting for the <c>Deleted</c> state start.
    /// </summary>
    Canceled = 1,

    /// <summary>
    /// The job fails at once, without the retries <see cref="AutomaticRetryAttribute"/> would make: retrying can't
    /// help, for example because its input is invalid.
    /// </summary>
    Failed = 2,
}

/// <summary>
/// Thrown by a job to end it now, without retries: <i>"there is nothing to do"</i>, <i>"stop here"</i> or
/// <i>"this can never work"</i>. <see cref="Outcome"/> decides whether the job is deleted as skipped or canceled, or
/// fails without being retried.
/// </summary>
/// <remarks>
/// <para>The exception can be thrown by the job method and by a server filter's <c>OnPerforming</c>. Any other
/// exception still goes through <see cref="AutomaticRetryAttribute"/> as usual.</para>
/// <para>A skipped or canceled job doesn't count as a failure for <see cref="CircuitBreakerAttribute"/> or the
/// insights, and telemetry reports it as <c>skipped</c> or <c>canceled</c>.</para>
/// </remarks>
/// <example>
/// <code>
/// if (invoice.IsPaid)
/// {
///     throw new TerminateJobException("The invoice is already paid");
/// }
///
/// if (!customer.HasValidAddress)
/// {
///     throw new TerminateJobException("No valid address", TerminateJobOutcome.Failed);
/// }
/// </code>
/// </example>
[Serializable]
public class TerminateJobException : Exception
{
    /// <summary>Ends the job as <see cref="TerminateJobOutcome.Skipped"/>.</summary>
    /// <param name="reason">Why the job ends, shown on the dashboard.</param>
    /// <exception cref="ArgumentException"><paramref name="reason"/> is <c>null</c>, empty or white space.</exception>
    public TerminateJobException([NotNull] string reason)
        : this(reason, TerminateJobOutcome.Skipped)
    {
    }

    /// <summary>Ends the job with the given outcome.</summary>
    /// <param name="reason">Why the job ends, shown on the dashboard.</param>
    /// <param name="outcome">How the job ends.</param>
    /// <exception cref="ArgumentException"><paramref name="reason"/> is <c>null</c>, empty or white space.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="outcome"/> isn't a defined value.</exception>
    public TerminateJobException([NotNull] string reason, TerminateJobOutcome outcome)
        : this(reason, outcome, null)
    {
    }

    /// <summary>Ends the job with the given outcome, keeping the exception that led to it.</summary>
    /// <param name="reason">Why the job ends, shown on the dashboard.</param>
    /// <param name="outcome">How the job ends.</param>
    /// <param name="innerException">The exception that led to the decision, or <c>null</c>.</param>
    /// <exception cref="ArgumentException"><paramref name="reason"/> is <c>null</c>, empty or white space.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="outcome"/> isn't a defined value.</exception>
    public TerminateJobException([NotNull] string reason, TerminateJobOutcome outcome, [CanBeNull] Exception? innerException)
        : base(ValidateReason(reason), innerException)
    {
        if (!Enum.IsDefined(outcome)) throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "Unknown outcome.");

        Outcome = outcome;
    }

    /// <summary>Gets how the job ends.</summary>
    public TerminateJobOutcome Outcome { get; }

    /// <summary>Gets whether the job is deleted (skipped or canceled) rather than failed.</summary>
    public bool DeletesJob => Outcome != TerminateJobOutcome.Failed;

    private static string ValidateReason(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return reason;
    }
}
