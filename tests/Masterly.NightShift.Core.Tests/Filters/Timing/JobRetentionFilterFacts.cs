namespace Masterly.NightShift.Core.Tests;

public class JobRetentionFilterFacts
{
    [Theory]
    [InlineData("Succeeded", 6)]
    [InlineData("Deleted", 48)]
    public void OnStateUnapplied_SetsTheExpiration_OfAFinalStateWithARetention(string state, int hours)
    {
        JobRetentionFilter filter = new(new JobRetentionOptions { Succeeded = TimeSpan.FromHours(6), Deleted = TimeSpan.FromHours(48) }.States);
        ApplyStateContextMock context = new()
        {
            NewStateObject = state == "Succeeded" ? new SucceededState(null, 0, 0) : new DeletedState(),
        };

        filter.OnStateUnapplied(context.Object, null);

        Assert.Equal(TimeSpan.FromHours(hours), context.Object.JobExpirationTimeout);
    }

    [Fact]
    public void OnStateUnapplied_LeavesTheExpiration_OfAStateWithoutRetention()
    {
        JobRetentionFilter filter = new(new JobRetentionOptions { Deleted = TimeSpan.FromHours(48) }.States);
        ApplyStateContextMock context = new() { NewStateObject = new SucceededState(null, 0, 0) };

        filter.OnStateUnapplied(context.Object, null);

        Assert.Equal(TimeSpan.FromMinutes(1), context.Object.JobExpirationTimeout);
    }

    [Fact]
    public void Options_KeepCustomStates_AndTreatFailedApart()
    {
        JobRetentionOptions options = new JobRetentionOptions()
            .Set("Archived", TimeSpan.FromDays(90))
            .Set("failed", TimeSpan.FromDays(3));

        Assert.Equal(TimeSpan.FromDays(90), options.States["archived"]);
        Assert.Equal(TimeSpan.FromDays(3), options.Failed);
        Assert.False(options.States.ContainsKey("Failed"));

        options.Set("Archived", null);
        Assert.Empty(options.States);
    }

    [Fact]
    public void Options_RefuseAnInvalidRetention()
    {
        JobRetentionOptions options = new();

        Assert.Throws<ArgumentOutOfRangeException>(() => options.Succeeded = TimeSpan.Zero);
        Assert.Throws<ArgumentOutOfRangeException>(() => options.Deleted = TimeSpan.FromDays(40000));
        Assert.Throws<ArgumentException>("stateName", () => options.Set(" ", TimeSpan.FromDays(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => options.Failed = TimeSpan.Zero);
        Assert.Throws<ArgumentOutOfRangeException>(() => options.Failed = TimeSpan.FromDays(40000));
        Assert.Throws<ArgumentOutOfRangeException>(() => options.Set("Failed", TimeSpan.FromSeconds(-1)));
    }

    [Fact, GlobalLock(Reason = "Changes the global filters")]
    public void UseJobRetention_CalledAgainWithoutFailed_RemovesTheFailedRetentionItSet()
    {
        IGlobalConfiguration configuration = new Mock<IGlobalConfiguration>().Object;
        try
        {
            configuration.UseJobRetention(o => o.Failed = TimeSpan.FromDays(7));
            configuration.UseJobRetention(o => o.Succeeded = TimeSpan.FromHours(1));

            Assert.DoesNotContain(GlobalJobFilters.Filters, static x => x.Instance is FailedJobRetentionFilter);
        }
        finally
        {
            GlobalJobFilters.Filters.Remove(typeof(JobRetentionFilter));
            GlobalJobFilters.Filters.Remove(typeof(FailedJobRetentionFilter));
        }
    }

    [Fact, GlobalLock(Reason = "Changes the global filters")]
    public void UseJobRetention_WithoutFailed_LeavesAFailedRetentionSetByUseFailedJobRetention()
    {
        IGlobalConfiguration configuration = new Mock<IGlobalConfiguration>().Object;
        try
        {
            configuration.UseJobRetention(o => o.Failed = TimeSpan.FromDays(7));
            configuration.UseFailedJobRetention(TimeSpan.FromDays(3));
            configuration.UseJobRetention(o => o.Succeeded = TimeSpan.FromHours(1));

            Assert.Contains(GlobalJobFilters.Filters, static x => x.Instance is FailedJobRetentionFilter { Retention.TotalDays: 3 });
        }
        finally
        {
            GlobalJobFilters.Filters.Remove(typeof(JobRetentionFilter));
            GlobalJobFilters.Filters.Remove(typeof(FailedJobRetentionFilter));
        }
    }

    [Fact, GlobalLock(Reason = "Changes the global filters")]
    public void UseJobRetention_AddsTheFilterOnce_AndUpdatesIt()
    {
        IGlobalConfiguration configuration = new Mock<IGlobalConfiguration>().Object;
        try
        {
            configuration.UseJobRetention(o => o.Succeeded = TimeSpan.FromHours(1));
            configuration.UseJobRetention(o => o.Succeeded = TimeSpan.FromHours(2));

            JobRetentionFilter filter = (JobRetentionFilter)Assert.Single(GlobalJobFilters.Filters, static x => x.Instance is JobRetentionFilter).Instance;
            Assert.Equal(TimeSpan.FromHours(2), filter.Retention["Succeeded"]);
        }
        finally
        {
            GlobalJobFilters.Filters.Remove(typeof(JobRetentionFilter));
        }
    }

    [Fact, GlobalLock(Reason = "Changes the global filters")]
    public void UseJobRetention_WithFailed_SetsTheFailedJobRetention()
    {
        IGlobalConfiguration configuration = new Mock<IGlobalConfiguration>().Object;
        try
        {
            configuration.UseJobRetention(o => o.Failed = TimeSpan.FromDays(7));

            Assert.Contains(GlobalJobFilters.Filters, static x => x.Instance is FailedJobRetentionFilter { Retention.TotalDays: 7 });
        }
        finally
        {
            GlobalJobFilters.Filters.Remove(typeof(JobRetentionFilter));
            GlobalJobFilters.Filters.Remove(typeof(FailedJobRetentionFilter));
        }
    }
}
