namespace Masterly.NightShift.Core.Tests.Dashboard;

public class StorageClockFacts
{
    private static readonly DateTime StorageNow = new(2026, 11, 5, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime ProcessNow = new(2026, 11, 5, 12, 10, 0, DateTimeKind.Utc);

    private readonly Mock<JobStorage> _storage = new();
    private readonly Mock<JobStorageConnection> _connection = new();

    public StorageClockFacts()
    {
        _storage.Setup(x => x.GetReadOnlyConnection()).Returns(_connection.Object);
        _connection.Setup(x => x.GetUtcDateTime()).Returns(StorageNow);
    }

    [Fact]
    public void UtcNow_IsTheStoragesTime_ReadOnce_WhenTheStorageHasAClock()
    {
        _storage.Setup(x => x.HasFeature(JobStorageFeatures.Connection.GetUtcDateTime)).Returns(true);

        DateTime now = StorageClock.UtcNow(_storage.Object, static () => ProcessNow);

        Assert.Equal(StorageNow, now);
        _connection.Verify(x => x.GetUtcDateTime(), Times.Once);
        _connection.Verify(x => x.Dispose(), Times.Once);
    }

    [Fact]
    public void UtcNow_IsThisProcesssTime_WithoutAskingTheStorage_WhenItHasNoClock()
    {
        DateTime now = StorageClock.UtcNow(_storage.Object, static () => ProcessNow);

        Assert.Equal(ProcessNow, now);
        _storage.Verify(x => x.GetReadOnlyConnection(), Times.Never);
    }

    [Fact]
    public void UtcNow_IsThisProcesssTime_WhenTheConnectionCanNotTellTheStorages()
    {
        _storage.Setup(x => x.HasFeature(JobStorageFeatures.Connection.GetUtcDateTime)).Returns(true);
        _storage.Setup(x => x.GetReadOnlyConnection()).Returns(new Mock<IStorageConnection>().Object);

        Assert.Equal(ProcessNow, StorageClock.UtcNow(_storage.Object, static () => ProcessNow));
    }

    [Fact]
    public void UtcNow_SurfacesTheStoragesError()
    {
        _storage.Setup(x => x.HasFeature(JobStorageFeatures.Connection.GetUtcDateTime)).Returns(true);
        _connection.Setup(x => x.GetUtcDateTime()).Throws<InvalidOperationException>();

        Assert.Throws<InvalidOperationException>(() => StorageClock.UtcNow(_storage.Object, static () => ProcessNow));
    }

    [Fact]
    public void UtcNow_ThrowsAnException_WhenAnArgumentIsNull()
    {
        Assert.Throws<ArgumentNullException>("storage", () => StorageClock.UtcNow(null, static () => ProcessNow));
        Assert.Throws<ArgumentNullException>("processClock", () => StorageClock.UtcNow(_storage.Object, null));
    }
}
