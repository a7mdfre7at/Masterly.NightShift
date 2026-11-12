using StackExchange.Redis;

namespace Masterly.NightShift.Redis.Tests;

/// <summary>
/// The Redis server the tests use, from the <c>NightShift_Redis_Configuration</c> environment variable (for example
/// <c>localhost:6379</c>). Tests are skipped when it isn't set.
/// </summary>
internal static class RedisTestServer
{
    private static readonly Lazy<ConnectionMultiplexer?> Shared = new(static () =>
    {
        string? configuration = Environment.GetEnvironmentVariable("NightShift_Redis_Configuration");
        return string.IsNullOrWhiteSpace(configuration) ? null : ConnectionMultiplexer.Connect(configuration);
    });

    public static ConnectionMultiplexer Connection
    {
        get
        {
            Assert.SkipWhen(Shared.Value == null, "Set NightShift_Redis_Configuration to run the Redis tests.");
            return Shared.Value;
        }
    }

    /// <summary>A storage with a prefix of its own, so tests never see each other's keys.</summary>
    public static RedisStorage CreateStorage(RedisStorageOptions? options = null)
    {
        options ??= new RedisStorageOptions();
        options.Prefix = $"{{nightshift-tests-{Guid.NewGuid():N}}}:";
        return new RedisStorage(Connection, options);
    }

    /// <summary>Deletes every key of the storage's prefix.</summary>
    public static void Delete(RedisStorage storage)
    {
        IDatabase database = storage.Connection.GetDatabase(storage.Options.Database);
        foreach (IServer server in storage.Connection.GetServers().Where(static x => !x.IsReplica))
        {
            RedisKey[] keys = [.. server.Keys(database.Database, storage.Options.Prefix + "*")];
            if (keys.Length > 0)
            {
                database.KeyDelete(keys);
            }
        }

        storage.Dispose();
    }
}
