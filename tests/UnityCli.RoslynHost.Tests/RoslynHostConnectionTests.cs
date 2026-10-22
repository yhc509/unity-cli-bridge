using UnityCli.Protocol;
using UnityCliBridge.Bridge.Editor;

namespace UnityCli.RoslynHost.Tests;

public sealed class RoslynHostConnectionTests
{
    [Fact]
    public void RealHostLoop_HelloPingQuit()
    {
        using var host = new InProcessHostInstance();

        Assert.Equal(RoslynHostHello.CurrentProtocol, host.Hello.Protocol);
        Assert.Equal("abc123def456", host.Hello.Marker);
        RoslynHostMessage pong = host.Request(new RoslynHostMessage(RoslynHostMessageTypes.Ping), RoslynHostMessageTypes.Pong, TimeSpan.FromSeconds(10));
        Assert.Equal(RoslynHostMessageTypes.Pong, pong.Type);

        host.Stop(TimeSpan.FromSeconds(5));

        Assert.True(host.WaitForServerExit(TimeSpan.FromSeconds(5)));
        Assert.False(host.IsAlive);
    }

    [Fact]
    public void Receive_TimesOutWithTheTimeoutCode()
    {
        var toHost = new BlockingPipe();
        var fromHost = new BlockingPipe();
        using var connection = new RoslynHostConnection(toHost.Writer, fromHost.Reader);
        connection.Start("t");

        var ex = Assert.Throws<RoslynHostException>(() => connection.Receive(TimeSpan.FromMilliseconds(100)));

        Assert.Equal(RoslynHostFailureCodes.Timeout, ex.Code);
        Assert.Equal("timeout: no reply from the host within 100 ms", ex.Reason);
        Assert.False(connection.IsClosed);
    }

    [Fact]
    public void EndOfStream_IsACrash_AndWakesAWaitingReceive()
    {
        var toHost = new BlockingPipe();
        var fromHost = new BlockingPipe();
        using var connection = new RoslynHostConnection(toHost.Writer, fromHost.Reader);
        connection.Start("t");
        var closer = new Thread(() =>
        {
            Thread.Sleep(100);
            fromHost.CloseWriter();
        });
        closer.Start();

        var ex = Assert.Throws<RoslynHostException>(() => connection.Receive(TimeSpan.FromSeconds(20)));

        Assert.Equal(RoslynHostFailureCodes.Crashed, ex.Code);
        Assert.True(connection.IsClosed);
        Assert.Contains("closed its output", connection.ClosedReason);
    }

    [Fact]
    public void CorruptFrame_IsAFrameError()
    {
        var toHost = new BlockingPipe();
        var fromHost = new BlockingPipe();
        using var connection = new RoslynHostConnection(toHost.Writer, fromHost.Reader);
        connection.Start("t");
        fromHost.Writer.Write(new byte[] { 3, 0, 0, 0, (byte)'{', (byte)'x', (byte)'}' }, 0, 7);

        var ex = Assert.Throws<RoslynHostException>(() => connection.Receive(TimeSpan.FromSeconds(20)));

        Assert.Equal(RoslynHostFailureCodes.FrameError, ex.Code);
    }

    [Fact]
    public void Request_HostErrorReply_IsAHostError_AndUnexpectedTypeIsAFrameError()
    {
        var toHost = new BlockingPipe();
        var fromHost = new BlockingPipe();
        using var connection = new RoslynHostConnection(toHost.Writer, fromHost.Reader);
        connection.Start("t");
        RoslynHostFrameCodec.WriteMessage(fromHost.Writer, new RoslynHostError { Message = "Unknown message type 'x'." }.ToMessage());
        RoslynHostFrameCodec.WriteMessage(fromHost.Writer, new RoslynHostMessage(RoslynHostMessageTypes.Pong));

        var hostError = Assert.Throws<RoslynHostException>(() => connection.Request(new RoslynHostMessage("x"), RoslynHostMessageTypes.Result, TimeSpan.FromSeconds(10)));
        var wrongType = Assert.Throws<RoslynHostException>(() => connection.Request(new RoslynHostMessage("y"), RoslynHostMessageTypes.Result, TimeSpan.FromSeconds(10)));

        Assert.Equal(RoslynHostFailureCodes.HostError, hostError.Code);
        Assert.Equal("Unknown message type 'x'.", hostError.Message);
        Assert.Equal(RoslynHostFailureCodes.FrameError, wrongType.Code);
    }

    [Fact]
    public void Send_AfterThePipeClosed_IsACrash()
    {
        var toHost = new BlockingPipe();
        var fromHost = new BlockingPipe();
        using var connection = new RoslynHostConnection(toHost.Writer, fromHost.Reader);
        toHost.CloseWriter();

        var ex = Assert.Throws<RoslynHostException>(() => connection.Send(new RoslynHostMessage(RoslynHostMessageTypes.Ping)));

        Assert.Equal(RoslynHostFailureCodes.Crashed, ex.Code);
    }

    [Fact]
    public void LaunchSpec_PutsTheMarkerFirstAfterTheHostAssembly()
    {
        var spec = new RoslynHostLaunchSpec(
            "/U/NetCoreRuntime/dotnet",
            "/P/Library/com.yhc509.unity-cli-bridge/roslyn-host/k/UcbRoslynHost.dll",
            "/U/NetCoreRuntime",
            new RoslynHostArguments("abc123def456", "/U/DotNetSdkRoslyn", 42, null, prewarm: true),
            TimeSpan.FromSeconds(1));

        List<string> args = spec.BuildCommandLine();

        Assert.Equal(new[] { "exec", "/P/Library/com.yhc509.unity-cli-bridge/roslyn-host/k/UcbRoslynHost.dll", "--ucb-roslyn-host", "abc123def456" }, args.Take(4));
        Assert.Contains("--prewarm", args);
        Assert.True(RoslynHostArguments.CommandLineHasMarker(string.Join(" ", args), "abc123def456"));
    }

    [Fact]
    public void ProcessStart_RelativeOrMissingMuxer_IsAStartFailure()
    {
        var args = new RoslynHostArguments("abc123def456", "/r", 42, null, false);
        var relative = new RoslynHostLaunchSpec("dotnet", "/x/UcbRoslynHost.dll", "/x", args, TimeSpan.FromSeconds(1));
        var missing = new RoslynHostLaunchSpec(Path.Combine(Path.GetTempPath(), "no-such-dir-" + Guid.NewGuid().ToString("N"), "dotnet"), "/x/UcbRoslynHost.dll", "/x", args, TimeSpan.FromSeconds(1));

        Assert.Equal(RoslynHostFailureCodes.StartFailed, Assert.Throws<RoslynHostException>(() => RoslynHostProcess.Start(relative)).Code);
        Assert.Equal(RoslynHostFailureCodes.StartFailed, Assert.Throws<RoslynHostException>(() => RoslynHostProcess.Start(missing)).Code);
    }
}
