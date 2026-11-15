using System.Globalization;

namespace Masterly.NightShift.Dashboard.Pages;

internal partial class SchedulePoliciesPage
{
    private const int PreviewCount = 20;

    // The excluded dates a card shows before "+N more"
    private const int DatesShown = 6;

    // The week as the editor and the cards show it, from Monday
    private static readonly DayOfWeek[] Week =
    [
        DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday,
    ];

    private IReadOnlyList<SchedulePolicy> ReadPolicies()
    {
        try
        {
            return Storage.GetSchedulePolicies();
        }
        catch (FormatException)
        {
            // A policy that can't be read is left out, the others stay editable
            using IStorageConnection connection = Storage.GetReadOnlyConnection();
            List<SchedulePolicy> readable = [];
            foreach (string name in connection.GetAllItemsFromSet(SchedulePolicies.SetKey).Order(StringComparer.Ordinal))
            {
                try
                {
                    if (Storage.GetSchedulePolicy(name) is { } policy)
                    {
                        readable.Add(policy);
                    }
                }
                catch (FormatException)
                {
                    // Left out
                }
            }

            return readable;
        }
    }

    private IReadOnlyList<SchedulePolicyDecision>? Preview(string name, string cron, string? timeZoneId, out string? error)
    {
        error = null;

        try
        {
            TimeZoneInfo? timeZone = string.IsNullOrWhiteSpace(timeZoneId)
                ? null
                : (DashboardOptions.TimeZoneResolver ?? GlobalTimeZoneResolver.Current).GetTimeZoneById(timeZoneId.Trim());

            return Storage.PreviewSchedulePolicy(name, Cron.FromText(cron), timeZone, PreviewCount);
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            error = ex.Message;
            return null;
        }
    }

    // The policies that require or make an exception for each policy, from the policies already read: the page knows
    // them without asking the storage (the recurring jobs that follow a policy would take a read per job)
    private static Dictionary<string, List<string>> FindUsers(IReadOnlyList<SchedulePolicy> policies)
    {
        Dictionary<string, List<string>> users = new(StringComparer.Ordinal);
        foreach (SchedulePolicy policy in policies)
        {
            foreach (string name in policy.Required.Concat(policy.Excepted).Distinct(StringComparer.Ordinal))
            {
                if (!users.TryGetValue(name, out List<string>? list))
                {
                    users[name] = list = [];
                }

                list.Add(policy.Name);
            }
        }

        return users;
    }

    private static IReadOnlyList<string> FindUsers(Dictionary<string, List<string>> users, string name)
    {
        return users.TryGetValue(name, out List<string>? list) ? list : [];
    }

    private static bool IsAllowed(SchedulePolicy policy, DayOfWeek day) => policy.Days.Count == 0 || policy.Days.Contains(day);

    private static bool CrossesMidnight(ScheduleWindow window) => window.End < window.Start;

    private static string ShortDayName(DayOfWeek day) => CultureInfo.CurrentCulture.DateTimeFormat.GetAbbreviatedDayName(day);

    private static string DayName(DayOfWeek day) => CultureInfo.CurrentCulture.DateTimeFormat.GetDayName(day);

    // The days of every year first, then the dates, as the editor writes them
    private static List<string> ExcludedDates(SchedulePolicy policy)
    {
        return
        [
            .. policy.ExcludedAnnualDates.Select(static x => x.ToString()),
            .. policy.ExcludedDates.Select(static x => x.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
        ];
    }

    // The pages write attributes as text, so a boolean attribute is written only when it applies
    private NonEscapedString DayChoice(DayOfWeek day, bool isChecked)
    {
        return new NonEscapedString($"<label class=\"ns-chip ns-chip-filter ns-policy-day-choice\"><input type=\"checkbox\" name=\"days\" value=\"{day}\"{(isChecked ? " checked=\"checked\"" : string.Empty)}/>{Html.Icon("check-lg", "ns-policy-day-check")}<span aria-hidden=\"true\">{Html.HtmlEncode(ShortDayName(day))}</span><span class=\"visually-hidden\">{Html.HtmlEncode(DayName(day))}</span></label>");
    }

    private static string DatesText(SchedulePolicy policy)
    {
        return string.Join(", ", ExcludedDates(policy));
    }
}
