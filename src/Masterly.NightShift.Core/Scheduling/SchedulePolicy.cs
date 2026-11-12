using System.Globalization;

namespace Masterly.NightShift;

/// <summary>
/// A business calendar: the days, the times of day and the dates when jobs that follow it may start. Policies are
/// stored by name (<see cref="SchedulePolicies"/>), so servers, the dashboard and declarations in code share them,
/// and a delayed job (<see cref="ScheduleOptions.Policy"/>) or a recurring job (<see cref="RecurringJobOptions.Policy"/>)
/// refers to one by its name.
/// <code>
/// new SchedulePolicy("business-hours")
///     .InTimeZone("Europe/Berlin")
///     .AllowWeekdays(DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday)
///     .AllowBetween("09:00", "17:00")
///     .ExcludeAnnualDate(12, 25)
///     .ExcludeDatesFrom("public-holidays");
/// </code>
/// An instant is allowed when every rule allows it: its day of the week is allowed (any day when none is given),
/// its date isn't excluded, it is inside one of the windows (any time when none is given), every policy named by
/// <see cref="Require"/> allows it and no policy named by <see cref="ExceptWhen"/> does. The rules are read in the
/// policy's time zone.
/// </summary>
public sealed class SchedulePolicy([NotNull] string name)
{
    /// <summary>The longest name of a policy or a date provider.</summary>
    public const int MaxNameLength = 64;

    /// <summary>The longest description.</summary>
    public const int MaxDescriptionLength = 500;

    private readonly SortedSet<DayOfWeek> _days = [];
    private readonly List<ScheduleWindow> _windows = [];
    private readonly SortedSet<DateOnly> _excludedDates = [];
    private readonly SortedSet<AnnualDate> _excludedAnnualDates = [];
    private readonly List<string> _dateProviders = [];
    private readonly List<string> _required = [];
    private readonly List<string> _excepted = [];

    /// <summary>
    /// The policy's name: up to 64 letters, digits, <c>.</c>, <c>_</c> and <c>-</c>, starting with a letter or a
    /// digit, lower-cased, so <c>Business-Hours</c> and <c>business-hours</c> are the same policy.
    /// </summary>
    public string Name { get; } = NormalizeName(name, nameof(name));

    /// <summary>What the policy is for, shown on the dashboard. <c>null</c> when none was given.</summary>
    public string? Description { get; private set; }

    /// <summary>The time zone the rules are read in. UTC unless <see cref="InTimeZone(string)"/> was called.</summary>
    public string TimeZoneId { get; private set; } = TimeZoneInfo.Utc.Id;

    /// <summary>The allowed days of the week. Empty means every day.</summary>
    public IReadOnlyCollection<DayOfWeek> Days => _days;

    /// <summary>The daily windows a job may start in. Empty means any time of day.</summary>
    public IReadOnlyList<ScheduleWindow> Windows => _windows;

    /// <summary>Dates on which nothing may start.</summary>
    public IReadOnlyCollection<DateOnly> ExcludedDates => _excludedDates;

    /// <summary>Days of the year on which nothing may start, every year.</summary>
    public IReadOnlyCollection<AnnualDate> ExcludedAnnualDates => _excludedAnnualDates;

    /// <summary>Names of the <see cref="IScheduleDateProvider"/>s whose dates are excluded.</summary>
    public IReadOnlyList<string> DateProviders => _dateProviders;

    /// <summary>Names of the policies that must allow an instant too.</summary>
    public IReadOnlyList<string> Required => _required;

    /// <summary>Names of the policies that block an instant they allow.</summary>
    public IReadOnlyList<string> Excepted => _excepted;

    /// <summary>
    /// The stored revision, raised by every change: 0 for a policy that isn't stored. Pass it to
    /// <c>UpdateSchedulePolicy</c> and <c>DeleteSchedulePolicy</c>, which refuse to act when someone else changed
    /// the policy since it was read.
    /// </summary>
    public long Revision { get; internal set; }

    /// <summary>When the stored policy last changed (UTC), or <c>null</c> for a policy that isn't stored.</summary>
    public DateTime? UpdatedAt { get; internal set; }

    /// <summary>Sets <see cref="Description"/>.</summary>
    /// <returns>The same policy, for chaining.</returns>
    public SchedulePolicy WithDescription([CanBeNull] string? description)
    {
        if (description != null && description.Length > MaxDescriptionLength)
        {
            throw new ArgumentOutOfRangeException(nameof(description), description.Length, $"A description has at most {MaxDescriptionLength} characters.");
        }

        Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        return this;
    }

    /// <summary>Reads the rules in the time zone with this id, Windows or IANA (<see cref="GlobalTimeZoneResolver"/>).</summary>
    /// <exception cref="TimeZoneNotFoundException">No time zone has this id.</exception>
    /// <returns>The same policy, for chaining.</returns>
    public SchedulePolicy InTimeZone([NotNull] string timeZoneId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(timeZoneId);

        TimeZoneId = GlobalTimeZoneResolver.Current.GetTimeZoneById(timeZoneId.Trim()).Id;
        return this;
    }

    /// <summary>Reads the rules in this time zone.</summary>
    /// <returns>The same policy, for chaining.</returns>
    public SchedulePolicy InTimeZone([NotNull] TimeZoneInfo timeZone)
    {
        ArgumentNullException.ThrowIfNull(timeZone);

        TimeZoneId = timeZone.Id;
        return this;
    }

    /// <summary>
    /// Allows these days of the week, and no others. Can be called several times. The day is the one of the
    /// instant checked, in the policy's time zone, so the part of an overnight window after midnight belongs to
    /// the next day.
    /// </summary>
    /// <returns>The same policy, for chaining.</returns>
    public SchedulePolicy AllowWeekdays([NotNull] params DayOfWeek[] days)
    {
        ArgumentNullException.ThrowIfNull(days);
        if (days.Length == 0) throw new ArgumentException("At least one day is required.", nameof(days));

        foreach (DayOfWeek day in days)
        {
            if (!Enum.IsDefined(day)) throw new ArgumentOutOfRangeException(nameof(days), day, "Not a day of the week.");
            _days.Add(day);
        }

        return this;
    }

    /// <summary>
    /// Allows jobs to start between two times of day, <c>HH:mm</c> or <c>HH:mm:ss</c>: from <paramref name="start"/>
    /// included to <paramref name="end"/> excluded. An end before the start crosses midnight (<c>22:00</c> to
    /// <c>06:00</c>). Can be called several times; an instant inside any window is allowed.
    /// </summary>
    /// <returns>The same policy, for chaining.</returns>
    public SchedulePolicy AllowBetween([NotNull] string start, [NotNull] string end)
    {
        return AllowBetween(ScheduleWindow.ParseTime(start, nameof(start)), ScheduleWindow.ParseTime(end, nameof(end)));
    }

    /// <inheritdoc cref="AllowBetween(string, string)"/>
    public SchedulePolicy AllowBetween(TimeSpan start, TimeSpan end)
    {
        ScheduleWindow window = new(start, end);
        if (!_windows.Contains(window)) _windows.Add(window);
        return this;
    }

    /// <summary>Excludes a date: nothing may start on it, in the policy's time zone.</summary>
    /// <returns>The same policy, for chaining.</returns>
    public SchedulePolicy ExcludeDate(DateOnly date)
    {
        _excludedDates.Add(date);
        return this;
    }

    /// <summary>Excludes a day of every year, such as 25 December: <c>ExcludeAnnualDate(12, 25)</c>.</summary>
    /// <returns>The same policy, for chaining.</returns>
    public SchedulePolicy ExcludeAnnualDate(int month, int day)
    {
        _excludedAnnualDates.Add(new AnnualDate(month, day));
        return this;
    }

    /// <summary>
    /// Excludes the dates of an <see cref="IScheduleDateProvider"/>, such as public holidays, registered under this
    /// name with <c>UseScheduleDateProvider</c> on every server. A server without it allows no instant of the
    /// policy, so the policy never lets a job start on a date it can't check.
    /// </summary>
    /// <returns>The same policy, for chaining.</returns>
    public SchedulePolicy ExcludeDatesFrom([NotNull] string providerName)
    {
        string name = NormalizeName(providerName, nameof(providerName));
        if (!_dateProviders.Contains(name)) _dateProviders.Add(name);
        return this;
    }

    /// <summary>
    /// Allows an instant only when the stored policy of this name allows it too, for example a policy for business
    /// hours that requires the one for the trading calendar. A policy that names itself, or a cycle of policies,
    /// is refused when the policy is saved.
    /// </summary>
    /// <returns>The same policy, for chaining.</returns>
    public SchedulePolicy Require([NotNull] string policyName)
    {
        string name = NormalizeName(policyName, nameof(policyName));
        CheckReference(name, _excepted, nameof(policyName));
        if (!_required.Contains(name)) _required.Add(name);
        return this;
    }

    /// <summary>Blocks every instant the stored policy of this name allows, for example a maintenance window.</summary>
    /// <returns>The same policy, for chaining.</returns>
    public SchedulePolicy ExceptWhen([NotNull] string policyName)
    {
        string name = NormalizeName(policyName, nameof(policyName));
        CheckReference(name, _required, nameof(policyName));
        if (!_excepted.Contains(name)) _excepted.Add(name);
        return this;
    }

    private void CheckReference(string name, List<string> other, string parameterName)
    {
        if (name == Name)
        {
            throw new ArgumentException($"Policy '{Name}' can't refer to itself.", parameterName);
        }

        if (other.Contains(name))
        {
            throw new ArgumentException($"Policy '{Name}' can't both require '{name}' and make an exception for it.", parameterName);
        }
    }

    /// <inheritdoc />
    public override string ToString() => Name;

    /// <summary>
    /// Checks a policy or date provider name, up to 64 letters, digits, <c>.</c>, <c>_</c> and <c>-</c> starting with
    /// a letter or a digit, and returns it lower-cased, as policies store it.
    /// </summary>
    /// <exception cref="ArgumentException">The name isn't valid.</exception>
    public static string NormalizeName([NotNull] string name) => NormalizeName(name, nameof(name));

    internal static string NormalizeName([CanBeNull] string? name, [InvokerParameterName] string parameterName)
    {
        ArgumentNullException.ThrowIfNull(name, parameterName);

        string trimmed = name.Trim().ToLowerInvariant();
        if (trimmed.Length == 0 || trimmed.Length > MaxNameLength || !char.IsAsciiLetterOrDigit(trimmed[0]) ||
            !trimmed.All(static c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-'))
        {
            throw new ArgumentException(
                $"'{name}' isn't a valid name: use 1 to {MaxNameLength} letters, digits, '.', '_' and '-', starting with a letter or a digit.",
                parameterName);
        }

        return trimmed;
    }

    // Hash fields of the stored policy. A field that is empty or missing means "no rule".
    internal const string DescriptionField = "Description";
    internal const string TimeZoneField = "TimeZone";
    internal const string DaysField = "Days";
    internal const string WindowsField = "Windows";
    internal const string ExcludedDatesField = "ExcludedDates";
    internal const string AnnualDatesField = "AnnualDates";
    internal const string DateProvidersField = "DateProviders";
    internal const string RequireField = "Require";
    internal const string ExceptField = "Except";
    internal const string RevisionField = "Revision";
    internal const string UpdatedAtField = "UpdatedAt";

    /// <summary>The rules as hash fields, every field present, so a cleared rule overwrites the stored one.</summary>
    internal Dictionary<string, string> ToFields()
    {
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [DescriptionField] = Description ?? string.Empty,
            [TimeZoneField] = TimeZoneId,
            [DaysField] = string.Join(",", _days),
            [WindowsField] = string.Join(",", _windows),
            [ExcludedDatesField] = string.Join(",", _excludedDates.Select(static x => x.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))),
            [AnnualDatesField] = string.Join(",", _excludedAnnualDates),
            [DateProvidersField] = string.Join(",", _dateProviders),
            [RequireField] = string.Join(",", _required),
            [ExceptField] = string.Join(",", _excepted),
        };
    }

    /// <summary>Whether two policies have the same rules, whatever their revisions.</summary>
    internal bool HasSameRules([NotNull] SchedulePolicy other)
    {
        Dictionary<string, string> mine = ToFields();
        Dictionary<string, string> theirs = other.ToFields();
        return mine.All(x => theirs.TryGetValue(x.Key, out string? value) && value == x.Value);
    }

    /// <summary>Reads a stored policy.</summary>
    /// <exception cref="FormatException">A field can't be read.</exception>
    internal static SchedulePolicy FromFields([NotNull] string name, [NotNull] IReadOnlyDictionary<string, string> fields)
    {
        SchedulePolicy policy = new(name);

        string? Field(string field) => fields.TryGetValue(field, out string? value) && !string.IsNullOrWhiteSpace(value) ? value : null;
        IEnumerable<string> List(string field) => Field(field)?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [];

        try
        {
            policy.Description = Field(DescriptionField);
            policy.TimeZoneId = Field(TimeZoneField) ?? TimeZoneInfo.Utc.Id;

            foreach (string day in List(DaysField)) policy._days.Add(Enum.Parse<DayOfWeek>(day, ignoreCase: true));
            foreach (string window in List(WindowsField)) policy._windows.Add(ScheduleWindow.Parse(window));
            foreach (string date in List(ExcludedDatesField)) policy._excludedDates.Add(DateOnly.ParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture));
            foreach (string date in List(AnnualDatesField)) policy._excludedAnnualDates.Add(AnnualDate.Parse(date));
            foreach (string provider in List(DateProvidersField)) policy._dateProviders.Add(NormalizeName(provider, nameof(fields)));
            foreach (string required in List(RequireField)) policy._required.Add(NormalizeName(required, nameof(fields)));
            foreach (string excepted in List(ExceptField)) policy._excepted.Add(NormalizeName(excepted, nameof(fields)));

            if (Field(RevisionField) is { } revision) policy.Revision = long.Parse(revision, NumberStyles.Integer, CultureInfo.InvariantCulture);
            if (Field(UpdatedAtField) is { } updatedAt) policy.UpdatedAt = JobHelper.DeserializeDateTime(updatedAt);
        }
        catch (Exception ex) when (ex is ArgumentException or OverflowException)
        {
            throw new FormatException($"Schedule policy '{name}' can't be read: {ex.Message}", ex);
        }

        return policy;
    }
}

/// <summary>
/// A daily window of a <see cref="SchedulePolicy"/>: from <see cref="Start"/> included to <see cref="End"/> excluded.
/// An end before the start crosses midnight.
/// </summary>
public readonly record struct ScheduleWindow
{
    private static readonly string[] TimeFormats = [@"h\:mm", @"hh\:mm", @"h\:mm\:ss", @"hh\:mm\:ss"];

    /// <summary>Creates a window from two times of day.</summary>
    /// <exception cref="ArgumentOutOfRangeException">A time isn't between 00:00 and 23:59:59.</exception>
    /// <exception cref="ArgumentException">The start and the end are the same.</exception>
    public ScheduleWindow(TimeSpan start, TimeSpan end)
    {
        CheckTime(start, nameof(start));
        CheckTime(end, nameof(end));
        if (start == end) throw new ArgumentException("A window can't start and end at the same time.", nameof(end));

        Start = start;
        End = end;
    }

    /// <summary>When the window opens, as a time of day.</summary>
    public TimeSpan Start { get; }

    /// <summary>When the window closes, as a time of day. Before <see cref="Start"/> for a window crossing midnight.</summary>
    public TimeSpan End { get; }

    /// <summary>
    /// Whether a time of day is inside the window. A window whose start and end are the same, which only
    /// <c>default(ScheduleWindow)</c> can be, contains nothing.
    /// </summary>
    public bool Contains(TimeSpan timeOfDay)
    {
        if (Start == End) return false;

        return Start < End
            ? timeOfDay >= Start && timeOfDay < End
            : timeOfDay >= Start || timeOfDay < End;
    }

    /// <summary>The window as <c>HH:mm-HH:mm</c>, with seconds when a time has them.</summary>
    public override string ToString() => $"{Format(Start)}-{Format(End)}";

    /// <summary>Reads a window written as <c>HH:mm-HH:mm</c>.</summary>
    /// <exception cref="FormatException">The text isn't a window.</exception>
    public static ScheduleWindow Parse([NotNull] string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        string[] parts = text.Split('-');
        if (parts.Length != 2 || !TryParseTime(parts[0], out TimeSpan start) || !TryParseTime(parts[1], out TimeSpan end) || start == end)
        {
            throw new FormatException($"'{text}' isn't a window in the form HH:mm-HH:mm.");
        }

        return new ScheduleWindow(start, end);
    }

    internal static TimeSpan ParseTime([CanBeNull] string? text, [InvokerParameterName] string parameterName)
    {
        ArgumentNullException.ThrowIfNull(text, parameterName);

        return TryParseTime(text, out TimeSpan time)
            ? time
            : throw new ArgumentException($"'{text}' isn't a time of day in the form HH:mm or HH:mm:ss.", parameterName);
    }

    private static bool TryParseTime(string text, out TimeSpan time)
    {
        return TimeSpan.TryParseExact(text.Trim(), TimeFormats, CultureInfo.InvariantCulture, out time) &&
               time >= TimeSpan.Zero && time < TimeSpan.FromDays(1);
    }

    private static void CheckTime(TimeSpan time, string parameterName)
    {
        if (time < TimeSpan.Zero || time >= TimeSpan.FromDays(1))
        {
            throw new ArgumentOutOfRangeException(parameterName, time, "A time of day is between 00:00 and 23:59:59.");
        }
    }

    private static string Format(TimeSpan time)
    {
        return time.Seconds == 0 && time.Milliseconds == 0
            ? time.ToString(@"hh\:mm", CultureInfo.InvariantCulture)
            : time.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture);
    }
}

/// <summary>A day of every year, such as 25 December, excluded by a <see cref="SchedulePolicy"/>.</summary>
public readonly record struct AnnualDate : IComparable<AnnualDate>
{
    /// <summary>Creates the day of the year. 29 February is allowed and matches leap years only.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The month or the day doesn't exist.</exception>
    public AnnualDate(int month, int day)
    {
        if (month is < 1 or > 12) throw new ArgumentOutOfRangeException(nameof(month), month, "A month is between 1 and 12.");

        // 2000 is a leap year, so 29 February is accepted
        if (day < 1 || day > DateTime.DaysInMonth(2000, month))
        {
            throw new ArgumentOutOfRangeException(nameof(day), day, $"Month {month} has no day {day}.");
        }

        Month = month;
        Day = day;
    }

    /// <summary>The month, 1 to 12.</summary>
    public int Month { get; }

    /// <summary>The day of the month.</summary>
    public int Day { get; }

    /// <summary>Whether a date falls on this day of the year.</summary>
    public bool Matches(DateOnly date) => date.Month == Month && date.Day == Day;

    /// <inheritdoc />
    public int CompareTo(AnnualDate other) => Month != other.Month ? Month.CompareTo(other.Month) : Day.CompareTo(other.Day);

    /// <summary>Whether the first day comes earlier in the year.</summary>
    public static bool operator <(AnnualDate left, AnnualDate right) => left.CompareTo(right) < 0;

    /// <summary>Whether the first day comes earlier in the year, or is the same.</summary>
    public static bool operator <=(AnnualDate left, AnnualDate right) => left.CompareTo(right) <= 0;

    /// <summary>Whether the first day comes later in the year.</summary>
    public static bool operator >(AnnualDate left, AnnualDate right) => left.CompareTo(right) > 0;

    /// <summary>Whether the first day comes later in the year, or is the same.</summary>
    public static bool operator >=(AnnualDate left, AnnualDate right) => left.CompareTo(right) >= 0;

    /// <summary>The day as <c>MM-dd</c>.</summary>
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Month:00}-{Day:00}");

    /// <summary>Reads a day written as <c>MM-dd</c>.</summary>
    /// <exception cref="FormatException">The text isn't a day of the year.</exception>
    public static AnnualDate Parse([NotNull] string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        string[] parts = text.Split('-');
        if (parts.Length == 2 &&
            int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int month) &&
            int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int day) &&
            month is >= 1 and <= 12 && day >= 1 && day <= DateTime.DaysInMonth(2000, month))
        {
            return new AnnualDate(month, day);
        }

        throw new FormatException($"'{text}' isn't a day of the year in the form MM-dd.");
    }
}
