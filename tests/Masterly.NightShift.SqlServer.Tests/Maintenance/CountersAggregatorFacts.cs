extern alias ReferencedDapper;
using System.Data.Common;
using ReferencedDapper::Dapper;

namespace Masterly.NightShift.SqlServer.Tests;

public class CountersAggregatorFacts
{
    [Fact, CleanDatabase]
    public void CountersAggregatorExecutesProperly()
    {
        string createSql = $@"
insert into [{Constants.DefaultSchema}].Counter ([Key], [Value], ExpireAt) 
values ('key', 1, @expireAt)";

        using DbConnection connection = CreateConnection();
        // Arrange
        connection.Execute(createSql, new { expireAt = DateTime.UtcNow.AddHours(1) });

        CountersAggregator aggregator = CreateAggregator();
        CancellationTokenSource cts = new();
        cts.Cancel();

        // Act
        aggregator.Execute(cts.Token);

        // Assert
        Assert.Equal(1, connection.Query<int>($"select count(*) from [{Constants.DefaultSchema}].AggregatedCounter").Single());
    }

    private static DbConnection CreateConnection()
    {
        return ConnectionUtils.CreateConnection();
    }

    private static CountersAggregator CreateAggregator()
    {
        SqlServerStorage storage = new(ConnectionUtils.CreateConnection);
        return new CountersAggregator(storage, TimeSpan.Zero);
    }
}
