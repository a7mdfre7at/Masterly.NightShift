namespace Masterly.NightShift
{
    /// <summary>
    /// What <see cref="ThrottleAttribute"/> and <see cref="RateLimitAttribute"/> do with a job that may not run
    /// right now (their <c>Action</c> property). <see cref="CircuitBreakerAttribute"/> always reschedules.
    /// </summary>
    public enum ConcurrencyAction
    {
        /// <summary>
        /// Moves the job back to the Scheduled state until a slot is expected to be free, so the worker is
        /// released at once. The job keeps its id, parameters and history, and runs later on any server.
        /// </summary>
        Reschedule = 0,

        /// <summary>
        /// Holds the worker thread, polling with back-off, until a slot frees up or the wait timeout
        /// expires. The timeout fails the job with <see cref="ConcurrencyLimitException"/>.
        /// </summary>
        Wait = 1,

        /// <summary>
        /// Cancels the job: it is moved to the Deleted state and never runs. For work that is pointless
        /// once another instance is already running, such as a cache refresh.
        /// </summary>
        Skip = 2,
    }
}
