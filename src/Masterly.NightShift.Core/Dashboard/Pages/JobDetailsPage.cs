using System;
using System.Linq;
using Masterly.NightShift.Annotations;
using Masterly.NightShift.Common;
using Masterly.NightShift.States;
using Masterly.NightShift.Storage.Monitoring;

namespace Masterly.NightShift.Dashboard.Pages
{
    partial class JobDetailsPage
    {
        public JobDetailsPage(string jobId)
        {
            JobId = jobId;
        }

        public string JobId { get; }

        /// <summary>The job whose page is being rendered, so the history renderers can read its own queue.</summary>
        internal Job RenderedJob { get; set; }

        /// <summary>
        /// The queue "Run again with changes" starts with: the job's own queue (<see cref="Job.Queue"/>), else the
        /// queue it was last enqueued to when that isn't <c>default</c>; empty for <c>default</c>.
        /// </summary>
        internal static string GetRunAgainQueue(JobDetailsDto details)
        {
            if (details?.Job?.Queue != null) return details.Job.Queue;

            var enqueued = details?.History?.FirstOrDefault(static x => x.StateName == EnqueuedState.StateName);
            if (enqueued?.Data != null && enqueued.Data.TryGetValue("Queue", out var queue) &&
                !String.IsNullOrEmpty(queue) && !EnqueuedState.DefaultQueue.Equals(queue, StringComparison.OrdinalIgnoreCase))
            {
                return queue;
            }

            return String.Empty;
        }
    }
}
