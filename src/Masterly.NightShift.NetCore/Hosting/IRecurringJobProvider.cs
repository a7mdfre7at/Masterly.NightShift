namespace Masterly.NightShift;

/// <summary>
/// Declares recurring jobs that a part of the application contributes. Every registered provider runs once
/// when the host starts. Register one with <c>AddNightShiftRecurringJobs&lt;TProvider&gt;()</c>.
/// </summary>
public interface IRecurringJobProvider
{
    /// <summary>Adds this provider's recurring jobs to the set.</summary>
    /// <param name="jobs">The set the application applies at start-up.</param>
    void DeclareJobs(RecurringJobSet jobs);
}
