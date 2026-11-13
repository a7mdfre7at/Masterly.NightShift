using System.Diagnostics;
using Masterly.NightShift.Server;

namespace Masterly.NightShift.Core.Tests.Server;

public class WorkerWaitsFacts
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(10);

    // The clock of the machine, moved by the test
    private long _now = Stopwatch.Frequency * 1000;
    private readonly WorkerWaits _waits;

    public WorkerWaitsFacts()
    {
        _waits = new WorkerWaits(Budget, Window, TimeSpan.FromSeconds(5), () => _now);
    }

    [Fact]
    public void Defaults_GiveAPoolTwoSecondsInTen_AndAJobFiveSeconds()
    {
        WorkerWaits waits = new();

        Assert.Equal(TimeSpan.FromSeconds(2), waits.Budget);
        Assert.Equal(TimeSpan.FromSeconds(10), waits.Window);
        Assert.Equal(TimeSpan.FromSeconds(5), waits.MaxPerJob);
        Assert.True(waits.CanWait);
    }

    [Fact]
    public void Allow_LetsAWorkerThatKeepsAJobWait_ForThePauseItAsksFor()
    {
        Assert.Equal(TimeSpan.FromMilliseconds(250), Begin(TimeSpan.FromMilliseconds(250)));
    }

    [Fact]
    public void Waits_UseUpTheBudget_WhileNoWorkerOfThePoolLooksForAJob()
    {
        // The only worker of a pool, or all of them at once: nothing else is taken meanwhile
        for (int i = 0; i < 8; i++)
        {
            Assert.True(_waits.CanWait);
            Wait(TimeSpan.FromMilliseconds(250));
        }

        // Two seconds were waited: no more for now
        Assert.False(_waits.CanWait);
        Assert.Equal(TimeSpan.Zero, Begin(TimeSpan.FromMilliseconds(250)));
    }

    [Fact]
    public void Allow_CutsAWaitShort_AtWhatIsLeftOfTheBudget_WhenNoWorkerLooksForAJob()
    {
        Wait(TimeSpan.FromMilliseconds(1900));

        Assert.InRange(Begin(TimeSpan.FromMilliseconds(250)), TimeSpan.FromMilliseconds(99), TimeSpan.FromMilliseconds(101));
    }

    [Fact]
    public void Waits_CostNothing_WhileAnotherWorkerOfThePoolLooksForAJob()
    {
        // That worker takes whatever becomes runnable in a queue of the pool: nobody waits for the one that waits
        _waits.EnterFetch();

        for (int i = 0; i < 100; i++)
        {
            Wait(TimeSpan.FromMilliseconds(250));
        }

        Assert.True(_waits.CanWait);
        Assert.Equal(TimeSpan.FromMilliseconds(250), Begin(TimeSpan.FromMilliseconds(250)));
    }

    [Fact]
    public void Waits_AreCounted_FromTheMomentTheLastWorkerThatLookedForAJobHasOne()
    {
        _waits.EnterFetch();
        Assert.Equal(TimeSpan.FromMilliseconds(250), Begin(TimeSpan.FromMilliseconds(250)));
        Pass(TimeSpan.FromMilliseconds(100));

        // The other worker found a job: from here on, this one's wait keeps the pool from every other job
        _waits.LeaveFetch();
        Pass(TimeSpan.FromMilliseconds(150));
        _waits.Leave();

        Wait(TimeSpan.FromMilliseconds(1800));
        Assert.True(_waits.CanWait);
        Wait(TimeSpan.FromMilliseconds(60));
        Assert.False(_waits.CanWait);
    }

    [Fact]
    public void Waits_OfSeveralWorkersAtOnce_AreCountedOnce_AsTheTimeThePoolTookNoOtherJob()
    {
        // Four workers wait side by side for a second: the other jobs waited one second, not four
        for (int i = 0; i < 4; i++)
        {
            Assert.True(Begin(TimeSpan.FromSeconds(1)) > TimeSpan.Zero);
        }

        Pass(TimeSpan.FromSeconds(1));

        for (int i = 0; i < 4; i++)
        {
            _waits.Leave();
        }

        Assert.True(_waits.CanWait);
        Wait(TimeSpan.FromMilliseconds(900));
        Assert.True(_waits.CanWait);
        Wait(TimeSpan.FromMilliseconds(150));
        Assert.False(_waits.CanWait);
    }

    [Fact]
    public void Enter_IsRefused_OnceTheBudgetIsUsedUp_AndCountsNothingThen()
    {
        Wait(TimeSpan.FromSeconds(2));

        Assert.False(_waits.Enter());
        Pass(TimeSpan.FromSeconds(12));
        Assert.True(_waits.CanWait);
    }

    [Fact]
    public void Keeping_IsCounted_BetweenThePausesToo()
    {
        // The looks at the job, between two pauses, keep it from the other jobs as much as the pauses do
        Assert.True(_waits.Enter());
        Assert.Equal(TimeSpan.FromMilliseconds(100), _waits.Allow(TimeSpan.FromMilliseconds(100)));
        Pass(TimeSpan.FromMilliseconds(100));
        Pass(TimeSpan.FromMilliseconds(1500));
        Assert.InRange(_waits.Allow(TimeSpan.FromMilliseconds(500)), TimeSpan.FromMilliseconds(399), TimeSpan.FromMilliseconds(401));
        Pass(TimeSpan.FromMilliseconds(400));
        _waits.Leave();

        Assert.False(_waits.CanWait);
    }

    [Fact]
    public void Budget_ComesBack_AsTheWindowMovesOn_AndNoSooner()
    {
        Wait(TimeSpan.FromSeconds(2));
        Assert.False(_waits.CanWait);

        // Ten seconds hold what was waited, counted in slots: a little longer rather than shorter
        Pass(TimeSpan.FromSeconds(8));
        Assert.False(_waits.CanWait);

        Pass(TimeSpan.FromSeconds(3.5));
        Assert.True(_waits.CanWait);
    }

    [Fact]
    public void Pool_NeverWaitsLongerThanItsBudget_InAnyWindow_HoweverTheWaitsFall()
    {
        // A minute of workers that wait whenever they may, a quarter of a second at a time, and do something
        // else for a moment in between
        List<(long At, long Waited)> waits = [];

        for (int i = 0; i < 2000 && _now < Stopwatch.Frequency * 1060; i++)
        {
            TimeSpan allowed = Begin(TimeSpan.FromMilliseconds(250));
            if (allowed > TimeSpan.Zero)
            {
                Pass(allowed);
                _waits.Leave();
                waits.Add((_now, (long)(allowed.TotalSeconds * Stopwatch.Frequency)));
            }

            Pass(TimeSpan.FromMilliseconds(37));
        }

        Assert.NotEmpty(waits);

        foreach ((long at, _) in waits)
        {
            long inWindow = waits.Where(x => x.At <= at && x.At > at - (Stopwatch.Frequency * 10)).Sum(static x => x.Waited);
            Assert.InRange(inWindow, 0, (long)(Stopwatch.Frequency * 2.01));
        }

        // And it does wait again and again: about a fifth of the time
        Assert.InRange(waits.Sum(static x => x.Waited), Stopwatch.Frequency * 8, Stopwatch.Frequency * 13);
    }

    // A worker waits, and no other looks for a job meanwhile unless the test said so
    private void Wait(TimeSpan time)
    {
        Assert.True(Begin(time) > TimeSpan.Zero);
        Pass(time);
        _waits.Leave();
    }

    // A worker keeps a job and asks to pause: how long it may, or nothing (and it doesn't keep the job)
    private TimeSpan Begin(TimeSpan pause)
    {
        if (!_waits.Enter())
        {
            return TimeSpan.Zero;
        }

        TimeSpan allowed = _waits.Allow(pause);
        if (allowed <= TimeSpan.Zero)
        {
            _waits.Leave();
        }

        return allowed;
    }

    private void Pass(TimeSpan time) => _now += (long)(time.TotalSeconds * Stopwatch.Frequency);
}
