using System.Globalization;
using System.Net;
using Masterly.NightShift.Dashboard.Resources;

namespace Masterly.NightShift.Dashboard;

/// <summary>Saves and deletes schedule policies from the dashboard, with the revision the form was read with.</summary>
internal static class SchedulePolicyDispatchers
{
    private const int UnprocessableEntity = 422;

    /// <summary>
    /// Saves the policy of the form. Its name is in the audit entry, whatever the answer, as the save's address
    /// doesn't carry it.
    /// </summary>
    public static async Task<CommandResult> SaveAsync(DashboardContext context)
    {
        string name = await GetAsync(context, "name") ?? throw new ArgumentException("A name is required.");
        string detail = $"policy '{name}'";

        try
        {
            return await SaveAsync(context, name, detail);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or FormatException or OverflowException
                                       or TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return new CommandResult(UnprocessableEntity, RecurringJobUpdateDispatcher.GetMessage(ex), detail);
        }
    }

    public static CommandResult Delete(DashboardContext context)
    {
        string name = RouteValues.Get(context, "Name");
        string revisionText = RouteValues.Get(context, "Revision");

        if (!TryParseRevision(revisionText, out long revision))
        {
            return CommandResult.Refused(UnprocessableEntity, $"'{revisionText}' isn't a revision.");
        }

        return context.Storage.DeleteSchedulePolicy(name, revision)
            ? CommandResult.Done()
            : CommandResult.Refused((int)HttpStatusCode.Conflict, Strings.PoliciesPage_Conflict);
    }

    private static async Task<CommandResult> SaveAsync(DashboardContext context, string name, string detail)
    {
        // A form without a revision creates the policy
        long revision = 0;
        if (await GetAsync(context, "revision") is { } revisionText && !TryParseRevision(revisionText, out revision))
        {
            return new CommandResult(UnprocessableEntity, $"'{revisionText}' isn't a revision.", detail);
        }

        SchedulePolicy policy = new SchedulePolicy(name).WithDescription(await GetAsync(context, "description"));

        if (await GetAsync(context, "timezone") is { } timeZoneId)
        {
            policy.InTimeZone((context.Options.TimeZoneResolver ?? GlobalTimeZoneResolver.Current).GetTimeZoneById(timeZoneId));
        }

        IList<string> days = await context.Request.GetFormValuesAsync("days");
        if (days is { Count: > 0 })
        {
            List<DayOfWeek> allowed = [];
            foreach (string day in days)
            {
                if (!TryParseDay(day, out DayOfWeek parsedDay))
                {
                    return new CommandResult(UnprocessableEntity, $"'{day}' isn't a day of the week.", detail);
                }

                allowed.Add(parsedDay);
            }

            policy.AllowWeekdays([.. allowed]);
        }

        foreach (string window in List(await GetAsync(context, "windows")))
        {
            ScheduleWindow parsedWindow = ScheduleWindow.Parse(window);
            policy.AllowBetween(parsedWindow.Start, parsedWindow.End);
        }

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

        foreach (string provider in List(await GetAsync(context, "providers")))
        {
            policy.ExcludeDatesFrom(provider);
        }

        foreach (string required in List(await GetAsync(context, "require")))
        {
            policy.Require(required);
        }

        foreach (string excepted in List(await GetAsync(context, "except")))
        {
            policy.ExceptWhen(excepted);
        }

        long? saved = context.Storage.UpdateSchedulePolicy(policy, revision);
        return saved.HasValue
            ? CommandResult.Done($"{detail}, revision {saved.Value.ToString(CultureInfo.InvariantCulture)}")
            : new CommandResult((int)HttpStatusCode.Conflict, Strings.PoliciesPage_Conflict, detail);
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
