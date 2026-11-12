// This file is part of Hangfire. Copyright © 2024 Hangfire OÜ.
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

using System.Collections.Concurrent;

namespace Masterly.NightShift.SqlServer;

internal static class DbCommandExtensions
{
    private static readonly ConcurrentDictionary<KeyValuePair<string, KeyValuePair<string, int>>, string> ExpandedQueries =
        new();

    extension([NotNull] DbConnection connection)
    {
        public DbCommand Create(
            [NotNull] string text,
            CommandType type = CommandType.Text,
            int? timeout = null)
        {
            ArgumentNullException.ThrowIfNull(connection);
            ArgumentNullException.ThrowIfNull(text);

            DbCommand command = connection.CreateCommand();
            command.CommandType = type;
            command.CommandText = text;

            if (timeout.HasValue)
            {
                command.CommandTimeout = timeout.Value;
            }

            return command;
        }
    }

    extension([NotNull] DbCommand command)
    {
        public DbCommand AddParameter(
            [NotNull] string parameterName,
            [CanBeNull] object? value,
            DbType dbType,
            [CanBeNull] int? size = null)
        {
            ArgumentNullException.ThrowIfNull(command);
            ArgumentNullException.ThrowIfNull(parameterName);

            DbParameter parameter = AddParameterInternal(command, parameterName, dbType, size);
            parameter.Value = value ?? DBNull.Value;

            return command;
        }

        public DbCommand AddReturnParameter(
            string parameterName,
            out DbParameter parameter,
            DbType dbType,
            int? size = null)
        {
            ArgumentNullException.ThrowIfNull(command);
            ArgumentNullException.ThrowIfNull(parameterName);

            parameter = AddParameterInternal(command, parameterName, dbType, size);
            parameter.Direction = ParameterDirection.ReturnValue;
            parameter.Value = DBNull.Value;

            return command;
        }

        public DbCommand AddExpandedParameter<T>(
            [NotNull] string parameterName,
            [NotNull] T[] parameterValues,
            DbType parameterType,
            int? parameterSize = null)
        {
            ArgumentNullException.ThrowIfNull(command);
            ArgumentNullException.ThrowIfNull(parameterName);
            ArgumentNullException.ThrowIfNull(parameterValues);

            command.CommandText = ExpandedQueries.GetOrAdd(
                new KeyValuePair<string, KeyValuePair<string, int>>(command.CommandText, new KeyValuePair<string, int>(parameterName, parameterValues.Length)),
                static pair =>
                {
                    return pair.Key.Replace(
                        pair.Value.Key,
                        "(" + string.Join(",", Enumerable.Range(0, pair.Value.Value).Select(i => pair.Value.Key + i.ToString(CultureInfo.InvariantCulture))) + ")");
                });

            for (int i = 0; i < parameterValues.Length; i++)
            {
                DbParameter parameter = AddParameterInternal(command, parameterName + i.ToString(CultureInfo.InvariantCulture), parameterType, parameterSize);
                parameter.Value = (object?)parameterValues[i] ?? DBNull.Value;
            }

            return command;
        }
    }

    extension([NotNull] DbParameter parameter)
    {
        public T? GetParameterValue<T>()
        {
            ArgumentNullException.ThrowIfNull(parameter);

            switch (parameter.Value)
            {
                case null or DBNull: return default;
                case T typed: return typed;
                default:
                    Type type = typeof(T);
                    type = Nullable.GetUnderlyingType(type) ?? type;
                    return (T)Convert.ChangeType(parameter.Value, type, CultureInfo.InvariantCulture);
            }
        }
    }

    private static DbParameter AddParameterInternal(
        DbCommand command,
        string parameterName,
        DbType dbType,
        int? size)
    {
        DbParameter parameter = command.CreateParameter();
        parameter.ParameterName = parameterName;
        parameter.DbType = dbType;

        if (size.HasValue)
        {
            parameter.Size = size.Value;
        }

        command.Parameters.Add(parameter);
        return parameter;
    }
}