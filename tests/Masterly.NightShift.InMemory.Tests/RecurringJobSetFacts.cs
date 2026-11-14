using System.Diagnostics.CodeAnalysis;

namespace Masterly.NightShift.InMemory.Tests;

/// <summary>
/// Sets of several applications in one storage: each retires or removes only the jobs it declared.
/// </summary>
[SuppressMessage("Usage", "xUnit1013:Public method should be marked as test", Justification = "Job methods must be public")]
public class RecurringJobSetFacts
{
    private const string Hourly = "0 * * * *";

    [Fact]
    public void RetireUndeclared_OfTwoNamedSets_NeverPausesTheJobsOfTheOther()
    {
        InMemoryStorage storage = new();
        RecurringJobManager manager = new(storage);

        for (int start = 0; start < 3; start++)
        {
            Assert.Empty(new RecurringJobSet().Named("web").Add("invoices", () => Run(), Hourly).RetireUndeclared().Apply(manager, storage).Retired);
            Assert.Empty(new RecurringJobSet().Named("worker").Add("reports", () => Run(), Hourly).RetireUndeclared().Apply(manager, storage).Retired);
        }

        Assert.False(Find(storage, "invoices").Paused);
        Assert.False(Find(storage, "reports").Paused);
        Assert.Equal("web", Find(storage, "invoices").DeclaredBy);
        Assert.Equal("worker", Find(storage, "reports").DeclaredBy);
    }

    [Fact]
    public void RetireUndeclared_PausesAJobItsOwnSetStoppedDeclaring_AndLeavesTheOtherSetAlone()
    {
        InMemoryStorage storage = new();
        RecurringJobManager manager = new(storage);
        new RecurringJobSet().Named("web").Add("invoices", () => Run(), Hourly).Add("reminders", () => Run(), Hourly).Apply(manager, storage);
        new RecurringJobSet().Named("worker").Add("reports", () => Run(), Hourly).Apply(manager, storage);

        RecurringJobSetResult web = new RecurringJobSet().Named("web").Add("invoices", () => Run(), Hourly).RetireUndeclared().Apply(manager, storage);

        Assert.Equal(["reminders"], web.Retired);
        Assert.True(Find(storage, "reminders").Paused);
        Assert.True(Find(storage, "reminders").Retired);
        Assert.False(Find(storage, "reports").Paused);
    }

    [Fact]
    public void RemoveUndeclared_OfTwoNamedSets_RemovesOnlyTheJobsItsOwnSetStoppedDeclaring()
    {
        InMemoryStorage storage = new();
        RecurringJobManager manager = new(storage);
        manager.AddOrUpdate("made-at-runtime", Job.FromExpression(() => Run()), Hourly, new RecurringJobOptions());

        for (int start = 0; start < 3; start++)
        {
            Assert.Empty(new RecurringJobSet().Named("web").Add("invoices", () => Run(), Hourly).Add("reminders", () => Run(), Hourly).RemoveUndeclared().Apply(manager, storage).Removed);
            Assert.Empty(new RecurringJobSet().Named("worker").Add("reports", () => Run(), Hourly).RemoveUndeclared().Apply(manager, storage).Removed);
        }

        RecurringJobSetResult web = new RecurringJobSet().Named("web").Add("invoices", () => Run(), Hourly).RemoveUndeclared().Apply(manager, storage);

        Assert.Equal(["reminders"], web.Removed);
        Assert.Equal(["invoices", "made-at-runtime", "reports"], Ids(storage));
    }

    [Fact]
    public void AJobMarkedByAnEarlierBuild_IsNeitherRetiredNorRemoved_AndIsMarkedWithTheNameWhenDeclaredAgain()
    {
        InMemoryStorage storage = new();
        RecurringJobManager manager = new(storage);
        manager.AddOrUpdate("legacy", Job.FromExpression(() => Run()), Hourly, new RecurringJobOptions());
        MarkAsAnEarlierBuildDid(storage, "legacy");

        RecurringJobSetResult retired = new RecurringJobSet().Named("web").Add("invoices", () => Run(), Hourly).RetireUndeclared().Apply(manager, storage);
        RecurringJobSetResult removed = new RecurringJobSet().Named("web").Add("invoices", () => Run(), Hourly).RemoveUndeclared().Apply(manager, storage);

        Assert.Empty(retired.Retired);
        Assert.Empty(removed.Removed);
        RecurringJobDto legacy = Find(storage, "legacy");
        Assert.True(legacy.Declared);
        Assert.Null(legacy.DeclaredBy);
        Assert.False(legacy.Paused);

        new RecurringJobSet().Named("web").Add("invoices", () => Run(), Hourly).Add("legacy", () => Run(), Hourly).Apply(manager, storage);

        Assert.Equal("web", Find(storage, "legacy").DeclaredBy);
    }

    [Fact]
    public void AJobDeclaredByAnotherSet_MovesToTheSetThatDeclaresItLast()
    {
        InMemoryStorage storage = new();
        RecurringJobManager manager = new(storage);
        new RecurringJobSet().Named("web").Add("shared", () => Run(), Hourly).Apply(manager, storage);

        new RecurringJobSet().Named("worker").Add("shared", () => Run(), Hourly).Apply(manager, storage);
        RecurringJobSetResult web = new RecurringJobSet().Named("web").Add("invoices", () => Run(), Hourly).RetireUndeclared().Apply(manager, storage);

        Assert.Equal("worker", Find(storage, "shared").DeclaredBy);
        Assert.Empty(web.Retired);
        Assert.False(Find(storage, "shared").Paused);

        RecurringJobSetResult worker = new RecurringJobSet().Named("worker").Add("reports", () => Run(), Hourly).RetireUndeclared().Apply(manager, storage);

        Assert.Equal(["shared"], worker.Retired);
    }

    public static void Run()
    {
    }

    private static void MarkAsAnEarlierBuildDid(InMemoryStorage storage, string recurringJobId)
    {
        using IStorageConnection connection = storage.GetConnection();
        using IWriteOnlyTransaction transaction = connection.CreateWriteTransaction();
        transaction.SetRangeInHash($"recurring-job:{recurringJobId}", [new KeyValuePair<string, string>("Declared", "1")]);
        transaction.Commit();
    }

    private static RecurringJobDto Find(InMemoryStorage storage, string recurringJobId)
    {
        using IStorageConnection connection = storage.GetConnection();
        return connection.GetRecurringJobs().Single(x => x.Id == recurringJobId);
    }

    private static List<string> Ids(InMemoryStorage storage)
    {
        using IStorageConnection connection = storage.GetConnection();
        return [.. connection.GetRecurringJobs().Where(static x => !x.Removed).Select(static x => x.Id).Order(StringComparer.Ordinal)];
    }
}
