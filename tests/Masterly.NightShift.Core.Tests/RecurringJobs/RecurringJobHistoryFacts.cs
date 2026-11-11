using System.Globalization;
using Masterly.NightShift.Storage.Monitoring;

namespace Masterly.NightShift.Core.Tests;

public class RecurringJobHistoryFacts
{
    private const string Key = "recurring-job:nightly:runs";

    private readonly Mock<JobStorage> _storage = new();
    private readonly Mock<JobStorageConnection> _connection = new();
    private readonly Mock<IMonitoringApi> _monitor = new();

    public RecurringJobHistoryFacts()
    {
        _storage.Setup(x => x.HasFeature(JobStorageFeatures.ExtendedApi)).Returns(true);
        _storage.Setup(x => x.GetReadOnlyConnection()).Returns(_connection.Object);
        _storage.Setup(x => x.GetMonitoringApi()).Returns(_monitor.Object);
    }

    [Fact]
    public void GetKey_NamesTheListAfterTheRecurringJob()
    {
        Assert.Equal(Key, RecurringJobHistory.GetKey("nightly"));
    }

    [Fact]
    public void GetKey_ShortensAKeyLongerThanSqlServerKeeps()
    {
        string key = RecurringJobHistory.GetKey(new string('x', 120));

        Assert.Equal(100, key.Length);
        Assert.StartsWith("recurring-job:xxx", key, StringComparison.Ordinal);
        Assert.NotEqual(key, RecurringJobHistory.GetKey(new string('x', 121)));
    }

    [Fact]
    public void GetKey_ThrowsAnException_WhenIdIsNull()
    {
        Assert.Throws<ArgumentNullException>(() => RecurringJobHistory.GetKey(null));
    }

    [Fact]
    public void GetRecurringJobRuns_ReadsEachRun_NewestFirst()
    {
        DateTime occurrence = new(2026, 11, 20, 9, 0, 0, DateTimeKind.Utc);
        _connection.Setup(x => x.GetRangeFromList(Key, 0, 49)).Returns(["2", "1"]);
        _monitor.Setup(x => x.JobDetails("2")).Returns(Details(
            occurrence,
            Properties(("Time", JobHelper.ToTimestamp(occurrence).ToString(CultureInfo.InvariantCulture)), ("RetryCount", "1")),
            State("Enqueued", occurrence, null),
            State("Processing", occurrence.AddSeconds(2), null, ("ServerId", "server-1")),
            State("Succeeded", occurrence.AddSeconds(5), "done", ("PerformanceDuration", "2500"))));
        _monitor.Setup(x => x.JobDetails("1")).Returns(Details(
            occurrence.AddDays(-1),
            Properties(),
            State("Deleted", occurrence.AddDays(-1).AddHours(3), "Skipped: started 3 h late")));

        IReadOnlyList<RecurringJobRun> runs = _storage.Object.GetRecurringJobRuns("nightly");

        Assert.Equal(2, runs.Count);
        RecurringJobRun succeeded = runs[0];
        Assert.Equal("2", succeeded.JobId);
        Assert.Equal("Succeeded", succeeded.State);
        Assert.Equal(occurrence, succeeded.ScheduledFor);
        Assert.Equal(occurrence.AddSeconds(2), succeeded.StartedAt);
        Assert.Equal(TimeSpan.FromMilliseconds(2500), succeeded.Duration);
        Assert.Equal(1, succeeded.RetryCount);
        Assert.Equal("server-1", succeeded.ServerId);
        Assert.Equal("done", succeeded.Reason);
        Assert.False(succeeded.Expired);

        RecurringJobRun skipped = runs[1];
        Assert.Equal("Deleted", skipped.State);
        Assert.Equal("Skipped: started 3 h late", skipped.Reason);
        Assert.Null(skipped.StartedAt);
        Assert.Null(skipped.Duration);
        Assert.Null(skipped.ScheduledFor);
    }

    [Fact]
    public void GetRecurringJobRuns_ListsAnExpiredJob_AsExpired()
    {
        _connection.Setup(x => x.GetRangeFromList(Key, 0, 9)).Returns(["gone"]);

        RecurringJobRun run = Assert.Single(_storage.Object.GetRecurringJobRuns("nightly", 10));

        Assert.Equal("gone", run.JobId);
        Assert.True(run.Expired);
        Assert.Null(run.State);
    }

    [Fact]
    public void GetRecurringJobRuns_ReturnsNothing_WithoutTheExtendedApi()
    {
        _storage.Setup(x => x.HasFeature(JobStorageFeatures.ExtendedApi)).Returns(false);

        Assert.Empty(_storage.Object.GetRecurringJobRuns("nightly"));
        _storage.Verify(x => x.GetReadOnlyConnection(), Times.Never);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(51)]
    public void GetRecurringJobRuns_ThrowsAnException_WhenCountIsOutOfRange(int count)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => _storage.Object.GetRecurringJobRuns("nightly", count));
    }

    [Fact]
    public void GetRecurringJobRuns_ThrowsAnException_WhenArgumentsAreNull()
    {
        Assert.Throws<ArgumentNullException>("storage", () => RecurringJobHistory.GetRecurringJobRuns(null, "nightly"));
        Assert.Throws<ArgumentNullException>("recurringJobId", () => _storage.Object.GetRecurringJobRuns(null));
    }

    private static Dictionary<string, string> Properties(params (string Key, string Value)[] values)
    {
        return values.ToDictionary(x => x.Key, x => x.Value);
    }

    private static JobDetailsDto Details(DateTime createdAt, Dictionary<string, string> properties, params StateHistoryDto[] history)
    {
        return new JobDetailsDto
        {
            CreatedAt = createdAt,
            Properties = properties,
            // The monitoring API lists the newest state first
            History = [.. Enumerable.Reverse(history)],
        };
    }

    private static StateHistoryDto State(string name, DateTime createdAt, string reason, params (string Key, string Value)[] data)
    {
        return new StateHistoryDto
        {
            StateName = name,
            CreatedAt = createdAt,
            Reason = reason,
            Data = data.ToDictionary(x => x.Key, x => x.Value),
        };
    }
}
