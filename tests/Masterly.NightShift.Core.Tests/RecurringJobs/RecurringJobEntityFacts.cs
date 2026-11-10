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
        _recurringJob["Misfire"] = "3";
        Assert.Throws<NotSupportedException>(CreateEntity);
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