extern alias ReferencedDapper;

using System.Collections.Concurrent;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using ReferencedDapper::Dapper;

namespace Masterly.NightShift.SqlServer.Tests;

/// <summary>
/// Fan-in continuations over many parents that finish at once: each parent writes what it finished as for each
/// child in its own state change, and nothing may fail, wait for a lock it can't get, or be left behind.
/// </summary>
[SuppressMessage("Usage", "xUnit1013:Public method should be marked as test", Justification = "Job methods must be public")]
public class FanInContentionFacts
{
    public static void Record(string name) => _ = name;

    [Theory, CleanDatabase]
    [InlineData(1500, 24, false)]
    [InlineData(1500, 24, true)]
    public void Parents_SharingSeveralChildren_FinishAtOnce_WithoutFailingAStateChange_AndEveryChildIsDecided_AndNothingIsLeftBehind(int count, int threads, bool ignoreDupKey)
    {
        SqlServerStorage storage = new(ConnectionUtils.GetConnectionString(), new SqlServerStorageOptions { PrepareSchemaIfNecessary = false, UseIgnoreDupKeyOption = ignoreDupKey });
        BackgroundJobClient client = new(storage);

        List<string> parents = [];
        for (int i = 0; i < count; i++)
        {
            parents.Add(client.Create(() => Record("p"), new EnqueuedState())!);
        }

        string any = client.ContinueJobWithAny(parents, () => Record("any"))!;
        string all = client.ContinueJobWithAll(parents, () => Record("all"))!;
        string completed = client.ContinueJobWithJoin(parents, FanInMode.AllCompleted, () => Record("completed"))!;
        string anyFailed = client.ContinueJobWithJoin(parents, FanInMode.AnyFailed, () => Record("anyFailed"))!;

        ConcurrentQueue<string> errors = new();
        Parallel.ForEach(parents, new ParallelOptions { MaxDegreeOfParallelism = threads }, parent =>
        {
            try
            {
                if (!client.ChangeState(parent, new SucceededState(null, 0, 0), null))
                {
                    errors.Enqueue($"{parent}: not changed");
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                errors.Enqueue($"{parent}: {ex.GetBaseException().Message}");
            }
        });

        Assert.Empty(errors);
        Assert.Equal(EnqueuedState.StateName, StateName(storage, any));
        Assert.Equal(EnqueuedState.StateName, StateName(storage, all));
        Assert.Equal(EnqueuedState.StateName, StateName(storage, completed));
        Assert.Equal(DeletedState.StateName, StateName(storage, anyFailed));

        using DbConnection sql = ConnectionUtils.CreateConnection();
        int left = sql.ExecuteScalar<int>($"select count(*) from [{Constants.DefaultSchema}].Hash where [Key] like 'fan-in:%'");
        Assert.Equal(0, left);
    }

    private static string? StateName(SqlServerStorage storage, string jobId)
    {
        using IStorageConnection connection = storage.GetConnection();
        return connection.GetStateData(jobId)?.Name;
    }
}
