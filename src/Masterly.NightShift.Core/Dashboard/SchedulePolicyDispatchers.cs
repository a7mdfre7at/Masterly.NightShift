using System.Globalization;
using System.Net;
using System.Runtime.CompilerServices;
using Masterly.NightShift.Dashboard.Resources;

namespace Masterly.NightShift.Dashboard;

/// <summary>Saves and deletes schedule policies from the dashboard, with the revision the form was read with.</summary>
internal static class SchedulePolicyDispatchers
{
    private const int UnprocessableEntity = 422;

    /// <summary>
    /// Saves the policy of the form. Its name is in the audit entry, whatever the answer, as the save's address
    /// doesn't carry it. A refused value names its form field (<see cref="CommandResult.Field"/>), so the editor
    /// shows the reason under it.
    /// </summary>
    public static async Task<CommandResult> SaveAsync(DashboardContext context)
    {
        string? name = await GetAsync(context, "name");
        if (name == null)
        {
            return new CommandResult(UnprocessableEntity, Strings.PoliciesPage_NameRequired, null) { Field = "name" };
        }

        string detail = $"policy '{name}'";
        StrongBox<string?> field = new("name");

        try
        {
            return await SaveAsync(context, name, detail, field);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or FormatException or OverflowException
                                       or TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return new CommandResult(UnprocessableEntity, RecurringJobUpdateDispatcher.GetMessage(ex), detail) { Field = field.Value };
        }
    }

    public static CommandResult Delete(DashboardContext context)
    {
        string name = RouteValues.Get(context, "Name");
        string revisionText = RouteValues.Get(context, "Revision");

        if (!TryParseRevision(revisionText, out long revision))
        {
            return CommandResult.Refused(UnprocessableEntity, string.Format(CultureInfo.CurrentCulture, Strings.PoliciesPage_NotARevision, revisionText));
        }

        return context.Storage.DeleteSchedulePolicy(name, revision)
            ? CommandResult.Done()
            : CommandResult.Refused((int)HttpStatusCode.Conflict, Strings.PoliciesPage_Conflict);
    }

    // field holds the form field being read, so an exception names the value it refuses (none: the general error)
    private static async Task<CommandResult> SaveAsync(DashboardContext context, string name, string detail, StrongBox<string?> field)
    {
        // A form without a revision creates the policy
        long revision = 0;
        if (await GetAsync(context, "revision") is { } revisionText && !TryParseRevision(revisionText, out revision))
        {
            return new CommandResult(UnprocessableEntity, string.Format(CultureInfo.CurrentCulture, Strings.PoliciesPage_NotARevision, revisionText), detail) { Field = "revision" };
        }

        SchedulePolicy policy = new(name);

        field.Value = "description";
        policy.WithDescription(await GetAsync(context, "description"));

        field.Value = "timezone";
        if (await GetAsync(context, "timezone") is { } timeZoneId)
        {
            policy.InTimeZone((context.Options.TimeZoneResolver ?? GlobalTimeZoneResolver.Current).GetTimeZoneById(timeZoneId));
        }

        field.Value = "days";
        IList<string> days = await context.Request.GetFormValuesAsync("days");
        if (days is { Count: > 0 })
        {
            List<DayOfWeek> allowed = [];
            foreach (string day in days)
            {
                if (!TryParseDay(day, out DayOfWeek parsedDay))
                {
                    return new CommandResult(UnprocessableEntity, string.Format(CultureInfo.CurrentCulture, Strings.PoliciesPage_NotADay, day), detail) { Field = "days" };
                }

                allowed.Add(parsedDay);
            }

            policy.AllowWeekdays([.. allowed]);
        }

        field.Value = "windows";
        foreach (string window in List(await GetAsync(context, "windows")))
        {
            ScheduleWindow parsedWindow = ScheduleWindow.Parse(window);
            policy.AllowBetween(parsedWindow.Start, parsedWindow.End);
        }

        field.Value = "dates";
        foreach (string date in List(await GetAsync(context, "dates")))
        {
            if (date.Length == 5)
            {
                AnnualDate annual = AnnualDate.Parse(date);
                policy.ExcludeAnnualDate(annual.Month, annual.Day);
            }
            else
            {
                policy.ExcludeDate(DateOnly.ParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture));
            }
        }

        field.Value = "providers";
        foreach (string provider in List(await GetAsync(context, "providers")))
        {
            policy.ExcludeDatesFrom(provider);
        }

        field.Value = "require";
        foreach (string required in List(await GetAsync(context, "require")))
        {
            policy.Require(required);
        }

        field.Value = "except";
        foreach (string excepted in List(await GetAsync(context, "except")))
        {
            policy.ExceptWhen(excepted);
        }

        // Whatever else the storage refuses isn't about one field
        field.Value = null;
        long? saved;
        try
        {
            saved = context.Storage.UpdateSchedulePolicy(policy, revision);
        }
        catch (ArgumentException)
        {
            // The storage refuses a policy it refers to that isn't stored, or a cycle: the field of the list that
            // names the missing policy, or none for a cycle
            field.Value = FieldOfMissingReference(context.Storage, policy);
            throw;
        }

        if (saved.HasValue)
        {
            return CommandResult.Done($"{detail}, revision {saved.Value.ToString(CultureInfo.InvariantCulture)}");
        }

        // A new policy whose name is taken isn't a change made meanwhile: the name is what to change
        return revision == 0
            ? new CommandResult((int)HttpStatusCode.Conflict, string.Format(CultureInfo.CurrentCulture, Strings.PoliciesPage_Exists, policy.Name), detail) { Field = "name" }
            : new CommandResult((int)HttpStatusCode.Conflict, Strings.PoliciesPage_Conflict, detail);
    }

    // The storage checks the required policies first, then the exceptions
    private static string? FieldOfMissingReference(JobStorage storage, SchedulePolicy policy)
    {
        using IStorageConnection connection = storage.GetConnection();
        if (policy.Required.Any(x => !SchedulePolicies.Exists(connection, x)))
        {
            return "require";
        }

        return policy.Excepted.Any(x => !SchedulePolicies.Exists(connection, x)) ? "except" : null;
    }

    private static bool TryParseRevision(string text, out long revision)
    {
        return long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out revision);
    }

    // A day's name; a number only when it names a day, so "99999999999" is refused rather than overflowing
    private static bool TryParseDay(string text, out DayOfWeek day)
    {
        return Enum.TryParse(text.Trim(), ignoreCase: true, out day) && Enum.IsDefined(day);
    }

    private static async Task<string?> GetAsync(DashboardContext context, string key)
    {
        IList<string> values = await context.Request.GetFormValuesAsync(key).ConfigureAwait(false);
        string? value = values?.FirstOrDefault();
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static IEnumerable<string> List(string? text)
    {
        return text?.Split([',', '\n', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [];
    }
}
