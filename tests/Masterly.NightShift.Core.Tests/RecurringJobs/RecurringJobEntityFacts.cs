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

    private RecurringJobEntity CreateEntity()
    {
        return new RecurringJobEntity(RecurringJobId, _recurringJob);
    }
}