namespace Masterly.NightShift.Dashboard.Pages;

internal partial class TaggedJobsPage(string tag)
{
    public string Tag { get; } = tag;

    /// <summary>
    /// <c>POST /tags/forget-expired/{tag}?from=&amp;count=</c>: removes from the tag the ids of the jobs on that
    /// page that no longer exist. The page itself only hides them, because showing a page must not write.
    /// Answers <c>false</c> (422) for an invalid tag.
    /// </summary>
    internal static bool ForgetExpired(DashboardContext context)
    {
        string tag = RouteValues.Get(context, "Tag");
        int from = int.TryParse(context.Request.GetQuery("from"), out int fromValue) ? fromValue : 0;
        int perPage = int.TryParse(context.Request.GetQuery("count"), out int countValue) ? countValue : 0;

        try
        {
            using IStorageConnection connection = context.Storage.GetConnection();
            Pager pager = new(from, perPage, context.Options.DefaultRecordsPerPage, JobTags.Count(connection, tag));
            List<string> expired = [];

            ReadJobs(connection, tag, pager, expired);
            JobTags.Forget(connection, tag, expired);
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
        List<KeyValuePair<string, JobData>> jobs = [];

        foreach (string jobId in JobTags.GetJobIds(connection, tag, pager.FromRecord, pager.RecordsPerPage))
        {
            JobData? data = connection.GetJobData(jobId);
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
