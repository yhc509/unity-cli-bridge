using System.Text.Json;
using UnityCli.Cli.Models;
using UnityCli.Cli.Services;
using UnityCli.Protocol;

namespace UnityCli.Cli.Tests;

public sealed class EditorReadyWaitTests
{
    [Fact]
    public async Task PollEditorReadyAsync_WaitsUntilStatusIsNotCompilingOrUpdating()
    {
        var parsed = new ParsedCommand(CommandKind.Compile) { Wait = true };
        var started = Success(new { message = "script compilation requested" });
        var statusResponses = new Queue<ResponseEnvelope>([
            Success(new StatusPayload { isCompiling = true }),
            Success(new StatusPayload { isUpdating = true }),
            Success(new StatusPayload()),
        ]);
        var polledCommands = new List<string>();

        var result = await UnityCli.Cli.CliApp.PollEditorReadyAsync(
            parsed,
            started,
            (command, _, _) =>
            {
                polledCommands.Add(command.command);
                return Task.FromResult(statusResponses.Dequeue());
            },
            delayAsync: (_, _) => Task.CompletedTask,
            timeout: TimeSpan.FromSeconds(5));

        Assert.Equal(ProtocolConstants.StatusSuccess, result.status);
        Assert.Equal([ProtocolConstants.CommandStatus, ProtocolConstants.CommandStatus, ProtocolConstants.CommandStatus], polledCommands);
        Assert.True(result.data.HasValue);
        JsonElement data = result.data.Value;
        Assert.Equal("script compilation requested", data.GetProperty("message").GetString());
        Assert.True(data.GetProperty("ready").GetBoolean());
        Assert.Equal("ready", data.GetProperty("readyMessage").GetString());
        Assert.True(data.GetProperty("waitedMs").GetInt64() >= 0);
    }

    [Fact]
    public async Task PollEditorReadyAsync_TreatsLiveUnavailableAsTransient()
    {
        var parsed = new ParsedCommand(CommandKind.Refresh) { Wait = true };
        var started = Success(new { message = "AssetDatabase.Refresh completed" });
        var statusResponses = new Queue<ResponseEnvelope>([
            ResponseEnvelope.Failure("poll-1", "target-1", "LIVE_UNAVAILABLE", "Bridge unavailable.", retryable: true),
            Success(new StatusPayload()),
        ]);

        var result = await UnityCli.Cli.CliApp.PollEditorReadyAsync(
            parsed,
            started,
            (_, _, _) => Task.FromResult(statusResponses.Dequeue()),
            delayAsync: (_, _) => Task.CompletedTask,
            timeout: TimeSpan.FromSeconds(5));

        Assert.Equal(ProtocolConstants.StatusSuccess, result.status);
        Assert.True(result.data.HasValue);
        Assert.True(result.data.Value.GetProperty("ready").GetBoolean());
    }

    [Theory]
    [InlineData(CommandKind.Compile, ProtocolConstants.ErrorCompileWaitTimeout)]
    [InlineData(CommandKind.Refresh, ProtocolConstants.ErrorRefreshWaitTimeout)]
    public async Task PollEditorReadyAsync_TimesOutWithCommandSpecificRetryableError(
        CommandKind kind,
        string expectedCode)
    {
        var parsed = new ParsedCommand(kind) { Wait = true };

        var result = await UnityCli.Cli.CliApp.PollEditorReadyAsync(
            parsed,
            Success(new { message = "started" }),
            (_, _, _) => throw new InvalidOperationException("Should not poll after timeout."),
            delayAsync: (_, _) => Task.CompletedTask,
            timeout: TimeSpan.Zero);

        Assert.Equal(ProtocolConstants.StatusError, result.status);
        Assert.Equal(expectedCode, result.error?.code);
        Assert.True(result.retryable);
    }

    [Fact]
    public async Task PollEditorReadyAsync_TimesOutAfterPollingLoop()
    {
        var parsed = new ParsedCommand(CommandKind.Compile) { Wait = true };

        var result = await UnityCli.Cli.CliApp.PollEditorReadyAsync(
            parsed,
            Success(new { message = "started" }),
            (_, _, _) => Task.FromResult(Success(new StatusPayload
            {
                isCompiling = true,
                isUpdating = false,
                projectName = "X",
            })),
            delayAsync: Task.Delay,
            timeout: TimeSpan.FromMilliseconds(120),
            pollInterval: TimeSpan.FromMilliseconds(50));

        Assert.Equal(ProtocolConstants.StatusError, result.status);
        Assert.Equal(ProtocolConstants.ErrorCompileWaitTimeout, result.error?.code);
        Assert.True(result.retryable);
    }

    [Fact]
    public async Task PollEditorReadyAsync_ReturnsLiveUnavailableAfterTransientFailureCap()
    {
        var parsed = new ParsedCommand(CommandKind.Compile) { Wait = true };
        int polls = 0;

        var result = await UnityCli.Cli.CliApp.PollEditorReadyAsync(
            parsed,
            Success(new { message = "started" }),
            (_, _, _) =>
            {
                polls++;
                return Task.FromResult(ResponseEnvelope.Failure(
                    "poll-1",
                    "target-1",
                    "LIVE_UNAVAILABLE",
                    "Bridge unavailable.",
                    retryable: true));
            },
            delayAsync: (_, _) => Task.CompletedTask,
            timeout: TimeSpan.FromSeconds(30));

        Assert.Equal(6, polls);
        Assert.Equal(ProtocolConstants.StatusError, result.status);
        Assert.Equal("LIVE_UNAVAILABLE", result.error?.code);
        Assert.True(result.retryable);
        Assert.Contains("6 consecutive transport failures", result.error?.details?.GetString());
    }

    [Fact]
    public async Task PollEditorReadyAsync_ReturnsNonRetryablePollFailureImmediately()
    {
        var parsed = new ParsedCommand(CommandKind.Refresh) { Wait = true };
        int polls = 0;

        var result = await UnityCli.Cli.CliApp.PollEditorReadyAsync(
            parsed,
            Success(new { message = "started" }),
            (_, _, _) =>
            {
                polls++;
                return Task.FromResult(ResponseEnvelope.Failure(
                    "poll-1",
                    "target-1",
                    "STATUS_FAILED",
                    "Status failed.",
                    retryable: false));
            },
            delayAsync: (_, _) => Task.CompletedTask,
            timeout: TimeSpan.FromSeconds(5));

        Assert.Equal(1, polls);
        Assert.Equal(ProtocolConstants.StatusError, result.status);
        Assert.Equal("STATUS_FAILED", result.error?.code);
        Assert.False(result.retryable);
    }

    [Fact]
    public async Task PollEditorReadyAsync_PropagatesCallerCancellation()
    {
        var parsed = new ParsedCommand(CommandKind.Compile) { Wait = true };
        using var cts = new CancellationTokenSource();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            UnityCli.Cli.CliApp.PollEditorReadyAsync(
                parsed,
                Success(new { message = "started" }),
                (_, _, _) =>
                {
                    cts.Cancel();
                    throw new OperationCanceledException(cts.Token);
                },
                cts.Token,
                delayAsync: (_, _) => Task.CompletedTask,
                timeout: TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task ExecuteUnityCommandAsync_NormalizesOperationCanceledIpcTimeoutToLiveUnavailable()
    {
        using var temp = new TempDirectory();
        string projectRoot = CreateUnityProject(temp.Path, "SampleProject");
        string expectedProjectHash = ProtocolConstants.ComputeProjectHash(projectRoot);
        string registryPath = Path.Combine(temp.Path, "instances.json");
        var registryStore = new InstanceRegistryStore(registryPath);
        SaveRegistry(registryStore, registryPath, projectRoot, "token");
        var parsed = new ParsedCommand(CommandKind.Compile);

        var result = await UnityCli.Cli.CliApp.ExecuteUnityCommandAsync(
            parsed,
            registryStore,
            projectRoot,
            (_, _, _, _) => throw new OperationCanceledException("simulated IPC timeout"));

        Assert.Equal(ProtocolConstants.StatusError, result.status);
        Assert.Equal("LIVE_UNAVAILABLE", result.error?.code);
        Assert.True(result.retryable);
        Assert.Equal(expectedProjectHash, result.target);
        Assert.Contains("simulated IPC timeout", result.error?.details?.GetString());
    }

    [Fact]
    public async Task ExecuteUnityCommandAsync_WhenSyntheticTargetHasNoAuthToken_DoesNotSendLiveRequest()
    {
        using var temp = new TempDirectory();
        string projectRoot = CreateUnityProject(temp.Path, "SampleProject");
        string expectedProjectHash = ProtocolConstants.ComputeProjectHash(projectRoot);
        var registryStore = new InstanceRegistryStore(Path.Combine(temp.Path, "instances.json"));
        var parsed = new ParsedCommand(CommandKind.Refresh);
        int calls = 0;

        var result = await UnityCli.Cli.CliApp.ExecuteUnityCommandAsync(
            parsed,
            registryStore,
            projectRoot,
            (_, _, _, _) =>
            {
                calls++;
                return Task.FromResult(Success(new { sent = true }));
            });

        Assert.Equal(0, calls);
        Assert.Equal(ProtocolConstants.StatusError, result.status);
        Assert.Equal("LIVE_UNAVAILABLE", result.error?.code);
        Assert.True(result.retryable);
        Assert.Equal(expectedProjectHash, result.target);
        Assert.Contains("Could not read the bridge auth token", result.error?.details?.GetString());
    }

    [Fact]
    public async Task ExecuteUnityCommandAsync_RetriesUnauthorizedOnceWhenRegistryTokenChanges()
    {
        using var temp = new TempDirectory();
        string projectRoot = CreateUnityProject(temp.Path, "SampleProject");
        string registryPath = Path.Combine(temp.Path, "instances.json");
        var registryStore = new InstanceRegistryStore(registryPath);
        SaveRegistry(registryStore, registryPath, projectRoot, "old-token");
        var seenTokens = new List<string?>();

        var result = await UnityCli.Cli.CliApp.ExecuteUnityCommandAsync(
            new ParsedCommand(CommandKind.Refresh),
            registryStore,
            projectRoot,
            (target, _, _, _) =>
            {
                seenTokens.Add(target.token);
                if (seenTokens.Count == 1)
                {
                    SaveRegistry(registryStore, registryPath, projectRoot, "new-token");
                    return Task.FromResult(Unauthorized());
                }

                return Task.FromResult(Success(new { retried = true }));
            });

        Assert.Equal(ProtocolConstants.StatusSuccess, result.status);
        Assert.Equal(["old-token", "new-token"], seenTokens);
    }

    [Fact]
    public async Task ExecuteUnityCommandAsync_WhenUnauthorizedTokenIsUnchanged_DoesNotRetryAndAddsHint()
    {
        using var temp = new TempDirectory();
        string projectRoot = CreateUnityProject(temp.Path, "SampleProject");
        string registryPath = Path.Combine(temp.Path, "instances.json");
        var registryStore = new InstanceRegistryStore(registryPath);
        SaveRegistry(registryStore, registryPath, projectRoot, "same-token");
        int calls = 0;

        var result = await UnityCli.Cli.CliApp.ExecuteUnityCommandAsync(
            new ParsedCommand(CommandKind.Refresh),
            registryStore,
            projectRoot,
            (_, _, _, _) =>
            {
                calls++;
                return Task.FromResult(Unauthorized());
            });

        Assert.Equal(1, calls);
        Assert.Equal(ProtocolConstants.StatusError, result.status);
        Assert.Equal(ProtocolConstants.ErrorUnauthorized, result.error?.code);
        Assert.Contains("registry heartbeat refreshes", result.error?.message);
    }

    [Fact]
    public async Task ExecuteUnityCommandAsync_WhenRetryIsStillUnauthorized_AddsHint()
    {
        using var temp = new TempDirectory();
        string projectRoot = CreateUnityProject(temp.Path, "SampleProject");
        string registryPath = Path.Combine(temp.Path, "instances.json");
        var registryStore = new InstanceRegistryStore(registryPath);
        SaveRegistry(registryStore, registryPath, projectRoot, "old-token");
        int calls = 0;

        var result = await UnityCli.Cli.CliApp.ExecuteUnityCommandAsync(
            new ParsedCommand(CommandKind.Refresh),
            registryStore,
            projectRoot,
            (_, _, _, _) =>
            {
                calls++;
                if (calls == 1)
                {
                    SaveRegistry(registryStore, registryPath, projectRoot, "new-token");
                }

                return Task.FromResult(Unauthorized());
            });

        Assert.Equal(2, calls);
        Assert.Equal(ProtocolConstants.StatusError, result.status);
        Assert.Equal(ProtocolConstants.ErrorUnauthorized, result.error?.code);
        Assert.Contains("registry heartbeat refreshes", result.error?.message);
    }

    [Fact]
    public async Task PollEditorReadyAsync_FailsFastWhenStatusPayloadIsInvalid()
    {
        var parsed = new ParsedCommand(CommandKind.Refresh) { Wait = true };
        int polls = 0;

        var result = await UnityCli.Cli.CliApp.PollEditorReadyAsync(
            parsed,
            Success(new { message = "started" }),
            (_, _, _) =>
            {
                polls++;
                return Task.FromResult(Success("not-json"));
            },
            delayAsync: (_, _) => Task.CompletedTask,
            timeout: TimeSpan.FromSeconds(5));

        Assert.Equal(1, polls);
        Assert.Equal(ProtocolConstants.StatusError, result.status);
        Assert.Equal("LIVE_UNAVAILABLE", result.error?.code);
        Assert.True(result.retryable);
        Assert.Contains("valid status payload", result.error?.details?.GetString());
    }

    private static ResponseEnvelope Success<T>(T data)
    {
        return ResponseEnvelope.Success(
            "req-1",
            "target-1",
            JsonSerializer.SerializeToElement(data, ProtocolJson.Default),
            durationMs: 1,
            transport: ProtocolConstants.TransportLive);
    }

    private static ResponseEnvelope Unauthorized()
    {
        return ResponseEnvelope.Failure(
            "req-1",
            "target-1",
            ProtocolConstants.ErrorUnauthorized,
            "Invalid IPC token.",
            retryable: false);
    }

    private static void SaveRegistry(InstanceRegistryStore registryStore, string registryPath, string projectRoot, string token)
    {
        string projectHash = ProtocolConstants.ComputeProjectHash(projectRoot);
        registryStore.Save(new InstanceRegistry
        {
            instances =
            [
                new InstanceRecord
                {
                    projectRoot = ProtocolConstants.GetCanonicalPath(projectRoot),
                    projectName = Path.GetFileName(projectRoot),
                    projectHash = projectHash,
                    pipeName = ProtocolConstants.BuildPipeName(projectHash),
                    state = "idle",
                    lastSeenUtc = DateTimeOffset.UtcNow.ToString("O"),
                },
            ],
        });
        InstanceRegistryFile.WriteTokenSidecar(registryPath, projectHash, token);
    }

    private static string CreateUnityProject(string root, string name)
    {
        string projectRoot = Path.Combine(root, name);
        Directory.CreateDirectory(Path.Combine(projectRoot, "Assets"));
        Directory.CreateDirectory(Path.Combine(projectRoot, "Packages"));
        return projectRoot;
    }
}
