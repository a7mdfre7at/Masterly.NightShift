using System.Globalization;
using System.Reflection;
using Microsoft.Data.SqlClient;

namespace NightShiftBenchmarks;

/// <summary>A storage connection that does nothing, so in-memory benchmarks don't measure a database or a mock.</summary>
public class NoOpProxy : DispatchProxy
{
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        Type? returnType = targetMethod?.ReturnType;
        return returnType is { IsValueType: true } && returnType != typeof(void)
            ? Activator.CreateInstance(returnType)
            : null;
    }
}

internal static class Database
{
    private const string DefaultTemplate =
        @"Server=(localdb)\MSSQLLocalDB;Database={0};Integrated Security=True;TrustServerCertificate=True;";

    private const string Clear =
        "DELETE FROM [HangFire].[JobQueue]; DELETE FROM [HangFire].[Job]; DELETE FROM [HangFire].[Counter]; " +
        "DELETE FROM [HangFire].[AggregatedCounter]; DELETE FROM [HangFire].[Set]; DELETE FROM [HangFire].[Hash]; " +
        "DELETE FROM [HangFire].[List]; DELETE FROM [HangFire].[Server];";

    public static string ConnectionString(string database)
    {
        string template = Environment.GetEnvironmentVariable("NightShift_SqlServer_ConnectionStringTemplate")
            ?? DefaultTemplate;
        return string.Format(CultureInfo.InvariantCulture, template, database);
    }

    public static void EnsureCreated(string database)
    {
        SqlConnectionStringBuilder builder = new(ConnectionString(database)) { InitialCatalog = "master" };
        using SqlConnection connection = new(builder.ConnectionString);
        using SqlCommand command = new(
            "IF DB_ID(@name) IS NULL BEGIN DECLARE @sql nvarchar(max) = N'CREATE DATABASE ' + QUOTENAME(@name); EXEC(@sql); END",
            connection);
        command.Parameters.AddWithValue("@name", database);
        connection.Open();
        command.ExecuteNonQuery();
    }

    public static void Reset(string database)
    {
        using SqlConnection connection = new(ConnectionString(database));
        using SqlCommand command = new(Clear, connection);
        connection.Open();
        command.ExecuteNonQuery();
    }
}
