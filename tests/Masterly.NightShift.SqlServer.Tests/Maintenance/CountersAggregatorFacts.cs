extern alias ReferencedDapper;

using System;
using System.Data.Common;
using System.Linq;
using System.Threading;
using ReferencedDapper::Dapper;
using Xunit;

namespace Masterly.NightShift.SqlServer.Tests
{
    public class CountersAggregatorFacts
    {
        [Fact, CleanDatabase]
        public void CountersAggregatorExecutesProperly()
        {
            var createSql = $@"
insert into [{Constants.DefaultSchema}].Counter ([Key], [Value], ExpireAt) 
values ('key', 1, @expireAt)";

            using (var connection = CreateConnection())
            {
                // Arrange
                connection.Execute(createSql, new { expireAt = DateTime.UtcNow.AddHours(1) });

                var aggregator = CreateAggregator();
                var cts = new CancellationTokenSource();
                cts.Cancel();

                // Act
                aggregator.Execute(cts.Token);

                // Assert
                Assert.Equal(1, connection.Query<int>($"select count(*) from [{Constants.DefaultSchema}].AggregatedCounter").Single());
            }
        }

        private static DbConnection CreateConnection()
        {
            return ConnectionUtils.CreateConnection();
        }

        private static CountersAggregator CreateAggregator()
        {
            var storage = new SqlServerStorage(() => ConnectionUtils.CreateConnection());
            return new CountersAggregator(storage, TimeSpan.Zero);
        }
    }
}
