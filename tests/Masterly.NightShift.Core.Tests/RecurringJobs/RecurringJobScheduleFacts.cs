namespace Masterly.NightShift.Core.Tests;

public sealed class RecurringJobScheduleFacts : IDisposable
{
    private static readonly DateTime Now = new(2026, 11, 20, 9, 30, 0, DateTimeKind.Utc);

    private readonly Mock<JobStorage> _storage = new();
    private readonly Mock<IStorageConnection> _connection = new();
    private readonly Dictionary<string, string> _hash = new()
    {
        ["Cron"] = "0 * * * *",
        ["Job"] = InvocationData.SerializeJob(Job.FromExpression(() => Run())).SerializePayload(),
    };

    public RecurringJobScheduleFacts()
    {
        _storage.Setup(x => x.GetReadOnlyConnection()).Returns(_connection.Object);
        _connection.Setup(x => x.GetAllEntriesFromHash("recurring-job:hourly")).Returns(_hash);
        JobClock.TimeProvider = new FixedClock(new DateTimeOffset(Now));
    }

    public void Dispose()
    {
        JobClock.TimeProvider = TimeProvider.System;
    }

    [Fact, GlobalLock(Reason = "Changes the clock")]
    public void GetNextOccurrences_ListsTheNextRuns_AfterNow()
    {
        IReadOnlyList<DateTime> next = _storage.Object.GetNextOccurrences("hourly", 3);

        Assert.Equal([Now.AddMinutes(30), Now.AddMinutes(90), Now.AddMinutes(150)], next);
    }

    [Fact, GlobalLock(Reason = "Changes the clock")]
    public void GetNextOccurrences_StartsAtTheStartDate_WhenItIsLater()
    {
        _hash["StartAt"] = JobHelper.SerializeDateTime(Now.AddDays(1).Date);

        IReadOnlyList<DateTime> next = _storage.Object.GetNextOccurrences("hourly", 2);

        Assert.Equal([Now.AddDays(1).Date, Now.AddDays(1).Date.AddHours(1)], next);
    }

    [Fact, GlobalLock(Reason = "Changes the clock")]
    public void GetNextOccurrences_StopsAtTheEndDate()
    {
        _hash["EndAt"] = JobHelper.SerializeDateTime(Now.AddHours(2));

        IReadOnlyList<DateTime> next = _storage.Object.GetNextOccurrences("hourly", 10);

        Assert.Equal([Now.AddMinutes(30), Now.AddMinutes(90)], next);
    }

    [Fact, GlobalLock(Reason = "Changes the clock")]
    public void GetNextOccurrences_ReturnsNothing_ForAPausedJob_OrAMissingOne()
    {
        Assert.Empty(_storage.Object.GetNextOccurrences("missing", 5));

        _hash["Paused"] = "1";
        Assert.Empty(_storage.Object.GetNextOccurrences("hourly", 5));
    }

    [Theory, GlobalLock(Reason = "Changes the clock")]
    [InlineData(0)]
    [InlineData(1001)]
    public void GetNextOccurrences_ThrowsAnException_WhenCountIsOutOfRange(int count)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => _storage.Object.GetNextOccurrences("hourly", count));
    }

    [Fact, GlobalLock(Reason = "Changes the clock")]
    public void GetNextOccurrences_ThrowsAnException_WhenArgumentsAreNull()
    {
        Assert.Throws<ArgumentNullException>("storage", () => RecurringJobSchedule.GetNextOccurrences(null, "hourly", 1));
        Assert.Throws<ArgumentNullException>("recurringJobId", () => _storage.Object.GetNextOccurrences(null, 1));
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "xUnit1013:Public method should be marked as test", Justification = "Should be public to be a job")]
    public static void Run()
    {
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
