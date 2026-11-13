namespace Masterly.NightShift.States;

/// <summary>
/// How an election filter says "not now, look again in a moment" about a job a worker is about to process, and
/// how the worker hears it. The worker hands one to the state change that moves the job to Processing
/// (<see cref="StateChangeContext.LookAgain"/>); a filter asks through <see cref="ElectStateContext.TryLookAgain"/>.
/// The state change then stores nothing at all (no state, no history entry, no job parameter, nothing a later
/// filter would have written), releases the job's lock and returns <c>null</c>. The worker keeps the job it
/// fetched, invisible to the other workers, waits for <see cref="Pause"/> and changes the state again: the job
/// doesn't go back to its queue, so it is fetched once, however often it is looked at. Only when the server
/// stops meanwhile does the worker put the job back with <see cref="IFetchedJob.Requeue"/>, as it does with any
/// job it can't finish.
/// </summary>
/// <remarks>
/// Only a worker offers it, only to the first election filter (by default <see cref="JobChainStepFilter"/>), and
/// only while its pool can afford to wait (<c>WorkerWaits</c>): every other state change has none, no filter has
/// seen the job when the worker waits, and a filter that asks otherwise is told no and decides another way.
/// </remarks>
internal sealed class JobLookAgain
{
    /// <summary>Whether a filter asked the worker to look again, during the current attempt.</summary>
    public bool Requested { get; private set; }

    /// <summary>How long the filter asks the worker to wait before it looks again.</summary>
    public TimeSpan Pause { get; private set; }

    /// <summary>Asks the worker to look again after <paramref name="pause"/>.</summary>
    public void Request(TimeSpan pause)
    {
        Requested = true;
        Pause = pause > TimeSpan.Zero ? pause : TimeSpan.Zero;
    }

    /// <summary>Forgets what an earlier attempt of the state change asked for.</summary>
    public void Reset()
    {
        Requested = false;
        Pause = TimeSpan.Zero;
    }
}
