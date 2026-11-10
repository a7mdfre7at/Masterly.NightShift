namespace Masterly.NightShift.Core.Tests.Storage;

public class StoragePurgeOptionsFacts
{
    [Fact]
    public void Ctor_KeepsEverythingButTheJobData_ByDefault()
    {
        StoragePurgeOptions options = new();

        Assert.True(options.KeepRecurringJobs);
        Assert.True(options.KeepServers);
        Assert.True(options.KeepAuditLog);
        Assert.True(options.KeepDrainMode);
    }
}
