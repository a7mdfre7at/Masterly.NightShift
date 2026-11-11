using MongoDB.Bson;
using MongoDB.Driver;

namespace Masterly.NightShift.MongoDB.Tests;

/// <summary>
/// The MongoDB replica set the tests use, from the <c>NightShift_Mongo_ConnectionString</c> environment variable
/// (for example <c>mongodb://localhost:27017/?replicaSet=rs0</c>). Tests are skipped when it isn't set.
/// </summary>
internal static class MongoTestServer
{
    private static readonly Lazy<IMongoDatabase?> Shared = new(static () =>
    {
        string? connectionString = Environment.GetEnvironmentVariable("NightShift_Mongo_ConnectionString");
        return string.IsNullOrWhiteSpace(connectionString) ? null : new MongoClient(connectionString).GetDatabase("nightshift-tests");
    });

    public static IMongoDatabase Database
    {
        get
        {
            Assert.SkipWhen(Shared.Value == null, "Set NightShift_Mongo_ConnectionString to run the MongoDB tests.");
            return Shared.Value;
        }
    }

    /// <summary>A storage with a collection prefix of its own, so tests never see each other's documents.</summary>
    public static MongoStorage CreateStorage(MongoStorageOptions? options = null)
    {
        options ??= new MongoStorageOptions();
        options.CollectionPrefix = $"t{Guid.NewGuid():N}.";
        return new MongoStorage(Database, options);
    }

    /// <summary>Drops the storage's collections.</summary>
    public static void Drop(MongoStorage storage)
    {
        foreach (string name in storage.Database.ListCollectionNames(new ListCollectionNamesOptions
        {
            Filter = new BsonDocument("name", new BsonRegularExpression("^" + System.Text.RegularExpressions.Regex.Escape(storage.Options.CollectionPrefix))),
        }).ToList())
        {
            storage.Database.DropCollection(name);
        }
    }
}
