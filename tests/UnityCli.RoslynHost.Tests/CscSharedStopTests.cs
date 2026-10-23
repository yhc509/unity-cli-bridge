using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using UnityCli.Protocol;
using UnityCliBridge.Bridge.Editor;
using FakeProcessTable = UnityCli.RoslynHost.Tests.CscSharedServerTests.FakeProcessTable;

namespace UnityCli.RoslynHost.Tests;

/// <summary>
/// The csc /shared server stop on reload/quit (main thread, no client process to wait for).
/// </summary>
public sealed class CscSharedStopTests
{
    private const string ProjectHash = "d6887d898597";
    private static readonly string Pipe = CscSharedPipe.BuildName(ProjectHash, "6000.3.10f1");
    private static readonly string ServerLine = "/U/dotnet exec /U/VBCSCompiler.dll -pipename:" + Pipe;

    [Fact]
    public void Shutdown_KillsTheKnownServerDirectly_WithoutListingOrAClient()
    {
        using var temp = new TempDir();
        var table = new FakeProcessTable { Alive = { 432 }, OnPipe = { 432 }, Processes = { Proc(432, ServerLine) } };
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
    public void Shutdown_KnownServerAlreadyGone_LeavesNothingToKill()
    {
        using var temp = new TempDir();
        var table = new FakeProcessTable { Alive = { 432 }, OnPipe = { 432 } };
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
    public void Shutdown_PidReusedByAnotherProcess_IsNotKilledDirectly()
    {
        using var temp = new TempDir();
        var table = new FakeProcessTable { Alive = { 432 }, OnPipe = { 432 } };
        CscSharedServer server = UsedServer(temp, table, 432);
        lock (table.Alive)
        {
            table.OnPipe.Remove(432);
        }

        table.Processes.Add(Proc(432, "/usr/bin/some-other-process"));
        table.Processes.Add(Proc(901, ServerLine));
        table.Alive.Add(901);

        server.Shutdown("domain-reload");

        Assert.Equal(new[] { 901 }, table.Killed);
    }

    [Fact]
    public void Shutdown_KillThatDoesNotTake_FallsBackToTheListing()
    {
        using var temp = new TempDir();
        var table = new FakeProcessTable { Alive = { 432 }, OnPipe = { 432 }, KillRemovesAlive = false, Processes = { Proc(432, ServerLine) } };
        CscSharedServerOptions options = CscSharedServerTests.Options(temp.Path);
        options.KillWait = TimeSpan.FromMilliseconds(30);
        CscSharedServer server = UsedServer(temp, table, 432, options);

        server.Shutdown("domain-reload");

        Assert.Equal(new[] { 432, 432 }, table.Killed);
        Assert.True(table.ListCalls >= 1);
    }

    [Fact]
    public void FailedRun_ForgetsADeadServer_SoAStopListsAgain()
    {
        using var temp = new TempDir();
        var table = new FakeProcessTable { Alive = { 432 }, OnPipe = { 432 } };
        CscSharedServer server = UsedServer(temp, table, 432);
        lock (table.Alive)
        {
            table.Alive.Remove(432);
        }

        Invoke(server, "Fail", "csc-shared-timeout: csc did not finish", Stopwatch.StartNew());

        Assert.Equal(0, server.Snapshot().ServerPid);
        table.Processes.Add(Proc(901, ServerLine));
        table.Alive.Add(901);
        server.Shutdown("domain-reload");
        Assert.Equal(new[] { 901 }, table.Killed);
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
}
