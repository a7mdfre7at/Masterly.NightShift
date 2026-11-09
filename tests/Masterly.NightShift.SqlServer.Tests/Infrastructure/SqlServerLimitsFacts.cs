namespace Masterly.NightShift.SqlServer.Tests;

public class SqlServerLimitsFacts
{
    [Fact]
    public void GetLockResource_ReturnsTheResource_WhenItFits()
    {
        var resource = new string('y', SqlServerLimits.LockResourceLength);

        Assert.Equal(resource, SqlServerLimits.GetLockResource(resource));
    }

    [Fact]
    public void GetLockResource_ShortensALongResource_TheSameWayAsEarlierVersions()
    {
        // Locks taken by servers of an earlier version must stay the same lock during a rolling upgrade,
        // so the hash is pinned: the first 8 bytes of SHA-256 over the UTF-8 resource
        var resource = "lock:" + new string('y', 300);

        var shortened = SqlServerLimits.GetLockResource(resource);

        Assert.Equal(SqlServerLimits.LockResourceLength, shortened.Length);
        Assert.Equal(resource[..238] + "~53da09d6f7bd60a2", shortened);
    }
}
