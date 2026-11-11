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
}
