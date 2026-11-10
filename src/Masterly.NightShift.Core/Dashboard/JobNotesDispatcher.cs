using System.Net;

namespace Masterly.NightShift.Dashboard;

/// <summary>
/// <c>POST /jobs/notes/add/{jobId}</c> with form fields <c>text</c> and <c>ack</c> (<c>true</c> to acknowledge):
/// adds a <see cref="JobNote"/>. Answers <c>204</c>, <c>404</c> for an unknown job, <c>422</c> for an empty note.
/// </summary>
internal sealed class JobNotesDispatcher : IDashboardDispatcher
{
    public async Task Dispatch(DashboardContext context)
    {
        if (context.IsReadOnly)
        {
            context.Response.StatusCode = 401;
            return;
        }

        if (!"POST".Equals(context.Request.Method, StringComparison.OrdinalIgnoreCase))
        {
            context.Response.StatusCode = (int)HttpStatusCode.MethodNotAllowed;
            return;
        }

        string jobId = RouteValues.Get(context, "JobId");
        string text = ((await context.Request.GetFormValuesAsync("text").ConfigureAwait(false))?.FirstOrDefault() ?? string.Empty).Trim();
        string? ackValue = (await context.Request.GetFormValuesAsync("ack").ConfigureAwait(false))?.FirstOrDefault();
        bool acknowledges = ackValue is "true" or "on" or "1";

        // The page's text box allows JobNotes.MaxLength characters; longer text didn't come from it
        if ((text.Length == 0 && !acknowledges) || text.Length > JobNotes.MaxLength)
        {
            context.Response.StatusCode = 422;
            return;
        }

        using (IStorageConnection connection = context.Storage.GetConnection())
        {
            if (connection.GetJobData(jobId) == null)
            {
                context.Response.StatusCode = (int)HttpStatusCode.NotFound;
                return;
            }

            JobNotes.Add(connection, jobId, text, context.GetUserName(), acknowledges);
        }

        AuditLog.Record(context, [jobId], acknowledges ? "acknowledged" + (text.Length > 0 ? ": " + text : string.Empty) : text);
        context.Response.StatusCode = (int)HttpStatusCode.NoContent;
    }
}
