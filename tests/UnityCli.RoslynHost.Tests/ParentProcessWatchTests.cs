using System.Diagnostics;
using UnityCliBridge.RoslynHost;

namespace UnityCli.RoslynHost.Tests;

public sealed class ParentProcessWatchTests
{
    private static readonly DateTime Started = new DateTime(2026, 10, 21, 1, 2, 3, DateTimeKind.Utc);
    private static readonly TimeSpan Fast = TimeSpan.FromMilliseconds(20);

    [Fact]
    public void FiresOnce_WhenTheParentStopsBeingAlive()
    {
        bool alive = true;
        var reasons = new List<string>();
        using ParentProcessWatch watch = ParentProcessWatch.Start(7, null, Fast, _ => alive ? new ParentProbe(true, Started) : ParentProbe.Dead, reasons.Add);

        Thread.Sleep(100);
        Assert.False(watch.IsGone);
        alive = false;

        Assert.True(SpinWait.SpinUntil(() => watch.IsGone, TimeSpan.FromSeconds(5)));
        Thread.Sleep(100);
        Assert.Equal("parent process 7 exited", Assert.Single(reasons));
    }

    [Fact]
    public void FiresImmediately_WhenTheParentIsNotRunningAtStart()
    {
        var reasons = new List<string>();

        using ParentProcessWatch watch = ParentProcessWatch.Start(7, null, Fast, _ => ParentProbe.Dead, reasons.Add);

        Assert.True(watch.IsGone);
        Assert.Contains("not running", Assert.Single(reasons));
    }

    [Fact]
    public void Fires_WhenThePidIsReusedByAProcessWithAnotherStartTime()
    {
        DateTime start = Started;
        var reasons = new List<string>();
        using ParentProcessWatch watch = ParentProcessWatch.Start(7, null, Fast, _ => new ParentProbe(true, start), reasons.Add);

        start = Started.AddSeconds(30);

        Assert.True(SpinWait.SpinUntil(() => watch.IsGone, TimeSpan.FromSeconds(5)));
        Assert.Contains("different process", watch.GoneReason);
    }

    [Fact]
    public void ExpectedStartTime_WithinTolerance_IsAccepted_FarOff_Fires()
    {
        long startMs = new DateTimeOffset(Started).ToUnixTimeMilliseconds();

        using ParentProcessWatch close = ParentProcessWatch.Start(7, startMs + 900, TimeSpan.FromHours(1), _ => new ParentProbe(true, Started), _ => { });
        using ParentProcessWatch far = ParentProcessWatch.Start(7, startMs + 60000, TimeSpan.FromHours(1), _ => new ParentProbe(true, Started), _ => { });

        Assert.False(close.IsGone);
        Assert.True(far.IsGone);
        Assert.Contains("pid reused", far.GoneReason);
    }

    [Fact]
    public void UnknownStartTime_FallsBackToExistenceOnly()
    {
        using ParentProcessWatch watch = ParentProcessWatch.Start(7, 12345, Fast, _ => new ParentProbe(true, null), _ => { });

        Thread.Sleep(100);
        Assert.False(watch.IsGone);
    }

    [Fact]
    public void Dispose_StopsFurtherChecks()
    {
        bool alive = true;
        int fired = 0;
        ParentProcessWatch watch = ParentProcessWatch.Start(7, null, Fast, _ => alive ? new ParentProbe(true, Started) : ParentProbe.Dead, _ => Interlocked.Increment(ref fired));

        watch.Dispose();
        alive = false;
        Thread.Sleep(150);

        Assert.Equal(0, fired);
    }

    [Fact]
    public void ProbeProcess_SeesTheCurrentProcess_AndNotAReapedOne()
    {
        ParentProbe self = ParentProcessWatch.ProbeProcess(Environment.ProcessId);
        Assert.True(self.Alive);
        Assert.NotNull(self.StartTimeUtc);

        using Process sleeper = HostTestSupport.StartSleeper();
        int pid = sleeper.Id;
        Assert.True(ParentProcessWatch.ProbeProcess(pid).Alive);
        HostTestSupport.KillAndReap(sleeper);

        Assert.False(ParentProcessWatch.ProbeProcess(pid).Alive);
    }
}
