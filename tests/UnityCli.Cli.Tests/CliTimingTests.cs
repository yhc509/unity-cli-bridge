using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using UnityCli.Cli.Services;
using UnityCli.Protocol;

namespace UnityCli.Cli.Tests;

[Collection(CurrentDirectoryCollection.Name)]
public sealed class CliTimingTests
{
    private static readonly SemaphoreSlim ConsoleLock = new(1, 1);

    [Fact]
    public void Write_FormatsPhaseOnTheSuppliedWriter()
    {
        using var writer = new StringWriter();

        CliTiming.Write(writer, "argParse", 15.25, 45.5);

        Assert.Equal(
            "ucb-timing phase=argParse ms=15.25 sinceProcessStartMs=45.5" + Environment.NewLine,
            writer.ToString());
    }

    [Fact]
    public async Task RunAsync_Help_WithoutTiming_LeavesStderrFreeOfPhaseLines()
    {
        var result = await InvokeAsync(["help"], timing: null);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("usage: unity-cli", result.Stdout);
        Assert.DoesNotContain("ucb-timing", result.Stdout);
        Assert.DoesNotContain("ucb-timing", result.Stderr);
    }

    [Fact]
    public async Task RunAsync_Help_WithTiming_PrintsLocalPhasesOnStderrOnly()
    {
        var result = await InvokeAsync(["--json", "help"], timing: "1");

        Assert.Equal(0, result.ExitCode);
        Assert.DoesNotContain("ucb-timing", result.Stdout);
        Assert.Contains("usage: unity-cli", result.Stdout);
        AssertPhase(result.Stderr, "processStartToMain");
        AssertPhase(result.Stderr, "argParse");
        AssertPhase(result.Stderr, "output");
        Assert.DoesNotContain("phase=connect", result.Stderr);
        Assert.DoesNotContain("phase=send", result.Stderr);
        Assert.DoesNotContain("phase=wait", result.Stderr);
        Assert.DoesNotContain("phase=receive", result.Stderr);
        Assert.DoesNotContain("phase=deserialize", result.Stderr);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("true")]
    public async Task RunAsync_NonOneTimingValue_DoesNotPrintPhases(string? timing)
    {
        var result = await InvokeAsync(["help"], timing);

        Assert.DoesNotContain("ucb-timing", result.Stdout);
        Assert.DoesNotContain("ucb-timing", result.Stderr);
    }

    [Fact]
    public async Task RunAsync_LiveJson_WithTiming_KeepsKoreanStdoutCleanAndLogsIpcPhases()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var response = ResponseEnvelope.Success(
            "req-1",
            "hash",
            JsonSerializer.SerializeToElement(new { message = "한글 프로젝트" }, ProtocolJson.Default),
            durationMs: 12,
            transport: ProtocolConstants.TransportLive);
        response.timings = new ResponseTimings { queueWaitMs = 15 };

        var result = await InvokeAgainstBridgeAsync(
            ["--json", "compile", "--no-wait"],
            ProtocolJson.Serialize(response),
            timing: "1");

        Assert.Equal(0, result.ExitCode);
        Assert.DoesNotContain("ucb-timing", result.Stdout);
        Assert.Contains("한글 프로젝트", result.Stdout);
        Assert.Contains("\"queueWaitMs\":15", result.Stdout);
        Assert.Contains("\"durationMs\":12", result.Stdout);

        var payload = JsonSerializer.Deserialize<JsonElement>(result.Stdout.Trim());
        Assert.Equal("한글 프로젝트", payload.GetProperty("data").GetProperty("message").GetString());

        AssertPhase(result.Stderr, "processStartToMain");
        AssertPhase(result.Stderr, "argParse");
        AssertPhase(result.Stderr, "connect");
        AssertPhase(result.Stderr, "send");
        AssertPhase(result.Stderr, "wait");
        AssertPhase(result.Stderr, "receive");
        AssertPhase(result.Stderr, "deserialize");
        AssertPhase(result.Stderr, "output");
    }

    private static void AssertPhase(string stderr, string phase)
    {
        string needle = "phase=" + phase + " ";
        int count = 0;
        int index = 0;
        while ((index = stderr.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        Assert.Equal(1, count);
        Assert.Contains("ucb-timing phase=" + phase + " ms=", stderr);
    }

    private static async Task<CliInvocationResult> InvokeAsync(string[] args, string? timing)
    {
        await ConsoleLock.WaitAsync();
        string? originalTiming = Environment.GetEnvironmentVariable(CliTiming.EnvironmentVariable);
        TextWriter originalOut = Console.Out;
        TextWriter originalError = Console.Error;
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        try
        {
            Environment.SetEnvironmentVariable(CliTiming.EnvironmentVariable, timing);
            Console.SetOut(stdout);
            Console.SetError(stderr);
            int exitCode = await UnityCli.Cli.CliApp.RunAsync(args);
            return new CliInvocationResult(exitCode, stdout.ToString(), stderr.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
            Environment.SetEnvironmentVariable(CliTiming.EnvironmentVariable, originalTiming);
            ConsoleLock.Release();
        }
    }

    private static async Task<CliInvocationResult> InvokeAgainstBridgeAsync(string[] args, string responseLine, string? timing)
    {
        await ConsoleLock.WaitAsync();
        using var temp = new TempDirectory();
        string projectRoot = Path.Combine(temp.Path, "SampleProject");
        Directory.CreateDirectory(Path.Combine(projectRoot, "Assets"));
        Directory.CreateDirectory(Path.Combine(projectRoot, "Packages"));
        string canonicalProjectRoot = ProtocolConstants.GetCanonicalPath(projectRoot);
        string projectHash = ProtocolConstants.ComputeProjectHash(canonicalProjectRoot);
        string socketPath = Path.Combine("/tmp", "ucb-" + Guid.NewGuid().ToString("N") + ".sock");
        string registryPath = Path.Combine(temp.Path, "instances.json");
        string escapedProjectRoot = canonicalProjectRoot.Replace("\\", "\\\\");
        File.WriteAllText(
            registryPath,
            "{\"activeProjectRoot\":\"" + escapedProjectRoot + "\",\"instances\":[{"
                + "\"projectRoot\":\"" + escapedProjectRoot + "\","
                + "\"projectName\":\"SampleProject\","
                + "\"projectHash\":\"" + projectHash + "\","
                + "\"pipeName\":\"" + socketPath.Replace("\\", "\\\\") + "\","
                + "\"editorProcessId\":1234,"
                + "\"unityVersion\":\"6000.3.10f1\","
                + "\"state\":\"idle\","
                + "\"lastSeenUtc\":\"" + DateTimeOffset.UtcNow.ToString("O") + "\","
                + "\"capabilities\":[]}]}");

        using Socket listener = StartResponder(socketPath, responseLine);
        string? originalTiming = Environment.GetEnvironmentVariable(CliTiming.EnvironmentVariable);
        string? originalRegistryPath = Environment.GetEnvironmentVariable("UNITY_CLI_REGISTRY_PATH");
        string originalCurrentDirectory = Environment.CurrentDirectory;
        TextWriter originalOut = Console.Out;
        TextWriter originalError = Console.Error;
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        try
        {
            Environment.SetEnvironmentVariable(CliTiming.EnvironmentVariable, timing);
            Environment.SetEnvironmentVariable("UNITY_CLI_REGISTRY_PATH", registryPath);
            Environment.CurrentDirectory = projectRoot;
            Console.SetOut(stdout);
            Console.SetError(stderr);
            int exitCode = await UnityCli.Cli.CliApp.RunAsync(args);
            return new CliInvocationResult(exitCode, stdout.ToString(), stderr.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
            Environment.CurrentDirectory = originalCurrentDirectory;
            Environment.SetEnvironmentVariable("UNITY_CLI_REGISTRY_PATH", originalRegistryPath);
            Environment.SetEnvironmentVariable(CliTiming.EnvironmentVariable, originalTiming);
            listener.Dispose();
            if (File.Exists(socketPath))
            {
                File.Delete(socketPath);
            }

            ConsoleLock.Release();
        }
    }

    private static Socket StartResponder(string socketPath, string responseLine)
    {
        var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(socketPath));
        listener.Listen(1);
        _ = Task.Run(async () =>
        {
            try
            {
                using Socket client = await listener.AcceptAsync();
                await using var stream = new NetworkStream(client, ownsSocket: true);
                using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
                await using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true);
                await reader.ReadLineAsync();
                await writer.WriteLineAsync(responseLine);
                await writer.FlushAsync();
            }
            catch (ObjectDisposedException)
            {
            }
            catch (SocketException)
            {
            }
        });
        return listener;
    }

    private sealed record CliInvocationResult(int ExitCode, string Stdout, string Stderr);
}
