using Masterly.NightShift.Storage.Conformance;

namespace Masterly.NightShift.InMemory.Tests;

public class InMemoryConformanceFacts : StorageConformanceFacts
{
    protected override JobStorage CreateStorage() => new InMemoryStorage();
}
