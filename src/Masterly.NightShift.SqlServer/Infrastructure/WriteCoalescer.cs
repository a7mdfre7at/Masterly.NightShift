namespace Masterly.NightShift.SqlServer;

/// <summary>
/// Lets the callers that want to write at the same time write together, once there are many of them. Up to
/// <c>maxWriters</c> callers write at once, each alone, as if this class wasn't there: the database handles
/// that many in parallel well. A caller that finds that many already writing waits, and when one of them is
/// done, the callers waiting are written as one group by one of them, in one round trip. Nobody waits for more
/// callers to show up, so few callers lose nothing, and the more callers there are, the larger the groups get.
/// </summary>
/// <remarks>
/// When writing a group fails, every member writes alone, so one bad request only fails its own caller.
/// </remarks>
/// <typeparam name="TRequest">What a caller wants written.</typeparam>
internal sealed class WriteCoalescer<TRequest>(Action<IReadOnlyList<TRequest>> writeTogether, int maxGroupSize, int maxWriters)
    where TRequest : class
{
    private readonly object _gate = new();
    private readonly Queue<Waiter> _waiting = new();
    private int _writers;

    /// <summary>
    /// Writes the request: alone with <paramref name="writeAlone"/>, or together with others. Returns when it is
    /// written, and throws what <paramref name="writeAlone"/> throws.
    /// </summary>
    public void Write(TRequest request, Action<TRequest> writeAlone)
    {
        Waiter? waiter = null;

        lock (_gate)
        {
            if (_writers >= maxWriters)
            {
                waiter = new Waiter(request);
                _waiting.Enqueue(waiter);
            }
            else
            {
                _writers++;
            }
        }

        if (waiter == null)
        {
            // Few enough are writing
            try
            {
                writeAlone(request);
            }
            finally
            {
                PassOn();
            }

            return;
        }

        waiter.Signal.Wait();
        waiter.Signal.Dispose();

        switch (waiter.Outcome)
        {
            case Outcome.Written:
                return;

            case Outcome.WriteAlone:
                writeAlone(request);
                return;

            default:
                WriteGroup(waiter, writeAlone);
                return;
        }
    }

    // Called by the member of a group that was chosen to write it
    private void WriteGroup(Waiter leader, Action<TRequest> writeAlone)
    {
        List<Waiter> group = leader.Group!; // Set for the waiter chosen to write
        bool together = group.Count > 1;
        bool written = false;

        try
        {
            if (together)
            {
                TRequest[] requests = new TRequest[group.Count];
                for (int i = 0; i < requests.Length; i++)
                {
                    requests[i] = group[i].Request;
                }

                try
                {
                    writeTogether(requests);
                    written = true;
                }
                catch (Exception ex) when (ex.IsCatchableExceptionType())
                {
                    // Every member writes alone below, and gets its own exception if it is the one that fails
                }
            }
        }
        finally
        {
            // The others first, so they don't wait for this caller's own write
            for (int i = 1; i < group.Count; i++)
            {
                group[i].Complete(written ? Outcome.Written : Outcome.WriteAlone);
            }

            PassOn();
        }

        if (!written)
        {
            writeAlone(leader.Request);
        }
    }

    // Lets the callers that are waiting write as a group in this writer's place, or gives the place up
    private void PassOn()
    {
        Waiter leader;

        lock (_gate)
        {
            if (_waiting.Count == 0)
            {
                _writers--;
                return;
            }

            List<Waiter> group = new(Math.Min(_waiting.Count, maxGroupSize));
            while (group.Count < maxGroupSize && _waiting.TryDequeue(out Waiter? next))
            {
                group.Add(next);
            }

            leader = group[0];
            leader.Group = group;
        }

        leader.Complete(Outcome.WriteGroup);
    }

    private enum Outcome
    {
        WriteGroup,
        Written,
        WriteAlone,
    }

    private sealed class Waiter(TRequest request)
    {
        public TRequest Request { get; } = request;
        public ManualResetEventSlim Signal { get; } = new(false);
        public Outcome Outcome { get; private set; }
        public List<Waiter>? Group { get; set; }

        public void Complete(Outcome outcome)
        {
            Outcome = outcome;
            Signal.Set();
        }
    }
}
