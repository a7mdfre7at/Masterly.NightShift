using System;
using System.Collections.Generic;
using Masterly.NightShift.Storage;

namespace Masterly.NightShift.Dashboard.Pages
{
    partial class TaggedJobsPage
    {
        public TaggedJobsPage(string tag)
        {
            Tag = tag;
        }

        public string Tag { get; }

        /// <summary>
        /// <c>POST /tags/forget-expired/{tag}?from=&amp;count=</c>: removes from the tag the ids of the jobs on that
        /// page that no longer exist. The page itself only hides them, because showing a page must not write.
        /// Answers <c>false</c> (422) for an invalid tag.
        /// </summary>
        internal static bool ForgetExpired(DashboardContext context)
        {
            var tag = RouteValues.Get(context, "Tag");
            var from = Int32.TryParse(context.Request.GetQuery("from"), out var fromValue) ? fromValue : 0;
            var perPage = Int32.TryParse(context.Request.GetQuery("count"), out var countValue) ? countValue : 0;

            try
            {
                using (var connection = context.Storage.GetConnection())
                {
                    var pager = new Pager(from, perPage, context.Options.DefaultRecordsPerPage, JobTags.Count(connection, tag));
                    var expired = new List<string>();

                    ReadJobs(connection, tag, pager, expired);
                    JobTags.Forget(connection, tag, expired);
                }
            }
            catch (ArgumentException)
            {
                return false;
            }

            return true;
        }

        /// <summary>Reads the jobs of one page of a tag. Ids of jobs that no longer exist go to <paramref name="expired"/>.</summary>
        internal static List<KeyValuePair<string, JobData>> ReadJobs(IStorageConnection connection, string tag, Pager pager, ICollection<string> expired)
        {
            var jobs = new List<KeyValuePair<string, JobData>>();

            foreach (var jobId in JobTags.GetJobIds(connection, tag, pager.FromRecord, pager.RecordsPerPage))
            {
                var data = connection.GetJobData(jobId);
                if (data == null)
                {
                    expired.Add(jobId);
                    continue;
                }

                jobs.Add(new KeyValuePair<string, JobData>(jobId, data));
            }

            return jobs;
        }
    }
}
