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
        RecurringJobEntity entity = CreatePolicyEntity(
            new SchedulePolicy("never").AllowWeekdays(DayOfWeek.Monday).ExceptWhen("mondays"),
            new SchedulePolicy("mondays").AllowWeekdays(DayOfWeek.Monday));

        List<DateTime> executions = [.. entity.ScheduleNext(UtcResolver(), _nowInstant, _nowInstant.AddHours(3), TimeSpan.Zero)];

        Assert.Empty(executions);
        Assert.Null(entity.NextExecution);
        Assert.Null(entity.PolicyFailure);
        Assert.Equal("Schedule policy 'never' allows none of its next occurrences in the next ten years.", entity.Error);
    }

    [Fact, StaticLock]
    public void ScheduleNext_KeepsTheJobScheduled_WithARetry_WhenTheDateProviderIsNotRegisteredOnThisServer()
    {
        ScheduleDateProviders.Clear();
        RecurringJobEntity entity = CreatePolicyEntity(new SchedulePolicy("holidays").ExcludeDatesFrom("not-registered"));
        entity.PolicyRetryDelay = TimeSpan.FromSeconds(15);

        // The first occurrence, 16:00, is in the past: the job is looked at again after the retry delay
        List<DateTime> executions = [.. entity.ScheduleNext(UtcResolver(), _nowInstant, _nowInstant.AddHours(1), TimeSpan.Zero)];

        Assert.Empty(executions);
        Assert.Equal(_nowInstant.AddHours(1).AddSeconds(15), entity.NextExecution);
        Assert.Equal(_nowInstant.Date.AddHours(16), entity.PolicyFailure?.Instant);
        Assert.Equal(
            "Schedule policy 'holidays' can't be evaluated, so the job waits and tries again: policy 'holidays': date provider 'not-registered' isn't registered on this server.",
            entity.Error);
    }

    [Fact, StaticLock]
    public void ScheduleNext_KeepsTheNextOccurrence_WhenTheDateProviderIsNotRegistered_AndTheOccurrenceIsAhead()
    {
        ScheduleDateProviders.Clear();
        RecurringJobEntity entity = CreatePolicyEntity(new SchedulePolicy("holidays").ExcludeDatesFrom("not-registered"));

        // Registered by a client without the provider: the servers, which have it, decide when the occurrence is due
        entity.ScheduleNext(UtcResolver(), _nowInstant);

        Assert.Equal(_nowInstant.Date.AddHours(16), entity.NextExecution);
        Assert.NotNull(entity.Error);
    }

    [Fact, StaticLock]
    public void ScheduleNext_RunsTheMissedOccurrence_OnceTheDateProviderWorksAgain()
    {
        ThrowingDates provider = new();
        ScheduleDateProviders.Set("flaky", provider);
        try
        {
            _recurringJob["Misfire"] = "1";
            RecurringJobEntity first = CreatePolicyEntity(new SchedulePolicy("flaky").ExcludeDatesFrom("flaky"));
            List<DateTime> failing = [.. first.ScheduleNext(UtcResolver(), _nowInstant, _nowInstant.AddHours(1), TimeSpan.Zero)];

            // The next pass reads the job and its policy again; the last execution hasn't moved
            provider.Fails = false;
            RecurringJobEntity entity = CreatePolicyEntity(new SchedulePolicy("flaky").ExcludeDatesFrom("flaky"));
            List<DateTime> working = [.. entity.ScheduleNext(UtcResolver(), _nowInstant, _nowInstant.AddHours(1).AddMinutes(1), TimeSpan.Zero)];

            Assert.Empty(failing);
            Assert.Equal([_nowInstant.Date.AddHours(16)], working);
            Assert.Equal(_nowInstant.Date.AddHours(17), entity.NextExecution);
            Assert.Null(entity.Error);
            Assert.Null(entity.PolicyFailure);
        }
        finally
        {
            ScheduleDateProviders.Clear();
        }
    }

    [Fact]
    public void ScheduleNext_WithRelaxed_DoesNotRunAMissedOccurrence_WhenThePolicyBlocksNow_AndWaitsForTheNextAllowedTime()
    {
        RecurringJobEntity entity = CreatePolicyEntity(new SchedulePolicy("office").AllowBetween("09:00", "17:00"));

        // 16:00 was missed; at 17:30 the office is closed, so it runs tomorrow at 09:00 rather than now
        List<DateTime> executions = [.. entity.ScheduleNext(UtcResolver(), _nowInstant, _nowInstant.Date.AddHours(17).AddMinutes(30), TimeSpan.Zero)];
        List<DateTime> tomorrow = [.. entity.ScheduleNext(UtcResolver(), _nowInstant, _nowInstant.Date.AddDays(1).AddHours(9), TimeSpan.Zero)];

        Assert.Empty(executions);
        Assert.Equal([_nowInstant.Date.AddDays(1).AddHours(9)], tomorrow);
    }

    [Fact]
    public void ScheduleNext_WithRelaxed_RunsAMissedOccurrenceNow_WhenThePolicyAllowsNow()
    {
        RecurringJobEntity entity = CreatePolicyEntity(new SchedulePolicy("office").AllowBetween("09:00", "17:00"));
        DateTime now = _nowInstant.Date.AddHours(16).AddMinutes(30);

        List<DateTime> executions = [.. entity.ScheduleNext(UtcResolver(), _nowInstant, now, TimeSpan.Zero)];

        Assert.Equal([now], executions);
        Assert.Equal(_nowInstant.Date.AddDays(1).AddHours(9), entity.NextExecution);
    }

    [Fact]
    public void ScheduleNext_ReadsEachPolicyOnce_WithCalendarsSharedByAPass()
    {
        PolicyStorage storage = new();
        storage.Add(new SchedulePolicy("office").AllowBetween("09:00", "17:00"));
        SchedulePolicyCalendars calendars = new(storage.Connection.Object);
        _recurringJob["TimeZoneId"] = "UTC";
        _recurringJob["Cron"] = "0 * * * *";
        _recurringJob["Policy"] = "office";

        ITimeZoneResolver resolver = UtcResolver();
        PolicyStorage dedicated = new();
        RecurringJobEntity first = new(RecurringJobId, _recurringJob) { PolicyCalendars = calendars };
        RecurringJobEntity second = new("second", _recurringJob) { PolicyCalendars = calendars };
        RecurringJobEntity third = new("third", _recurringJob) { PolicyCalendars = calendars.WithConnection(dedicated.Connection.Object) };

        first.ScheduleNext(resolver, _nowInstant);
        second.ScheduleNext(resolver, _nowInstant);
        third.ScheduleNext(resolver, _nowInstant);

        storage.Connection.Verify(x => x.GetAllEntriesFromHash(SchedulePolicies.GetKey("office")), Times.Once);
        dedicated.Connection.Verify(x => x.GetAllEntriesFromHash(It.IsAny<string>()), Times.Never);
        Assert.Equal(_nowInstant.Date.AddHours(16), third.NextExecution);
    }

    [Fact]
    public void ScheduleNext_RunsEveryOccurrence_WhenThePolicyDoesNotExist_OrCannotBeRead()
    {
        _recurringJob["TimeZoneId"] = "UTC";
        _recurringJob["Cron"] = "0 * * * *";
        _recurringJob["Policy"] = "gone";
        RecurringJobEntity missing = new(RecurringJobId, _recurringJob) { PolicyCalendars = new SchedulePolicyCalendars(new PolicyStorage().Connection.Object) };
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

    private RecurringJobEntity CreatePolicyEntity(SchedulePolicy policy, params SchedulePolicy[] others)
    {
        PolicyStorage storage = new();
        storage.Add(policy);
        foreach (SchedulePolicy other in others)
        {
            storage.Add(other);
        }

        _recurringJob["TimeZoneId"] = "UTC";
        _recurringJob["Cron"] = "0 * * * *";
        _recurringJob["Policy"] = policy.Name;
        return new RecurringJobEntity(RecurringJobId, _recurringJob) { PolicyCalendars = new SchedulePolicyCalendars(storage.Connection.Object) };
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

    private sealed class ThrowingDates : IScheduleDateProvider
    {
        public bool Fails { get; set; } = true;

        public bool Contains(DateOnly day) => Fails ? throw new InvalidOperationException("database down") : false;
    }

    [Fact, StaticLock]
    public void WaitsForPolicy_IsTrue_ForTheJobAPolicyFailureLeftOnTheSchedule_AlsoOnceItIsReadBack()
    {
        ScheduleDateProviders.Clear();
        RecurringJobEntity entity = CreatePolicyEntity(new SchedulePolicy("holidays").ExcludeDatesFrom("not-registered"));

        entity.ScheduleNext(UtcResolver(), _nowInstant, _nowInstant.AddHours(1), TimeSpan.Zero);
        entity.IsChanged(_nowInstant, out IReadOnlyDictionary<string, string> changedFields);
        foreach (KeyValuePair<string, string> field in changedFields)
        {
            _recurringJob[field.Key] = field.Value;
        }

        RecurringJobEntity stored = new(RecurringJobId, _recurringJob);

        Assert.True(entity.WaitsForPolicy);
        Assert.True(stored.WaitsForPolicy);
    }

    [Fact]
    public void WaitsForPolicy_IsFalse_ForAJobWithoutAnError_OrWithAnotherError()
    {
        RecurringJobEntity allowed = CreatePolicyEntity(new SchedulePolicy("office"));
        allowed.ScheduleNext(UtcResolver(), _nowInstant);

        RecurringJobEntity failed = CreatePolicyEntity(new SchedulePolicy("office"));
        failed.ScheduleRetry(_nowInstant.AddMinutes(1), "The job couldn't be created");

        Assert.False(allowed.WaitsForPolicy);
        Assert.False(failed.WaitsForPolicy);
    }

    [Fact]
    public void WaitsForPolicy_IsFalse_ForAJobWhosePolicyAllowsNothing_AsItLeftTheSchedule()
    {
        RecurringJobEntity entity = CreatePolicyEntity(
            new SchedulePolicy("never").AllowWeekdays(DayOfWeek.Monday).ExceptWhen("mondays"),
            new SchedulePolicy("mondays").AllowWeekdays(DayOfWeek.Monday));

        entity.ScheduleNext(UtcResolver(), _nowInstant, _nowInstant.AddHours(3), TimeSpan.Zero);

        Assert.NotNull(entity.Error);
        Assert.False(entity.WaitsForPolicy);
    }

    [Fact]
    public void WaitsForPolicy_IsFalse_WhenTheErrorIsAboutAnotherPolicy_OrTheJobFollowsNone()
    {
        _recurringJob["Error"] = "Schedule policy 'holidays' can't be evaluated, so the job waits and tries again: policy 'holidays' can't be read: bad.";
        _recurringJob["NextExecution"] = JobHelper.SerializeDateTime(_nowInstant);
        RecurringJobEntity withoutPolicy = CreateEntity();

        _recurringJob["Policy"] = "office";
        RecurringJobEntity otherPolicy = CreateEntity();

        _recurringJob["Policy"] = "holidays";
        RecurringJobEntity samePolicy = CreateEntity();

        Assert.False(withoutPolicy.WaitsForPolicy);
        Assert.False(otherPolicy.WaitsForPolicy);
        Assert.True(samePolicy.WaitsForPolicy);
    }
}
