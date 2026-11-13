using System.Diagnostics;

namespace Masterly.NightShift.Server;

/// <summary>
/// How long the workers of one pool may wait with a job in hand (see <see cref="JobLookAgain"/>): a worker that
/// waits for the first step of a chain to be recorded keeps that job and takes no other, so what it costs the
/// other jobs of the pool is bounded here, whatever the number of chains being created and of workers.
/// </summary>
/// <remarks>
/// <para>
/// A wait costs the other jobs nothing while another worker of the pool is looking for a job
/// (<see cref="EnterFetch"/>): whatever becomes runnable in a queue of the pool is taken by that worker at once.
/// It costs them time from the moment no worker is: every worker is then waiting, or running a job. That time is
/// counted, by the clock of the machine (<see cref="Stopwatch"/>, not the application's clock, which a test or a
/// simulation may stop), and the pool is given <see cref="Budget"/> of it in any <see cref="Window"/>. Once it
/// is used up, no worker of the pool is offered to wait until time has passed, and the filter that would have
/// asked holds the job the way it did before workers waited.
/// </para>
/// <para>
/// So a job that is runnable in a queue of the pool waits for waiting workers for <see cref="Budget"/> at most in
/// any <see cref="Window"/> it spends in its queue, plus the wait in progress when the budget runs out
/// (<see cref="MaxPause"/> at most). And a single job is waited for <see cref="MaxPerJob"/> at most.
/// </para>
/// </remarks>
internal sealed class WorkerWaits
{
    /// <summary>How long a pool may have a worker waiting and none looking for a job, in any <see cref="Window"/>.</summary>
    public static readonly TimeSpan DefaultBudget = TimeSpan.FromSeconds(2);

    /// <summary>The time over which <see cref="DefaultBudget"/> is counted.</summary>
    public static readonly TimeSpan DefaultWindow = TimeSpan.FromSeconds(10);

    /// <summary>How long a worker waits for one job at most, all its looks together, by the clock of the machine.</summary>
    public static readonly TimeSpan DefaultMaxPerJob = TimeSpan.FromSeconds(5);

    /// <summary>The shortest time between two looks at a job.</summary>
    public static readonly TimeSpan MinPause = TimeSpan.FromMilliseconds(10);

    /// <summary>The longest time between two looks at a job.</summary>
    public static readonly TimeSpan MaxPause = TimeSpan.FromMilliseconds(250);

    // The window is counted in this many slots, and one more: what was spent leaves the count a slot at a time
    private const int Slots = 10;

    private readonly NightShiftLock _lock = new();
    private readonly Func<long> _timestamp;
    private readonly long _budget;
    private readonly long _slotLength;
    private readonly long[] _spent = new long[Slots + 1];
    private readonly long[] _slotOf = new long[Slots + 1];

    private int _fetching;
    private int _waiting;

    // Since when a worker waits and none looks for a job, or -1 while that isn't so
    private long _costlySince = -1;

    public WorkerWaits()
        : this(DefaultBudget, DefaultWindow, DefaultMaxPerJob, Stopwatch.GetTimestamp)
    {
    }

    /// <param name="budget">See <see cref="Budget"/>.</param>
    /// <param name="window">See <see cref="Window"/>.</param>
    /// <param name="maxPerJob">See <see cref="MaxPerJob"/>.</param>
    /// <param name="timestamp">The clock, in the units of <see cref="Stopwatch.GetTimestamp"/>. Replaceable for tests.</param>
    internal WorkerWaits(TimeSpan budget, TimeSpan window, TimeSpan maxPerJob, Func<long> timestamp)
    {
        Budget = budget;
        Window = window;
        MaxPerJob = maxPerJob;

        _timestamp = timestamp;
        _budget = ToTimestamp(budget);
        _slotLength = Math.Max(1, ToTimestamp(window) / Slots);
        Array.Fill(_slotOf, -1);
    }

    /// <summary>How long the pool may have a worker waiting and none looking for a job, in any <see cref="Window"/>.</summary>
    public TimeSpan Budget { get; }

    /// <summary>The time over which <see cref="Budget"/> is counted. Counted in slots, so a little longer rather than shorter.</summary>
    public TimeSpan Window { get; }

    /// <summary>How long a worker waits for one job at most, all its looks together.</summary>
    public TimeSpan MaxPerJob { get; }

    /// <summary>Whether a worker of the pool may wait now: the pool hasn't used up its budget.</summary>
    public bool CanWait
    {
        get
        {
            lock (_lock)
            {
                long now = _timestamp();
                Settle(now);
                return Spent(now) < _budget;
            }
        }
    }

    /// <summary>A worker of the pool starts looking for a job: while it does, the waits of the others cost nothing.</summary>
    public void EnterFetch()
    {
        lock (_lock)
        {
            Settle(_timestamp());
            _fetching++;
            _costlySince = -1;
        }
    }

    /// <summary>A worker of the pool has a job, or gave up looking for one.</summary>
    public void LeaveFetch()
    {
        lock (_lock)
        {
            long now = _timestamp();
            Settle(now);
            _fetching--;
            Mark(now);
        }
    }

    /// <summary>
    /// A worker is about to wait for <paramref name="pause"/> with a job in hand. Returns how long it may: the
    /// pause, or less when no other worker is looking for a job and the budget doesn't cover it, or
    /// <see cref="TimeSpan.Zero"/> when the budget is used up, and the worker doesn't wait. A worker that was
    /// told a time calls <see cref="End"/> when it has waited.
    /// </summary>
    public TimeSpan Begin(TimeSpan pause)
    {
        lock (_lock)
        {
            long now = _timestamp();
            Settle(now);

            long left = _budget - Spent(now);
            if (left <= 0 || pause <= TimeSpan.Zero)
            {
                return TimeSpan.Zero;
            }

            _waiting++;
            Mark(now);

            // Counted from now on: not longer than what is left. A wait that begins while another worker looks
            // for a job may run into the budget's end when that worker finds one: by one pause at most.
            if (_costlySince >= 0 && ToTimestamp(pause) > left)
            {
                return FromTimestamp(left);
            }

            return pause;
        }
    }

    /// <summary>The worker has waited.</summary>
    public void End()
    {
        lock (_lock)
        {
            long now = _timestamp();
            Settle(now);
            _waiting--;
            Mark(now);
        }
    }

    private static long ToTimestamp(TimeSpan time) => (long)(time.TotalSeconds * Stopwatch.Frequency);

    private static TimeSpan FromTimestamp(long timestamp) => TimeSpan.FromSeconds((double)timestamp / Stopwatch.Frequency);

    // Counts the time since the last change during which a worker waited and none looked for a job
    private void Settle(long now)
    {
        if (_costlySince < 0)
        {
            return;
        }

        long elapsed = now - _costlySince;
        _costlySince = now;

        if (elapsed <= 0)
        {
            return;
        }

        long slot = now / _slotLength;
        int index = (int)(slot % _spent.Length);

        if (_slotOf[index] != slot)
        {
            _slotOf[index] = slot;
            _spent[index] = 0;
        }

        _spent[index] += elapsed;
    }

    // Starts or stops the count, after the number of workers that wait or look for a job changed
    private void Mark(long now)
    {
        if (_waiting > 0 && _fetching == 0)
        {
            if (_costlySince < 0)
            {
                _costlySince = now;
            }
        }
        else
        {
            _costlySince = -1;
        }
    }

    // What was counted in the window that ends now: this slot and the ten before it
    private long Spent(long now)
    {
        long slot = now / _slotLength;
        long spent = 0;

        for (int i = 0; i < _spent.Length; i++)
        {
            if (_slotOf[i] >= 0 && slot - _slotOf[i] <= Slots)
            {
                spent += _spent[i];
            }
        }

        return spent;
    }
}
