using System;
using Microsoft.Data.SqlClient;

namespace NightShiftSamples
{
    /// <summary>
    /// Creates the sample's database when it doesn't exist yet, so the samples run on a fresh machine: the storage
    /// creates its tables, but not the database. Samples only; a real app gets its database from its deployment.
    /// </summary>
    internal static class SampleDatabase
    {
        public static void EnsureExists(string connectionString)
        {
            var builder = new SqlConnectionStringBuilder(connectionString);
            var database = builder.InitialCatalog;

            if (String.IsNullOrEmpty(database)) return;

            builder.InitialCatalog = "master";

            try
            {
                using var connection = new SqlConnection(builder.ConnectionString);
                connection.Open();

                using var command = connection.CreateCommand();
                command.CommandText =
                    "IF DB_ID(@name) IS NULL BEGIN DECLARE @sql nvarchar(max) = N'CREATE DATABASE ' + QUOTENAME(@name); EXEC(@sql); END";
                command.Parameters.AddWithValue("@name", database);
                command.ExecuteNonQuery();
            }
            catch (SqlException ex)
            {
                // The storage reports the missing database again when it starts
                Console.Error.WriteLine($"Could not create the database '{database}': {ex.Message}");
            }
        }
    }
}
