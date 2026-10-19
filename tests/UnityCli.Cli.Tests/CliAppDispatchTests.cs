using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using UnityCli.Protocol;

namespace UnityCli.Cli.Tests;

/// <summary>
/// End-to-end cover for the PROTOCOL_MISMATCH hand-off: a fake bridge answers with an error envelope
/// stamped with its own protocol version, exactly as a real 0.3.x bridge does.
/// </summary>
[Collection(CurrentDirectoryCollection.Name)]
public sealed class CliAppDispatchTests
{
    private static readonly SemaphoreSlim ConsoleLock = new(1, 1);

    [Fact]
    public async Task RunAsync_WhenBridgeReportsOlderProtocol_ExecsMatchingCliWithOriginalArgv()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var dispatcher = new FakeCliVersionDispatcher(
            installedVersions: [Installed("0.3.5", "4"), Installed("0.4.1", "5")],
            exitCode: 7);

        var result = await InvokeAgainstMismatchedBridgeAsync(
            ["--json", "read-console", "--limit", "5", "--no-stacktrace"],
            bridgeProtocolVersion: "4",
            dispatcher);

        Assert.Equal(7, result.ExitCode);
        Assert.Equal(1, dispatcher.ExecCallCount);
        Assert.Equal("/versions/0.3.5/unity-cli", dispatcher.ExecutedExecutablePath);
        Assert.Equal(["--json", "read-console", "--limit", "5", "--no-stacktrace"], dispatcher.ExecutedArgs!);
        Assert.Equal(string.Empty, result.Stdout);
    }

    [Fact]
    public async Task RunAsync_WhenDispatchGuardIsSet_ReportsMismatchInsteadOfLooping()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var dispatcher = new FakeCliVersionDispatcher(
            installedVersions: [Installed("0.3.5", "4")],
            isDispatchGuardSet: true);

        var result = await InvokeAgainstMismatchedBridgeAsync(
            ["--json", "compile"],
            bridgeProtocolVersion: "4",
            dispatcher);

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(0, dispatcher.ExecCallCount);

        ResponseEnvelope response = ParseResponse(result.Stdout);
        Assert.Equal(ProtocolConstants.ErrorProtocolMismatch, response.error?.code);
        Assert.Contains("incompatible", response.error?.message);
    }

    [Fact]
    public async Task RunAsync_WhenNoInstalledVersionSpeaksTheBridgeProtocol_FailsWithActionableError()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var dispatcher = new FakeCliVersionDispatcher(installedVersions: [Installed("0.4.1", "5")]);

        var result = await InvokeAgainstMismatchedBridgeAsync(
            ["--json", "compile"],
            bridgeProtocolVersion: "4",
            dispatcher);

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(0, dispatcher.ExecCallCount);

        ResponseEnvelope response = ParseResponse(result.Stdout);
        Assert.Equal(ProtocolConstants.ErrorProtocolMismatch, response.error?.code);
        Assert.Contains("protocol 4", response.error!.message);
        Assert.Contains("Window > Unity CLI Manager > Install CLI", response.error.message);
    }

    [Fact]
    public async Task RunAsync_WhenBridgeAnswersNormally_DoesNotConsultTheDispatcher()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var dispatcher = new FakeCliVersionDispatcher(installedVersions: [Installed("0.3.5", "4")]);

        var result = await InvokeAgainstBridgeAsync(
            ["--json", "compile", "--no-wait"],
            ResponseEnvelope.Success("req-1", "hash", data: null, durationMs: 1, transport: ProtocolConstants.TransportLive),
            dispatcher);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(0, dispatcher.ExecCallCount);
        Assert.Equal(0, dispatcher.ListInstalledCallCount);
        Assert.Equal("success", ParseResponse(result.Stdout).status);
    }

    [Fact]
    public async Task RunAsync_Status_DispatchesOnMismatchBecauseItFailsOnOne()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var dispatcher = new FakeCliVersionDispatcher(
            installedVersions: [Installed("0.3.5", "4")],
            exitCode: 0);

        var result = await InvokeAgainstMismatchedBridgeAsync(
            ["--json", "status"],
            bridgeProtocolVersion: "4",
            dispatcher);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(1, dispatcher.ExecCallCount);
        Assert.Equal(["--json", "status"], dispatcher.ExecutedArgs!);
    }

    [Fact]
    public async Task RunAsync_WhenHandoffFails_KeepsTheProtocolMismatchAndExplainsWhy()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var dispatcher = new FakeCliVersionDispatcher(
            installedVersions: [Installed("0.3.5", "4")],
            execFailureMessage: "execve failed with errno 13.");

        var result = await InvokeAgainstMismatchedBridgeAsync(
            ["--json", "compile"],
            bridgeProtocolVersion: "4",
            dispatcher);

        Assert.Equal(1, result.ExitCode);

        ResponseEnvelope response = ParseResponse(result.Stdout);
        Assert.Equal(ProtocolConstants.ErrorProtocolMismatch, response.error?.code);
        Assert.Equal("4", response.protocolVersion);
        Assert.Contains("errno 13", response.error?.details?.GetString());
    }

    [Fact]
    public async Task RunAsync_LocalOnlyDoctor_NeverDispatchesOnMismatch()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var dispatcher = new FakeCliVersionDispatcher(installedVersions: [Installed("0.3.5", "4")]);

        var result = await InvokeAgainstMismatchedBridgeAsync(
            ["--json", "doctor"],
            bridgeProtocolVersion: "4",
            dispatcher);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(0, dispatcher.ExecCallCount);
        Assert.Equal(0, dispatcher.ListInstalledCallCount);

        ResponseEnvelope response = ParseResponse(result.Stdout);
        Assert.Equal("success", response.status);
        Assert.Equal(
            ProtocolConstants.ErrorProtocolMismatch,
            response.data!.Value.GetProperty("liveErrorCode").GetString());
    }

    private static InstalledCliVersion Installed(string version, string protocolVersion) =>
        new(version, protocolVersion, "/versions/" + version, "/versions/" + version + "/unity-cli");

    private static Task<CliInvocationResult> InvokeAgainstMismatchedBridgeAsync(
        string[] args,
        string bridgeProtocolVersion,
        FakeCliVersionDispatcher dispatcher)
    {
        var mismatch = ResponseEnvelope.Failure(
            "req-1",
            "hash",
            ProtocolConstants.ErrorProtocolMismatch,
            "CLI version is incompatible with this Unity package. Please upgrade both CLI binary and Unity package together.",
            retryable: false,
            durationMs: 0,
            transport: ProtocolConstants.TransportLive);
        mismatch.protocolVersion = bridgeProtocolVersion;
        return InvokeAgainstBridgeAsync(args, mismatch, dispatcher);
    }

    private static async Task<CliInvocationResult> InvokeAgainstBridgeAsync(
        string[] args,
        ResponseEnvelope bridgeResponse,
        FakeCliVersionDispatcher dispatcher)
    {
        await ConsoleLock.WaitAsync();

        try
        {
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

            using Socket listener = StartResponder(socketPath, ProtocolJson.Serialize(bridgeResponse));

            string? originalRegistryPath = Environment.GetEnvironmentVariable("UNITY_CLI_REGISTRY_PATH");
            string originalCurrentDirectory = Environment.CurrentDirectory;
            TextWriter originalOut = Console.Out;
            TextWriter originalError = Console.Error;

            using var stdout = new StringWriter();
            using var stderr = new StringWriter();

            try
            {
                Environment.SetEnvironmentVariable("UNITY_CLI_REGISTRY_PATH", registryPath);
                Environment.CurrentDirectory = projectRoot;
                Console.SetOut(stdout);
                Console.SetError(stderr);

                int exitCode = await UnityCli.Cli.CliApp.RunAsync(args, dispatcher);
                return new CliInvocationResult(exitCode, stdout.ToString(), stderr.ToString());
            }
            finally
            {
                Console.SetOut(originalOut);
                Console.SetError(originalError);
                Environment.CurrentDirectory = originalCurrentDirectory;
                Environment.SetEnvironmentVariable("UNITY_CLI_REGISTRY_PATH", originalRegistryPath);
                listener.Dispose();
                if (File.Exists(socketPath))
                {
                    File.Delete(socketPath);
                }
            }
        }
        finally
        {
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

    private static ResponseEnvelope ParseResponse(string stdout)
    {
        return JsonSerializer.Deserialize<ResponseEnvelope>(stdout.Trim(), ProtocolJson.Default)!;
    }

    private sealed record CliInvocationResult(int ExitCode, string Stdout, string Stderr);
}
