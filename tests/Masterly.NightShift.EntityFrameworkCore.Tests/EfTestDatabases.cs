using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Masterly.NightShift.EntityFrameworkCore.Tests;

/// <summary>A SQLite database in a file of its own, deleted when disposed.</summary>
internal sealed class SqliteDatabase : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"nightshift-{Guid.NewGuid():N}.db");

    public string ConnectionString => $"Data Source={Path}";

    public EntityFrameworkCoreStorage CreateStorage(EntityFrameworkCoreStorageOptions? options = null)
    {
        options ??= new EntityFrameworkCoreStorageOptions();
        options.Schema = null;
        return new EntityFrameworkCoreStorage(o => o.UseSqlite(ConnectionString), options);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(Path))
        {
            File.Delete(Path);
        }
    }
}

/// <summary>
/// The SQL Server database the tests use, from <c>NightShift_SqlServer_ConnectionStringTemplate</c> (a connection
/// string with a <c>{0}</c> database placeholder), as the SQL Server storage's tests do. Tests are skipped when
/// it isn't set.
/// </summary>
internal static class SqlServerDatabase
{
    /// <summary>The connection string of a test database, one per target framework.</summary>
    public static string ConnectionString(string name)
    {
        string? template = Environment.GetEnvironmentVariable("NightShift_SqlServer_ConnectionStringTemplate");
        Assert.SkipWhen(string.IsNullOrWhiteSpace(template), "Set NightShift_SqlServer_ConnectionStringTemplate to run the SQL Server tests.");

        // The schema version is part of the name: the storage refuses tables of another version, so a test database
        // left by an earlier build would fail every test instead of starting afresh
        string database = $"Masterly.NightShift.EntityFrameworkCore.Tests.{name}.v{SchemaVersionRow.Current}.{(Environment.Version.Major >= 10 ? "net10_0" : "net8_0")}";
        return string.Format(System.Globalization.CultureInfo.InvariantCulture, template, database);
    }

    public static EntityFrameworkCoreStorage CreateStorage(EntityFrameworkCoreStorageOptions? options = null)
    {
        string connectionString = ConnectionString("Storage");
        EntityFrameworkCoreStorage storage = new(o => o.UseSqlServer(connectionString), options);

        // Starts empty: every row of every table, locks included
        storage.Purge(new StoragePurgeOptions { KeepAuditLog = false, KeepDrainMode = false, KeepRecurringJobs = false, KeepServers = false });
        using DbContext context = storage.CreateContext();
        context.Set<LockRow>().ExecuteDelete();
        return storage;
    }
}
