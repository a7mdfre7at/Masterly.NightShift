using System.Globalization;

namespace Masterly.NightShift.Dashboard.Pages;

internal partial class SchedulePoliciesPage
{
    private const int PreviewCount = 20;

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

    // The pages write attributes as text, so a boolean attribute is written only when it applies
    private NonEscapedString Option(string value, bool selected)
    {
        return new NonEscapedString($"<option value=\"{Html.HtmlEncode(value)}\"{(selected ? " selected=\"selected\"" : string.Empty)}>{Html.HtmlEncode(value)}</option>");
    }

    private NonEscapedString DayCheckbox(DayOfWeek day, bool isChecked)
    {
        string name = CultureInfo.CurrentCulture.DateTimeFormat.GetAbbreviatedDayName(day);
        return new NonEscapedString($"<label class=\"checkbox-inline\"><input type=\"checkbox\" name=\"days\" value=\"{day}\"{(isChecked ? " checked=\"checked\"" : string.Empty)}/> {Html.HtmlEncode(name)}</label>");
    }

    private static string ExcludedText(SchedulePolicy policy)
    {
        const int maxDatesShown = 12;

        IEnumerable<string> dates = policy.ExcludedAnnualDates.Select(static x => x.ToString())
            .Concat(policy.ExcludedDates.Select(static x => x.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));
        List<string> parts = [.. dates.Take(maxDatesShown)];

        int more = policy.ExcludedDates.Count + policy.ExcludedAnnualDates.Count - parts.Count;
        if (more > 0)
        {
            parts.Add($"+{more.ToString(CultureInfo.CurrentCulture)}");
        }

        parts.AddRange(policy.DateProviders.Select(static x => $"[{x}]"));

        return string.Join(", ", parts);
    }

    private static string DatesText(SchedulePolicy policy)
    {
        return string.Join(", ", policy.ExcludedAnnualDates.Select(static x => x.ToString())
            .Concat(policy.ExcludedDates.Select(static x => x.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))));
    }
}
