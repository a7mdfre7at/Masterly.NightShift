namespace Masterly.NightShift.States;

/// <summary>
/// How an election filter says "not now" about a job a worker is about to process, and how the worker hears it.
/// The worker hands one to the state change that moves the job to Processing
/// (<see cref="StateChangeContext.GiveBack"/>); a filter asks through <see cref="ElectStateContext.TryGiveBack"/>.
/// The state change then stores nothing at all (no state, no history entry, no job parameter, nothing a later
/// filter would have written), releases the job's lock and returns <c>null</c>; the worker waits for
/// <see cref="Pause"/> with the job still invisible to the other workers, and puts it back in its queue with
/// <see cref="IFetchedJob.Requeue"/>. Only a worker offers it: every other state change has none, and a filter
/// that asks is told no.
/// </summary>
internal sealed class JobGiveBack
{
    /// <summary>Whether a filter asked for the job to be given back during the current attempt.</summary>
    public bool Requested { get; private set; }

    /// <summary>How long the worker keeps the job to itself before it puts it back.</summary>
    public TimeSpan Pause { get; private set; }

    /// <summary>Asks for the job to be given back after <paramref name="pause"/>.</summary>
    public void Request(TimeSpan pause)
    {
        Requested = true;
        Pause = pause > TimeSpan.Zero ? pause : TimeSpan.Zero;
    }

    /// <summary>Forgets what an earlier attempt of the same state change asked for.</summary>
    public void Reset()
    {
        Requested = false;
        Pause = TimeSpan.Zero;
    }
}
