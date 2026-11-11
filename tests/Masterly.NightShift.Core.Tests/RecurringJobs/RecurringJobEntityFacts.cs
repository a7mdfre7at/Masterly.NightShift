using System.Diagnostics.CodeAnalysis;

namespace Masterly.NightShift.Core.Tests;

[SuppressMessage("ReSharper", "AssignNullToNotNullAttribute")]
[SuppressMessage("ReSharper", "ConvertClosureToMethodGroup")]
public class RecurringJobEntityFacts
{
    private const string RecurringJobId = "recurring-job-id";
    private readonly DateTime _nowInstant = new(2017, 03, 30, 15, 30, 0, DateTimeKind.Utc);
    private readonly Dictionary<string, string> _recurringJob;
    private readonly Mock<ITimeZoneResolver> _timeZoneResolver;

    public RecurringJobEntityFacts()
    {
        TimeZoneInfo timeZone = TimeZoneInfo.Local;

        _recurringJob = new Dictionary<string, string>
        {
            { "Cron", "* * * * *" },
            { "Job", InvocationData.SerializeJob(Job.FromExpression(() => Console.WriteLine())).SerializePayload() },
            { "TimeZoneId", timeZone.Id }
        };

        _timeZoneResolver = new Mock<ITimeZoneResolver>();
        _timeZoneResolver.Setup(x => x.GetTimeZoneById(It.IsAny<string>())).Throws<InvalidTimeZoneException>();
        _timeZoneResolver.Setup(x => x.GetTimeZoneById(timeZone.Id)).Returns(timeZone);
    }

    [Fact]
    public void Ctor_ThrowsAnException_WhenRecurringJobId_IsNull()
    {
        ArgumentNullException exception = Assert.Throws<ArgumentNullException>(() =>
            new RecurringJobEntity(null, _recurringJob));

        Assert.Equal("recurringJobId", exception.ParamName);
    }

    [Fact]
    public void Ctor_ThrowsAnException_WhenRecurringJob_IsNull()
    {
        ArgumentNullException exception = Assert.Throws<ArgumentNullException>(() =>
            new RecurringJobEntity(RecurringJobId, null));

        Assert.Equal("recurringJob", exception.ParamName);
    }

    [Fact]
    public void Ctor_SetsRelaxedMisfireOption_WhenCorrespondingKeyIsMissing()
    {
        RecurringJobEntity entity = CreateEntity();
        Assert.Equal(MisfireHandlingMode.Relaxed, entity.MisfireHandling);
    }

    [Fact]
    public void Ctor_CorrectlyParses_RelaxedMisfireOption_SerializedAsInt()
    {
        _recurringJob["Misfire"] = "0";

        RecurringJobEntity entity = CreateEntity();

        Assert.Equal(MisfireHandlingMode.Relaxed, entity.MisfireHandling);
    }

    [Fact]
    public void Ctor_CorrectlyParses_StrictMisfireOption_SerializedAsInt()
    {
        _recurringJob["Misfire"] = "1";

        RecurringJobEntity entity = CreateEntity();

        Assert.Equal(MisfireHandlingMode.Strict, entity.MisfireHandling);
    }

    [Fact]
    public void Ctor_ThrowsAnException_WhenMisfireOption_CanNotBeParsed()
    {
        _recurringJob["Misfire"] = "2adgadsg";
        Assert.Throws<ArgumentException>(CreateEntity);
    }

    [Fact]
    public void Ctor_ThrowsAnException_WhenMisfireOption_IsNotWithinAValidRange()
    {
        _recurringJob["Misfire"] = "4";
        Assert.Throws<NotSupportedException>(CreateEntity);
    }

    [Fact]
    public void ScheduleNext_WithRunLatest_ReturnsOnlyTheLatestMissedOccurrence()
    {
        _recurringJob["Misfire"] = "3";
        _recurringJob["SkipAfter"] = "3600";
        RecurringJobEntity entity = CreateEntity();
        DateTime now = _nowInstant.AddSeconds(30);

        List<DateTime> executions = [.. entity.ScheduleNext(_timeZoneResolver.Object, now.AddMinutes(-20), now, TimeSpan.Zero)];

        Assert.Equal([_nowInstant], executions);
        Assert.Equal(_nowInstant.AddMinutes(1), entity.NextExecution);
    }

    [Fact]
    public void ScheduleNext_WithRunLatest_ReturnsNothing_WhenTheLatestMissedOccurrenceIsTooOld()
    {
        _recurringJob["Cron"] = "0 * * * *";
        _recurringJob["Misfire"] = "3";
        _recurringJob["SkipAfter"] = "600";
        RecurringJobEntity entity = CreateEntity();
        DateTime now = _nowInstant.AddSeconds(30);

        List<DateTime> executions = [.. entity.ScheduleNext(_timeZoneResolver.Object, now.AddHours(-5), now, TimeSpan.Zero)];

        Assert.Empty(executions);
        Assert.NotNull(entity.NextExecution);
        Assert.True(entity.NextExecution > now);
    }

    [Fact]
    public void ScheduleNext_WithRunLatest_LooksOnlyInsideTheWindow_AfterALongOutage()
    {
        _recurringJob["Misfire"] = "3";
        _recurringJob["SkipAfter"] = "300";
        RecurringJobEntity entity = CreateEntity();
        DateTime now = _nowInstant.AddSeconds(30);

        List<DateTime> executions = [.. entity.ScheduleNext(_timeZoneResolver.Object, now.AddDays(-400), now, TimeSpan.Zero)];

        Assert.Equal([_nowInstant], executions);
    }

    [Fact]
    public void ScheduleNext_WithRunLatest_RunsAnOccurrenceThatIsDueNow()
    {
        _recurringJob["Misfire"] = "3";
        _recurringJob["SkipAfter"] = "60";
        RecurringJobEntity entity = CreateEntity();

        List<DateTime> executions = [.. entity.ScheduleNext(_timeZoneResolver.Object, _nowInstant.AddSeconds(-1), _nowInstant, TimeSpan.Zero)];

        Assert.Equal([_nowInstant], executions);
    }

    [Theory]
    [InlineData("3600", 3600)]
    [InlineData("0", null)]
    [InlineData("-5", null)]
    [InlineData("soon", null)]
    [InlineData("", null)]
    public void Ctor_ReadsSkipAfter_InSeconds(string stored, int? seconds)
    {
        _recurringJob["SkipAfter"] = stored;

        RecurringJobEntity entity = CreateEntity();

        Assert.Equal(seconds.HasValue ? TimeSpan.FromSeconds(seconds.Value) : null, entity.SkipAfter);
    }

    [Fact]
    public void IsChanged_WritesSkipAfter_WhenItIsSet_AndClearsIt_WhenItIsRemoved()
    {
        RecurringJobEntity entity = CreateEntity();
        entity.SkipAfter = TimeSpan.FromMinutes(30);

        entity.IsChanged(_nowInstant, out IReadOnlyDictionary<string, string> set);
        Assert.Equal("1800", set["SkipAfter"]);

        _recurringJob["SkipAfter"] = "1800";
        RecurringJobEntity stored = CreateEntity();
        stored.IsChanged(_nowInstant, out IReadOnlyDictionary<string, string> unchanged);
        Assert.False(unchanged.ContainsKey("SkipAfter"));

        stored.SkipAfter = null;
        stored.IsChanged(_nowInstant, out IReadOnlyDictionary<string, string> cleared);
        Assert.Equal(string.Empty, cleared["SkipAfter"]);
    }

    [Fact]
    public void IsChanged_DoesNotAddMisfireKey_WhenItIsNotPresentAndDefaultValueIsUnchanged()
    {
        RecurringJobEntity entity = CreateEntity();

        entity.IsChanged(_nowInstant, out IReadOnlyDictionary<string, string>? fields);

        Assert.False(fields.ContainsKey("Misfire"));
    }

    [Fact]
    public void IsChanged_DoesNotAddMisfireKey_WhenItIsSetToDefaultAndDefaultValueIsUnchanged()
    {
        _recurringJob["Misfire"] = "0";
        RecurringJobEntity entity = CreateEntity();

        entity.IsChanged(_nowInstant, out IReadOnlyDictionary<string, string>? fields);

        Assert.False(fields.ContainsKey("Misfire"));
    }

    [Fact]
    public void IsChanged_AddsMisfireKey_WhenItIsNotPresent_ButDefaultValueIsChanged()
    {
        RecurringJobEntity entity = CreateEntity();
        entity.MisfireHandling = MisfireHandlingMode.Strict;

        entity.IsChanged(_nowInstant, out IReadOnlyDictionary<string, string>? fields);

        Assert.True(fields.ContainsKey("Misfire"));
        Assert.Equal("1", fields["Misfire"]);
    }

    [Fact]
    public void IsChanged_ExplicitlySetsTheDefaultValue_WhenItWasSetToStrict()
    {
        _recurringJob["Misfire"] = "1";
        RecurringJobEntity entity = CreateEntity();
        entity.MisfireHandling = MisfireHandlingMode.Relaxed;

        entity.IsChanged(_nowInstant, out IReadOnlyDictionary<string, string>? fields);

        Assert.True(fields.ContainsKey("Misfire"));
        Assert.Equal("0", fields["Misfire"]);
    }

    [Fact]
    public void IsChanged_DoesNotAddMisfireKey_WhenItsNonDefaultValueIsUnchanged()
    {
        _recurringJob["Misfire"] = "1";
        RecurringJobEntity entity = CreateEntity();
        entity.MisfireHandling = MisfireHandlingMode.Strict;

        entity.IsChanged(_nowInstant, out IReadOnlyDictionary<string, string>? fields);

        Assert.False(fields.ContainsKey("Misfire"));
    }

    [Fact]
    public void ScheduleNext_InStrictMode_ReturnsAtMostMaxExecutionsPerPass_AndLeavesTheJobDue()
    {
        _recurringJob["Misfire"] = "1";
        RecurringJobEntity entity = CreateEntity();
        DateTime from = _nowInstant.AddDays(-1);

        List<DateTime> executions = [.. entity.ScheduleNext(_timeZoneResolver.Object, from, _nowInstant, TimeSpan.Zero)];

        Assert.Equal(RecurringJobEntity.MaxExecutionsPerPass, executions.Count);
        Assert.Equal(from.AddMinutes(1), executions.First());
        Assert.Equal(from.AddMinutes(RecurringJobEntity.MaxExecutionsPerPass), executions.Last());
        Assert.Equal(from.AddMinutes(RecurringJobEntity.MaxExecutionsPerPass + 1), entity.NextExecution);
    }

    [Fact]
    public void ScheduleNext_InStrictMode_CreatesTheRemainingRuns_OnTheNextPass()
    {
        _recurringJob["Misfire"] = "1";
        RecurringJobEntity entity = CreateEntity();
        DateTime from = _nowInstant.AddMinutes(-150);

        List<DateTime> firstPass = [.. entity.ScheduleNext(_timeZoneResolver.Object, from, _nowInstant, TimeSpan.Zero)];
        List<DateTime> secondPass = [.. entity.ScheduleNext(_timeZoneResolver.Object, firstPass.Last(), _nowInstant, TimeSpan.Zero)];

        Assert.Equal(
            Enumerable.Range(1, 150).Select(minutes => from.AddMinutes(minutes)),
            firstPass.Concat(secondPass));
        Assert.Equal(_nowInstant.AddMinutes(1), entity.NextExecution);
    }

    [Fact]
    public void Ctor_ReadsThePolicy()
    {
        _recurringJob["Policy"] = "weekdays";

        Assert.Equal("weekdays", CreateEntity().Policy);
    }

    [Fact]
    public void ScheduleNext_LeavesOutTheOccurrencesThePolicyBlocks_AndJumpsToTheNextAllowedOne()
    {
        RecurringJobEntity entity = CreatePolicyEntity(new SchedulePolicy("office").AllowBetween("09:00", "17:00"));
        entity.MisfireHandling = MisfireHandlingMode.Strict;

        // 15:30 to 18:30 UTC: 16:00 runs, 17:00 and 18:00 are blocked, and the next run is tomorrow at 09:00
        List<DateTime> executions = [.. entity.ScheduleNext(UtcResolver(), _nowInstant, _nowInstant.AddHours(3), TimeSpan.Zero)];

        Assert.Equal([_nowInstant.Date.AddHours(16)], executions);
        Assert.Equal(_nowInstant.Date.AddDays(1).AddHours(9), entity.NextExecution);
        Assert.Null(entity.Error);
    }

    [Fact]
    public void ScheduleNext_WithRelaxed_RunsNothing_WhenEveryMissedOccurrenceIsBlocked()
    {
        RecurringJobEntity entity = CreatePolicyEntity(new SchedulePolicy("office").AllowBetween("09:00", "17:00"));

        List<DateTime> executions = [.. entity.ScheduleNext(UtcResolver(), _nowInstant.Date.AddHours(17).AddMinutes(30), _nowInstant.Date.AddHours(20), TimeSpan.Zero)];

        Assert.Empty(executions);
        Assert.Equal(_nowInstant.Date.AddDays(1).AddHours(9), entity.NextExecution);
    }

    [Fact]
    public void ScheduleNext_StopsSchedulingWithAnError_WhenThePolicyAllowsNoOccurrence()
    {
        RecurringJobEntity entity = CreatePolicyEntity(new SchedulePolicy("never").ExcludeDatesFrom("not-registered"));

        List<DateTime> executions = [.. entity.ScheduleNext(UtcResolver(), _nowInstant, _nowInstant.AddHours(3), TimeSpan.Zero)];

        Assert.Empty(executions);
        Assert.Null(entity.NextExecution);
        Assert.Equal("Schedule policy 'never' allows none of its next occurrences in the next ten years.", entity.Error);
    }

    [Fact]
    public void ScheduleNext_RunsEveryOccurrence_WhenThePolicyDoesNotExist_OrCannotBeRead()
    {
        _recurringJob["TimeZoneId"] = "UTC";
        _recurringJob["Cron"] = "0 * * * *";
        _recurringJob["Policy"] = "gone";
        RecurringJobEntity missing = new(RecurringJobId, _recurringJob) { PolicySource = new PolicyStorage().Connection.Object };
        RecurringJobEntity noSource = new(RecurringJobId, _recurringJob);

        missing.ScheduleNext(UtcResolver(), _nowInstant.Date.AddHours(17).AddMinutes(30));
        noSource.ScheduleNext(UtcResolver(), _nowInstant.Date.AddHours(17).AddMinutes(30));

        Assert.Equal(_nowInstant.Date.AddHours(18), missing.NextExecution);
        Assert.Equal(_nowInstant.Date.AddHours(18), noSource.NextExecution);
    }

    [Fact]
    public void GetNextOccurrences_LeavesOutTheBlockedOccurrences()
    {
        RecurringJobEntity entity = CreatePolicyEntity(new SchedulePolicy("office").AllowBetween("09:00", "11:00"));

        List<DateTime> next = [.. entity.GetNextOccurrences(UtcResolver(), _nowInstant).Take(3)];

        Assert.Equal([_nowInstant.Date.AddDays(1).AddHours(9), _nowInstant.Date.AddDays(1).AddHours(10), _nowInstant.Date.AddDays(2).AddHours(9)], next);
    }

    [Fact]
    public void IsChanged_WritesThePolicy_WhenItIsSet_AndClearsIt_WhenItIsRemoved()
    {
        RecurringJobEntity entity = CreateEntity();
        entity.Policy = "weekdays";

        entity.IsChanged(_nowInstant, out IReadOnlyDictionary<string, string> set);
        Assert.Equal("weekdays", set["Policy"]);

        _recurringJob["Policy"] = "weekdays";
        RecurringJobEntity stored = CreateEntity();
        stored.IsChanged(_nowInstant, out IReadOnlyDictionary<string, string> unchanged);
        Assert.False(unchanged.ContainsKey("Policy"));

        stored.Policy = null;
        stored.IsChanged(_nowInstant, out IReadOnlyDictionary<string, string> cleared);
        Assert.Equal(string.Empty, cleared["Policy"]);
    }

    private RecurringJobEntity CreatePolicyEntity(SchedulePolicy policy)
    {
        PolicyStorage storage = new();
        storage.Add(policy);
        _recurringJob["TimeZoneId"] = "UTC";
        _recurringJob["Cron"] = "0 * * * *";
        _recurringJob["Policy"] = policy.Name;
        return new RecurringJobEntity(RecurringJobId, _recurringJob) { PolicySource = storage.Connection.Object };
    }

    private static ITimeZoneResolver UtcResolver()
    {
        Mock<ITimeZoneResolver> resolver = new();
        resolver.Setup(x => x.GetTimeZoneById(It.IsAny<string>())).Returns(TimeZoneInfo.Utc);
        return resolver.Object;
    }

    private RecurringJobEntity CreateEntity()
    {
        return new RecurringJobEntity(RecurringJobId, _recurringJob);
    }
}