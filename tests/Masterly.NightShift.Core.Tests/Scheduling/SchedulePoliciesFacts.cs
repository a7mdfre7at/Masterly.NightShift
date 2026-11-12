using System.Globalization;

namespace Masterly.NightShift.Core.Tests;

public sealed class SchedulePoliciesFacts : IDisposable
{
    private static readonly DateTime Now = new(2026, 11, 18, 10, 0, 0, DateTimeKind.Utc);

    private readonly PolicyStorage _storage = new();

    public SchedulePoliciesFacts()
    {
        JobClock.TimeProvider = new FixedClock(new DateTimeOffset(Now));
    }

    public void Dispose()
    {
        JobClock.TimeProvider = TimeProvider.System;
    }

    [Fact]
    public void GetKey_NamesTheHashAfterThePolicy()
    {
        Assert.Equal("schedule-policy:business-hours", SchedulePolicies.GetKey("Business-Hours"));
        Assert.Throws<ArgumentException>("name", () => SchedulePolicies.GetKey("not valid"));
    }

    [Fact, GlobalLock(Reason = "Changes the clock")]
    public void SaveSchedulePolicy_StoresANewPolicy_WithRevisionOne()
    {
        SchedulePolicy policy = new SchedulePolicy("business-hours").AllowBetween("09:00", "17:00");

        long revision = _storage.Object.SaveSchedulePolicy(policy);

        Assert.Equal(1, revision);
        Assert.Equal(1, policy.Revision);
        Assert.Equal(Now, policy.UpdatedAt);
        Dictionary<string, string> hash = _storage.Hashes["schedule-policy:business-hours"];
        Assert.Equal("09:00-17:00", hash["Windows"]);
        Assert.Equal("1", hash["Revision"]);
        Assert.Contains("business-hours", _storage.Sets[SchedulePolicies.SetKey]);
        Assert.Contains("lock:schedule-policies", _storage.Locks);
    }

    [Fact, GlobalLock(Reason = "Changes the clock")]
    public void SaveSchedulePolicy_WritesNothing_WhenTheRulesAreTheSame()
    {
        _storage.Add(new SchedulePolicy("p").AllowBetween("09:00", "17:00"), revision: 3);
        SchedulePolicy policy = new SchedulePolicy("p").AllowBetween("09:00", "17:00");

        long revision = _storage.Object.SaveSchedulePolicy(policy);

        Assert.Equal(3, revision);
        Assert.Equal(3, policy.Revision);
        Assert.Equal(0, _storage.Commits);
    }

    [Fact, GlobalLock(Reason = "Changes the clock")]
    public void SaveSchedulePolicy_RaisesTheRevision_WhenTheRulesChange()
    {
        _storage.Add(new SchedulePolicy("p").AllowBetween("09:00", "17:00"), revision: 3);

        long revision = _storage.Object.SaveSchedulePolicy(new SchedulePolicy("p").AllowBetween("08:00", "17:00"));

        Assert.Equal(4, revision);
        Assert.Equal("08:00-17:00", _storage.Hashes["schedule-policy:p"]["Windows"]);
    }

    [Fact, GlobalLock(Reason = "Changes the clock")]
    public void SaveSchedulePolicy_ReplacesAPolicyThatCannotBeRead()
    {
        _storage.Hashes["schedule-policy:p"] = new Dictionary<string, string> { ["Days"] = "Someday", ["Revision"] = "2" };

        long revision = _storage.Object.SaveSchedulePolicy(new SchedulePolicy("p"));

        Assert.Equal(3, revision);
        Assert.Equal(string.Empty, _storage.Hashes["schedule-policy:p"]["Days"]);
    }

    [Fact, GlobalLock(Reason = "Changes the clock")]
    public void SaveSchedulePolicy_ThrowsAnException_WhenAReferencedPolicyIsNotStored()
    {
        ArgumentException exception = Assert.Throws<ArgumentException>("policy", () => _storage.Object.SaveSchedulePolicy(new SchedulePolicy("p").Require("trading")));

        Assert.Contains("'trading', which isn't stored", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, _storage.Commits);
    }

    [Fact, GlobalLock(Reason = "Changes the clock")]
    public void SaveSchedulePolicy_ThrowsAnException_WhenTheReferencesFormACycle()
    {
        _storage.Add(new SchedulePolicy("b").Require("c"));
        _storage.Add(new SchedulePolicy("c").ExceptWhen("a"));
        _storage.Add(new SchedulePolicy("a"));

        ArgumentException exception = Assert.Throws<ArgumentException>("policy", () => _storage.Object.SaveSchedulePolicy(new SchedulePolicy("a").Require("b")));

        Assert.Contains("a -> b -> c -> a", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SaveSchedulePolicy_ThrowsAnException_WhenArgumentsAreNull()
    {
        Assert.Throws<ArgumentNullException>("storage", () => SchedulePolicies.SaveSchedulePolicy(null, new SchedulePolicy("p")));
        Assert.Throws<ArgumentNullException>("policy", () => _storage.Object.SaveSchedulePolicy(null));
    }

    [Fact, GlobalLock(Reason = "Changes the clock")]
    public void UpdateSchedulePolicy_WritesOnlyWhenTheRevisionMatches()
    {
        _storage.Add(new SchedulePolicy("p"), revision: 2);

        Assert.Null(_storage.Object.UpdateSchedulePolicy(new SchedulePolicy("p").AllowBetween("09:00", "10:00"), 1));
        Assert.Equal(string.Empty, _storage.Hashes["schedule-policy:p"]["Windows"]);

        Assert.Equal(3, _storage.Object.UpdateSchedulePolicy(new SchedulePolicy("p").AllowBetween("09:00", "10:00"), 2));
        Assert.Equal("09:00-10:00", _storage.Hashes["schedule-policy:p"]["Windows"]);
    }

    [Fact, GlobalLock(Reason = "Changes the clock")]
    public void UpdateSchedulePolicy_WithRevisionZero_CreatesOnlyAPolicyThatDoesNotExist()
    {
        Assert.Equal(1, _storage.Object.UpdateSchedulePolicy(new SchedulePolicy("new"), 0));
        Assert.Null(_storage.Object.UpdateSchedulePolicy(new SchedulePolicy("new").AllowBetween("09:00", "10:00"), 0));
    }

    [Fact]
    public void UpdateSchedulePolicy_ThrowsAnException_ForANegativeRevision()
    {
        Assert.Throws<ArgumentOutOfRangeException>("expectedRevision", () => _storage.Object.UpdateSchedulePolicy(new SchedulePolicy("p"), -1));
    }

    [Fact]
    public void GetSchedulePolicy_ReadsThePolicyWithItsRevision_OrReturnsNull()
    {
        _storage.Add(new SchedulePolicy("p").AllowWeekdays(DayOfWeek.Monday), revision: 5);

        SchedulePolicy policy = _storage.Object.GetSchedulePolicy("P");

        Assert.Equal(5, policy?.Revision);
        Assert.Equal([DayOfWeek.Monday], policy?.Days);
        Assert.Null(_storage.Object.GetSchedulePolicy("other"));
    }

    [Fact]
    public void GetSchedulePolicies_ListsTheStoredPolicies_ByName()
    {
        _storage.Add(new SchedulePolicy("zeta"));
        _storage.Add(new SchedulePolicy("alpha"));
        _storage.Sets[SchedulePolicies.SetKey].Add("deleted-meanwhile");

        Assert.Equal(["alpha", "zeta"], _storage.Object.GetSchedulePolicies().Select(static x => x.Name));
    }

    [Fact]
    public void DeleteSchedulePolicy_RemovesThePolicy_WhenTheRevisionMatches()
    {
        _storage.Add(new SchedulePolicy("p"), revision: 2);

        Assert.False(_storage.Object.DeleteSchedulePolicy("p", 1));
        Assert.True(_storage.Hashes.ContainsKey("schedule-policy:p"));

        Assert.True(_storage.Object.DeleteSchedulePolicy("p", 2));
        Assert.False(_storage.Hashes.ContainsKey("schedule-policy:p"));
        Assert.DoesNotContain("p", _storage.Sets[SchedulePolicies.SetKey]);
    }

    [Fact, GlobalLock(Reason = "Changes the clock")]
    public void SaveAndDelete_TakeTheSameLock_WhateverThePolicy()
    {
        _storage.Add(new SchedulePolicy("trading"));

        _storage.Object.SaveSchedulePolicy(new SchedulePolicy("business-hours").Require("trading"));
        Assert.Throws<InvalidOperationException>(() => _storage.Object.DeleteSchedulePolicy("trading", 1));

        Assert.Equal(["lock:schedule-policies", "lock:schedule-policies"], _storage.Locks);
    }

    [Fact]
    public void DeleteSchedulePolicy_ReturnsFalse_WhenThePolicyDoesNotExist()
    {
        Assert.False(_storage.Object.DeleteSchedulePolicy("p", 0));
    }

    [Fact]
    public void DeleteSchedulePolicy_ThrowsAnException_WhileAPolicyOrARecurringJobUsesIt()
    {
        _storage.Add(new SchedulePolicy("trading"));
        _storage.Add(new SchedulePolicy("business-hours").Require("trading"));
        _storage.AddRecurringJob("report", new Dictionary<string, string> { ["Cron"] = "0 9 * * *", ["Policy"] = "trading" });
        _storage.AddRecurringJob("other", new Dictionary<string, string> { ["Cron"] = "0 9 * * *" });

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => _storage.Object.DeleteSchedulePolicy("trading", 1));

        Assert.Equal("Schedule policy 'trading' can't be deleted while policy 'business-hours', recurring job 'report' use it.", exception.Message);
        Assert.True(_storage.Hashes.ContainsKey("schedule-policy:trading"));
    }

    [Fact, GlobalLock(Reason = "Changes the clock")]
    public void EvaluateSchedulePolicy_EvaluatesTheStoredPolicy()
    {
        _storage.Add(new SchedulePolicy("p").AllowWeekdays(DayOfWeek.Monday));

        SchedulePolicyDecision decision = _storage.Object.EvaluateSchedulePolicy("p", Now);

        Assert.False(decision.Allowed);
        Assert.Equal("policy 'p': Wednesday isn't an allowed day", decision.Reason);
    }

    [Fact]
    public void EvaluateSchedulePolicy_ThrowsAnException_WhenThePolicyDoesNotExist()
    {
        Assert.Throws<ArgumentException>("name", () => _storage.Object.EvaluateSchedulePolicy("nope", Now));
    }

    [Fact]
    public void FindNextAllowed_ReturnsTheNextAllowedInstant()
    {
        _storage.Add(new SchedulePolicy("p").AllowWeekdays(DayOfWeek.Monday));

        Assert.Equal(new DateTime(2026, 11, 23, 0, 0, 0, DateTimeKind.Utc), _storage.Object.FindNextAllowed("p", Now));
    }

    [Fact, GlobalLock(Reason = "Changes the clock")]
    public void PreviewSchedulePolicy_MarksEachOccurrence()
    {
        _storage.Add(new SchedulePolicy("weekdays").AllowWeekdays(DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday));

        IReadOnlyList<SchedulePolicyDecision> preview = _storage.Object.PreviewSchedulePolicy("weekdays", "0 12 * * *", count: 5);

        Assert.Equal(5, preview.Count);
        Assert.Equal(new DateTime(2026, 11, 18, 12, 0, 0, DateTimeKind.Utc), preview[0].Instant);
        Assert.Equal([true, true, true, false, false], preview.Select(static x => x.Allowed));
        Assert.Equal("policy 'weekdays': Saturday isn't an allowed day", preview[3].Reason);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1001)]
    public void PreviewSchedulePolicy_ThrowsAnException_WhenCountIsOutOfRange(int count)
    {
        Assert.Throws<ArgumentOutOfRangeException>("count", () => _storage.Object.PreviewSchedulePolicy("p", "* * * * *", count: count));
    }

    [Fact]
    public void PreviewSchedulePolicy_ThrowsAnException_ForAnInvalidCron()
    {
        _storage.Add(new SchedulePolicy("p"));

        Assert.Throws<ArgumentException>("cronExpression", () => _storage.Object.PreviewSchedulePolicy("p", "not a cron"));
    }

    [Fact, GlobalLock(Reason = "Changes the clock")]
    public void SaveSchedulePolicy_ReschedulesTheRecurringJobsThatFollowIt_DirectlyOrNot()
    {
        _storage.Add(new SchedulePolicy("weekdays"));
        _storage.Add(new SchedulePolicy("reports").Require("weekdays"));
        _storage.AddRecurringJob("direct", RecurringJob("weekdays", Now.AddHours(2)));
        _storage.AddRecurringJob("indirect", RecurringJob("reports", Now.AddHours(2)));
        _storage.AddRecurringJob("paused", RecurringJob("weekdays", null, paused: true));
        _storage.AddRecurringJob("unrelated", RecurringJob(null, Now.AddHours(2)));

        // Saturday and Sunday only: the next noon allowed is Saturday the 21st
        _storage.Object.SaveSchedulePolicy(new SchedulePolicy("weekdays").AllowWeekdays(DayOfWeek.Saturday, DayOfWeek.Sunday));

        string saturdayNoon = JobHelper.SerializeDateTime(new DateTime(2026, 11, 21, 12, 0, 0, DateTimeKind.Utc));
        Assert.Equal(saturdayNoon, _storage.Hashes["recurring-job:direct"]["NextExecution"]);
        Assert.Equal(saturdayNoon, _storage.Hashes["recurring-job:indirect"]["NextExecution"]);
        Assert.Equal(JobHelper.SerializeDateTime(Now), _storage.Hashes["recurring-job:direct"]["ScheduleChangedAt"]);
        Assert.False(_storage.Hashes["recurring-job:paused"].ContainsKey("NextExecution"));
        Assert.Equal(JobHelper.SerializeDateTime(Now.AddHours(2)), _storage.Hashes["recurring-job:unrelated"]["NextExecution"]);
    }

    [Fact, GlobalLock(Reason = "Changes the clock")]
    public void SaveSchedulePolicy_ReadsEachPolicyOnce_WhenItReschedulesManyJobs()
    {
        _storage.Add(new SchedulePolicy("weekdays"));
        _storage.Add(new SchedulePolicy("reports").Require("weekdays"));
        _storage.AddRecurringJob("first", RecurringJob("reports", Now.AddHours(2)));
        _storage.AddRecurringJob("second", RecurringJob("reports", Now.AddHours(2)));
        _storage.AddRecurringJob("third", RecurringJob("reports", Now.AddHours(2)));

        _storage.Object.SaveSchedulePolicy(new SchedulePolicy("weekdays").AllowWeekdays(DayOfWeek.Saturday));

        // Once to find the policies that refer to the saved one, once to evaluate it for the three jobs
        _storage.Connection.Verify(x => x.GetAllEntriesFromHash(SchedulePolicies.GetKey("reports")), Times.Exactly(2));
        Assert.Equal(
            JobHelper.SerializeDateTime(new DateTime(2026, 11, 21, 12, 0, 0, DateTimeKind.Utc)),
            _storage.Hashes["recurring-job:third"]["NextExecution"]);
    }

    private static Dictionary<string, string> RecurringJob(string policy, DateTime? nextExecution, bool paused = false)
    {
        Dictionary<string, string> hash = new()
        {
            ["Cron"] = "0 12 * * *",
            ["Job"] = InvocationData.SerializeJob(Job.FromExpression(() => Run())).SerializePayload(),
            ["CreatedAt"] = JobHelper.SerializeDateTime(Now.AddDays(-1)),
            ["V"] = "2",
        };

        if (policy != null)
        {
            hash["Policy"] = policy;
        }

        if (nextExecution.HasValue)
        {
            hash["NextExecution"] = JobHelper.SerializeDateTime(nextExecution.Value);
        }

        if (paused)
        {
            hash["Paused"] = "1";
        }

        return hash;
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "xUnit1013:Public method should be marked as test", Justification = "Should be public to be a job")]
    public static void Run()
    {
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    internal static DateTime Utc(string value) => DateTime.SpecifyKind(DateTime.Parse(value, CultureInfo.InvariantCulture), DateTimeKind.Utc);
}
