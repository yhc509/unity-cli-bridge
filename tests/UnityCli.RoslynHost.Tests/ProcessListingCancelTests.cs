using System.Diagnostics;
using System.Runtime.InteropServices;
using UnityCli.Protocol;
using UnityCliBridge.Bridge.Editor;

namespace UnityCli.RoslynHost.Tests;

/// <summary>
/// A process listing must return when a reload cancels it. The calling thread waits on the line
/// event, and the child is killed, so the read is not still inside a pipe when the domain unloads.
/// </summary>
public sealed class ProcessListingCancelTests
{
    [Fact]
    public void LocalCancel_UnblocksTheRead()
    {
        using IDisposable scope = RoslynHostProcessInspector.AcquireListingScope();
        using var cancel = new CancellationTokenSource();
        string? result = "pending";
        var thread = new Thread(() =>
        {
            result = RoslynHostProcessInspector.RunListing(SlowFile(), SlowArguments(), cancel.Token, false, true);
        })
        {
            IsBackground = true,
            Name = "ucb-listing-cancel-test",
        };
        thread.Start();
        try
        {
            Assert.True(WaitFor(() => RoslynHostProcessInspector.InFlightListingCount > 0, 5000), "listing did not start");
            var watch = Stopwatch.StartNew();
            cancel.Cancel();
            Assert.True(thread.Join(3000), "the listing thread was still blocked after cancel");
            watch.Stop();
            Assert.True(watch.ElapsedMilliseconds < 2000, "cancel took " + watch.ElapsedMilliseconds + " ms");
            Assert.Null(result);
            Assert.Equal(0, RoslynHostProcessInspector.InFlightListingCount);
        }
        finally
        {
            cancel.Cancel();
            thread.Join(2000);
        }
    }

    [Fact]
    public void DomainCancel_UnblocksTheRead_AndALaterListingStillWorks()
    {
        using IDisposable scope = RoslynHostProcessInspector.AcquireListingScope();
        string? result = "pending";
        var thread = new Thread(() =>
        {
            result = RoslynHostProcessInspector.RunListing(SlowFile(), SlowArguments(), CancellationToken.None, true, true);
        })
        {
            IsBackground = true,
            Name = "ucb-listing-domain-cancel-test",
        };
        thread.Start();
        try
        {
            Assert.True(WaitFor(() => RoslynHostProcessInspector.InFlightListingCount > 0, 5000), "listing did not start");
            var watch = Stopwatch.StartNew();
            RoslynHostProcessInspector.CancelInFlightListings();
            Assert.True(thread.Join(3000), "the listing thread was still blocked after the reload cancel");
            watch.Stop();
            Assert.True(watch.ElapsedMilliseconds < 2000, "domain cancel took " + watch.ElapsedMilliseconds + " ms");
            Assert.Null(result);
            Assert.Equal(0, RoslynHostProcessInspector.InFlightListingCount);

            string? echo = RoslynHostProcessInspector.RunListing(EchoFile(), EchoArguments(), CancellationToken.None, true, true);
            Assert.NotNull(echo);
            Assert.Contains("hello", echo, StringComparison.Ordinal);
        }
        finally
        {
            RoslynHostProcessInspector.CancelInFlightListings();
            thread.Join(2000);
        }
    }

    [Fact]
    public void ExitedChild_WithStdoutStillOpen_ReturnsOutputWithoutWaitingForEof()
    {
        using IDisposable scope = RoslynHostProcessInspector.AcquireListingScope();
        var watch = Stopwatch.StartNew();
        string? output = RoslynHostProcessInspector.RunListing(
            HolderFile(),
            HolderArguments(),
            CancellationToken.None,
            true,
            true,
            8000,
            out bool cancelled,
            out bool failed);
        watch.Stop();

        Assert.False(cancelled);
        Assert.False(failed, output ?? "null");
        Assert.NotNull(output);
        Assert.Contains("hello", output, StringComparison.Ordinal);
        // The grandchild holds the stdout write end for 15 s. Waiting for EOF would miss this bound.
        Assert.True(watch.ElapsedMilliseconds < 3000, "listing waited " + watch.ElapsedMilliseconds + " ms for EOF");
        _ = scope;
    }

    [Fact]
    public void Timeout_IsAFailure_NotAnEmptyListing()
    {
        using IDisposable scope = RoslynHostProcessInspector.AcquireListingScope();
        var watch = Stopwatch.StartNew();
        string? output = RoslynHostProcessInspector.RunListing(
            SlowFile(),
            SlowArguments(),
            CancellationToken.None,
            true,
            true,
            400,
            out bool cancelled,
            out bool failed);
        watch.Stop();

        Assert.False(cancelled);
        Assert.True(failed);
        Assert.Null(output);
        Assert.True(watch.ElapsedMilliseconds < 2000, "timeout took " + watch.ElapsedMilliseconds + " ms");
        _ = scope;
    }

    [Fact]
    public void Spawn_WaitsUntilTheListingPipesClose()
    {
        using IDisposable pipes = ProcessSpawnGate.EnterListing();
        bool started = false;
        var thread = new Thread(() => ProcessSpawnGate.Run(() => started = true))
        {
            IsBackground = true,
            Name = "ucb-spawn-gate-test",
        };
        thread.Start();
        try
        {
            Thread.Sleep(150);
            Assert.False(started);
        }
        finally
        {
            pipes.Dispose();
        }

        Assert.True(thread.Join(2000), "spawn was still waiting after the listing pipes closed");
        Assert.True(started);
    }

    private static string HolderFile()
    {
        return RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "cmd.exe" : "/bin/sh";
    }

    private static string HolderArguments()
    {
        // The shell prints and exits. The background child inherits stdout, so EOF does not arrive.
        return RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? "/d /c \"echo hello& start /b ping -n 15 127.0.0.1 & exit /b 0\""
            : "-c \"echo hello; sleep 15 &\"";
    }

    private static string SlowFile()
    {
        return RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "ping.exe" : "/bin/sh";
    }

    private static string SlowArguments()
    {
        return RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "-n 40 127.0.0.1" : "-c \"exec sleep 30\"";
    }

    private static string EchoFile()
    {
        return RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "cmd.exe" : "/bin/echo";
    }

    private static string EchoArguments()
    {
        return RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "/d /c echo hello" : "hello";
    }

    private static bool WaitFor(Func<bool> condition, int timeoutMs)
    {
        var watch = Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < timeoutMs)
        {
            if (condition())
            {
                return true;
            }

            Thread.Sleep(10);
        }

        return condition();
    }
}
