#nullable enable

using System;
using Masterly.NightShift.Storage;
using Masterly.NightShift.Storage.Monitoring;

namespace Masterly.NightShift
{
    /// <summary>
    /// What a bulk operation did, such as <see cref="BatchJob.Cancel(string, TimeSpan?)"/>,
    /// <see cref="BatchJob.RequeueFailed(string, TimeSpan?)"/> or
    /// <see cref="JobStorageExtensions.RequeueFailedJobs(JobStorage, Func{FailedJobDto, bool}, TimeSpan?)"/>.
    /// </summary>
    public sealed class BulkJobResult
    {
        /// <summary>Initializes the result.</summary>
        /// <param name="processed">The number of jobs whose state was changed.</param>
        /// <param name="remaining">The number of jobs not reached, or <c>null</c> when it isn't known.</param>
        /// <param name="complete">Whether the operation reached the end of its jobs.</param>
        public BulkJobResult(int processed, long? remaining, bool complete)
        {
            Processed = processed;
            Remaining = remaining;
            Complete = complete;
        }

        /// <summary>The number of jobs whose state was changed.</summary>
        public int Processed { get; }

        /// <summary>
        /// <c>0</c> when the operation is complete. Otherwise an upper bound of the jobs another call would change,
        /// or <c>null</c> when the storage can't count them.
        /// </summary>
        public long? Remaining { get; }

        /// <summary>
        /// <c>false</c> when the operation stopped at its time budget before reaching every job. Calling it again goes
        /// on with the jobs that are left.
        /// </summary>
        public bool Complete { get; }
    }
}
