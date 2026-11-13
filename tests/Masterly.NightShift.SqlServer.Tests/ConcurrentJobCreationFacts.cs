extern alias ReferencedDapper;

using System.Collections.Concurrent;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Xml.Linq;
using Microsoft.Data.SqlClient;
using ReferencedDapper::Dapper;

namespace Masterly.NightShift.SqlServer.Tests;

/// <summary>
/// Jobs created by many callers at once never deadlock each other. They did when SQL Server compiled the plan
/// of the command that creates a job while the Job table was empty: it then checked the foreign key of the
/// job's parameters by reading the whole Job table, the rows other callers had just inserted included.
/// </summary>
[SuppressMessage("Usage", "xUnit1013:Public method should be marked as test", Justification = "Job methods must be public")]
public class ConcurrentJobCreationFacts
{
    private const int Callers = 16;
    private const int JobsPerCaller = 25;
    private const int Rounds = 10;

    private static readonly XNamespace Showplan = "http://schemas.microsoft.com/sqlserver/2004/07/showplan";

    [Theory, CleanDatabase]
    // Every caller writes on its own
    [InlineData(0, 2)]
    [InlineData(0, 4)]
    // Up to eight callers on their own, the others in groups: the default
    [InlineData(8, 2)]
    // One caller on its own, the others in groups
    [InlineData(1, 2)]
    [InlineData(1, 3)]
    public void CreateExpiredJob_FromManyCallersAtOnce_NeverDeadlocks_WhenItsPlanIsCompiledForAnEmptyTable(int maxConcurrentWriters, int parameters)
    {
        SqlServerStorage storage = CreateStorage(x => x.MaxConcurrentWriters = maxConcurrentWriters);
        ConcurrentQueue<Exception> failures = new();
        Dictionary<string, string> jobParameters = Parameters(parameters);

        for (int round = 0; round < Rounds && failures.IsEmpty; round++)
        {
            // A new or purged database, or a queue that ran dry: the Job table is empty when SQL Server compiles
            // the plans of the commands that create jobs, and every caller starts at the same moment
            EmptyTheJobTableAndForgetThePlans();

            using Barrier barrier = new(Callers);
            Thread[] threads = [.. Enumerable.Range(0, Callers).Select(_ => new Thread(() =>
            {
                try
                {
                    using IStorageConnection connection = storage.GetConnection();
                    barrier.SignalAndWait(TimeSpan.FromSeconds(30));

                    for (int i = 0; i < JobsPerCaller; i++)
                    {
                        connection.CreateExpiredJob(Job.FromExpression(() => Run()), jobParameters, DateTime.UtcNow, TimeSpan.FromHours(1));
                    }
                }
                catch (Exception ex)
                {
                    failures.Enqueue(ex);
                }
            }))];

            foreach (Thread thread in threads)
            {
                thread.Start();
            }

            foreach (Thread thread in threads)
            {
                thread.Join();
            }
        }

        Assert.Empty(failures);

        using DbConnection sql = ConnectionUtils.CreateConnection();
        Assert.Equal(Callers * JobsPerCaller, sql.ExecuteScalar<int>($"select count(*) from [{Constants.DefaultSchema}].Job"));
        Assert.Equal(Callers * JobsPerCaller * parameters, sql.ExecuteScalar<int>($"select count(*) from [{Constants.DefaultSchema}].JobParameter"));
    }

    [Fact, CleanDatabase]
    public void CommandsThatCreateJobs_ReadTheJobTableOnlyBySeekingTheirOwnJob_WhenCompiledForAnEmptyTable()
    {
        SqlServerStorage storage = CreateStorage();
        using JobStorageConnection connection = (JobStorageConnection)storage.GetConnection();
        Job job = Job.FromExpression(() => Run());
        List<(string Text, string Plan)> plans = [];

        // One job with one to five parameters (each count has its own command), then groups of jobs as callers
        // that create at the same time and EnqueueMany write them, each compiled while the Job table is empty
        List<Action> creations = [.. Enumerable.Range(1, 5).Select<int, Action>(count =>
            () => connection.CreateExpiredJob(job, Parameters(count), DateTime.UtcNow, TimeSpan.FromHours(1)))];
        creations.AddRange(new[] { 1, 2, 4, 16 }.Select<int, Action>(count =>
            () => connection.CreateExpiredJobs([job, job, job], [Parameters(count), Parameters(count), Parameters(count)], DateTime.UtcNow, TimeSpan.FromHours(1))));

        foreach (Action create in creations)
        {
            EmptyTheJobTableAndForgetThePlans();
            create();

            try
            {
                using DbConnection sql = ConnectionUtils.CreateConnection();
                plans.AddRange(sql.Query<(string, string)>(
                    """
                    select st.text, cast(qp.query_plan as nvarchar(max))
                    from sys.dm_exec_cached_plans cp
                    cross apply sys.dm_exec_sql_text(cp.plan_handle) st
                    cross apply sys.dm_exec_query_plan(cp.plan_handle) qp
                    where qp.dbid = db_id() and st.text like N'%JobParameter (JobId, Name, Value)%' and st.text not like N'%dm_exec_cached_plans%'
                    """));
            }
            catch (SqlException ex) when (ex.Number is 300 or 297 or 229)
            {
                Assert.Skip("The login can't read the plan cache (VIEW SERVER STATE).");
            }
        }

        // The command of each of the nine creations was compiled, and its plan kept
        Assert.True(plans.Count >= creations.Count, $"Expected the plans of {creations.Count} commands, found {plans.Count}.");

        // What a plan does with the Job table: insert the job, and seek it to check the foreign key of its
        // parameters. A scan (under a spool, a merge join or a hash join) reads the uncommitted jobs of the
        // other callers, which is how two of them deadlock
        Assert.All(plans, plan =>
        {
            string[] operators = [.. XDocument.Parse(plan.Plan)
                .Descendants(Showplan + "RelOp")
                .Where(relOp => relOp.Elements().Any(op => op.Elements(Showplan + "Object").Any(static x => (string)x.Attribute("Table") == "[Job]")))
                .Select(static relOp => (string)relOp.Attribute("PhysicalOp"))
                .Distinct()
                .Order()];

            Assert.True(
                operators.All(static x => x is "Clustered Index Insert" or "Clustered Index Seek"),
                $"The plan reads the Job table with {string.Join(", ", operators)}: {plan.Text}");
        });
    }

    public static void Run()
    {
    }

    private static Dictionary<string, string> Parameters(int count)
    {
        return Enumerable.Range(0, count).ToDictionary(static x => $"Name{x}", static x => $"Value{x}");
    }

    /// <summary>
    /// Empties the Job table and makes SQL Server forget the plans it compiled for this database, so the next
    /// command that creates a job is compiled for an empty table.
    /// </summary>
    private static void EmptyTheJobTableAndForgetThePlans()
    {
        using DbConnection sql = ConnectionUtils.CreateConnection();
        sql.Execute($"delete from [{Constants.DefaultSchema}].Job; alter database scoped configuration clear procedure_cache;");
    }

    private static SqlServerStorage CreateStorage(Action<SqlServerStorageOptions> configure = null)
    {
        SqlServerStorageOptions options = new() { PrepareSchemaIfNecessary = false };
        configure?.Invoke(options);
        return new SqlServerStorage(ConnectionUtils.GetConnectionString(), options);
    }
}
