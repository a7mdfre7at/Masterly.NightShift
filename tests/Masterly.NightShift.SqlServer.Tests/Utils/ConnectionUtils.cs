using System.Data.Common;
using Microsoft.Data.SqlClient;

namespace Masterly.NightShift.SqlServer.Tests;

public static class ConnectionUtils
{
    private const string DatabaseVariable = "NightShift_SqlServer_DatabaseName";
    private const string ConnectionStringTemplateVariable
        = "NightShift_SqlServer_ConnectionStringTemplate";

    private const string MasterDatabaseName = "master";
    // Each target framework gets its own database, because they run in parallel
    private const string DefaultDatabaseName =
#if NET10_0
            "Masterly.NightShift.SqlServer.Tests.net10_0"
#elif NET8_0
            "Masterly.NightShift.SqlServer.Tests.net8_0"
#else
            "Masterly.NightShift.SqlServer.Tests"
#endif
        ;
    private const string DefaultConnectionStringTemplate
        = @"Server=.\;Database={0};Trusted_Connection=True;TrustServerCertificate=True;";

    public static string GetDatabaseName()
    {
        return Environment.GetEnvironmentVariable(DatabaseVariable) ?? DefaultDatabaseName;
    }

    public static string GetMasterConnectionString()
    {
        return string.Format(GetConnectionStringTemplate(), MasterDatabaseName);
    }

    public static string GetConnectionString()
    {
        return GetConnectionString(GetDatabaseName());
    }

    public static string GetConnectionString(string databaseName)
    {
        return string.Format(GetConnectionStringTemplate(), databaseName);
    }

    private static string GetConnectionStringTemplate()
    {
        return Environment.GetEnvironmentVariable(ConnectionStringTemplateVariable)
               ?? DefaultConnectionStringTemplate;
    }

    public static DbConnection CreateConnection()
    {
        SqlConnection connection = new(GetConnectionString());

        connection.Open();

        return connection;
    }
}
