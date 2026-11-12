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
        Assert.True(Check(old, "old-server", 20));
        Assert.True(Check(old, "old-server", 35));

        // The old server has watched v2 without a sign of life for more than TakeOverAfter
        bool fenced = Check(old, "old-server", 120);

        Assert.False(fenced);
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
        Assert.False(Check(older, "old-server", 120));
        Assert.Equal("v1", DeploymentFencing.GetCurrentDeployment(_storage.Object));

        Assert.False(Check(newer, "new-server", 125));
        Assert.True(Check(older, "old-server", 130));

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

        // The stray server has watched v2 without a sign of life for more than TakeOverAfter
        Assert.False(Check(stray, "stray-server", 120));

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
        Assert.False(Check(first, "new-server", 120));
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

        bool[] fenced = [Check(process, "server-1", 0), Check(process, "server-1", 30), Check(process, "server-1", 45)];

        Assert.Equal([true, true, false], fenced);
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
        Assert.Equal($"{JobHelper.SerializeDateTime(Now.AddSeconds(45))}|3|15000", Hash["Seen:v3"]);
        RecordingLogProvider.LogEntry warning = Assert.Single(recording.Entries, static x => x.Level == LogLevel.Warn);
        Assert.Contains("couldn't be removed", warning.Message, StringComparison.Ordinal);
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

    private static List<T> Simulate<T>(int rounds, Func<int, T> round)
    {
        return [.. Enumerable.Range(0, rounds).Select(round)];
    }
}
