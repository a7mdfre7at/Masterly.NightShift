namespace Masterly.NightShift.Core.Tests;

/// <summary>
/// Earlier builds resumed a queue or a server by writing a marker into its hash field; a resume now removes the field,
/// writing the hash again without it and without the markers.
/// </summary>
public class DrainModeResumedMarkerFacts : IDisposable
{
    private const string LegacyResumedMarker = "\u0000resumed";
    private const string ResumedMarker = "[resumed]";

    private readonly Mock<IStorageConnection> _connection = new();
    private readonly Mock<IWriteOnlyTransaction> _transaction = new();
    private readonly Dictionary<string, string> _hash = [];

    public DrainModeResumedMarkerFacts()
    {
        _connection.Setup(x => x.CreateWriteTransaction()).Returns(_transaction.Object);
        _connection.Setup(x => x.GetAllEntriesFromHash(DrainMode.HashKey)).Returns(() => _hash);
        _transaction.Setup(x => x.SetRangeInHash(DrainMode.HashKey, It.IsAny<IEnumerable<KeyValuePair<string, string>>>()))
            .Callback<string, IEnumerable<KeyValuePair<string, string>>>((_, pairs) => { foreach (KeyValuePair<string, string> pair in pairs) { _hash[pair.Key] = pair.Value; } });
        _transaction.Setup(x => x.RemoveHash(DrainMode.HashKey)).Callback(_hash.Clear);
        DrainMode.InvalidateCache();
    }

    public void Dispose()
    {
        DrainMode.InvalidateCache();
    }

    [Fact, StaticLock]
    public void ResumeQueue_RemovesTheField_AndTheMarkersOfEarlierBuilds()
    {
        _hash[DrainMode.QueueFieldPrefix + "reports"] = ResumedMarker;
        DrainMode.PauseQueue(_connection.Object, "emails", "maintenance");

        DrainMode.ResumeQueue(_connection.Object, "emails");

        Assert.Empty(_hash);
        Assert.Same(DrainModeStatus.Inactive, DrainMode.Read(_connection.Object));
    }

    [Fact, StaticLock]
    public void ResumeServer_RemovesTheField_AndTheMarkersOfEarlierBuilds()
    {
        _hash[DrainMode.ServerFieldPrefix + "web-2"] = LegacyResumedMarker;
        DrainMode.PauseServer(_connection.Object, "web-1", "patching");

        DrainMode.ResumeServer(_connection.Object, "web-1");

        Assert.Empty(_hash);
        Assert.Same(DrainModeStatus.Inactive, DrainMode.Read(_connection.Object));
    }

    [Fact, StaticLock]
    public void ResumeServer_WritesNothing_WhenThereIsNothingToRemove()
    {
        DrainMode.PauseServer(_connection.Object, "web-1", "patching");
        _transaction.Invocations.Clear();

        DrainMode.ResumeServer(_connection.Object, "web-2");

        _transaction.Verify(x => x.Commit(), Times.Never);
        Assert.True(DrainMode.Read(_connection.Object).IsServerPaused("web-1"));
    }

    [Fact, StaticLock]
    public void ClearStaleServerPauses_RemovesTheFields()
    {
        Mock<JobStorage> storage = new();
        Mock<IMonitoringApi> monitoring = new();
        storage.Setup(x => x.GetConnection()).Returns(_connection.Object);
        storage.Setup(x => x.GetMonitoringApi()).Returns(monitoring.Object);
        monitoring.Setup(x => x.Servers()).Returns([]);
        DrainMode.PauseServer(_connection.Object, "web-1");

        IReadOnlyList<string> cleared = DrainMode.ClearStaleServerPauses(storage.Object);

        Assert.Equal(["web-1"], cleared);
        Assert.Empty(_hash);
    }

    [Fact, StaticLock]
    public void Read_StillTreatsTheMarkerOfEarlierBuilds_AsResumed()
    {
        _hash[DrainMode.QueueFieldPrefix + "emails"] = LegacyResumedMarker;
        _hash[DrainMode.ServerFieldPrefix + "web-1"] = LegacyResumedMarker;

        Assert.Same(DrainModeStatus.Inactive, DrainMode.Read(_connection.Object));
    }

    [Fact, StaticLock]
    public void PauseQueue_WithAReasonThatReadsLikeTheMarker_StaysPaused()
    {
        DrainMode.PauseQueue(_connection.Object, "emails", ResumedMarker);
        DrainMode.PauseServer(_connection.Object, "web-1", ResumedMarker);
        DrainMode.PauseQueue(_connection.Object, "reports");

        // A rewrite of the hash keeps them
        DrainMode.ResumeQueue(_connection.Object, "reports");

        DrainModeStatus status = DrainMode.Read(_connection.Object);
        Assert.True(status.IsQueuePaused("emails"));
        Assert.True(status.IsServerPaused("web-1"));
    }

    [Fact, StaticLock]
    public void Read_StillTreatsThePrintableMarkerOfEarlierBuilds_AsResumed()
    {
        _hash[DrainMode.QueueFieldPrefix + "emails"] = ResumedMarker;
        _hash[DrainMode.ServerFieldPrefix + "web-1"] = ResumedMarker;

        Assert.Same(DrainModeStatus.Inactive, DrainMode.Read(_connection.Object));
    }
}
