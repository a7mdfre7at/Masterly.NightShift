using System.Globalization;
using System.Net;
using Masterly.NightShift.Dashboard.Resources;

namespace Masterly.NightShift.Dashboard;

/// <summary>Saves and deletes schedule policies from the dashboard, with the revision the form was read with.</summary>
internal static class SchedulePolicyDispatchers
{
    public static async Task<CommandResult> SaveAsync(DashboardContext context)
    {
        string name = await GetAsync(context, "name") ?? throw new ArgumentException("A name is required.");
        long revision = long.TryParse(await GetAsync(context, "revision"), NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed) ? parsed : 0;

        SchedulePolicy policy = new SchedulePolicy(name).WithDescription(await GetAsync(context, "description"));

        if (await GetAsync(context, "timezone") is { } timeZoneId)
        {
            policy.InTimeZone((context.Options.TimeZoneResolver ?? GlobalTimeZoneResolver.Current).GetTimeZoneById(timeZoneId));
        }

        IList<string> days = await context.Request.GetFormValuesAsync("days");
        if (days is { Count: > 0 })
        {
            policy.AllowWeekdays([.. days.Select(static x => Enum.Parse<DayOfWeek>(x, ignoreCase: true))]);
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

        foreach (string provider in List(await GetAsync(context, "providers"))) policy.ExcludeDatesFrom(provider);
        foreach (string required in List(await GetAsync(context, "require"))) policy.Require(required);
        foreach (string excepted in List(await GetAsync(context, "except"))) policy.ExceptWhen(excepted);

        long? saved = context.Storage.UpdateSchedulePolicy(policy, revision);
        return saved.HasValue
            ? CommandResult.Done($"revision {saved.Value.ToString(CultureInfo.InvariantCulture)}")
            : CommandResult.Refused((int)HttpStatusCode.Conflict, Strings.PoliciesPage_Conflict);
    }

    public static CommandResult Delete(DashboardContext context)
    {
        string name = RouteValues.Get(context, "Name");
        long revision = long.Parse(RouteValues.Get(context, "Revision"), CultureInfo.InvariantCulture);

        return context.Storage.DeleteSchedulePolicy(name, revision)
            ? CommandResult.Done()
            : CommandResult.Refused((int)HttpStatusCode.Conflict, Strings.PoliciesPage_Conflict);
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
