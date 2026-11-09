// This file is part of Hangfire. Copyright © 2017 Hangfire OÜ.
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
using System.Linq.Expressions;
using System.Reflection;

namespace Masterly.NightShift.SqlServer;

internal sealed class SqlCommandSet : IDisposable
{
    private static readonly ConcurrentDictionary<Assembly, Type> SqlCommandSetType = new();
    private static readonly ConcurrentDictionary<Type, Action<object, DbConnection>> SetConnection = new();
    private static readonly ConcurrentDictionary<Type, Action<object, DbTransaction>> SetTransaction = new();
    private static readonly ConcurrentDictionary<Type, Func<object, DbCommand>> GetBatchCommand = new();
    private static readonly ConcurrentDictionary<Type, PropertyInfo> BatchCommandProperty = new();
    private static readonly ConcurrentDictionary<Type, Action<object, DbCommand>> AppendMethod = new();
    private static readonly ConcurrentDictionary<Type, Func<object, int>> ExecuteNonQueryMethod = new();
    private static readonly ConcurrentDictionary<Type, Action<object>> DisposeMethod = new();
    private static readonly ConcurrentDictionary<Type, Func<object>> SqlCommandSetConstructor = new();

    private readonly object _instance;

    private readonly Action<object, DbConnection> _setConnection;
    private readonly Action<object, DbTransaction> _setTransaction;
    private readonly Func<object, DbCommand> _getBatchCommand;
    private readonly Action<object, DbCommand> _appendMethod;
    private readonly Func<object, int> _executeNonQueryMethod;
    private readonly Action<object> _disposeMethod;
    private readonly Func<object> _constructor;

    public SqlCommandSet(DbConnection connection)
    {
        Type sqlCommandSetType;
        try
        {
            sqlCommandSetType = SqlCommandSetType.GetOrAdd(connection.GetType().GetTypeInfo().Assembly, static sqlClientAssembly =>
            {
                var type = sqlClientAssembly.GetTypes().FirstOrDefault(static x => x.Name == "SqlCommandSet") ?? throw new TypeLoadException($"Could not load type 'SqlCommandSet' from assembly '{sqlClientAssembly}'.");
                return type;
            });

            _setConnection = SetConnection.GetOrAdd(sqlCommandSetType, static type =>
            {
                var p = Expression.Parameter(typeof(object));
                var converted = Expression.Convert(p, type);
                var connectionParameter = Expression.Parameter(typeof(DbConnection));
                var connectionProperty = type.GetProperty("Connection", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) ?? throw new MissingMemberException($"Property '{type.FullName}.Connection' not found.");
                return Expression.Lambda<Action<object, DbConnection>>(Expression.Assign(Expression.Property(converted, connectionProperty), Expression.Convert(connectionParameter, connectionProperty.PropertyType)), p, connectionParameter).Compile();
            });
            _setTransaction = SetTransaction.GetOrAdd(sqlCommandSetType, static type =>
            {
                var p = Expression.Parameter(typeof(object));
                var converted = Expression.Convert(p, type);
                var transactionParameter = Expression.Parameter(typeof(DbTransaction));
                var transactionProperty = type.GetProperty("Transaction", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) ?? throw new MissingMemberException($"Property '{type.FullName}.Transaction' not found.");
                return Expression.Lambda<Action<object, DbTransaction>>(Expression.Assign(Expression.Property(converted, transactionProperty), Expression.Convert(transactionParameter, transactionProperty.PropertyType)), p, transactionParameter).Compile();
            });
            var batchCommandProperty = BatchCommandProperty.GetOrAdd(sqlCommandSetType, static type =>
            {
                return type.GetProperty("BatchCommand", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) ?? throw new MissingMemberException($"Property '{type.FullName}.BatchCommand' not found.");
            });
            _getBatchCommand = GetBatchCommand.GetOrAdd(sqlCommandSetType, type =>
            {
                var p = Expression.Parameter(typeof(object));
                var converted = Expression.Convert(p, type);
                return Expression.Lambda<Func<object, DbCommand>>(Expression.Property(converted, batchCommandProperty), p).Compile();
            });
            _appendMethod = AppendMethod.GetOrAdd(sqlCommandSetType, type =>
            {
                var p = Expression.Parameter(typeof(object));
                var converted = Expression.Convert(p, type);
                var batchCommandParameter = Expression.Parameter(typeof(DbCommand));
                return Expression.Lambda<Action<object, DbCommand>>(Expression.Call(converted, "Append", null, Expression.Convert(batchCommandParameter, batchCommandProperty.PropertyType)), p, batchCommandParameter).Compile();
            });
            _executeNonQueryMethod = ExecuteNonQueryMethod.GetOrAdd(sqlCommandSetType, static type =>
            {
                var p = Expression.Parameter(typeof(object));
                var converted = Expression.Convert(p, type);
                return Expression.Lambda<Func<object, int>>(Expression.Call(converted, "ExecuteNonQuery", null), p).Compile();
            });
            _disposeMethod = DisposeMethod.GetOrAdd(sqlCommandSetType, static type =>
            {
                var p = Expression.Parameter(typeof(object));
                var converted = Expression.Convert(p, type);
                return Expression.Lambda<Action<object>>(Expression.Call(converted, "Dispose", null), p).Compile();
            });
            _constructor = SqlCommandSetConstructor.GetOrAdd(sqlCommandSetType, static type =>
            {
                var ctor = Expression.New(type);
                return Expression.Lambda<Func<object>>(ctor).Compile();
            });
        }
        catch (Exception exception) when (exception.IsCatchableExceptionType())
        {
            throw new NotSupportedException($"SqlCommandSet for {connection.GetType().FullName} is not supported, use regular commands instead", exception);
        }

        _instance = _constructor();
    }

    public DbConnection Connection
    {
        set => _setConnection(_instance, value);
    }

    public DbTransaction Transaction
    {
        set => _setTransaction(_instance, value);
    }

    public DbCommand BatchCommand => _getBatchCommand(_instance);
    public int CommandCount { get; private set; }

    public void Append(DbCommand command)
    {
        _appendMethod(_instance, command);
        CommandCount++;
    }

    public int ExecuteNonQuery()
    {
        if (CommandCount == 0)
        {
            return 0;
        }

        return _executeNonQueryMethod(_instance);
    }

    public void Dispose()
    {
        _disposeMethod(_instance);
    }
}