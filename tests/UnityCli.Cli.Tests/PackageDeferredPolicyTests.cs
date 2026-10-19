using UnityCli.DocGen;
using UnityCli.Protocol;

namespace UnityCli.Cli.Tests;

public sealed class PackageDeferredPolicyTests
{
    public static TheoryData<string> PackageCommands()
    {
        var commands = new TheoryData<string>();
        commands.Add(ProtocolConstants.CommandPackageList);
        commands.Add(ProtocolConstants.CommandPackageAdd);
        commands.Add(ProtocolConstants.CommandPackageRemove);
        commands.Add(ProtocolConstants.CommandPackageSearch);
        return commands;
    }

    [Theory]
    [MemberData(nameof(PackageCommands))]
    public void IsDeferredPackageCommand_ReturnsTrueForAllPackageCommands(string command)
    {
        Assert.True(ProtocolHelpers.IsPackageCommand(command));
        Assert.True(ProtocolHelpers.IsDeferredPackageCommand(command));
    }

    [Fact]
    public void PackageRequestTimeoutPolicy_UsesFiveMinuteDefault()
    {
        Assert.Equal(300, ProtocolConstants.DefaultPackageRequestTimeoutSeconds);
        Assert.Equal(360_000, ProtocolConstants.DefaultPackageLiveTimeoutMs);
        Assert.False(ProtocolConstants.IsPackageRequestTimedOut(TimeSpan.FromMilliseconds(299_999)));
        Assert.True(ProtocolConstants.IsPackageRequestTimedOut(TimeSpan.FromSeconds(300)));
    }

    [Fact]
    public void PackageRequestTimeoutPolicy_UsesPackageTimeoutError()
    {
        Assert.Equal("PACKAGE_TIMEOUT", ProtocolConstants.ErrorPackageTimeout);
        Assert.Equal(
            "The package command did not finish within 300 seconds. Check Package Manager in the Editor.",
            ProtocolConstants.BuildPackageRequestTimeoutMessage(300));
    }

    [Fact]
    public void PackageBusyPolicy_UsesPackageBusyError()
    {
        Assert.Equal("PACKAGE_BUSY", ProtocolConstants.ErrorPackageBusy);
        Assert.Equal(
            "Another package command is in progress. Try again when it finishes.",
            ProtocolConstants.PackageBusyMessage);
    }

    [Fact]
    public void EditorPackageDispatch_UsesDeferredRouteWithoutSleepPolling()
    {
        string repoRoot = RepositoryPaths.FindRepoRoot(AppContext.BaseDirectory);
        string bridgeHost = File.ReadAllText(Path.Combine(
            repoRoot,
            "unity-package",
            "com.yhc509.unity-cli-bridge",
            "Editor",
            "BridgeHost.cs"));
        string packageHandler = File.ReadAllText(Path.Combine(
            repoRoot,
            "unity-package",
            "com.yhc509.unity-cli-bridge",
            "Editor",
            "PackageCommandHandler.cs"));

        Assert.Contains(
            "_packageCommandHandler.CanHandle(pending.Command.command) && _packageCommandHandler.IsDeferred",
            bridgeHost);
        Assert.Contains("StartDeferredPackageRequest(pending);", bridgeHost);
        Assert.Contains(
            "_packageCommandHandler.StartDeferred(command.command, command.argumentsJson, pending.Completion, _projectHash);",
            bridgeHost);

        Assert.DoesNotContain("Thread.Sleep", packageHandler);
        Assert.DoesNotContain("WaitForRequest", packageHandler);
        Assert.Contains("EditorTickPump.Add(Poll);", packageHandler);
        Assert.Contains("ProtocolConstants.ErrorPackageTimeout", packageHandler);
    }

    [Fact]
    public void EditorPackageDispatch_RejectsConcurrentPackageRequests()
    {
        string packageHandler = ReadPackageHandler();

        Assert.Contains("private static readonly object _activeLock", packageHandler);
        Assert.Contains("private static bool _hasActiveRequest", packageHandler);
        Assert.Contains("if (!TryBeginActiveRequest())", packageHandler);
        Assert.Contains("ProtocolConstants.ErrorPackageBusy", packageHandler);
        Assert.Contains("ProtocolConstants.PackageBusyMessage", packageHandler);
        Assert.Contains("completion.TrySetResult(ResponseEnvelope.Failure", packageHandler);
    }

    [Fact]
    public void EditorPackageDispatch_ReleasesActiveRequestWhenPollingFinishes()
    {
        string packageHandler = ReadPackageHandler();

        Assert.Contains("void FinishPolling()", packageHandler);
        Assert.Contains("EndActiveRequest();", packageHandler);
        Assert.Contains("if (completion.Task.IsCompleted)", packageHandler);
        Assert.True(
            CountOccurrences(packageHandler, "FinishPolling();") >= 4,
            "Package polling should finish on cancellation, success, request failure, and exception paths.");
    }

    [Fact]
    public void EditorPackageDispatch_KeepsActiveRequestAfterTimeoutUntilUnityRequestCompletes()
    {
        string packageHandler = ReadPackageHandler();
        int timeoutIndex = packageHandler.IndexOf("ProtocolConstants.ErrorPackageTimeout", StringComparison.Ordinal);
        int finishTimeoutIndex = packageHandler.IndexOf("FinishPollingAfterTimeout();", timeoutIndex, StringComparison.Ordinal);

        Assert.True(timeoutIndex >= 0, "Timeout response should use the PACKAGE_TIMEOUT error code.");
        Assert.True(finishTimeoutIndex > timeoutIndex, "Timeout response should enter the timeout-specific finish path.");
        Assert.DoesNotContain(
            "EndActiveRequest();",
            packageHandler.Substring(timeoutIndex, finishTimeoutIndex - timeoutIndex));
        Assert.Contains("void FinishPollingAfterTimeout()", packageHandler);
        Assert.Contains("StartBackgroundActiveRequestTracker(request);", packageHandler);
        Assert.Contains("private static void StartBackgroundActiveRequestTracker(Request request)", packageHandler);
        Assert.Contains("new ActiveRequestCompletionTracker(", packageHandler);
        Assert.Contains("() => request.IsCompleted", packageHandler);
        Assert.Contains("() => EditorTickPump.Remove(BackgroundPoll)", packageHandler);
    }

    private static string ReadPackageHandler()
    {
        string repoRoot = RepositoryPaths.FindRepoRoot(AppContext.BaseDirectory);
        return File.ReadAllText(Path.Combine(
            repoRoot,
            "unity-package",
            "com.yhc509.unity-cli-bridge",
            "Editor",
            "PackageCommandHandler.cs"));
    }

    private static int CountOccurrences(string value, string search)
    {
        int count = 0;
        int index = 0;
        while ((index = value.IndexOf(search, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += search.Length;
        }

        return count;
    }
}
