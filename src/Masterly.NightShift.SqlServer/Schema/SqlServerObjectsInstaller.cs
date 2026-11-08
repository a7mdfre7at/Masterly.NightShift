// This file is part of Hangfire. Copyright © 2013-2014 Hangfire OÜ.
// 
// Hangfire is free software: you can redistribute it and/or modify
// it under the terms of the GNU Lesser General Public License as 
// published by the Free Software Foundation, either version 3 
// of the License, or any later version.
// 
// Hangfire is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU Lesser General Public License for more details.
// 
// You should have received a copy of the GNU Lesser General Public 
// License along with Hangfire. If not, see <http://www.gnu.org/licenses/>.

using System;
using System.Data.Common;
using System.IO;
using System.Reflection;
using Dapper;
using Masterly.NightShift.Logging;

namespace Masterly.NightShift.SqlServer
{
    /// <summary>
    /// Creates or upgrades the SQL Server schema with the embedded install script, for deployments that install
    /// it themselves instead of letting the storage do it at startup.
    /// </summary>
    public static class SqlServerObjectsInstaller
    {
        /// <summary>Not used.</summary>
        [Obsolete("This field isn't used. Use LatestSchemaVersion instead. Kept for code ported from Hangfire.")]
        public static readonly int RequiredSchemaVersion = 5;

        /// <summary>The schema version the install script upgrades to.</summary>
        public static readonly int LatestSchemaVersion = 9;

        /// <summary>
        /// Runs the install script in the default schema (<c>HangFire</c>), without the heavy migrations.
        /// </summary>
        /// <param name="connection">An open connection to the database.</param>
        public static void Install(DbConnection connection)
        {
            Install(connection, null);
        }

        /// <summary>
        /// Runs the install script in the given schema, without the heavy migrations.
        /// </summary>
        /// <param name="connection">An open connection to the database.</param>
        /// <param name="schema">The schema name, or <c>null</c> for <c>HangFire</c>.</param>
        public static void Install(DbConnection connection, string schema)
        {
            Install(connection, schema, false);
        }

        /// <summary>
        /// Runs the install script in the given schema.
        /// </summary>
        /// <param name="connection">An open connection to the database.</param>
        /// <param name="schema">The schema name, or <c>null</c> for <c>HangFire</c>.</param>
        /// <param name="enableHeavyMigrations">Whether the upgrades that rebuild large tables (5 to 6, 7 to 8) may run.</param>
        public static void Install(DbConnection connection, string schema, bool enableHeavyMigrations)
        {
            if (connection == null) throw new ArgumentNullException(nameof(connection));

            var script = GetInstallScript(schema, enableHeavyMigrations);

            connection.Execute(script, commandTimeout: 0);
        }

        /// <summary>
        /// Returns the install script for the given schema, to run it yourself.
        /// </summary>
        /// <param name="schema">The schema name, or <c>null</c> or white space for <c>HangFire</c>.</param>
        /// <param name="enableHeavyMigrations">Whether the upgrades that rebuild large tables (5 to 6, 7 to 8) may run.</param>
        /// <exception cref="ArgumentException">The schema name is longer than 128 characters or contains <c>]</c>,
        /// <c>'</c> or a control character.</exception>
        public static string GetInstallScript(string schema, bool enableHeavyMigrations)
        {
            schema = !string.IsNullOrWhiteSpace(schema) ? schema : Constants.DefaultSchema;

            // The name is pasted into the script both as [name] and as 'name', and no single escaping fits
            // both, so names that would need escaping are refused, as the SchemaName option does
            SqlServerStorageOptions.ValidateSchemaName(schema, nameof(schema));

            var script = GetStringResource(
                typeof(SqlServerObjectsInstaller).GetTypeInfo().Assembly,
                "Masterly.NightShift.SqlServer.Install.sql");

            script = script.Replace("$(HangFireSchema)", schema);

            if (!enableHeavyMigrations)
            {
                script = script.Replace("--SET @DISABLE_HEAVY_MIGRATIONS = 1;", "SET @DISABLE_HEAVY_MIGRATIONS = 1;");
            }

            return script;
        }

        private static string GetStringResource(Assembly assembly, string resourceName)
        {
            using (var stream = assembly.GetManifestResourceStream(resourceName))
            {
                if (stream == null) 
                {
                    throw new InvalidOperationException(
                        $"Requested resource `{resourceName}` was not found in the assembly `{assembly}`.");
                }

                using (var reader = new StreamReader(stream))
                {
                    return reader.ReadToEnd();
                }
            }
        }
    }
}
