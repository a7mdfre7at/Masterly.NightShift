using System;
using System.Data.Common;
using System.Data.SqlClient;

namespace Hangfire.SqlServer.Tests
{
    public static class ConnectionUtils
    {
        private const string DatabaseVariable = "Hangfire_SqlServer_DatabaseName";
        private const string ConnectionStringTemplateVariable 
            = "Hangfire_SqlServer_ConnectionStringTemplate";

        private const string MasterDatabaseName = "master";
        // Each target framework gets its own database, because they run in parallel
        private const string DefaultDatabaseName =
#if NET10_0
                "Hangfire.SqlServer.Tests.net10_0"
#elif NET8_0
                "Hangfire.SqlServer.Tests.net8_0"
#else
                "Hangfire.SqlServer.Tests"
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
            return String.Format(GetConnectionStringTemplate(), MasterDatabaseName);
        }

        public static string GetConnectionString()
        {
            return String.Format(GetConnectionStringTemplate(), GetDatabaseName());
        }

        private static string GetConnectionStringTemplate()
        {
            return Environment.GetEnvironmentVariable(ConnectionStringTemplateVariable)
                   ?? DefaultConnectionStringTemplate;
        }

        public static DbConnection CreateConnection(bool microsoftDataSqlClient)
        {
            var connection =
                microsoftDataSqlClient ? (DbConnection)new Microsoft.Data.SqlClient.SqlConnection(GetConnectionString()) :
                new SqlConnection(GetConnectionString());

            connection.Open();

            return connection;
        }
    }
}
