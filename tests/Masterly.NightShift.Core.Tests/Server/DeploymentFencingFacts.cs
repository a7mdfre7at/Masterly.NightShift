namespace Masterly.NightShift.Core.Tests;

public sealed class DeploymentFencingFacts : IDisposable
{
    private static readonly DateTime Now = new(2026, 11, 20, 9, 0, 0, DateTimeKind.Utc);

    private readonly PolicyStorage _storage = new();

    public DeploymentFencingFacts()
    {
        DrainMode.InvalidateCache();
    }

    public void Dispose()
    {
        DeploymentFencing.DeploymentId = null;
        DeploymentFencing.DeploymentOrder = null;
        DrainMode.InvalidateCache();
    }

    [Fact, GlobalLock(Reason = "Uses the drain mode cache")]
    public void Check_TheFirstDeployment_ClaimsTheCluster()
    {
        bool fenced = new DeploymentFencingProcess("v1").Check(_storage.Connection.Object, "server-1", Now);

        Assert.False(fenced);
        Assert.Equal("v1", _storage.Hashes[DeploymentFencing.HashKey]["Current"]);
        Assert.Equal("v1", DeploymentFencing.GetCurrentDeployment(_storage.Object));
    }

    [Fact, GlobalLock(Reason = "Uses the drain mode cache")]
    public void Check_ANewDeployment_ClaimsTheCluster_AndTheOldServersPauseThemselves()
    {
        new DeploymentFencingProcess("v1").Check(_storage.Connection.Object, "old-server", Now);

        Assert.False(new DeploymentFencingProcess("v2").Check(_storage.Connection.Object, "new-server", Now.AddSeconds(5)));
        Assert.True(new DeploymentFencingProcess("v1").Check(_storage.Connection.Object, "old-server", Now.AddSeconds(10)));

        DrainModeStatus status = DrainMode.Read(_storage.Connection.Object);
        Assert.True(status.IsServerPaused("old-server"));
        Assert.Equal("Fenced: deployment 'v1' was replaced by 'v2'", status.PausedServers["old-server"]);
        Assert.False(status.IsServerPaused("new-server"));
        Assert.Equal("v2", DeploymentFencing.GetCurrentDeployment(_storage.Object));
    }

    [Fact, GlobalLock(Reason = "Uses the drain mode cache")]
    public void Check_ADeploymentSeenBefore_TakesOverOnlyOnceTheCurrentIsSilent_AndResumesItself()
    {
        new DeploymentFencingProcess("v1").Check(_storage.Connection.Object, "old-server", Now);
        new DeploymentFencingProcess("v2").Check(_storage.Connection.Object, "new-server", Now.AddSeconds(1));
        Assert.True(new DeploymentFencingProcess("v1").Check(_storage.Connection.Object, "old-server", Now.AddSeconds(20)));

        // v2 has been silent for more than TakeOverAfter
        bool fenced = new DeploymentFencingProcess("v1").Check(_storage.Connection.Object, "old-server", Now.AddMinutes(2));

        Assert.False(fenced);
        Assert.Equal("v1", DeploymentFencing.GetCurrentDeployment(_storage.Object));
        Assert.False(DrainMode.Read(_storage.Connection.Object).IsServerPaused("old-server"));
    }

    [Fact, GlobalLock(Reason = "Uses the drain mode cache and the log provider")]
    public void Check_WarnsWhenADeploymentNeverSeen_TakesOverFromLiveServers()
    {
        RecordingLogProvider recording = new();
        new DeploymentFencingProcess("v2").Check(_storage.Connection.Object, "new-server", Now);

        using (recording.Use())
        {
            new DeploymentFencingProcess("v1-stray").Check(_storage.Connection.Object, "stray-server", Now.AddSeconds(5));
        }

        RecordingLogProvider.LogEntry warning = Assert.Single(recording.Entries, static x => x.Level == Masterly.NightShift.Logging.LogLevel.Warn);
        Assert.Contains("'v1-stray', never seen before, takes over from 'v2'", warning.Message, StringComparison.Ordinal);
        Assert.Equal("v1-stray", DeploymentFencing.GetCurrentDeployment(_storage.Object));
    }

    [Fact, GlobalLock(Reason = "Uses the drain mode cache and the log provider")]
    public void Check_DoesNotWarn_WhenTheCurrentDeploymentIsSilent()
    {
        RecordingLogProvider recording = new();
        new DeploymentFencingProcess("v1").Check(_storage.Connection.Object, "old-server", Now);

        using (recording.Use())
        {
            new DeploymentFencingProcess("v2").Check(_storage.Connection.Object, "new-server", Now.AddMinutes(5));
        }

        Assert.DoesNotContain(recording.Entries, static x => x.Level == Masterly.NightShift.Logging.LogLevel.Warn);
    }

    [Fact, GlobalLock(Reason = "Uses the drain mode cache")]
    public void Check_KeepsAPauseSetByHand()
    {
        DrainMode.PauseServer(_storage.Connection.Object, "server-1", "maintenance");

        new DeploymentFencingProcess("v1").Check(_storage.Connection.Object, "server-1", Now);

        Assert.Equal("maintenance", DrainMode.Read(_storage.Connection.Object).PausedServers["server-1"]);
    }

    [Fact]
    public void GetCurrentDeployment_ReturnsNull_BeforeAnyServerChecked()
    {
        Assert.Null(DeploymentFencing.GetCurrentDeployment(_storage.Object));
    }

    [Fact, StaticLock]
    public void UseDeploymentFencing_SetsTheDeployment_OrRefusesABadOne()
    {
        IGlobalConfiguration configuration = new Mock<IGlobalConfiguration>().Object;

        configuration.UseDeploymentFencing(" release-42 ");
        Assert.Equal("release-42", DeploymentFencing.DeploymentId);

        Assert.Throws<ArgumentException>("deploymentId", () => configuration.UseDeploymentFencing(new string('x', 65)));
        Assert.Throws<ArgumentException>("deploymentId", () => configuration.UseDeploymentFencing(" "));
        Assert.Throws<ArgumentException>("deploymentId", () => configuration.UseDeploymentFencing("a\nb"));
    }

    [Fact, GlobalLock(Reason = "Uses the drain mode cache and the log provider")]
    public void Check_AHigherOrder_TakesOverALiveCurrentAtOnce_WithoutAWarning()
    {
        RecordingLogProvider recording = new();
        new DeploymentFencingProcess("v1", 41).Check(_storage.Connection.Object, "old-server", Now);

        bool fenced;
        using (recording.Use())
        {
            fenced = new DeploymentFencingProcess("v2", 42).Check(_storage.Connection.Object, "new-server", Now.AddSeconds(5));
        }

        Assert.False(fenced);
        Assert.Equal("v2", DeploymentFencing.GetCurrentDeployment(_storage.Object));
        Assert.Equal(42, DeploymentFencing.GetCurrentDeploymentOrder(_storage.Object));
        Assert.DoesNotContain(recording.Entries, static x => x.Level == Masterly.NightShift.Logging.LogLevel.Warn);
        Assert.Contains(recording.Entries, static x => x.Message.Contains("'v2' (order 42) takes over from 'v1' (order 41)", StringComparison.Ordinal));
    }

    [Fact, GlobalLock(Reason = "Uses the drain mode cache")]
    public void Check_AHigherOrder_TakesOverALiveCurrentAtOnce_EvenWhenItWasSeenBefore()
    {
        new DeploymentFencingProcess("v2", 42).Check(_storage.Connection.Object, "new-server", Now);
        new DeploymentFencingProcess("v1", 41).Check(_storage.Connection.Object, "old-server", Now.AddSeconds(1));

        // v2 went silent, so v1 took over; then the servers of v2 come back while those of v1 are running
        Assert.False(new DeploymentFencingProcess("v1", 41).Check(_storage.Connection.Object, "old-server", Now.AddMinutes(2)));
        Assert.Equal("v1", DeploymentFencing.GetCurrentDeployment(_storage.Object));

        Assert.False(new DeploymentFencingProcess("v2", 42).Check(_storage.Connection.Object, "new-server", Now.AddMinutes(2).AddSeconds(5)));
        Assert.True(new DeploymentFencingProcess("v1", 41).Check(_storage.Connection.Object, "old-server", Now.AddMinutes(2).AddSeconds(10)));

        Assert.Equal("v2", DeploymentFencing.GetCurrentDeployment(_storage.Object));
    }

    [Fact, GlobalLock(Reason = "Uses the drain mode cache")]
    public void Check_ALowerOrderNeverSeen_StaysFencedWhileTheCurrentIsAlive_AndTakesOverOnceItIsSilent()
    {
        DeploymentFencingProcess current = new("v2", 42);
        DeploymentFencingProcess stray = new("v1-stray", 7);
        current.Check(_storage.Connection.Object, "new-server", Now);

        Assert.True(stray.Check(_storage.Connection.Object, "stray-server", Now.AddSeconds(5)));
        Assert.False(current.Check(_storage.Connection.Object, "new-server", Now.AddSeconds(20)));
        Assert.True(stray.Check(_storage.Connection.Object, "stray-server", Now.AddSeconds(25)));

        DrainModeStatus status = DrainMode.Read(_storage.Connection.Object);
        Assert.Equal("Fenced: deployment 'v1-stray' (order 7) is older than 'v2' (order 42)", status.PausedServers["stray-server"]);
        Assert.False(status.IsServerPaused("new-server"));
        Assert.Equal("v2", DeploymentFencing.GetCurrentDeployment(_storage.Object));

        // v2 has been silent for more than TakeOverAfter
        Assert.False(stray.Check(_storage.Connection.Object, "stray-server", Now.AddMinutes(2)));

        Assert.Equal("v1-stray", DeploymentFencing.GetCurrentDeployment(_storage.Object));
        Assert.Equal(7, DeploymentFencing.GetCurrentDeploymentOrder(_storage.Object));
        Assert.False(DrainMode.Read(_storage.Connection.Object).IsServerPaused("stray-server"));
    }

    [Fact, GlobalLock(Reason = "Uses the drain mode cache and the log provider")]
    public void Check_WarnsOnce_WhenANewerDeploymentFencesIt()
    {
        RecordingLogProvider recording = new();
        DeploymentFencingProcess current = new("v2", 42);
        DeploymentFencingProcess stray = new("v1-stray", 7);
        current.Check(_storage.Connection.Object, "new-server", Now);

        using (recording.Use())
        {
            stray.Check(_storage.Connection.Object, "stray-server", Now.AddSeconds(5));
            current.Check(_storage.Connection.Object, "new-server", Now.AddSeconds(10));
            stray.Check(_storage.Connection.Object, "stray-server", Now.AddSeconds(20));
            current.Check(_storage.Connection.Object, "new-server", Now.AddSeconds(25));
            stray.Check(_storage.Connection.Object, "stray-server", Now.AddSeconds(35));
        }

        RecordingLogProvider.LogEntry warning = Assert.Single(recording.Entries, static x => x.Level == Masterly.NightShift.Logging.LogLevel.Warn);
        Assert.Contains("'v1-stray' (order 7) is fenced, because the newer deployment 'v2' (order 42) is the current one", warning.Message, StringComparison.Ordinal);
    }

    [Fact, GlobalLock(Reason = "Uses the drain mode cache and the log provider")]
    public void Check_WithEqualOrders_FollowsTheRuleOfSeenAndUnseenDeployments()
    {
        RecordingLogProvider recording = new();
        new DeploymentFencingProcess("v1", 42).Check(_storage.Connection.Object, "old-server", Now);

        // Never seen: takes over at once, with the warning
        bool unseenFenced;
        using (recording.Use())
        {
            unseenFenced = new DeploymentFencingProcess("v2", 42).Check(_storage.Connection.Object, "new-server", Now.AddSeconds(5));
        }

        // Seen before: waits for the current one to be silent
        bool seenFenced = new DeploymentFencingProcess("v1", 42).Check(_storage.Connection.Object, "old-server", Now.AddSeconds(10));

        Assert.False(unseenFenced);
        Assert.True(seenFenced);
        Assert.Equal("v2", DeploymentFencing.GetCurrentDeployment(_storage.Object));
        Assert.Equal("Fenced: deployment 'v1' was replaced by 'v2'", DrainMode.Read(_storage.Connection.Object).PausedServers["old-server"]);
        RecordingLogProvider.LogEntry warning = Assert.Single(recording.Entries, static x => x.Level == Masterly.NightShift.Logging.LogLevel.Warn);
        Assert.Contains("'v2', never seen before, takes over from 'v1'", warning.Message, StringComparison.Ordinal);
    }

    [Theory, GlobalLock(Reason = "Uses the drain mode cache")]
    [InlineData(42L, null)]
    [InlineData(null, 7L)]
    [InlineData(null, null)]
    public void Check_WithoutAnOrderOnEitherSide_BehavesAsWithoutOrders(long? currentOrder, long? otherOrder)
    {
        new DeploymentFencingProcess("v2", currentOrder).Check(_storage.Connection.Object, "new-server", Now);

        // Never seen: takes over at once, whatever its order
        Assert.False(new DeploymentFencingProcess("v1", otherOrder).Check(_storage.Connection.Object, "old-server", Now.AddSeconds(5)));
        Assert.Equal("v1", DeploymentFencing.GetCurrentDeployment(_storage.Object));

        // Seen before: fenced while the current one is alive, and takes over once it is silent
        Assert.True(new DeploymentFencingProcess("v2", currentOrder).Check(_storage.Connection.Object, "new-server", Now.AddSeconds(10)));
        Assert.Equal("Fenced: deployment 'v2' was replaced by 'v1'", DrainMode.Read(_storage.Connection.Object).PausedServers["new-server"]);
        Assert.False(new DeploymentFencingProcess("v2", currentOrder).Check(_storage.Connection.Object, "new-server", Now.AddMinutes(2)));
        Assert.Equal("v2", DeploymentFencing.GetCurrentDeployment(_storage.Object));
    }

    [Fact, GlobalLock(Reason = "Uses the drain mode cache")]
    public void Check_KeepsTheOrderWithTheDeployment_WithoutChangingTheOtherFields()
    {
        new DeploymentFencingProcess("v1", 41).Check(_storage.Connection.Object, "server-1", Now);

        Dictionary<string, string> hash = _storage.Hashes[DeploymentFencing.HashKey];
        Assert.Equal("v1", hash["Current"]);
        Assert.Equal(JobHelper.SerializeDateTime(Now), hash["Seen:v1"]);
        Assert.Equal("41", hash["Order:v1"]);

        // The same deployment started without an order has none any more
        new DeploymentFencingProcess("v1", null).Check(_storage.Connection.Object, "server-1", Now.AddSeconds(15));

        Assert.Equal(string.Empty, hash["Order:v1"]);
        Assert.Equal("v1", DeploymentFencing.GetCurrentDeployment(_storage.Object));
        Assert.Null(DeploymentFencing.GetCurrentDeploymentOrder(_storage.Object));
    }

    [Fact, GlobalLock(Reason = "Uses the drain mode cache")]
    public void Check_WritesNoOrderField_ForADeploymentWithoutAnOrder()
    {
        new DeploymentFencingProcess("v1").Check(_storage.Connection.Object, "server-1", Now);

        Assert.Equal(["Current", "Seen:v1"], _storage.Hashes[DeploymentFencing.HashKey].Keys.Order(StringComparer.Ordinal));
        Assert.Null(DeploymentFencing.GetCurrentDeploymentOrder(_storage.Object));
    }

    [Fact]
    public void GetCurrentDeploymentOrder_ReturnsNull_BeforeAnyServerChecked()
    {
        Assert.Null(DeploymentFencing.GetCurrentDeploymentOrder(_storage.Object));
    }

    [Fact, StaticLock]
    public void UseDeploymentFencing_WithAnOrder_SetsBoth_AndTheProcessTakesTheOrder()
    {
        IGlobalConfiguration configuration = new Mock<IGlobalConfiguration>().Object;

        configuration.UseDeploymentFencing("release-42", 42);

        Assert.Equal("release-42", DeploymentFencing.DeploymentId);
        Assert.Equal(42, DeploymentFencing.DeploymentOrder);
        Assert.Equal(42, new DeploymentFencingProcess("release-42").DeploymentOrder);
        Assert.Equal("Deployment fencing (release-42 (order 42))", new DeploymentFencingProcess("release-42").ToString());

        configuration.UseDeploymentFencing("release-0", 0);
        Assert.Equal(0, DeploymentFencing.DeploymentOrder);

        // Without an order, the one set before no longer applies
        configuration.UseDeploymentFencing("release-43");
        Assert.Null(DeploymentFencing.DeploymentOrder);
        Assert.Null(new DeploymentFencingProcess("release-43").DeploymentOrder);
    }

    [Fact, StaticLock]
    public void UseDeploymentFencing_RefusesANegativeOrder_AndChangesNothing()
    {
        IGlobalConfiguration configuration = new Mock<IGlobalConfiguration>().Object;
        configuration.UseDeploymentFencing("release-42", 42);

        Assert.Throws<ArgumentOutOfRangeException>("deploymentOrder", () => configuration.UseDeploymentFencing("release-43", -1));
        Assert.Throws<ArgumentException>("deploymentId", () => configuration.UseDeploymentFencing(" ", 43));

        Assert.Equal("release-42", DeploymentFencing.DeploymentId);
        Assert.Equal(42, DeploymentFencing.DeploymentOrder);
    }

    [Theory]
    [InlineData("v2", null, null, null, "v2")]
    [InlineData("v2", null, "v2", null, "v2")]
    [InlineData("v1", null, "v2", null, "v1 (v2)")]
    [InlineData("v2", 42L, "v2", 42L, "v2 (order 42)")]
    [InlineData("v1", 41L, "v2", 42L, "v1 (order 41) (v2, order 42)")]
    [InlineData("v1", null, "v2", 42L, "v1 (v2, order 42)")]
    [InlineData("v1", 41L, "v2", null, "v1 (order 41) (v2)")]
    public void AboutPage_ShowsTheOrderWithTheDeployment(string deployment, long? order, string current, long? currentOrder, string expected)
    {
        Assert.Equal(expected, Masterly.NightShift.Dashboard.Pages.AboutPage.DescribeDeployment(deployment, order, current, currentOrder));
    }

    [Fact, StaticLock]
    public void AboutPage_ReadsTheCurrentDeploymentAndItsOrder()
    {
        Masterly.NightShift.Core.Tests.Dashboard.Pages.PageRenderer renderer = new();
        renderer.Connection.Setup(x => x.GetAllEntriesFromHash(DeploymentFencing.HashKey)).Returns(new Dictionary<string, string>
        {
            ["Current"] = "v2",
            ["Order:v2"] = "42",
        });
        DeploymentFencing.DeploymentId = "v1";
        DeploymentFencing.DeploymentOrder = 41;

        string html = renderer.Render(new Masterly.NightShift.Dashboard.Pages.AboutPage());

        Assert.Contains("v1 (order 41) (v2, order 42)", html, StringComparison.Ordinal);
    }
}
