using Masterly.NightShift.Server;
using LogLevel = Masterly.NightShift.Logging.LogLevel;

namespace Masterly.NightShift.Core.Tests;

public sealed class DeploymentFencingFacts : IDisposable
{
    private const double Day = 24 * 60 * 60;

    private static readonly DateTime Now = new(2026, 11, 20, 9, 0, 0, DateTimeKind.Utc);

    private readonly PolicyStorage _storage = new();

    public DeploymentFencingFacts()
    {
        DrainMode.InvalidateCache();
    }

    public void Dispose()
    {
        DeploymentFencing.DeploymentId = null;
        DeploymentFencing.DeploymentOrder = null;
        DeploymentFencing.CheckInterval = TimeSpan.FromSeconds(15);
        DeploymentFencing.TakeOverAfter = TimeSpan.FromSeconds(30);
        DrainMode.InvalidateCache();
    }

    [Fact, GlobalLock(Reason = "Uses the drain mode cache")]
    public void Check_TheFirstDeployment_ClaimsTheCluster()
    {
        bool fenced = Check(new DeploymentFencingProcess("v1"), "server-1", 0);

        Assert.False(fenced);
        Assert.Equal("v1", Hash["Current"]);
        Assert.Equal("v1", DeploymentFencing.GetCurrentDeployment(_storage.Object));
    }

    [Fact, GlobalLock(Reason = "Uses the drain mode cache")]
    public void Check_ANewDeployment_ClaimsTheCluster_AndTheOldServersPauseThemselves()
    {
        DeploymentFencingProcess old = new("v1");
        Check(old, "old-server", 0);

        Assert.False(Check(new DeploymentFencingProcess("v2"), "new-server", 5));
        Assert.True(Check(old, "old-server", 10));

        DrainModeStatus status = DrainMode.Read(_storage.Connection.Object);
        Assert.True(status.IsServerPaused("old-server"));
        Assert.Equal("Fenced: deployment 'v1' was replaced by 'v2'", status.PausedServers["old-server"]);
        Assert.False(status.IsServerPaused("new-server"));
        Assert.Equal("v2", DeploymentFencing.GetCurrentDeployment(_storage.Object));
    }

    [Fact, GlobalLock(Reason = "Uses the drain mode cache")]
    public void Check_ADeploymentSeenBefore_TakesOverOnlyOnceTheCurrentIsSilent_AndResumesItself()
    {
        DeploymentFencingProcess old = new("v1");
        Check(old, "old-server", 0);
        Check(new DeploymentFencingProcess("v2"), "new-server", 1);

        // The old server checks every 15 seconds, and v2 shows no sign of life after its first check
        bool[] fenced =
        [
            Check(old, "old-server", 15),
            Check(old, "old-server", 30),
            Check(old, "old-server", 45),
            Check(old, "old-server", 60),
        ];

        // Watched without a sign of life for 0, 15 and 30 seconds, then for more than TakeOverAfter
        Assert.Equal([true, true, true, false], fenced);
        Assert.Equal("v1", DeploymentFencing.GetCurrentDeployment(_storage.Object));
        Assert.False(DrainMode.Read(_storage.Connection.Object).IsServerPaused("old-server"));
    }

    [Fact, GlobalLock(Reason = "Uses the drain mode cache and the log provider")]
    public void Check_WarnsWhenADeploymentNeverSeen_TakesOverFromLiveServers()
    {
        RecordingLogProvider recording = new();
        Check(new DeploymentFencingProcess("v2"), "new-server", 0);

        using (recording.Use())
        {
            Check(new DeploymentFencingProcess("v1-stray"), "stray-server", 5);
        }

        RecordingLogProvider.LogEntry warning = Assert.Single(recording.Entries, static x => x.Level == LogLevel.Warn);
        Assert.Contains("'v1-stray', never seen before, takes over from 'v2'", warning.Message, StringComparison.Ordinal);
        Assert.Equal("v1-stray", DeploymentFencing.GetCurrentDeployment(_storage.Object));
    }

    [Fact, GlobalLock(Reason = "Uses the drain mode cache and the log provider")]
    public void Check_DoesNotWarn_WhenTheCurrentDeploymentIsSilent()
    {
        RecordingLogProvider recording = new();
        Check(new DeploymentFencingProcess("v1"), "old-server", 0);

        using (recording.Use())
        {
            Check(new DeploymentFencingProcess("v2"), "new-server", 300);
        }

        Assert.DoesNotContain(recording.Entries, static x => x.Level == LogLevel.Warn);
        Assert.Equal("v2", DeploymentFencing.GetCurrentDeployment(_storage.Object));
    }

    [Fact, GlobalLock(Reason = "Uses the drain mode cache")]
    public void Check_KeepsAPauseSetByHand()
    {
        DrainMode.PauseServer(_storage.Connection.Object, "server-1", "maintenance");

        Check(new DeploymentFencingProcess("v1"), "server-1", 0);

        Assert.Equal("maintenance", DrainMode.Read(_storage.Connection.Object).PausedServers["server-1"]);
    }

    [Fact]
    public void GetCurrentDeployment_ReturnsNull_BeforeAnyServerChecked()
    {
        Assert.Null(DeploymentFencing.GetCurrentDeployment(_storage.Object));
    }

    [Fact, StaticLock]
    public void UseDeploymentFencing_SetsTheDeployment_OrRefusesABadOne()
    {
        IGlobalConfiguration configuration = new Mock<IGlobalConfiguration>().Object;

        configuration.UseDeploymentFencing(" release-42 ");
        Assert.Equal("release-42", DeploymentFencing.DeploymentId);

        Assert.Throws<ArgumentException>("deploymentId", () => configuration.UseDeploymentFencing(new string('x', 65)));
        Assert.Throws<ArgumentException>("deploymentId", () => configuration.UseDeploymentFencing(" "));
        Assert.Throws<ArgumentException>("deploymentId", () => configuration.UseDeploymentFencing("a\nb"));
    }

    [Fact, GlobalLock(Reason = "Uses the drain mode cache and the log provider")]
    public void Check_AHigherOrder_TakesOverALiveCurrentAtOnce_WithoutAWarning()
    {
        RecordingLogProvider recording = new();
        Check(new DeploymentFencingProcess("v1", 41), "old-server", 0);

        bool fenced;
        using (recording.Use())
        {
            fenced = Check(new DeploymentFencingProcess("v2", 42), "new-server", 5);
        }

        Assert.False(fenced);
        Assert.Equal("v2", DeploymentFencing.GetCurrentDeployment(_storage.Object));
        Assert.Equal(42, DeploymentFencing.GetCurrentDeploymentOrder(_storage.Object));
        Assert.DoesNotContain(recording.Entries, static x => x.Level == LogLevel.Warn);
        Assert.Contains(recording.Entries, static x => x.Message.Contains("'v2' (order 42) takes over from 'v1' (order 41)", StringComparison.Ordinal));
    }

    [Fact, GlobalLock(Reason = "Uses the drain mode cache")]
    public void Check_AHigherOrder_TakesOverALiveCurrentAtOnce_EvenWhenItWasSeenBefore()
    {
        DeploymentFencingProcess newer = new("v2", 42);
        DeploymentFencingProcess older = new("v1", 41);
        Check(newer, "new-server", 0);
        Check(older, "old-server", 1);

        // v2 went silent, so v1 took over; then the servers of v2 come back while those of v1 are running
        Assert.True(Check(older, "old-server", 16));
        Assert.True(Check(older, "old-server", 31));
        Assert.False(Check(older, "old-server", 46));
        Assert.Equal("v1", DeploymentFencing.GetCurrentDeployment(_storage.Object));

        Assert.False(Check(newer, "new-server", 50));
        Assert.True(Check(older, "old-server", 55));

        Assert.Equal("v2", DeploymentFencing.GetCurrentDeployment(_storage.Object));
    }

    [Fact, GlobalLock(Reason = "Uses the drain mode cache")]
    public void Check_ALowerOrderNeverSeen_StaysFencedWhileTheCurrentIsAlive_AndTakesOverOnceItIsSilent()
    {
        DeploymentFencingProcess current = new("v2", 42);
        DeploymentFencingProcess stray = new("v1-stray", 7);
        Check(current, "new-server", 0);

        Assert.True(Check(stray, "stray-server", 5));
        Assert.False(Check(current, "new-server", 20));
        Assert.True(Check(stray, "stray-server", 25));

        DrainModeStatus status = DrainMode.Read(_storage.Connection.Object);
        Assert.Equal("Fenced: deployment 'v1-stray' (order 7) is older than 'v2' (order 42)", status.PausedServers["stray-server"]);
        Assert.False(status.IsServerPaused("new-server"));
        Assert.Equal("v2", DeploymentFencing.GetCurrentDeployment(_storage.Object));

        // The stray server keeps checking, and has watched v2 without a sign of life for more than TakeOverAfter
        Assert.True(Check(stray, "stray-server", 40));
        Assert.True(Check(stray, "stray-server", 55));
        Assert.False(Check(stray, "stray-server", 70));

        Assert.Equal("v1-stray", DeploymentFencing.GetCurrentDeployment(_storage.Object));
        Assert.Equal(7, DeploymentFencing.GetCurrentDeploymentOrder(_storage.Object));
        Assert.False(DrainMode.Read(_storage.Connection.Object).IsServerPaused("stray-server"));
    }

    [Fact, GlobalLock(Reason = "Uses the drain mode cache and the log provider")]
    public void Check_WarnsOnce_WhenANewerDeploymentFencesIt()
    {
        RecordingLogProvider recording = new();
        DeploymentFencingProcess current = new("v2", 42);
        DeploymentFencingProcess stray = new("v1-stray", 7);
        Check(current, "new-server", 0);

        using (recording.Use())
        {
            Check(stray, "stray-server", 5);
            Check(current, "new-server", 10);
            Check(stray, "stray-server", 20);
            Check(current, "new-server", 25);
            Check(stray, "stray-server", 35);
        }

        RecordingLogProvider.LogEntry warning = Assert.Single(recording.Entries, static x => x.Level == LogLevel.Warn);
        Assert.Contains("'v1-stray' (order 7) is fenced, because the newer deployment 'v2' (order 42) is the current one", warning.Message, StringComparison.Ordinal);
    }

    [Fact, GlobalLock(Reason = "Uses the drain mode cache and the log provider")]
    public void Check_WithEqualOrders_FollowsTheRuleOfSeenAndUnseenDeployments()
    {
        RecordingLogProvider recording = new();
        DeploymentFencingProcess old = new("v1", 42);
        Check(old, "old-server", 0);

        // Never seen: takes over at once, with the warning
        bool unseenFenced;
        using (recording.Use())
        {
            unseenFenced = Check(new DeploymentFencingProcess("v2", 42), "new-server", 5);
        }

        // Seen before: waits for the current one to be silent
        bool seenFenced = Check(old, "old-server", 10);

        Assert.False(unseenFenced);
        Assert.True(seenFenced);
        Assert.Equal("v2", DeploymentFencing.GetCurrentDeployment(_storage.Object));
        Assert.Equal("Fenced: deployment 'v1' was replaced by 'v2'", DrainMode.Read(_storage.Connection.Object).PausedServers["old-server"]);
        RecordingLogProvider.LogEntry warning = Assert.Single(recording.Entries, static x => x.Level == LogLevel.Warn);
        Assert.Contains("'v2', never seen before, takes over from 'v1'", warning.Message, StringComparison.Ordinal);
    }

    [Theory, GlobalLock(Reason = "Uses the drain mode cache")]
    [InlineData(42L, null)]
    [InlineData(null, 7L)]
    [InlineData(null, null)]
    public void Check_WithoutAnOrderOnEitherSide_BehavesAsWithoutOrders(long? currentOrder, long? otherOrder)
    {
        DeploymentFencingProcess first = new("v2", currentOrder);
        Check(first, "new-server", 0);

        // Never seen: takes over at once, whatever its order
        Assert.False(Check(new DeploymentFencingProcess("v1", otherOrder), "old-server", 5));
        Assert.Equal("v1", DeploymentFencing.GetCurrentDeployment(_storage.Object));

        // Seen before: fenced while the current one is alive, and takes over once it is silent
        Assert.True(Check(first, "new-server", 10));
        Assert.Equal("Fenced: deployment 'v2' was replaced by 'v1'", DrainMode.Read(_storage.Connection.Object).PausedServers["new-server"]);
        Assert.True(Check(first, "new-server", 25));
        Assert.True(Check(first, "new-server", 40));
        Assert.False(Check(first, "new-server", 55));
        Assert.Equal("v2", DeploymentFencing.GetCurrentDeployment(_storage.Object));
    }

    [Fact, GlobalLock(Reason = "Uses the drain mode cache")]
    public void Check_KeepsTheOrderWithTheDeployment_WithoutChangingTheOtherFields()
    {
        Check(new DeploymentFencingProcess("v1", 41), "server-1", 0);

        Assert.Equal("v1", Hash["Current"]);
        Assert.Equal($"{JobHelper.SerializeDateTime(Now)}|1|15000", Hash["Seen:v1"]);
        Assert.Equal("41", Hash["Order:v1"]);
        Assert.Equal(["Current", "Order:v1", "Seen:v1"], Hash.Keys.Order(StringComparer.Ordinal));
    }

    [Fact, GlobalLock(Reason = "Uses the drain mode cache")]
    public void Check_WritesNoOrderField_ForADeploymentWithoutAnOrder()
    {
        Check(new DeploymentFencingProcess("v1"), "server-1", 0);

        Assert.Equal(["Current", "Seen:v1"], Hash.Keys.Order(StringComparer.Ordinal));
        Assert.Null(DeploymentFencing.GetCurrentDeploymentOrder(_storage.Object));
    }

    [Fact]
    public void GetCurrentDeploymentOrder_ReturnsNull_BeforeAnyServerChecked()
    {
        Assert.Null(DeploymentFencing.GetCurrentDeploymentOrder(_storage.Object));
    }

    [Theory, GlobalLock(Reason = "Uses the drain mode cache")]
    [InlineData(40)]
    [InlineData(300)]
    [InlineData(-40)]
    [InlineData(-300)]
    public void Check_WithAClockDifference_KeepsTheOlderDeploymentFencedWhileTheNewerOneChecks(int skewSeconds)
    {
        // The older server's clock is ahead of or behind the newer one's, and its monotonic clock starts elsewhere
        TimeSpan skew = TimeSpan.FromSeconds(skewSeconds);
        TimeSpan origin = TimeSpan.FromHours(3);
        DeploymentFencingProcess older = new("v1", 1);
        DeploymentFencingProcess newer = new("v2", 2);
        Check(older, "old-server", 0, skew, origin);
        Check(newer, "new-server", 5);

        // Five minutes of both checking every 15 seconds
        List<(bool OlderFenced, bool NewerFenced, string Current)> rounds = Simulate(20, round =>
        (
            Check(older, "old-server", 10 + (15 * round), skew, origin),
            Check(newer, "new-server", 20 + (15 * round)),
            DeploymentFencing.GetCurrentDeployment(_storage.Object)
        ));

        Assert.All(rounds, static x => Assert.True(x.OlderFenced));
        Assert.All(rounds, static x => Assert.False(x.NewerFenced));
        Assert.All(rounds, static x => Assert.Equal("v2", x.Current));
    }

    [Theory, GlobalLock(Reason = "Uses the drain mode cache")]
    [InlineData(40)]
    [InlineData(300)]
    [InlineData(-40)]
    [InlineData(-300)]
    public void Check_WithAClockDifference_TakesOverOnlyOnceTheCurrentHasBeenSilentForTakeOverAfter_ByItsOwnMeasure(int skewSeconds)
    {
        TimeSpan skew = TimeSpan.FromSeconds(skewSeconds);
        DeploymentFencingProcess older = new("v1", 1);
        DeploymentFencingProcess newer = new("v2", 2);
        Check(older, "old-server", 0, skew);
        Check(newer, "new-server", 5);
        Check(older, "old-server", 10, skew);

        // The last check of v2, which the older server reads five seconds later
        Check(newer, "new-server", 20);

        bool[] fenced =
        [
            Check(older, "old-server", 25, skew),
            Check(older, "old-server", 40, skew),
            Check(older, "old-server", 55, skew),
            Check(older, "old-server", 70, skew),
        ];

        // Silent for 0, 15 and 30 seconds since the older server read the last change, then for 45
        Assert.Equal([true, true, true, false], fenced);
        Assert.Equal("v1", DeploymentFencing.GetCurrentDeployment(_storage.Object));
    }

    [Theory, GlobalLock(Reason = "Uses the drain mode cache")]
    [InlineData(7L, 42L)]
    [InlineData(null, null)]
    public void Check_AServerJustStarted_DoesNotTakeOverBeforeItHasWatchedTheCurrentForTakeOverAfter(long? order, long? currentOrder)
    {
        // v1 was seen, v2 replaced it, and both are gone for a day: nothing tells a server that starts now
        Check(new DeploymentFencingProcess("v1", order), "old-server", 0);
        Check(new DeploymentFencingProcess("v2", currentOrder), "new-server", 5);
        DeploymentFencingProcess started = new("v1", order);

        bool[] fenced =
        [
            Check(started, "restarted-server", Day),
            Check(started, "restarted-server", Day + 15),
            Check(started, "restarted-server", Day + 30),
            Check(started, "restarted-server", Day + 45),
        ];

        Assert.Equal([true, true, true, false], fenced);
        Assert.Equal("v1", DeploymentFencing.GetCurrentDeployment(_storage.Object));
    }

    [Fact, GlobalLock(Reason = "Uses the drain mode cache")]
    public void Check_WatchesACurrentDeploymentWithoutASeenField_UntilItHasStayedAway()
    {
        _storage.Hashes[DeploymentFencing.HashKey] = new Dictionary<string, string>
        {
            ["Current"] = "v9",
            ["Seen:v1"] = JobHelper.SerializeDateTime(Now.AddDays(-1)),
        };
        DeploymentFencingProcess process = new("v1");

        bool[] fenced =
        [
            Check(process, "server-1", 0),
            Check(process, "server-1", 15),
            Check(process, "server-1", 30),
            Check(process, "server-1", 45),
        ];

        Assert.Equal([true, true, true, false], fenced);
    }

    [Fact, GlobalLock(Reason = "Uses the drain mode cache")]
    public void Check_WritesAValueThatAlwaysChanges_AlsoWithinTheSameInstant()
    {
        DeploymentFencingProcess first = new("v1");
        DeploymentFencingProcess second = new("v1");

        Check(first, "server-1", 0);
        string one = Hash["Seen:v1"];
        Check(second, "server-2", 0);
        string two = Hash["Seen:v1"];
        Check(first, "server-1", 0);
        string three = Hash["Seen:v1"];

        Assert.Equal($"{JobHelper.SerializeDateTime(Now)}|1|15000", one);
        Assert.Equal($"{JobHelper.SerializeDateTime(Now)}|2|15000", two);
        Assert.Equal($"{JobHelper.SerializeDateTime(Now)}|3|15000", three);
    }

    [Fact, GlobalLock(Reason = "Uses the drain mode cache")]
    public void Check_ReadsASeenValueWithOnlyADate_AsWrittenBefore()
    {
        _storage.Hashes[DeploymentFencing.HashKey] = new Dictionary<string, string>
        {
            ["Current"] = "v1",
            ["Seen:v1"] = JobHelper.SerializeDateTime(Now.AddMinutes(-1)),
        };

        bool fenced = Check(new DeploymentFencingProcess("v1"), "server-1", 0);

        Assert.False(fenced);
        Assert.Equal($"{JobHelper.SerializeDateTime(Now)}|1|15000", Hash["Seen:v1"]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a date|x|-5")]
    [InlineData("|9223372036854775807|999999999999")]
    public void ReadSeen_ReadsNothing_FromADamagedValue(string value)
    {
        (DateTime? lastSeen, long sequence, TimeSpan? checkInterval) = DeploymentFencing.ReadSeen(value);

        Assert.Null(lastSeen);
        Assert.Equal(0, sequence);
        Assert.Null(checkInterval);
    }

    [Fact, GlobalLock(Reason = "Uses the drain mode cache and changes the check interval")]
    public void Check_WaitsForTwoChecksOfADeploymentThatChecksLessOften_BeforeItIsSilent()
    {
        // v2 was configured to check every two minutes; v1 keeps the defaults: 15 seconds, silent after 30
        DeploymentFencingProcess newer = new("v2", 2);
        DeploymentFencingProcess older = new("v1", 1);
        CheckEvery(TimeSpan.FromMinutes(2), newer, "new-server", 0);

        bool[] beforeTheSecondCheck = [.. Enumerable.Range(0, 8).Select(i => Check(older, "old-server", 5 + (15 * i)))];
        CheckEvery(TimeSpan.FromMinutes(2), newer, "new-server", 120);

        // The older server reads the second check at 125; four minutes later is 365
        bool[] afterTheLastCheck = [.. Enumerable.Range(0, 17).Select(i => Check(older, "old-server", 125 + (15 * i)))];
        bool afterFourMinutesOfSilence = Check(older, "old-server", 380);

        Assert.All(beforeTheSecondCheck, static x => Assert.True(x));
        Assert.All(afterTheLastCheck, static x => Assert.True(x));
        Assert.False(afterFourMinutesOfSilence);
        Assert.Equal($"{JobHelper.SerializeDateTime(Now.AddSeconds(120))}|2|120000", Hash["Seen:v2"]);
    }

    [Fact, GlobalLock(Reason = "Uses the drain mode cache, the log provider and changes the take-over time")]
    public void Check_UsesTwiceTheCheckInterval_WhenTakeOverAfterIsShorter_AndWarnsOnce()
    {
        RecordingLogProvider recording = new();
        DeploymentFencing.TakeOverAfter = TimeSpan.FromSeconds(10);
        DeploymentFencingProcess older = new("v1", 1);
        Check(new DeploymentFencingProcess("v2", 2), "new-server", 0);

        bool[] fenced;
        using (recording.Use())
        {
            fenced = [Check(older, "old-server", 5), Check(older, "old-server", 20), Check(older, "old-server", 35), Check(older, "old-server", 50)];
        }

        // After 15 and 30 seconds of silence v2 is still taken to be alive: one missed check isn't enough
        Assert.Equal(TimeSpan.FromSeconds(30), DeploymentFencing.EffectiveTakeOverAfter);
        Assert.Equal([true, true, true, false], fenced);
        RecordingLogProvider.LogEntry warning = Assert.Single(recording.Entries, static x => x.Message.Contains("less than twice", StringComparison.Ordinal));
        Assert.Equal(LogLevel.Warn, warning.Level);
        Assert.Contains("TakeOverAfter (00:00:10) is less than twice DeploymentFencing.CheckInterval (00:00:15)", warning.Message, StringComparison.Ordinal);
        Assert.Contains("00:00:30 is used instead", warning.Message, StringComparison.Ordinal);
    }

    [Fact, StaticLock]
    public void CheckInterval_AndTakeOverAfter_MustBePositive()
    {
        Assert.Throws<ArgumentOutOfRangeException>("value", static () => DeploymentFencing.CheckInterval = TimeSpan.Zero);
        Assert.Throws<ArgumentOutOfRangeException>("value", static () => DeploymentFencing.CheckInterval = TimeSpan.FromSeconds(-1));
        Assert.Throws<ArgumentOutOfRangeException>("value", static () => DeploymentFencing.TakeOverAfter = TimeSpan.Zero);
        Assert.Throws<ArgumentOutOfRangeException>("value", static () => DeploymentFencing.TakeOverAfter = TimeSpan.FromSeconds(-1));

        Assert.Equal(TimeSpan.FromSeconds(15), DeploymentFencing.CheckInterval);
        Assert.Equal(TimeSpan.FromSeconds(30), DeploymentFencing.TakeOverAfter);
        Assert.Equal(TimeSpan.FromSeconds(30), DeploymentFencing.EffectiveTakeOverAfter);
    }

    [Fact, GlobalLock(Reason = "Uses the drain mode cache and the log provider")]
    public void Check_WithServersOfOneIdStartedWithDifferentOrders_UsesTheHighestForAllOfThem()
    {
        // Servers of v2 started with orders 5 and 3, and v1 with order 4: v2 is the newer one, for all its servers
        RecordingLogProvider recording = new();
        DeploymentFencingProcess high = new("v2", 5);
        DeploymentFencingProcess low = new("v2", 3);
        DeploymentFencingProcess older = new("v1", 4);

        List<(bool OlderFenced, string Current, string Order)> rounds;
        using (recording.Use())
        {
            Check(high, "v2-a", 0);
            Check(low, "v2-b", 2);
            Check(older, "v1-a", 4);

            rounds = Simulate(20, round =>
            {
                Check(high, "v2-a", 15 + (15 * round));
                Check(low, "v2-b", 17 + (15 * round));
                return (Check(older, "v1-a", 19 + (15 * round)), DeploymentFencing.GetCurrentDeployment(_storage.Object), Hash["Order:v2"]);
            });
        }

        Assert.All(rounds, static x => Assert.True(x.OlderFenced));
        Assert.All(rounds, static x => Assert.Equal("v2", x.Current));
        Assert.All(rounds, static x => Assert.Equal("5", x.Order));
        Assert.Equal("Fenced: deployment 'v1' (order 4) is older than 'v2' (order 5)", DrainMode.Read(_storage.Connection.Object).PausedServers["v1-a"]);

        RecordingLogProvider.LogEntry warning = Assert.Single(recording.Entries, static x => x.Message.Contains("A deployment id has one order", StringComparison.Ordinal));
        Assert.Equal(LogLevel.Warn, warning.Level);
        Assert.Contains("'v2' was started with order 3, but other servers of 'v2' were started with order 5", warning.Message, StringComparison.Ordinal);
        Assert.Contains("the highest, 5, applies", warning.Message, StringComparison.Ordinal);
    }

    [Fact, GlobalLock(Reason = "Uses the drain mode cache and the log provider")]
    public void Check_WithServersOfOneIdStartedWithDifferentOrders_SettlesOnTheHighest_WhateverTheOrderTheyStartIn()
    {
        RecordingLogProvider recording = new();
        DeploymentFencingProcess high = new("v2", 5);
        DeploymentFencingProcess low = new("v2", 3);
        DeploymentFencingProcess older = new("v1", 4);

        List<(bool OlderFenced, string Current, string Order)> rounds;
        using (recording.Use())
        {
            // Until a server of v2 brings order 5, v1 (order 4) is the newer one by what the cluster knows
            Check(low, "v2-b", 0);
            Check(older, "v1-a", 2);
            Check(high, "v2-a", 4);

            rounds = Simulate(20, round =>
            {
                Check(low, "v2-b", 15 + (15 * round));
                Check(high, "v2-a", 17 + (15 * round));
                return (Check(older, "v1-a", 19 + (15 * round)), DeploymentFencing.GetCurrentDeployment(_storage.Object), Hash["Order:v2"]);
            });
        }

        Assert.All(rounds, static x => Assert.True(x.OlderFenced));
        Assert.All(rounds, static x => Assert.Equal("v2", x.Current));
        Assert.All(rounds, static x => Assert.Equal("5", x.Order));

        // Each of the two servers of v2 says once that the other was given another order
        Assert.Equal(2, recording.Entries.Count(static x => x.Message.Contains("A deployment id has one order", StringComparison.Ordinal)));
    }

    [Fact, GlobalLock(Reason = "Uses the drain mode cache and the log provider")]
    public void Check_AServerStartedWithoutAnOrder_KeepsTheOrderOfItsDeployment_AndWarnsOnce()
    {
        RecordingLogProvider recording = new();
        Check(new DeploymentFencingProcess("v1", 41), "server-1", 0);
        DeploymentFencingProcess without = new("v1", null);

        using (recording.Use())
        {
            Check(without, "server-2", 15);
            Check(without, "server-2", 30);
        }

        Assert.Equal("41", Hash["Order:v1"]);
        Assert.Equal(41, DeploymentFencing.GetCurrentDeploymentOrder(_storage.Object));
        RecordingLogProvider.LogEntry warning = Assert.Single(recording.Entries, static x => x.Level == LogLevel.Warn);
        Assert.Contains("'v1' was started without an order, but other servers of 'v1' were started with order 41", warning.Message, StringComparison.Ordinal);
    }

    [Fact, GlobalLock(Reason = "Uses the drain mode cache")]
    public void Check_ForgetsADeploymentLongGone_ButNotOneSeenThisWeek_NorTheOnesThatRun()
    {
        Check(new DeploymentFencingProcess("v1", 1), "server-1", -8 * Day);
        Check(new DeploymentFencingProcess("v2", 2), "server-2", -6 * Day);
        DeploymentFencingProcess current = new("v3", 3);

        // A server that just started hasn't watched the others yet, so it forgets none of them
        Check(current, "server-3", 0);
        Check(current, "server-3", 15);
        Check(current, "server-3", 30);
        string[] whileWatching = [.. Hash.Keys.Order(StringComparer.Ordinal)];

        Check(current, "server-3", 45);

        Assert.Equal(["Current", "Order:v1", "Order:v2", "Order:v3", "Seen:v1", "Seen:v2", "Seen:v3"], whileWatching);
        Assert.Equal(["Current", "Order:v2", "Order:v3", "Seen:v2", "Seen:v3"], Hash.Keys.Order(StringComparer.Ordinal));
        Assert.Equal("v3", DeploymentFencing.GetCurrentDeployment(_storage.Object));
        Assert.Equal(3, DeploymentFencing.GetCurrentDeploymentOrder(_storage.Object));
        Assert.Equal("2", Hash["Order:v2"]);
        Assert.Equal($"{JobHelper.SerializeDateTime(Now.AddSeconds(45))}|4|15000", Hash["Seen:v3"]);
    }

    [Fact, GlobalLock(Reason = "Uses the drain mode cache")]
    public void Check_NeverForgetsADeploymentWhoseServersAreAlive_WhateverTheDateTheyWrite()
    {
        // The clock of the fenced server is a month behind, so the date it writes is long past
        TimeSpan skew = TimeSpan.FromDays(-30);
        DeploymentFencingProcess older = new("v1", 1);
        DeploymentFencingProcess current = new("v2", 2);
        Check(older, "old-server", 0, skew);
        Check(current, "new-server", 5);

        List<(bool OlderFenced, bool HasOlder)> rounds = Simulate(10, round =>
        (
            Check(older, "old-server", 10 + (15 * round), skew),
            !Check(current, "new-server", 20 + (15 * round)) && Hash.ContainsKey("Seen:v1") && Hash.ContainsKey("Order:v1")
        ));

        Assert.All(rounds, static x => Assert.True(x.OlderFenced));
        Assert.All(rounds, static x => Assert.True(x.HasOlder));
    }

    [Fact, GlobalLock(Reason = "Uses the drain mode cache")]
    public void Check_TakesAForgottenDeploymentForOneNeverSeen_WhenItComesBack()
    {
        Check(new DeploymentFencingProcess("v1"), "old-server", -8 * Day);
        DeploymentFencingProcess current = new("v2");
        Check(current, "new-server", 0);
        Check(current, "new-server", 15);
        Check(current, "new-server", 30);
        Check(current, "new-server", 45);
        bool forgotten = !Hash.ContainsKey("Seen:v1");

        bool fenced = Check(new DeploymentFencingProcess("v1"), "old-server", 50);

        Assert.True(forgotten);
        Assert.False(fenced);
        Assert.Equal("v1", DeploymentFencing.GetCurrentDeployment(_storage.Object));
    }

    [Fact, GlobalLock(Reason = "Uses the drain mode cache and the log provider")]
    public void Check_GoesOn_AndLosesNothing_WhenForgettingFails()
    {
        RecordingLogProvider recording = new();
        Check(new DeploymentFencingProcess("v1", 1), "server-1", -8 * Day);
        Check(new DeploymentFencingProcess("v2", 2), "server-2", -6 * Day);
        DeploymentFencingProcess current = new("v3", 3);
        Check(current, "server-3", 0);
        Check(current, "server-3", 15);
        Check(current, "server-3", 30);
        _storage.FailCommitsThatRemoveAHash = true;

        bool fenced;
        using (recording.Use())
        {
            fenced = Check(current, "server-3", 45);
        }

        // The failed transaction had removed the hash: everything that must stay was written again
        Assert.False(fenced);
        Assert.Equal("v3", DeploymentFencing.GetCurrentDeployment(_storage.Object));
        Assert.Equal(3, DeploymentFencing.GetCurrentDeploymentOrder(_storage.Object));
        Assert.Equal("2", Hash["Order:v2"]);
        Assert.True(Hash.ContainsKey("Seen:v2"));
        Assert.Equal($"{JobHelper.SerializeDateTime(Now.AddSeconds(45))}|4|15000", Hash["Seen:v3"]);
        RecordingLogProvider.LogEntry warning = Assert.Single(recording.Entries, static x => x.Level == LogLevel.Warn);
        Assert.Contains("couldn't be removed", warning.Message, StringComparison.Ordinal);
    }

    [Theory, GlobalLock(Reason = "Uses the drain mode cache")]
    [InlineData(1L, 2L)]
    [InlineData(null, null)]
    public void Check_AfterAGapInWhichNoServerCouldCheck_KeepsTheOlderDeploymentFenced(long? olderOrder, long? newerOrder)
    {
        DeploymentFencingProcess older = new("v1", olderOrder);
        DeploymentFencingProcess newer = new("v2", newerOrder);
        Check(older, "old-server", 0);
        Check(newer, "new-server", 5);
        Check(older, "old-server", 10);
        Check(newer, "new-server", 20);
        Check(older, "old-server", 25);

        // The storage is down from 30 to 95, so the check of v2 due at 35 and the ones after it fail. The older
        // server is the first whose check succeeds again: the value of v2 is still the one it read at 25
        bool olderFencedAfterTheGap = Check(older, "old-server", 100);
        string currentAfterTheGap = DeploymentFencing.GetCurrentDeployment(_storage.Object);
        bool newerFencedAfterTheGap = Check(newer, "new-server", 105);

        bool[] olderFencedLater =
        [
            Check(older, "old-server", 115),
            !Check(newer, "new-server", 120) && Check(older, "old-server", 130),
            !Check(newer, "new-server", 135) && Check(older, "old-server", 145),
        ];

        Assert.True(olderFencedAfterTheGap);
        Assert.Equal("v2", currentAfterTheGap);
        Assert.False(newerFencedAfterTheGap);
        Assert.Equal([true, true, true], olderFencedLater);
        Assert.Equal("v2", DeploymentFencing.GetCurrentDeployment(_storage.Object));
        Assert.True(DrainMode.Read(_storage.Connection.Object).IsServerPaused("old-server"));
    }

    [Theory, GlobalLock(Reason = "Uses the drain mode cache")]
    [InlineData(1L, 2L)]
    [InlineData(null, null)]
    public void Check_AfterAGap_TakesOverOnlyOnceItHasWatchedTheCurrentSilentForTheWholeLimitAgain(long? olderOrder, long? newerOrder)
    {
        DeploymentFencingProcess older = new("v1", olderOrder);
        DeploymentFencingProcess newer = new("v2", newerOrder);
        Check(older, "old-server", 0);
        Check(newer, "new-server", 5);
        Check(older, "old-server", 10);
        Check(newer, "new-server", 20);
        Check(older, "old-server", 25);

        // The storage is down for 75 seconds, and the servers of v2 don't come back
        bool[] fenced =
        [
            Check(older, "old-server", 100),
            Check(older, "old-server", 115),
            Check(older, "old-server", 130),
            Check(older, "old-server", 145),
        ];

        // The 75 seconds don't count: silent for 0, 15 and 30 seconds since the first check after the gap, then 45
        Assert.Equal([true, true, true, false], fenced);
        Assert.Equal("v1", DeploymentFencing.GetCurrentDeployment(_storage.Object));
    }

    [Fact, GlobalLock(Reason = "Uses the drain mode cache")]
    public void Check_AServerWhoseChecksAreSlow_StillSeesTheCurrentGoSilent()
    {
        DeploymentFencingProcess older = new("v1", 1);
        Check(older, "old-server", 0);
        Check(new DeploymentFencingProcess("v2", 2), "new-server", 5);

        // 18 seconds apart, 1.2 times the check interval: no gap, so what was watched keeps counting
        bool[] fenced = [Check(older, "old-server", 18), Check(older, "old-server", 36), Check(older, "old-server", 54)];

        Assert.Equal([true, true, false], fenced);
        Assert.Equal("v1", DeploymentFencing.GetCurrentDeployment(_storage.Object));
    }

    [Fact, GlobalLock(Reason = "Uses the drain mode cache")]
    public void Check_AServerWhoseChecksAreMoreThanOneAndAHalfIntervalsApart_NeverCountsSilence()
    {
        DeploymentFencingProcess older = new("v1", 1);
        Check(older, "old-server", 0);
        Check(new DeploymentFencingProcess("v2", 2), "new-server", 5);

        // 23 seconds apart: each check comes after a gap in which the server saw nothing
        bool[] fenced = [.. Enumerable.Range(1, 8).Select(i => Check(older, "old-server", 23 * i))];

        Assert.All(fenced, static x => Assert.True(x));
        Assert.Equal("v2", DeploymentFencing.GetCurrentDeployment(_storage.Object));
    }

    [Fact, GlobalLock(Reason = "Uses the drain mode cache and the log provider")]
    public void Check_WarnsAFencedServer_OnceThreeChecksInARowCameAfterAGap_NotBefore()
    {
        RecordingLogProvider recording = new();
        DeploymentFencingProcess older = new("v1", 1);
        Check(older, "old-server", 0);
        Check(new DeploymentFencingProcess("v2", 2), "new-server", 5);

        int warningsAfterTwoGaps;
        using (recording.Use())
        {
            // 23 seconds apart, more than one and a half check intervals: each check comes after a gap
            Check(older, "old-server", 23);
            Check(older, "old-server", 46);
            warningsAfterTwoGaps = GapWarnings(recording).Length;

            Check(older, "old-server", 69);
        }

        Assert.Equal(0, warningsAfterTwoGaps);
        RecordingLogProvider.LogEntry warning = Assert.Single(GapWarnings(recording));
        Assert.Equal(LogLevel.Warn, warning.Level);
        Assert.Equal(
            "The last 3 checks of this server of deployment 'v1' each came too long after the one before: the last one " +
            "after 00:00:23, with a check interval of 00:00:15 and at most 00:00:22.5000000 between two checks to watch " +
            "without a gap. It therefore can't judge whether the current deployment 'v2' is silent, and will not take " +
            "over from it while this lasts. Raise DeploymentFencing.CheckInterval, and TakeOverAfter with it, or fix " +
            "the latency of the storage, so that a check takes less than half a check interval.",
            warning.Message);
    }

    [Fact, GlobalLock(Reason = "Uses the drain mode cache and the log provider")]
    public void Check_RepeatsTheWarningAboutItsGaps_AtMostOnceAnHour()
    {
        RecordingLogProvider recording = new();
        DeploymentFencingProcess older = new("v1", 1);
        Check(older, "old-server", 0);
        Check(new DeploymentFencingProcess("v2", 2), "new-server", 5);

        int warningsWithinTheHour;
        using (recording.Use())
        {
            // The first warning comes with the check at 69; the check at 3657 is the last one within the hour after it
            foreach (int i in Enumerable.Range(1, 159))
            {
                Check(older, "old-server", 23 * i);
            }

            warningsWithinTheHour = GapWarnings(recording).Length;
            Check(older, "old-server", 23 * 160);
        }

        Assert.Equal(1, warningsWithinTheHour);
        RecordingLogProvider.LogEntry[] warnings = GapWarnings(recording);
        Assert.Equal(2, warnings.Length);
        Assert.StartsWith("The last 160 checks of this server of deployment 'v1'", warnings[1].Message, StringComparison.Ordinal);
        Assert.Equal("v2", DeploymentFencing.GetCurrentDeployment(_storage.Object));
    }

    [Fact, GlobalLock(Reason = "Uses the drain mode cache and the log provider")]
    public void Check_CountsItsGapsFromTheStart_AfterACheckThatCameInTime()
    {
        RecordingLogProvider recording = new();
        DeploymentFencingProcess older = new("v1", 1);
        Check(older, "old-server", 0);
        Check(new DeploymentFencingProcess("v2", 2), "new-server", 5);

        int warningsBeforeTheThirdGapInARow;
        using (recording.Use())
        {
            // Two gaps, a check 15 seconds after the one before, then two gaps again: never three in a row
            Check(older, "old-server", 23);
            Check(older, "old-server", 46);
            Check(older, "old-server", 61);
            Check(older, "old-server", 84);
            Check(older, "old-server", 107);
            warningsBeforeTheThirdGapInARow = GapWarnings(recording).Length;

            Check(older, "old-server", 130);
        }

        Assert.Equal(0, warningsBeforeTheThirdGapInARow);
        RecordingLogProvider.LogEntry warning = Assert.Single(GapWarnings(recording));
        Assert.StartsWith("The last 3 checks of this server of deployment 'v1'", warning.Message, StringComparison.Ordinal);
        Assert.Contains("the last one after 00:00:23,", warning.Message, StringComparison.Ordinal);
    }

    [Fact, GlobalLock(Reason = "Uses the drain mode cache and the log provider")]
    public void Check_DoesNotRepeatTheWarningAboutItsGaps_WhenTheyComeBackWithinTheHour()
    {
        RecordingLogProvider recording = new();
        DeploymentFencingProcess older = new("v1", 1);
        Check(older, "old-server", 0);
        Check(new DeploymentFencingProcess("v2", 2), "new-server", 5);

        using (recording.Use())
        {
            // Three gaps, a check in time, then three gaps again: a server whose checks are sometimes in time
            // would otherwise repeat the warning every few checks
            Check(older, "old-server", 23);
            Check(older, "old-server", 46);
            Check(older, "old-server", 69);
            Check(older, "old-server", 84);
            Check(older, "old-server", 107);
            Check(older, "old-server", 130);
            Check(older, "old-server", 153);
        }

        Assert.Single(GapWarnings(recording));
    }

    [Fact, GlobalLock(Reason = "Uses the drain mode cache and the log provider")]
    public void Check_DoesNotWarnAboutTheGaps_OfAServerThatIsNotFenced()
    {
        RecordingLogProvider recording = new();
        DeploymentFencingProcess current = new("v2", 2);
        Check(new DeploymentFencingProcess("v1", 1), "old-server", 0);
        Check(current, "new-server", 5);

        bool[] fenced;
        using (recording.Use())
        {
            // The server of the current deployment has nothing to take over, however far apart its checks are
            fenced = [.. Enumerable.Range(1, 10).Select(i => Check(current, "new-server", 5 + (23 * i)))];
        }

        Assert.All(fenced, static x => Assert.False(x));
        Assert.DoesNotContain(recording.Entries, static x => x.Level == LogLevel.Warn);
    }

    [Fact, GlobalLock(Reason = "Uses the drain mode cache")]
    public void Check_ThatFails_DoesNotCountAsACheck_SoTheGapStaysOpen()
    {
        bool storageDown = false;
        _storage.Connection.When(() => storageDown).Setup(x => x.CreateWriteTransaction()).Throws(new InvalidOperationException("storage down"));
        DeploymentFencingProcess older = new("v1", 1);
        Check(older, "old-server", 0);
        Check(new DeploymentFencingProcess("v2", 2), "new-server", 5);
        Check(older, "old-server", 10);

        // The check at 25 reads the hash but can't write: the last check that went through is the one at 10
        storageDown = true;
        Assert.Throws<InvalidOperationException>(() => Check(older, "old-server", 25));
        storageDown = false;

        bool[] fenced =
        [
            Check(older, "old-server", 40),
            Check(older, "old-server", 55),
            Check(older, "old-server", 70),
            Check(older, "old-server", 85),
        ];

        // 30 seconds after the last check that succeeded is a gap, so v2 is watched afresh from 40
        Assert.Equal([true, true, true, false], fenced);
    }

    [Fact, GlobalLock(Reason = "Uses the drain mode cache")]
    public void Check_AfterOnlyChecksThatFailed_WatchesAfresh()
    {
        bool storageDown = true;
        _storage.Connection.When(() => storageDown).Setup(x => x.CreateWriteTransaction()).Throws(new InvalidOperationException("storage down"));
        _storage.Hashes[DeploymentFencing.HashKey] = new Dictionary<string, string>
        {
            ["Current"] = "v2",
            ["Seen:v2"] = DeploymentFencing.FormatSeen(Now, 7, TimeSpan.FromSeconds(15)),
            ["Seen:v1"] = DeploymentFencing.FormatSeen(Now, 3, TimeSpan.FromSeconds(15)),
        };
        DeploymentFencingProcess started = new("v1");

        // The first check of a server that just started reads the hash, then fails; the next one comes 70 seconds later
        Assert.Throws<InvalidOperationException>(() => Check(started, "old-server", 0));
        storageDown = false;

        bool[] fenced =
        [
            Check(started, "old-server", 70),
            Check(started, "old-server", 85),
            Check(started, "old-server", 100),
            Check(started, "old-server", 115),
        ];

        Assert.Equal([true, true, true, false], fenced);
    }

    [Fact, GlobalLock(Reason = "Uses the drain mode cache")]
    public void Gate_KeepsAWorkerFromRunning_UntilTheFirstCheckHasPausedTheFencedServer()
    {
        Check(new DeploymentFencingProcess("v2", 2), "new-server", 0);
        DeploymentFencingProcess older = new("v1", 1);
        bool? pausedWhenTheWorkerRan = null;
        Mock<IBackgroundProcess> worker = new();
        worker
            .Setup(x => x.Execute(It.IsAny<BackgroundProcessContext>()))
            .Callback(() => pausedWhenTheWorkerRan = DrainMode.Read(_storage.Connection.Object).IsServerPaused("old-server"));
        GatedWorker gated = new(worker.Object, older.Gate);

        // The server is stopping, so the wait ends at once: a worker that didn't wait would have run
        Assert.ThrowsAny<OperationCanceledException>(() => gated.Execute(Context("old-server", new CancellationToken(canceled: true))));
        bool openBeforeTheCheck = older.Gate.IsOpen;
        worker.Verify(x => x.Execute(It.IsAny<BackgroundProcessContext>()), Times.Never);

        bool fenced = Check(older, "old-server", 5);
        gated.Execute(Context("old-server", CancellationToken.None));

        Assert.False(openBeforeTheCheck);
        Assert.True(fenced);
        Assert.True(older.Gate.IsOpen);
        Assert.True(pausedWhenTheWorkerRan);
        worker.Verify(x => x.Execute(It.IsAny<BackgroundProcessContext>()), Times.Once);
    }

    [Fact, GlobalLock(Reason = "Uses the drain mode cache")]
    public void Gate_StaysClosed_WhileTheFirstCheckFails_AndOpensWithTheFirstCheckThatSucceeds()
    {
        bool storageDown = true;
        _storage.Connection.When(() => storageDown).Setup(x => x.CreateWriteTransaction()).Throws(new InvalidOperationException("storage down"));
        _storage.Hashes[DeploymentFencing.HashKey] = new Dictionary<string, string>
        {
            ["Current"] = "v2",
            ["Seen:v2"] = DeploymentFencing.FormatSeen(Now, 7, TimeSpan.FromSeconds(15)),
            ["Seen:v1"] = DeploymentFencing.FormatSeen(Now, 3, TimeSpan.FromSeconds(15)),
        };
        DeploymentFencingProcess started = new("v1");
        Mock<IBackgroundProcess> worker = new();
        GatedWorker gated = new(worker.Object, started.Gate);

        // The check reads the hash, then fails: it has decided nothing, and the server isn't paused
        Assert.Throws<InvalidOperationException>(() => Check(started, "old-server", 0));
        bool openAfterTheFailure = started.Gate.IsOpen;
        Assert.ThrowsAny<OperationCanceledException>(() => gated.Execute(Context("old-server", new CancellationToken(canceled: true))));
        worker.Verify(x => x.Execute(It.IsAny<BackgroundProcessContext>()), Times.Never);

        storageDown = false;
        bool fenced = Check(started, "old-server", 1);

        Assert.False(openAfterTheFailure);
        Assert.True(fenced);
        Assert.True(started.Gate.IsOpen);
        Assert.True(DrainMode.Read(_storage.Connection.Object).IsServerPaused("old-server"));
    }

    [Fact, GlobalLock(Reason = "Uses the drain mode cache")]
    public void Gate_Opens_ForAServerOfTheCurrentDeployment_AndStaysOpen()
    {
        DeploymentFencingProcess current = new("v1");
        Mock<IBackgroundProcess> worker = new();
        GatedWorker gated = new(worker.Object, current.Gate);

        Assert.False(Check(current, "server-1", 0));
        gated.Execute(Context("server-1", CancellationToken.None));

        // Fenced later by a newer deployment: the pause holds its jobs from then on, the gate isn't closed again
        Check(new DeploymentFencingProcess("v2"), "new-server", 5);
        Assert.True(Check(current, "server-1", 15));
        gated.Execute(Context("server-1", CancellationToken.None));

        Assert.True(current.Gate.IsOpen);
        Assert.False(DrainMode.Read(_storage.Connection.Object).IsServerPaused("new-server"));
        worker.Verify(x => x.Execute(It.IsAny<BackgroundProcessContext>()), Times.Exactly(2));
    }

    [Fact, GlobalLock(Reason = "Uses the log provider")]
    public void Gate_SaysOnce_ThatTheServerWaitsForItsFirstCheck()
    {
        RecordingLogProvider recording = new();
        DeploymentFencingGate gate = new("v1");

        using (recording.Use())
        {
            Assert.ThrowsAny<OperationCanceledException>(() => gate.Wait(Context("server-1", new CancellationToken(canceled: true))));
            Assert.ThrowsAny<OperationCanceledException>(() => gate.Wait(Context("server-1", new CancellationToken(canceled: true))));
            gate.Open();
            gate.Wait(Context("server-1", CancellationToken.None));
        }

        RecordingLogProvider.LogEntry entry = Assert.Single(recording.Entries);
        Assert.Equal(LogLevel.Debug, entry.Level);
        Assert.Equal(
            "Server 'server-1' takes no jobs until its first deployment check has decided whether deployment 'v1' is fenced.",
            entry.Message);
    }

    [Fact, StaticLock]
    public void UseDeploymentFencing_WithAnOrder_SetsBoth_AndTheProcessTakesTheOrder()
    {
        IGlobalConfiguration configuration = new Mock<IGlobalConfiguration>().Object;

        configuration.UseDeploymentFencing("release-42", 42);

        Assert.Equal("release-42", DeploymentFencing.DeploymentId);
        Assert.Equal(42, DeploymentFencing.DeploymentOrder);
        Assert.Equal(42, new DeploymentFencingProcess("release-42").DeploymentOrder);
        Assert.Equal("Deployment fencing (release-42 (order 42))", new DeploymentFencingProcess("release-42").ToString());

        configuration.UseDeploymentFencing("release-0", 0);
        Assert.Equal(0, DeploymentFencing.DeploymentOrder);

        // Without an order, the one set before no longer applies
        configuration.UseDeploymentFencing("release-43");
        Assert.Null(DeploymentFencing.DeploymentOrder);
        Assert.Null(new DeploymentFencingProcess("release-43").DeploymentOrder);
    }

    [Fact, StaticLock]
    public void UseDeploymentFencing_RefusesANegativeOrder_AndChangesNothing()
    {
        IGlobalConfiguration configuration = new Mock<IGlobalConfiguration>().Object;
        configuration.UseDeploymentFencing("release-42", 42);

        Assert.Throws<ArgumentOutOfRangeException>("deploymentOrder", () => configuration.UseDeploymentFencing("release-43", -1));
        Assert.Throws<ArgumentException>("deploymentId", () => configuration.UseDeploymentFencing(" ", 43));

        Assert.Equal("release-42", DeploymentFencing.DeploymentId);
        Assert.Equal(42, DeploymentFencing.DeploymentOrder);
    }

    [Theory]
    [InlineData("v2", null, null, null, "v2")]
    [InlineData("v2", null, "v2", null, "v2")]
    [InlineData("v1", null, "v2", null, "v1 (v2)")]
    [InlineData("v2", 42L, "v2", 42L, "v2 (order 42)")]
    [InlineData("v1", 41L, "v2", 42L, "v1 (order 41) (v2, order 42)")]
    [InlineData("v1", null, "v2", 42L, "v1 (v2, order 42)")]
    [InlineData("v1", 41L, "v2", null, "v1 (order 41) (v2)")]
    public void AboutPage_ShowsTheOrderWithTheDeployment(string deployment, long? order, string current, long? currentOrder, string expected)
    {
        Assert.Equal(expected, Masterly.NightShift.Dashboard.Pages.AboutPage.DescribeDeployment(deployment, order, current, currentOrder));
    }

    [Fact, StaticLock]
    public void AboutPage_ReadsTheCurrentDeploymentAndItsOrder()
    {
        Masterly.NightShift.Core.Tests.Dashboard.Pages.PageRenderer renderer = new();
        renderer.Connection.Setup(x => x.GetAllEntriesFromHash(DeploymentFencing.HashKey)).Returns(new Dictionary<string, string>
        {
            ["Current"] = "v2",
            ["Order:v2"] = "42",
        });
        DeploymentFencing.DeploymentId = "v1";
        DeploymentFencing.DeploymentOrder = 41;

        string html = renderer.Render(new Masterly.NightShift.Dashboard.Pages.AboutPage());

        Assert.Contains("v1 (order 41) (v2, order 42)", html, StringComparison.Ordinal);
    }

    private Dictionary<string, string> Hash => _storage.Hashes[DeploymentFencing.HashKey];

    private BackgroundProcessContext Context(string serverId, CancellationToken stoppingToken)
    {
        return new BackgroundProcessContext(
            serverId,
            _storage.Object,
            new Dictionary<string, object>(),
            Guid.NewGuid(),
            stoppingToken,
            CancellationToken.None,
            CancellationToken.None);
    }

    // One check of a server, so many seconds after the start. The server's wall clock may be ahead of or behind the
    // others, and its monotonic clock counts the same seconds from an origin of its own
    private bool Check(DeploymentFencingProcess process, string serverId, double seconds, TimeSpan clockDifference = default, TimeSpan uptimeOrigin = default)
    {
        return process.Check(
            _storage.Connection.Object,
            serverId,
            Now.AddSeconds(seconds) + clockDifference,
            uptimeOrigin + TimeSpan.FromSeconds(seconds));
    }

    // A check of a server configured with another check interval, and a take-over time that suits it
    private void CheckEvery(TimeSpan checkInterval, DeploymentFencingProcess process, string serverId, double seconds)
    {
        DeploymentFencing.CheckInterval = checkInterval;
        DeploymentFencing.TakeOverAfter = checkInterval + checkInterval;
        Check(process, serverId, seconds);
        DeploymentFencing.CheckInterval = TimeSpan.FromSeconds(15);
        DeploymentFencing.TakeOverAfter = TimeSpan.FromSeconds(30);
    }

    // The warnings of a fenced server whose own checks are too far apart for it to take over
    private static RecordingLogProvider.LogEntry[] GapWarnings(RecordingLogProvider recording)
    {
        return [.. recording.Entries.Where(static x => x.Message.Contains("will not take over", StringComparison.Ordinal))];
    }

    private static List<T> Simulate<T>(int rounds, Func<int, T> round)
    {
        return [.. Enumerable.Range(0, rounds).Select(round)];
    }
}
