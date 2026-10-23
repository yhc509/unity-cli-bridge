using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using UnityCli.Protocol;
using UnityCliBridge.RoslynHost;

namespace UnityCli.RoslynHost.Tests;

public sealed class RoslynHostServerTests
{
    [Fact]
    public void Hello_IsFirst_AndDescribesTheRuntimeAndRoslyn()
    {
        HostTestSupport.ServerRun run = HostTestSupport.RunServer(HostTestSupport.Encode(HostTestSupport.Simple("quit")));

        RoslynHostHello hello = RoslynHostHello.FromMessage(run.Messages[0]);
        Assert.Equal(1, hello.Protocol);
        Assert.Equal(typeof(object).Assembly.Location, hello.CoreLibraryPath);
        Assert.Equal(RuntimeEnvironment.GetRuntimeDirectory(), hello.RuntimeDirectory);
        Assert.StartsWith("4.3.1", hello.RoslynVersion);
        Assert.Equal(Environment.ProcessId, hello.ProcessId);
        Assert.Equal(0, hello.PrewarmMs);
        Assert.Equal("abc123def456", hello.Marker);
    }

    [Fact]
    public void Quit_StopsTheLoop_AndLaterRequestsAreNotAnswered()
    {
        HostTestSupport.ServerRun run = HostTestSupport.RunServer(HostTestSupport.Encode(
            HostTestSupport.Simple("ping"),
            HostTestSupport.Simple("quit"),
            HostTestSupport.Simple("ping")));

        Assert.Equal(RoslynHostExitReason.Quit, run.Reason);
        Assert.Equal(new[] { "hello", "pong" }, run.Messages.Select(m => m.Type));
    }

    [Fact]
    public void EndOfInput_StopsTheLoopCleanly()
    {
        HostTestSupport.ServerRun run = HostTestSupport.RunServer(HostTestSupport.Encode(HostTestSupport.Simple("ping")));

        Assert.Equal(RoslynHostExitReason.EndOfInput, run.Reason);
        Assert.Equal(new[] { "hello", "pong" }, run.Messages.Select(m => m.Type));
        Assert.Contains("stdin closed", run.Log);
    }

    [Fact]
    public void Ping_EchoesItsId()
    {
        var ping = new RoslynHostMessage("ping", new Dictionary<string, object?> { ["id"] = "p7" });

        HostTestSupport.ServerRun run = HostTestSupport.RunServer(HostTestSupport.Encode(ping));

        Assert.Equal("p7", run.Messages[1].Header["id"]);
    }

    [Fact]
    public void SetRefsThenCompile_ReturnsAckAndResultWithAssemblyPayload()
    {
        List<string> refs = HostTestSupport.FrameworkReferences();
        HostTestSupport.ServerRun run = HostTestSupport.RunServer(HostTestSupport.Encode(
            HostTestSupport.SetRefs("r1", refs),
            HostTestSupport.Compile("c1", "public static class S { public static int M() { return 1; } }", "r1"),
            HostTestSupport.Compile("c2", "public static class S { public static int M() { return nope; } }", "r1"),
            HostTestSupport.Simple("quit")));

        Assert.Equal(new[] { "hello", "refsSet", "result", "result" }, run.Messages.Select(m => m.Type));
        RoslynHostRefsSet ack = RoslynHostRefsSet.FromMessage(run.Messages[1]);
        Assert.Equal(("r1", refs.Count, refs.Count), (ack.RefsHash, ack.Count, ack.Loaded));
        Assert.Equal(0, ack.PrewarmMs);

        RoslynHostCompileResult ok = RoslynHostCompileResult.FromMessage(run.Messages[2]);
        Assert.Equal("c1", ok.Id);
        Assert.True(ok.Ok);
        Assert.NotEmpty(ok.Assembly!);
        Assert.Equal(new byte[] { (byte)'M', (byte)'Z' }, ok.Assembly!.Take(2).ToArray());

        RoslynHostCompileResult failed = RoslynHostCompileResult.FromMessage(run.Messages[3]);
        Assert.Equal("c2", failed.Id);
        Assert.False(failed.Ok);
        Assert.Null(failed.Assembly);
        Assert.Equal("CS0103", Assert.Single(failed.Diagnostics).Id);
    }

    [Fact]
    public void Prewarm_ReportsFrameworkMsInHello_AndReferenceMsOnTheFirstSetRefsOnly()
    {
        List<string> refs = HostTestSupport.FrameworkReferences();
        HostTestSupport.ServerRun run = HostTestSupport.RunServer(
            HostTestSupport.Encode(HostTestSupport.SetRefs("a", refs), HostTestSupport.SetRefs("b", refs), HostTestSupport.Simple("quit")),
            prewarm: true);

        Assert.True(RoslynHostHello.FromMessage(run.Messages[0]).PrewarmMs > 0);
        Assert.True(RoslynHostRefsSet.FromMessage(run.Messages[1]).PrewarmMs > 0);
        Assert.Equal(0, RoslynHostRefsSet.FromMessage(run.Messages[2]).PrewarmMs);
    }

    [Fact]
    public void UnknownType_GetsANonFatalError_AndTheLoopContinues()
    {
        HostTestSupport.ServerRun run = HostTestSupport.RunServer(HostTestSupport.Encode(
            HostTestSupport.Simple("bogus"),
            HostTestSupport.Simple("ping")));

        RoslynHostError error = RoslynHostError.FromMessage(run.Messages[1]);
        Assert.False(error.Fatal);
        Assert.Contains("bogus", error.Message);
        Assert.Equal("pong", run.Messages[2].Type);
    }

    [Fact]
    public void CorruptFrame_GetsAFatalError_AndStopsTheLoop()
    {
        byte[] good = HostTestSupport.Encode(HostTestSupport.Simple("ping"));
        byte[] input = good.Concat(new byte[] { 0xFF, 0xFF, 0xFF, 0x7F, 1, 2 }).Concat(good).ToArray();

        HostTestSupport.ServerRun run = HostTestSupport.RunServer(input);

        Assert.Equal(RoslynHostExitReason.ProtocolError, run.Reason);
        Assert.Equal(new[] { "hello", "pong", "error" }, run.Messages.Select(m => m.Type));
        RoslynHostError error = RoslynHostError.FromMessage(run.Messages[2]);
        Assert.True(error.Fatal);
        Assert.Contains("limit", error.Message);
    }

    [Fact]
    public void ClosedOutput_StopsTheLoop()
    {
        var output = new MemoryStream();
        output.Dispose();
        var server = new RoslynHostServer(new MemoryStream(), output, TextWriter.Null, "m", prewarm: false);

        Assert.Equal(RoslynHostExitReason.OutputClosed, server.Run());
    }

    [Fact]
    public void Run_RejectsInvalidArguments()
    {
        var log = new StringWriter();

        int code = UcbRoslynHost.Run(new[] { "--parent-pid", "1" }, new MemoryStream(), new MemoryStream(), log, TimeSpan.FromSeconds(1), _ => { });

        Assert.Equal(UcbRoslynHost.ExitStartupFailed, code);
        Assert.Contains("invalid arguments", log.ToString());
    }

    [Fact]
    public void Run_RejectsAMissingRoslynDirectory()
    {
        string[] args = Args(Path.Combine(Path.GetTempPath(), "no-such-roslyn-" + Guid.NewGuid().ToString("N")), Environment.ProcessId);

        int code = UcbRoslynHost.Run(args, new MemoryStream(), new MemoryStream(), TextWriter.Null, TimeSpan.FromSeconds(1), _ => { });

        Assert.Equal(UcbRoslynHost.ExitStartupFailed, code);
    }

    [Fact]
    public void Run_ExitsAtOnceWhenTheParentIsAlreadyGone()
    {
        using Process sleeper = HostTestSupport.StartSleeper();
        int pid = sleeper.Id;
        HostTestSupport.KillAndReap(sleeper);
        var output = new MemoryStream();

        int code = UcbRoslynHost.Run(Args(RoslynDirectory, pid), new MemoryStream(), output, TextWriter.Null, TimeSpan.FromSeconds(1), _ => { });

        Assert.Equal(UcbRoslynHost.ExitParentGone, code);
        Assert.Equal(0, output.Length);
    }

    [Fact]
    public void Run_QuitAndEndOfInput_ExitClean()
    {
        var quitOutput = new MemoryStream();
        int quit = UcbRoslynHost.Run(
            Args(RoslynDirectory, Environment.ProcessId),
            new MemoryStream(HostTestSupport.Encode(HostTestSupport.Simple("quit"))),
            quitOutput,
            TextWriter.Null,
            TimeSpan.FromSeconds(1),
            _ => { });
        int eof = UcbRoslynHost.Run(Args(RoslynDirectory, Environment.ProcessId), new MemoryStream(), new MemoryStream(), TextWriter.Null, TimeSpan.FromSeconds(1), _ => { });

        Assert.Equal(UcbRoslynHost.ExitClean, quit);
        Assert.Equal(UcbRoslynHost.ExitClean, eof);
        Assert.Equal("hello", Assert.Single(HostTestSupport.DecodeAll(quitOutput.ToArray())).Type);
    }

    [Fact]
    public async Task Run_ExitsWithParentGoneWhenTheParentIsKilledWhileServing()
    {
        using Process sleeper = HostTestSupport.StartSleeper();
        using var toHost = new AnonymousPipeServerStream(PipeDirection.Out);
        using var hostInput = new AnonymousPipeClientStream(PipeDirection.In, toHost.ClientSafePipeHandle);
        var exitCode = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var output = new MemoryStream();

        Task<int> serving = Task.Run(() => UcbRoslynHost.Run(
            Args(RoslynDirectory, sleeper.Id),
            hostInput,
            output,
            TextWriter.Null,
            TimeSpan.FromMilliseconds(200),
            code =>
            {
                exitCode.TrySetResult(code);
                toHost.Dispose();
            }));
        try
        {
            await Task.Delay(500);
            Assert.False(exitCode.Task.IsCompleted);
            Stopwatch stopwatch = Stopwatch.StartNew();
            HostTestSupport.KillAndReap(sleeper);

            int code = await exitCode.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(UcbRoslynHost.ExitParentGone, code);
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(3), "took " + stopwatch.Elapsed);
            await serving.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            HostTestSupport.KillAndReap(sleeper);
            toHost.Dispose();
        }
    }

    private static string RoslynDirectory => Path.GetDirectoryName(typeof(Microsoft.CodeAnalysis.CSharp.CSharpCompilation).Assembly.Location)!;

    private static string[] Args(string roslynDirectory, int parentPid)
    {
        return new RoslynHostArguments("abc123def456", roslynDirectory, parentPid, null, prewarm: false).ToArgumentList().ToArray();
    }
}
