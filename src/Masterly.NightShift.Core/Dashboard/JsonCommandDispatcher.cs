using System.Net;

namespace Masterly.NightShift.Dashboard;

/// <summary>
/// A dashboard command that answers with a status and, when it is refused, the reason as <c>{ "error": "..." }</c>,
/// which the dashboard's scripts show. POST only, refused for read-only users, and recorded in the audit log, done,
/// refused or failed.
/// </summary>
/// <param name="command">
/// Performs the command and returns the status: 204 when done, another code (409 for a conflict) when it isn't. An
/// <see cref="InvalidOperationException"/>, an <see cref="ArgumentException"/>, an <see cref="OverflowException"/>
/// or a format or time zone error answers 422 with its message; a <see cref="DistributedLockTimeoutException"/>
/// (another change of the same thing is under way) answers 409. Any other exception is recorded with the status 500
/// and thrown.
/// </param>
internal sealed class JsonCommandDispatcher(Func<DashboardContext, Task<CommandResult>> command) : IDashboardDispatcher
{
    public JsonCommandDispatcher(Func<DashboardContext, CommandResult> command)
        : this(context => Task.FromResult(command(context)))
    {
    }

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

        CommandResult result;
        try
        {
            result = await command(context).ConfigureAwait(false);
        }
        catch (DistributedLockTimeoutException)
        {
            result = CommandResult.Refused((int)HttpStatusCode.Conflict, "Another change of the same item is under way. Try again in a moment.");
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or FormatException or OverflowException or TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            result = CommandResult.Refused(422, RecurringJobUpdateDispatcher.GetMessage(ex));
        }
        catch (Exception ex) when (ex.IsCatchableExceptionType())
        {
            // A failure is recorded like a refusal, then the host answers it
            AuditLog.Record(context, GetTargets(context), ex.GetType().Name, (int)HttpStatusCode.InternalServerError);
            throw;
        }

        AuditLog.Record(context, GetTargets(context), result.Detail ?? result.Error, result.StatusCode);

        if (result.Error != null)
        {
            await DashboardJson.WriteAsync(context, result.StatusCode, new { error = result.Error }).ConfigureAwait(false);
        }
        else
        {
            context.Response.StatusCode = result.StatusCode;
        }
    }

    // The route's named groups (a job id, a chain, a policy) are what the command was about
    private static List<string> GetTargets(DashboardContext context)
    {
        List<string> targets = [];
        if (context.UriMatch != null)
        {
            for (int i = 1; i < context.UriMatch.Groups.Count; i++)
            {
                string value = context.UriMatch.Groups[i].Value;
                if (!string.IsNullOrEmpty(value))
                {
                    targets.Add(RouteValues.Decode(value));
                }
            }
        }

        return targets;
    }
}

/// <summary>What a <see cref="JsonCommandDispatcher"/> command did.</summary>
internal sealed record CommandResult(int StatusCode, string? Error, string? Detail)
{
    public static CommandResult Done(string? detail = null) => new((int)HttpStatusCode.NoContent, null, detail);

    public static CommandResult Refused(int statusCode, string error) => new(statusCode, error, null);
}
