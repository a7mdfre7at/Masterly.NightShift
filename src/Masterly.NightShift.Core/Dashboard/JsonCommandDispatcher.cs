using System.Net;

namespace Masterly.NightShift.Dashboard;

/// <summary>
/// A dashboard command that answers with a status and, when it is refused, the reason as <c>{ "error": "..." }</c>,
/// which the dashboard's scripts show. POST only, refused for read-only users, and recorded in the audit log.
/// </summary>
/// <param name="command">
/// Performs the command and returns the status: 204 when done, another code (409 for a conflict) when it isn't. An
/// <see cref="InvalidOperationException"/> or an <see cref="ArgumentException"/> answers 422 with its message.
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
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or FormatException or TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            result = CommandResult.Refused(422, RecurringJobUpdateDispatcher.GetMessage(ex));
        }

        List<string> targets = [];
        if (context.UriMatch != null)
        {
            for (int i = 1; i < context.UriMatch.Groups.Count; i++)
            {
                string value = context.UriMatch.Groups[i].Value;
                if (!string.IsNullOrEmpty(value)) targets.Add(RouteValues.Decode(value));
            }
        }

        AuditLog.Record(context, targets, result.Detail, result.StatusCode);

        if (result.Error != null)
        {
            await DashboardJson.WriteAsync(context, result.StatusCode, new { error = result.Error }).ConfigureAwait(false);
        }
        else
        {
            context.Response.StatusCode = result.StatusCode;
        }
    }
}

/// <summary>What a <see cref="JsonCommandDispatcher"/> command did.</summary>
internal sealed record CommandResult(int StatusCode, string? Error, string? Detail)
{
    public static CommandResult Done(string? detail = null) => new((int)HttpStatusCode.NoContent, null, detail);

    public static CommandResult Refused(int statusCode, string error) => new(statusCode, error, null);
}
