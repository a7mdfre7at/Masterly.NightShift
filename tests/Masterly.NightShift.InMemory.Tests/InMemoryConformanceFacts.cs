using Masterly.NightShift.Storage.Conformance;

namespace Masterly.NightShift.InMemory.Tests;

public class InMemoryConformanceFacts : StorageConformanceFacts
{
    protected override JobStorage CreateStorage() => new InMemoryStorage();

    protected override JobStorage CreateStorageWithoutKeepAlive(TimeSpan invisibilityTimeout)
    {
        return new InMemoryStorage(new InMemoryStorageOptions
        {
            InvisibilityTimeout = invisibilityTimeout,
            QueuePollInterval = TimeSpan.FromMilliseconds(100),
            KeepFetchedJobsAlive = false,
        });
    }

    protected override JobStorage CreateStorageWithLockLifetime(TimeSpan lifetime)
    {
        return new InMemoryStorage(new InMemoryStorageOptions { DistributedLockLifetime = lifetime });
    }
}
