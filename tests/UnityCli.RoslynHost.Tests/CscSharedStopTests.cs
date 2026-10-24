using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using UnityCli.Protocol;
using UnityCliBridge.Bridge.Editor;
using FakeProcessTable = UnityCli.RoslynHost.Tests.CscSharedServerTests.FakeProcessTable;

namespace UnityCli.RoslynHost.Tests;

/// <summary>
/// The csc /shared server stop on reload/quit (main thread, no client process to wait for) and
/// the background retire once the Roslyn host serves again.
/// </summary>
public sealed class CscSharedStopTests
{
    private const string ProjectHash = "d6887d898597";
    private static readonly string Pipe = CscSharedPipe.BuildName(ProjectHash, "6000.3.10f1");
    private static readonly string ServerLine = "/U/dotnet exec /U/VBCSCompiler.dll -pipename:" + Pipe;

    [Fact]
    public void Shutdown_WhereListingIsSlow_KillsTheKnownServerDirectly_WithoutListingOrAClient()
    {
        using var temp = new TempDir();
        var table = new FakeProcessTable { ListingIsSlow = true, Alive = { 432 }, OnPipe = { 432 }, Processes = { Proc(432, ServerLine) } };
        CscSharedServer server = UsedServer(temp, table, 432);
        int listed = table.ListCalls;

        server.Shutdown("editor-quit");

        Assert.Equal(new[] { 432 }, table.Killed);
        Assert.Equal(listed, table.ListCalls);
        Assert.Empty(table.Detached);
        Assert.False(File.Exists(server.RecordPath));
        CscSharedStatus status = server.Snapshot();
        Assert.Equal("stopped", status.State);
        Assert.Equal("editor-quit", status.Note);
        Assert.Equal(0, status.ServerPid);
        Assert.True(status.LastShutdownMs > 0);
    }

    [Fact]
    public void Shutdown_OnWindowsTimings_StaysUnder300MsWhenListingTakesSeconds()
    {
        using var temp = new TempDir();
        var table = new FakeProcessTable { ListingIsSlow = true, Alive = { 432 }, OnPipe = { 432 }, StartTicks = { [432] = 638_000_000_000_000_000 } };
        CscSharedServer server = UsedServer(temp, table, 432);
        table.Listing = () =>
        {
            Thread.Sleep(3500);
            return new[] { Proc(432, ServerLine) };
        };

        var watch = Stopwatch.StartNew();
        server.Shutdown("editor-quit");
        watch.Stop();

        Assert.True(watch.ElapsedMilliseconds < 300, "main-thread stop took " + watch.ElapsedMilliseconds + " ms");
        Assert.Equal(new[] { 432 }, table.Killed);
        Assert.Empty(table.Detached);
        Assert.False(File.Exists(server.RecordPath));
        Assert.True(server.Snapshot().LastShutdownMs < 300);
    }

    [Fact]
    public void Shutdown_WhereListingIsSlow_KnownServerAlreadyGone_LeavesNothingToKill()
    {
        using var temp = new TempDir();
        var table = new FakeProcessTable { ListingIsSlow = true, Alive = { 432 }, OnPipe = { 432 } };
        CscSharedServer server = UsedServer(temp, table, 432);
        lock (table.Alive)
        {
            table.Alive.Remove(432);
        }

        int listed = table.ListCalls;

        server.Shutdown("domain-reload");

        Assert.Empty(table.Killed);
        Assert.Equal(listed, table.ListCalls);
        Assert.Empty(table.Detached);
        Assert.False(File.Exists(server.RecordPath));
    }

    [Fact]
    public void Shutdown_WhereListingIsCheap_SweepsEveryServerOnOurPipe()
    {
        using var temp = new TempDir();
        var table = new FakeProcessTable
        {
            Alive = { 432, 433 },
            OnPipe = { 432 },
            Processes =
            {
                Proc(432, ServerLine),
                // A server another login session left on the same pipe name (named mutexes are per session).
                Proc(433, ServerLine),
                Proc(434, ServerLine + "0"),
                Proc(435, "/U/dotnet exec /U/VBCSCompiler.dll -pipename:Aq3bTL0ZJ9ExP4jQn2N1wSGx+Ko"),
            },
        };
        CscSharedServer server = UsedServer(temp, table, 432);
        int listed = table.ListCalls;

        server.Shutdown("domain-reload");

        Assert.Equal(new[] { 432, 433 }, table.Killed);
        Assert.Equal(listed + 1, table.ListCalls);
        Assert.Empty(table.Detached);
        Assert.False(File.Exists(server.RecordPath));
        Assert.Equal("domain-reload", server.Snapshot().Note);
    }

    [Fact]
    public void Shutdown_UnknownPid_ListsByPipeWhereListingIsCheap()
    {
        using var temp = new TempDir();
        var table = new FakeProcessTable
        {
            Alive = { 432 },
            Processes =
            {
                Proc(432, ServerLine),
                Proc(433, "/U/dotnet exec /U/VBCSCompiler.dll -pipename:Aq3bTL0ZJ9ExP4jQn2N1wSGx+Ko"),
                Proc(434, ServerLine + "0"),
            },
        };
        var server = new CscSharedServer(CscSharedServerTests.Options(temp.Path), table);
        Invoke(server, "EnsureRecord");

        server.Shutdown("domain-reload");

        Assert.Equal(new[] { 432 }, table.Killed);
        Assert.Equal(1, table.ListCalls);
        Assert.Empty(table.Detached);
        Assert.False(File.Exists(server.RecordPath));
    }

    [Fact]
    public void Shutdown_UnknownPidWhereListingIsSlow_StartsTheClientAndKeepsTheRecordForTheNextLoad()
    {
        using var temp = new TempDir();
        var table = new FakeProcessTable { ListingIsSlow = true, Alive = { 432 }, Processes = { Proc(432, ServerLine) } };
        CscSharedServerOptions options = WithToolchain(CscSharedServerTests.Options(temp.Path), temp.Path);
        var server = new CscSharedServer(options, table);
        Invoke(server, "EnsureRecord");

        var watch = Stopwatch.StartNew();
        server.Shutdown("editor-quit");
        watch.Stop();

        Assert.True(watch.ElapsedMilliseconds < 300, "main-thread stop took " + watch.ElapsedMilliseconds + " ms");
        Assert.Equal(0, table.ListCalls);
        Assert.Empty(table.Killed);
        ProcessStartInfo client = Assert.Single(table.Detached);
        Assert.Contains("-shutdown", client.Arguments);
        Assert.Contains("-pipename:" + Pipe, client.Arguments);
        Assert.True(File.Exists(server.RecordPath));
        Assert.Contains("shutdown client started", server.Snapshot().Note);

        // Next domain load (same editor): the record proves the leftover server is ours.
        var next = new CscSharedServer(CscSharedServerTests.Options(temp.Path), table);
        Assert.Equal(new[] { 432 }, next.CleanUpLeftoverServer());
        Assert.False(File.Exists(next.RecordPath));
    }

    [Fact]
    public void Shutdown_WhereListingIsSlow_PidReusedByAnotherProcess_IsNotKilled()
    {
        using var temp = new TempDir();
        var table = new FakeProcessTable { ListingIsSlow = true, Alive = { 432 }, OnPipe = { 432 } };
        CscSharedServer server = UsedServer(temp, table, 432, WithToolchain(CscSharedServerTests.Options(temp.Path), temp.Path));
        lock (table.Alive)
        {
            table.OnPipe.Remove(432);
        }

        int listed = table.ListCalls;

        server.Shutdown("editor-quit");

        Assert.Empty(table.Killed);
        Assert.Equal(listed, table.ListCalls);
        Assert.Single(table.Detached);
        Assert.True(File.Exists(server.RecordPath));
    }

    [Fact]
    public void Shutdown_WhereListingIsSlow_KillThatDoesNotTake_HandsOffToTheClient()
    {
        using var temp = new TempDir();
        var table = new FakeProcessTable { ListingIsSlow = true, Alive = { 432 }, OnPipe = { 432 }, KillRemovesAlive = false };
        CscSharedServerOptions options = WithToolchain(CscSharedServerTests.Options(temp.Path), temp.Path);
        options.KillWait = TimeSpan.FromMilliseconds(30);
        CscSharedServer server = UsedServer(temp, table, 432, options);
        int listed = table.ListCalls;

        server.Shutdown("editor-quit");

        Assert.Equal(new[] { 432 }, table.Killed);
        Assert.Equal(listed, table.ListCalls);
        Assert.Single(table.Detached);
        Assert.True(File.Exists(server.RecordPath));
    }

    [Fact]
    public void Retire_StopsTheIdleServerInTheBackground_AndTheNextRunStartsOver()
    {
        using var temp = new TempDir();
        var table = new FakeProcessTable { Alive = { 432 }, OnPipe = { 432 }, Processes = { Proc(432, ServerLine) } };
        CscSharedServer server = UsedServer(temp, table, 432);

        Assert.True(server.RetireInBackground("host-ready"));
        Assert.True(WaitFor(() => !server.IsRetiring, 2000), "retire did not finish");

        Assert.Equal(new[] { 432 }, table.Killed);
        Assert.False(server.UsedThisDomain);
        Assert.False(File.Exists(server.RecordPath));
        Assert.Equal(1, server.Retires);
        CscSharedStatus status = server.Snapshot();
        Assert.Equal("stopped", status.State);
        Assert.Equal("retired: host-ready", status.Note);
        Assert.Equal(0, status.ServerPid);
        Assert.Equal("retired: host-ready", CscSharedStatus.TryRead(server.StatusPath)!.Note);

        // Nothing left to retire, and a stop has nothing to do.
        Assert.False(server.RetireInBackground("host-ready"));
        int listed = table.ListCalls;
        server.Shutdown("editor-quit");
        Assert.Equal(listed, table.ListCalls);

        // The host fails again later: the next csc run records a new server and finds its pid.
        var replacement = Proc(2244, ServerLine);
        table.Alive.Add(2244);
        table.OnPipe.Add(2244);
        table.Listing = () => new[] { replacement };
        Invoke(server, "EnsureRecord");
        Invoke(server, "Succeed", new CscSharedRun(), Stopwatch.StartNew());
        Assert.True(WaitFor(() => server.Snapshot().ServerPid == 2244 && !Discovering(server), 2000), "replacement server was not recorded");
        Assert.True(server.UsedThisDomain);
        Assert.True(WaitFor(() => CscSharedServerRecord.TryRead(server.RecordPath)?.ServerPid == 2244, 2000), "the record has no replacement pid");

        Assert.True(server.RetireInBackground("host-ready"));
        Assert.True(WaitFor(() => !server.IsRetiring, 2000), "second retire did not finish");
        Assert.Equal(new[] { 432, 2244 }, table.Killed);
        Assert.Equal(2, server.Retires);
    }

    [Fact]
    public void Retire_NeverUsed_DoesNothing()
    {
        using var temp = new TempDir();
        var table = new FakeProcessTable();
        var server = new CscSharedServer(CscSharedServerTests.Options(temp.Path), table);

        Assert.False(server.RetireInBackground("host-ready"));
        Assert.Equal(0, table.ListCalls);
        Assert.Empty(table.Killed);
    }

    [Fact]
    public void Retire_AbandonsWhenAReloadStartsDuringTheListing()
    {
        using var temp = new TempDir();
        using var entered = new ManualResetEventSlim(false);
        var table = new FakeProcessTable { ListingIsSlow = true, Alive = { 432 }, Processes = { Proc(432, ServerLine) } };
        var server = new CscSharedServer(CscSharedServerTests.Options(temp.Path), table);
        table.Listing = () =>
        {
            entered.Set();
            var wait = Stopwatch.StartNew();
            while (!server.DomainUnloadRequested && wait.ElapsedMilliseconds < 5000)
            {
                Thread.Sleep(10);
            }

            return new[] { Proc(432, ServerLine) };
        };
        Invoke(server, "EnsureRecord");

        Assert.True(server.RetireInBackground("host-ready"));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(2)), "retire did not list");
        var watch = Stopwatch.StartNew();
        server.AbandonForDomainUnload();
        Assert.True(WaitFor(() => !server.IsRetiring, 2000), "retire did not abandon");
        watch.Stop();

        Assert.True(watch.ElapsedMilliseconds < 1000, "abandon took " + watch.ElapsedMilliseconds + " ms");
        Assert.Equal(0, server.Retires);
        Assert.Empty(table.Killed);
        Assert.True(File.Exists(server.RecordPath));
        Assert.DoesNotContain("retired", server.Snapshot().Note);
    }

    [Fact]
    public void Retire_DoesNotBlockTheCaller_EvenWhenListingTakesSeconds()
    {
        using var temp = new TempDir();
        using var release = new ManualResetEventSlim(false);
        var table = new FakeProcessTable { ListingIsSlow = true, Alive = { 432 } };
        table.Listing = () =>
        {
            release.Wait(TimeSpan.FromSeconds(5));
            return new[] { Proc(432, ServerLine) };
        };
        var server = new CscSharedServer(CscSharedServerTests.Options(temp.Path), table);
        Invoke(server, "EnsureRecord");

        var watch = Stopwatch.StartNew();
        Assert.True(server.RetireInBackground("host-ready"));
        watch.Stop();
        Assert.True(watch.ElapsedMilliseconds < 100, "retire blocked the caller for " + watch.ElapsedMilliseconds + " ms");
        Assert.True(server.IsRetiring);

        release.Set();
        Assert.True(WaitFor(() => !server.IsRetiring, 2000), "retire did not finish");
        Assert.Equal(new[] { 432 }, table.Killed);
        Assert.Empty(table.Detached);
        Assert.False(server.UsedThisDomain);
    }

    [Fact]
    public void Retire_LeavesAServerAloneWhenACscRunStartsMeanwhile()
    {
        using var temp = new TempDir();
        using var listing = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        var table = new FakeProcessTable { Alive = { 432 } };
        table.Listing = () =>
        {
            listing.Set();
            release.Wait(TimeSpan.FromSeconds(5));
            return new[] { Proc(432, ServerLine) };
        };
        var server = new CscSharedServer(CscSharedServerTests.Options(temp.Path), table);
        Invoke(server, "EnsureRecord");

        Assert.True(server.RetireInBackground("host-ready"));
        Assert.True(listing.Wait(TimeSpan.FromSeconds(2)), "retire did not list");
        Invoke(server, "EnsureRecord");
        release.Set();
        Assert.True(WaitFor(() => !server.IsRetiring, 2000), "retire did not finish");

        Assert.Empty(table.Killed);
        Assert.True(server.UsedThisDomain);
        Assert.True(File.Exists(server.RecordPath));
        Assert.Equal(0, server.Retires);
    }

    [Fact]
    public void Retire_InFlightWhenTheDomainStops_DoesNotUndoTheStop()
    {
        using var temp = new TempDir();
        using var listing = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        var table = new FakeProcessTable { Alive = { 432 } };
        int calls = 0;
        table.Listing = () =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                listing.Set();
                release.Wait(TimeSpan.FromSeconds(5));
            }

            return new[] { Proc(432, ServerLine) };
        };
        var server = new CscSharedServer(CscSharedServerTests.Options(temp.Path), table);
        Invoke(server, "EnsureRecord");

        Assert.True(server.RetireInBackground("host-ready"));
        Assert.True(listing.Wait(TimeSpan.FromSeconds(2)), "retire did not list");
        server.Shutdown("domain-reload");
        release.Set();
        Assert.True(WaitFor(() => !server.IsRetiring, 2000), "retire did not finish");

        Assert.Equal(new[] { 432 }, table.Killed);
        Assert.Equal("domain-reload", server.Snapshot().Note);
        Assert.Equal(0, server.Retires);
    }

    [Fact]
    public void Compile_WaitsForARetireInFlight()
    {
        using var temp = new TempDir();
        using var listing = new ManualResetEventSlim(false);
        var table = new FakeProcessTable { Alive = { 432 } };
        table.Listing = () =>
        {
            listing.Set();
            Thread.Sleep(400);
            return new[] { Proc(432, ServerLine) };
        };
        CscSharedServerOptions options = CscSharedServerTests.Options(temp.Path);
        options.Toolchain = () => null;
        var server = new CscSharedServer(options, table);
        Invoke(server, "EnsureRecord");

        Assert.True(server.RetireInBackground("host-ready"));
        Assert.True(listing.Wait(TimeSpan.FromSeconds(2)), "retire did not list");
        var watch = Stopwatch.StartNew();
        CscSharedRun run = server.Compile(new RoslynHostCompileRequest { Source = "class A {}", AssemblyName = "ucb_execute_x" }, Array.Empty<string>());
        watch.Stop();

        Assert.False(server.IsRetiring);
        Assert.True(watch.ElapsedMilliseconds >= 250, "compile did not wait for the retire (" + watch.ElapsedMilliseconds + " ms)");
        Assert.StartsWith("csc-shared-unavailable", run.InfrastructureFailure);
        Assert.Equal(new[] { 432 }, table.Killed);
    }

    [Fact]
    public void FailedRun_ForgetsADeadServer_SoAStopDoesNotTrustIt()
    {
        using var temp = new TempDir();
        var table = new FakeProcessTable { ListingIsSlow = true, Alive = { 432 }, OnPipe = { 432 } };
        CscSharedServer server = UsedServer(temp, table, 432, WithToolchain(CscSharedServerTests.Options(temp.Path), temp.Path));
        lock (table.Alive)
        {
            table.Alive.Remove(432);
        }

        // That failed run may have started a replacement nobody has looked up yet.
        Invoke(server, "Fail", "csc-shared-timeout: csc did not finish", Stopwatch.StartNew());

        Assert.Equal(0, server.Snapshot().ServerPid);
        server.Shutdown("editor-quit");
        Assert.Empty(table.Killed);
        Assert.Single(table.Detached);
        Assert.True(File.Exists(server.RecordPath));
    }

    [Fact]
    public void HostServing_RetiresTheServer_HostInfrastructureFailureDoesNot()
    {
        using var temp = new TempDir();
        var table = new FakeProcessTable { Alive = { 432 }, OnPipe = { 432 }, Processes = { Proc(432, ServerLine) } };
        CscSharedServer server = UsedServer(temp, table, 432);
        CompileResult next = CompileResult.InfrastructureFailure("roslyn-host", "/langversion:9", null, null, "host-building: the host is still being built");
        var host = new SwitchBackend("roslyn-host", () => next);
        var backend = new RetiresCscSharedBackend(host, server);
        var request = new CompileRequest("class A {}", "9.0", Array.Empty<string>(), Array.Empty<string>());

        Assert.Equal("roslyn-host", backend.Id);
        Assert.True(backend.TryCompile(request).IsInfrastructureFailure);
        Assert.False(server.IsRetiring);
        Assert.Empty(table.Killed);
        Assert.True(server.UsedThisDomain);

        next = CompileResult.CompileError("roslyn-host", "/langversion:9", new[] { "Line 1: CS0029: x" }, null);
        Assert.False(backend.TryCompile(request).Succeeded);
        Assert.True(WaitFor(() => !server.IsRetiring && server.Retires == 1, 2000), "a host-served compile did not retire the server");
        Assert.Equal(new[] { 432 }, table.Killed);

        next = CompileResult.Success("roslyn-host", typeof(CscSharedStopTests).Assembly, "/langversion:9");
        Assert.True(backend.TryCompile(request).Succeeded);
        Thread.Sleep(50);
        Assert.Equal(1, server.Retires);
    }

    [Fact]
    public void DefaultChain_HostBuildingThenReady_RetiresOnlyAfterTheHostServes()
    {
        using var temp = new TempDir();
        var table = new FakeProcessTable { Alive = { 432 }, OnPipe = { 432 }, Processes = { Proc(432, ServerLine) } };
        CscSharedServer server = UsedServer(temp, table, 432);
        bool hostReady = false;
        var host = new SwitchBackend("roslyn-host", () => hostReady
            ? CompileResult.Success("roslyn-host", typeof(CscSharedStopTests).Assembly, "/langversion:9")
            : CompileResult.InfrastructureFailure("roslyn-host", "/langversion:9", null, null, "host-building: the host is still being built"));
        var cscShared = new SwitchBackend("csc-shared", () => CompileResult.Success("csc-shared", typeof(CscSharedStopTests).Assembly, "/langversion:9"));
        var codeDom = new SwitchBackend("codedom", () => throw new InvalidOperationException("must not run"));
        var selector = new CompileBackendSelector(codeDom, new ICompileBackend[] { new RetiresCscSharedBackend(host, server), cscShared }, null);
        var request = new CompileRequest("class A {}", "9.0", Array.Empty<string>(), Array.Empty<string>());

        CompileOutcome first = selector.Compile(request);
        Assert.Equal("csc-shared", first.Compiler);
        Assert.StartsWith("host-building", first.FallbackReason);
        Thread.Sleep(50);
        Assert.Equal(0, server.Retires);

        hostReady = true;
        CompileOutcome second = selector.Compile(request);
        Assert.Equal("roslyn-host", second.Compiler);
        Assert.True(WaitFor(() => server.Retires == 1 && !server.IsRetiring, 2000), "the server was not retired");
        Assert.Equal(new[] { 432 }, table.Killed);
        Assert.Equal(0, codeDom.Calls);
    }

    [Fact]
    public void ProductionTable_ListingIsSlowOnlyOnWindows()
    {
        CscSharedProcessTable table = CscSharedProcessTable.Instance;
        int self = Environment.ProcessId;
        Assert.False(table.IsServerOnPipe(0, Pipe, null));
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // Identity is the start time captured when the server was found; no capture, no kill.
            long? ticks = table.ReadStartTicks(self);
            Assert.True(table.ListingIsSlow);
            Assert.NotNull(ticks);
            Assert.False(table.IsServerOnPipe(self, Pipe, null));
            Assert.False(table.IsServerOnPipe(self, Pipe, ticks!.Value + 1));
        }
        else
        {
            // The command line is re-read: this test process is not a compiler server.
            Assert.False(table.ListingIsSlow);
            Assert.Null(table.ReadStartTicks(self));
            Assert.False(table.IsServerOnPipe(self, Pipe, null));
        }
    }

    /// <summary>A server object that ran csc this domain and found <paramref name="pid"/> on its pipe.</summary>
    private static CscSharedServer UsedServer(TempDir temp, FakeProcessTable table, int pid, CscSharedServerOptions? options = null)
    {
        Func<IReadOnlyList<KeyValuePair<int, string>>>? saved = table.Listing;
        table.Listing = () => new[] { Proc(pid, ServerLine) };
        var server = new CscSharedServer(options ?? CscSharedServerTests.Options(temp.Path), table);
        Invoke(server, "EnsureRecord");
        Invoke(server, "Succeed", new CscSharedRun(), Stopwatch.StartNew());
        Assert.True(WaitFor(() => server.Snapshot().ServerPid == pid && !Discovering(server), 2000), "the server pid was not found");
        Assert.True(WaitFor(() => CscSharedServerRecord.TryRead(server.RecordPath)?.ServerPid == pid, 2000), "the record has no server pid");
        table.Listing = saved;
        return server;
    }

    private static CscSharedServerOptions WithToolchain(CscSharedServerOptions options, string directory)
    {
        string dotnet = Path.Combine(directory, RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "dotnet.exe" : "dotnet");
        File.WriteAllText(dotnet, string.Empty);
        options.Toolchain = () => new CscSharedToolchain(dotnet, "/U/csc.dll", "/U/VBCSCompiler.dll", "4.3.1");
        return options;
    }

    private static bool Discovering(CscSharedServer server)
    {
        return (bool)typeof(CscSharedServer).GetField("_discovering", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(server)!;
    }

    private static void Invoke(CscSharedServer server, string name, params object[] args)
    {
        MethodInfo method = typeof(CscSharedServer).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
        method.Invoke(server, args.Length == 0 ? null : args);
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

    private static KeyValuePair<int, string> Proc(int pid, string commandLine)
    {
        return new KeyValuePair<int, string>(pid, commandLine);
    }

    private sealed class SwitchBackend : ICompileBackend
    {
        private readonly Func<CompileResult> _result;

        public SwitchBackend(string id, Func<CompileResult> result)
        {
            Id = id;
            _result = result;
        }

        public string Id { get; }

        public int Calls { get; private set; }

        public CompileResult TryCompile(CompileRequest request)
        {
            Calls++;
            return _result();
        }
    }
}
