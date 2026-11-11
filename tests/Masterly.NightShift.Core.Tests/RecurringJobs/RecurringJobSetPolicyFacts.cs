namespace Masterly.NightShift.Core.Tests;

public class RecurringJobSetPolicyFacts
{
    private readonly PolicyStorage _storage = new();
    private readonly Mock<IRecurringJobManager> _manager = new();

    [Fact]
    public void AddPolicy_ThrowsAnException_ForANameDeclaredTwice_OrNull()
    {
        RecurringJobSet set = new RecurringJobSet().AddPolicy(new SchedulePolicy("weekdays"));

        Assert.Throws<InvalidOperationException>(() => set.AddPolicy(new SchedulePolicy("Weekdays")));
        Assert.Throws<ArgumentNullException>("policy", () => set.AddPolicy(null));
        Assert.Equal(["weekdays"], set.DeclaredPolicies);
    }

    [Fact]
    public void Apply_SavesThePolicies_TheReferencedOnesFirst_BeforeTheJobs()
    {
        bool policyStoredFirst = false;
        _manager.Setup(x => x.AddOrUpdate("report", It.IsAny<Job>(), "0 9 * * *", It.IsAny<RecurringJobOptions>()))
            .Callback(() => policyStoredFirst = _storage.Hashes.ContainsKey("schedule-policy:reports"));

        RecurringJobSetResult result = new RecurringJobSet()
            .AddPolicy(new SchedulePolicy("reports").Require("weekdays"))
            .AddPolicy(new SchedulePolicy("weekdays").AllowWeekdays(DayOfWeek.Monday))
            .Add("report", () => Run(), "0 9 * * *", new RecurringJobOptions { Policy = "reports" })
            .Apply(_manager.Object, _storage.Object);

        Assert.Equal(["weekdays", "reports"], result.PoliciesSaved);
        Assert.True(policyStoredFirst);
        Assert.Equal("weekdays", _storage.Hashes["schedule-policy:reports"]["Require"]);
    }

    [Fact]
    public void Apply_SavesNothing_WhenThePoliciesDidNotChange()
    {
        _storage.Add(new SchedulePolicy("weekdays").AllowWeekdays(DayOfWeek.Monday), revision: 2);

        RecurringJobSetResult result = new RecurringJobSet()
            .AddPolicy(new SchedulePolicy("weekdays").AllowWeekdays(DayOfWeek.Monday))
            .Apply(_manager.Object, _storage.Object);

        Assert.Empty(result.PoliciesSaved);
        Assert.Equal(0, _storage.Commits);
    }

    [Fact]
    public void Apply_AcceptsAReference_ToAStoredPolicyThatIsNotDeclared()
    {
        _storage.Add(new SchedulePolicy("trading"));

        RecurringJobSetResult result = new RecurringJobSet()
            .AddPolicy(new SchedulePolicy("reports").Require("trading"))
            .Add("report", () => Run(), "0 9 * * *", new RecurringJobOptions { Policy = "trading" })
            .Apply(_manager.Object, _storage.Object);

        Assert.Equal(["reports"], result.PoliciesSaved);
    }

    [Fact]
    public void Apply_ThrowsBeforeWritingAnything_WhenAJobFollowsAnUnknownPolicy()
    {
        RecurringJobSet set = new RecurringJobSet()
            .AddPolicy(new SchedulePolicy("weekdays"))
            .Add("report", () => Run(), "0 9 * * *", new RecurringJobOptions { Policy = "holidays" });

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => set.Apply(_manager.Object, _storage.Object));

        Assert.Contains("'holidays'", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, _storage.Commits);
        _manager.Verify(x => x.AddOrUpdate(It.IsAny<string>(), It.IsAny<Job>(), It.IsAny<string>(), It.IsAny<RecurringJobOptions>()), Times.Never);
    }

    [Fact]
    public void Apply_ThrowsBeforeWritingAnything_WhenDeclaredPoliciesFormACycle()
    {
        RecurringJobSet set = new RecurringJobSet()
            .AddPolicy(new SchedulePolicy("a").Require("b"))
            .AddPolicy(new SchedulePolicy("b").ExceptWhen("a"));

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => set.Apply(_manager.Object, _storage.Object));

        Assert.Contains("a -> b -> a", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, _storage.Commits);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "xUnit1013:Public method should be marked as test", Justification = "Should be public to be a job")]
    public static void Run()
    {
    }
}
