using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using UnityCli.Cli.Services;
using UnityCli.DocGen;
using UnityCli.Protocol;

namespace UnityCli.Cli.Tests;

[Collection(CurrentDirectoryCollection.Name)]
public sealed class CliAppTests
{
    private static readonly SemaphoreSlim ConsoleLock = new(1, 1);
    private const string GlobalUsage = "usage: unity-cli [--json] [--output <default|json|compact>] [--project <path|name>] <command> [options]";

    [Theory]
    [InlineData("--version")]
    [InlineData("version")]
    public async Task RunAsync_Version_PrintsPackageVersion(string command)
    {
        var result = await InvokeAsync([command]);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(string.Empty, result.Stderr);
        string packageVersion = ReadPackageVersion();
        string printed = result.Stdout.Trim();
        Assert.Equal("0.6.1", packageVersion);
        Assert.True(
            printed == packageVersion || printed.StartsWith(packageVersion + "+", StringComparison.Ordinal),
            "stdout was: " + printed);
    }

    private static string ReadPackageVersion()
    {
        string repoRoot = RepositoryPaths.FindRepoRoot(AppContext.BaseDirectory);
        string json = File.ReadAllText(Path.Combine(
            repoRoot,
            "unity-package",
            "com.yhc509.unity-cli-bridge",
            "package.json"));
        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.GetProperty("version").GetString() ?? string.Empty;
    }

    [Fact]
    public void ExecuteHelp_DescribesRoslynHostNotCodeDomRange()
    {
        string? help = CliCommandMetadata.BuildTopicHelpText("execute");

        Assert.NotNull(help);
        Assert.Contains("Roslyn host", help);
        Assert.Contains("C# 9", help);
        Assert.Contains("UCB_EXECUTE_COMPILER=codedom", help);
        Assert.DoesNotContain("CodeDOM compiler (mcs)", help);
        Assert.DoesNotContain("1 through 7.2", help);
    }

    [Fact]
    public async Task RunAsync_JsonUnknownCommand_WritesStructuredErrorToStdoutOnly()
    {
        var result = await InvokeAsync(["--json", "stats"]);

        Assert.Equal(2, result.ExitCode);
        Assert.Equal(string.Empty, result.Stderr);

        var response = ParseResponse(result.Stdout);
        Assert.Equal("error", response.status);
        Assert.Equal("CLI_USAGE", response.error?.code);
        Assert.Contains("Unknown command", response.error?.message);

        var details = ParseDetails(response.error?.details);
        Assert.Equal(GlobalUsage, details.GetProperty("usage").GetString());
        Assert.Equal("status", details.GetProperty("suggestions")[0].GetString());
    }

    [Fact]
    public async Task RunAsync_JsonUnknownSubcommand_IncludesSuggestionsAndAvailableCommands()
    {
        var result = await InvokeAsync(["--json", "asset", "delte"]);

        Assert.Equal(2, result.ExitCode);
        Assert.Equal(string.Empty, result.Stderr);

        var response = ParseResponse(result.Stdout);
        Assert.Equal("CLI_USAGE", response.error?.code);
        Assert.Contains("asset subcommand", response.error?.message);

        var details = ParseDetails(response.error?.details);
        Assert.Equal("asset delete", details.GetProperty("suggestions")[0].GetString());
        Assert.Equal("asset create", details.GetProperty("availableCommands")[0].GetString());
    }

    [Fact]
    public async Task RunAsync_JsonMissingRequiredOption_IncludesCommandUsage()
    {
        var result = await InvokeAsync(["--json", "asset", "delete", "--path", "Assets/Test.asset"]);

        Assert.Equal(2, result.ExitCode);
        Assert.Equal(string.Empty, result.Stderr);

        var response = ParseResponse(result.Stdout);
        Assert.Equal("CLI_USAGE", response.error?.code);
        Assert.Contains("--force", response.error?.message);

        var details = ParseDetails(response.error?.details);
        Assert.Equal(
            "usage: unity-cli [--json] [--output <default|json|compact>] [--project <path|name>] asset delete (<Assets/...> | --path <Assets/...>) --force",
            details.GetProperty("usage").GetString());
    }

    [Fact]
    public async Task RunAsync_SceneOpen_ConflictingPaths_IsCliUsage()
    {
        var result = await InvokeAsync([
            "--json",
            "scene",
            "open",
            "Assets/Scenes/Foo.unity",
            "--path",
            "Assets/Scenes/Bar.unity"
        ]);

        Assert.Equal(2, result.ExitCode);
        Assert.Equal(string.Empty, result.Stderr);

        var response = ParseResponse(result.Stdout);
        Assert.Equal("CLI_USAGE", response.error?.code);
        Assert.Contains("two different paths", response.error?.message);
        Assert.Contains("Assets/Scenes/Foo.unity", response.error?.message);
        Assert.Contains("Assets/Scenes/Bar.unity", response.error?.message);
    }

    [Fact]
    public async Task RunAsync_JsonInvalidOption_IncludesCommandUsage()
    {
        var result = await InvokeAsync(["status", "--json", "--verbose"]);

        Assert.Equal(2, result.ExitCode);
        Assert.Equal(string.Empty, result.Stderr);

        var response = ParseResponse(result.Stdout);
        Assert.Equal("CLI_USAGE", response.error?.code);
        Assert.Contains("Unsupported option", response.error?.message);

        var details = ParseDetails(response.error?.details);
        Assert.Equal(
            "usage: unity-cli [--json] [--output <default|json|compact>] [--project <path|name>] status",
            details.GetProperty("usage").GetString());
    }

    [Fact]
    public async Task RunAsync_CompactUnknownCommand_WritesReducedErrorJsonToStdoutOnly()
    {
        var result = await InvokeAsync(["--output", "compact", "stats"]);

        Assert.Equal(2, result.ExitCode);
        Assert.Equal(string.Empty, result.Stderr);

        JsonElement response = ParseJson(result.Stdout);
        Assert.Equal("CLI_USAGE", response.GetProperty("error").GetString());
        Assert.Contains("Unknown command", response.GetProperty("message").GetString());
    }

    [Fact]
    public async Task RunAsync_JsonQaSwipeInvalidCoordinate_WritesStructuredUsageError()
    {
        var result = await InvokeAsync(["--json", "qa", "swipe", "--from", "10,right", "--to", "20,30"]);

        Assert.Equal(2, result.ExitCode);
        Assert.Equal(string.Empty, result.Stderr);

        var response = ParseResponse(result.Stdout);
        Assert.Equal("CLI_USAGE", response.error?.code);
        Assert.Contains("--from", response.error?.message);

        var details = ParseDetails(response.error?.details);
        Assert.Contains("qa swipe", details.GetProperty("usage").GetString());
    }

    [Fact]
    public async Task RunAsync_JsonNoTarget_KeepsFailureStructuredOnStdout()
    {
        var result = await InvokeAsync(["--json", "compile"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(string.Empty, result.Stderr);

        var response = ParseResponse(result.Stdout);
        Assert.Equal("error", response.status);
        Assert.Equal("NO_TARGET", response.error?.code);
        Assert.Equal("cli", response.transport);
    }

    [Fact]
    public async Task RunAsync_JsonNoTarget_WhenOfflineInstancesExist_ListsThemInDetails()
    {
        using var temp = new TempDirectory();
        string projectRoot = CreateUnityProject(temp.Path, "SampleProject");
        string projectHash = ProtocolConstants.ComputeProjectHash(projectRoot);
        string registryContents =
            $$"""
            {"activeProjectRoot":"","instances":[{"projectRoot":"{{projectRoot.Replace("\\", "\\\\")}}","projectName":"SampleProject","projectHash":"{{projectHash}}","pipeName":"{{ProtocolConstants.BuildPipeName(projectHash).Replace("\\", "\\\\")}}","editorProcessId":0,"unityVersion":"6000.3.10f1","state":"offline","lastSeenUtc":"2026-04-02T03:19:16.4545650+00:00","capabilities":[]}]}
            """;
        string currentDirectory = Path.Combine(temp.Path, "cwd");
        Directory.CreateDirectory(currentDirectory);

        var result = await InvokeAsync(["--json", "compile"], registryContents: registryContents, currentDirectory: currentDirectory);

        Assert.Equal(1, result.ExitCode);
        var response = ParseResponse(result.Stdout);
        Assert.Equal("NO_TARGET", response.error?.code);
        var details = response.error?.details?.ToString() ?? string.Empty;
        Assert.Contains("SampleProject", details);
    }

    [Fact]
    public async Task RunAsync_JsonNoProject_TwoLiveInstances_ReturnsAmbiguousUsageError()
    {
        await AssertJsonNoProjectTwoLiveInstancesReturnsAmbiguousUsageError(["--json", "compile"]);
    }

    [Fact]
    public async Task RunAsync_JsonStatusNoProject_TwoLiveInstances_ReturnsAmbiguousUsageError()
    {
        await AssertJsonNoProjectTwoLiveInstancesReturnsAmbiguousUsageError(["--json", "status"]);
    }

    [Fact]
    public async Task RunAsync_JsonDoctorNoProject_TwoLiveInstances_ReturnsAmbiguousUsageError()
    {
        await AssertJsonNoProjectTwoLiveInstancesReturnsAmbiguousUsageError(["--json", "doctor"]);
    }

    [Fact]
    public async Task RunAsync_JsonMultipleProjects_ReturnsAmbiguousProjectError()
    {
        using var temp = new TempDirectory();
        string projectB = CreateUnityProject(Path.Combine(temp.Path, "z-group"), "Beta");
        string projectA = CreateUnityProject(Path.Combine(temp.Path, "a-group"), "Alpha");
        string canonicalA = ProtocolConstants.GetCanonicalPath(projectA);
        string canonicalB = ProtocolConstants.GetCanonicalPath(projectB);

        var result = await InvokeAsync(
            ["--json", "instances", "list"],
            registryContents: "{\"instances\":[]}",
            currentDirectory: temp.Path);

        Assert.Equal(2, result.ExitCode);
        Assert.Equal(string.Empty, result.Stderr);

        var response = ParseResponse(result.Stdout);
        Assert.Equal("error", response.status);
        Assert.Equal(AmbiguousProjectException.ErrorCode, response.error?.code);
        Assert.Equal("cli", response.transport);
        Assert.False(response.retryable);
        Assert.Contains("--project", response.error?.message);
        Assert.Contains(canonicalA, response.error?.message);
        Assert.Contains(canonicalB, response.error?.message);

        JsonElement details = ParseDetails(response.error?.details);
        string?[] candidates = details.GetProperty("candidates").EnumerateArray().Select(item => item.GetString()).ToArray();
        Assert.Equal(new[] { canonicalA, canonicalB }, candidates);
    }

    [Fact]
    public async Task RunAsync_CompactMultipleProjects_ReturnsAmbiguousProjectError()
    {
        using var temp = new TempDirectory();
        CreateUnityProject(temp.Path, "Alpha");
        CreateUnityProject(temp.Path, "Beta");

        var result = await InvokeAsync(
            ["--output", "compact", "instances", "list"],
            registryContents: "{\"instances\":[]}",
            currentDirectory: temp.Path);

        Assert.Equal(2, result.ExitCode);
        Assert.Equal(string.Empty, result.Stderr);

        JsonElement response = ParseJson(result.Stdout);
        Assert.Equal(AmbiguousProjectException.ErrorCode, response.GetProperty("error").GetString());
        Assert.Contains("--project", response.GetProperty("message").GetString());
    }

    [Fact]
    public async Task RunAsync_JsonMultipleProjects_SingleLiveEditor_SelectsThatProject()
    {
        using var temp = new TempDirectory();
        string projectA = CreateUnityProject(temp.Path, "ProjectA");
        string projectB = CreateUnityProject(temp.Path, "ProjectB");
        string canonicalB = ProtocolConstants.GetCanonicalPath(projectB);
        string hashB = ProtocolConstants.ComputeProjectHash(canonicalB);
        string registryContents =
            $$"""
            {"activeProjectRoot":"","instances":[{"projectRoot":"{{canonicalB.Replace("\\", "\\\\")}}","projectName":"ProjectB","projectHash":"{{hashB}}","pipeName":"{{ProtocolConstants.BuildPipeName(hashB).Replace("\\", "\\\\")}}","editorProcessId":{{Environment.ProcessId}},"unityVersion":"6000.3.10f1","state":"idle","lastSeenUtc":"{{DateTimeOffset.UtcNow.ToString("O")}}","capabilities":[]},{"projectRoot":"{{ProtocolConstants.GetCanonicalPath(projectA).Replace("\\", "\\\\")}}","projectName":"ProjectA","projectHash":"{{ProtocolConstants.ComputeProjectHash(projectA)}}","pipeName":"pipe-a","editorProcessId":0,"unityVersion":"6000.3.10f1","state":"offline","lastSeenUtc":"{{DateTimeOffset.UtcNow.ToString("O")}}","capabilities":[]}]}
            """;

        var result = await InvokeAsync(
            ["--json", "instances", "list"],
            registryContents: registryContents,
            currentDirectory: temp.Path);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(string.Empty, result.Stderr);

        JsonElement data = ParseResponse(result.Stdout).data!.Value;
        Assert.Equal(canonicalB, data.GetProperty("currentProjectRoot").GetString());
    }

    [Fact]
    public async Task RunAsync_JsonStatus_DeadRegistryProcess_DoesNotDisambiguate()
    {
        using var temp = new TempDirectory();
        string projectA = CreateUnityProject(temp.Path, "ProjectA");
        string projectB = CreateUnityProject(temp.Path, "ProjectB");
        string canonicalB = ProtocolConstants.GetCanonicalPath(projectB);
        int deadPid = StartAndKillProcess();
        string missingPipe = OperatingSystem.IsWindows()
            ? "unity-cli-missing-" + Guid.NewGuid().ToString("N")
            : Path.Combine(Path.GetTempPath(), "ucb-missing-" + Guid.NewGuid().ToString("N") + ".sock");
        string registryContents =
            $$"""
            {"activeProjectRoot":"","instances":[{"projectRoot":"{{canonicalB.Replace("\\", "\\\\")}}","projectName":"ProjectB","projectHash":"{{ProtocolConstants.ComputeProjectHash(canonicalB)}}","pipeName":"{{missingPipe.Replace("\\", "\\\\")}}","editorProcessId":{{deadPid}},"unityVersion":"6000.3.10f1","state":"idle","lastSeenUtc":"{{DateTimeOffset.UtcNow.ToString("O")}}","capabilities":[]}]}
            """;

        var result = await InvokeAsync(
            ["--json", "status"],
            registryContents: registryContents,
            currentDirectory: temp.Path);

        Assert.Equal(2, result.ExitCode);
        Assert.Equal(string.Empty, result.Stderr);
        Assert.DoesNotContain("liveReachable", result.Stdout);

        var response = ParseResponse(result.Stdout);
        Assert.Equal(AmbiguousProjectException.ErrorCode, response.error?.code);
        Assert.Contains("--project", response.error?.message);
        Assert.Contains(canonicalB, response.error?.message);
        Assert.Contains(ProtocolConstants.GetCanonicalPath(projectA), response.error?.message);
    }

    [Fact]
    public async Task RunAsync_JsonInstancesUse_SetsPinnedFlagInRegistry()
    {
        using var temp = new TempDirectory();
        string projectRoot = CreateUnityProject(temp.Path, "SampleProject");
        string projectHash = ProtocolConstants.ComputeProjectHash(projectRoot);
        string registryPath = Path.Combine(temp.Path, "instances.json");
        string registryContents =
            $$"""
            {"activeProjectRoot":"","instances":[{"projectRoot":"{{projectRoot.Replace("\\", "\\\\")}}","projectName":"SampleProject","projectHash":"{{projectHash}}","pipeName":"{{ProtocolConstants.BuildPipeName(projectHash).Replace("\\", "\\\\")}}","editorProcessId":1234,"unityVersion":"6000.3.10f1","state":"idle","lastSeenUtc":"{{DateTimeOffset.UtcNow.ToString("O")}}","capabilities":[]}]}
            """;
        string currentDirectory = Path.Combine(temp.Path, "cwd");
        Directory.CreateDirectory(currentDirectory);

        var result = await InvokeAsync(
            ["--json", "instances", "use", projectRoot],
            registryContents: registryContents,
            currentDirectory: currentDirectory,
            registryPath: registryPath);

        Assert.Equal(0, result.ExitCode);
        var reloaded = new InstanceRegistryStore(result.RegistryPath).Load();
        Assert.True(reloaded.activeProjectRootPinned);
        Assert.Equal(ProtocolConstants.GetCanonicalPath(projectRoot), reloaded.activeProjectRoot);
    }

    [Fact]
    public async Task RunAsync_Help_ExplainsProjectPathPriority()
    {
        var result = await InvokeAsync(["help"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(string.Empty, result.Stderr);
        Assert.Contains(GlobalUsage, result.Stdout);
        Assert.Contains("--json                Equivalent to --output json. If both --json and --output are specified, the last one wins.", result.Stdout);
        Assert.Contains("--output <mode>       Response format: default, json (full envelope), or compact (data payload / compact error JSON).", result.Stdout);
        Assert.Contains("--project <path|name>  Existing directory paths take precedence over registered project names.", result.Stdout);
        Assert.Contains("Project-name matches are case-insensitive.", result.Stdout);
    }

    [Fact]
    public async Task RunAsync_JsonProjectOverride_DoesNotFallBackToActiveProject()
    {
        using var temp = new TempDirectory();
        string projectRoot = CreateUnityProject(temp.Path, "SampleProject");
        string otherProjectRoot = CreateUnityProject(temp.Path, "OtherProject");
        string otherProjectHash = ProtocolConstants.ComputeProjectHash(otherProjectRoot);
        string expectedProjectHash = ProtocolConstants.ComputeProjectHash(projectRoot);
        string registryContents =
            $$"""
            {"activeProjectHash":"{{otherProjectHash}}","instances":[{"projectRoot":"{{otherProjectRoot.Replace("\\", "\\\\")}}","projectName":"OtherProject","projectHash":"{{otherProjectHash}}","pipeName":"{{ProtocolConstants.BuildPipeName(otherProjectHash).Replace("\\", "\\\\")}}","editorProcessId":1234,"unityVersion":"6000.3.10f1","state":"idle","lastSeenUtc":"2026-04-02T03:19:16.4545650+00:00","capabilities":[]}]}
            """;

        var result = await InvokeAsync(["--json", "--project", projectRoot, "compile"], registryContents: registryContents);

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(string.Empty, result.Stderr);

        var response = ParseResponse(result.Stdout);
        Assert.Equal("LIVE_UNAVAILABLE", response.error?.code);
        Assert.Equal(expectedProjectHash, response.target);
        Assert.DoesNotContain(otherProjectHash, result.Stdout);
    }

    [Fact]
    public async Task RunAsync_JsonProjectOverride_ProjectName_UsesRegisteredProjectRoot()
    {
        using var projects = new TempDirectory();
        string projectRoot = CreateUnityProject(projects.Path, "SampleProject");
        string expectedProjectHash = ProtocolConstants.ComputeProjectHash(projectRoot);
        string registryContents =
            $$"""
            {"instances":[{"projectRoot":"{{projectRoot.Replace("\\", "\\\\")}}","projectName":"SampleProject","projectHash":"{{expectedProjectHash}}","pipeName":"{{ProtocolConstants.BuildPipeName(expectedProjectHash).Replace("\\", "\\\\")}}","editorProcessId":1234,"unityVersion":"6000.3.10f1","state":"idle","lastSeenUtc":"2026-04-02T03:19:16.4545650+00:00","capabilities":[]}]}
            """;

        var result = await InvokeAsync(["--json", "--project", "SampleProject", "compile"], registryContents: registryContents);

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(string.Empty, result.Stderr);

        var response = ParseResponse(result.Stdout);
        Assert.Equal("LIVE_UNAVAILABLE", response.error?.code);
        Assert.Equal(expectedProjectHash, response.target);
    }

    [Fact]
    public async Task RunAsync_JsonProjectOverride_ProjectName_WhenRegistryDoesNotMatch_ReturnsUsageError()
    {
        const string projectOverride = "UnityCliBridge";
        var result = await InvokeAsync(["--json", "--project", projectOverride, "compile"], registryContents: "{\"instances\":[]}");

        Assert.Equal(2, result.ExitCode);
        Assert.Equal(string.Empty, result.Stderr);

        var response = ParseResponse(result.Stdout);
        Assert.Equal("CLI_USAGE", response.error?.code);
        Assert.Equal(
            "'UnityCliBridge' is not a registered project name or a valid directory path. Run 'unity-cli instances list' to see registered projects.",
            response.error?.message);
    }

    [Fact]
    public async Task RunAsync_JsonProjectOverride_DotPath_UsesCurrentProjectPath()
    {
        using var temp = new TempDirectory();
        string projectRoot = CreateUnityProject(temp.Path, "SampleProject");
        string expectedProjectHash = ProtocolConstants.ComputeProjectHash(projectRoot);

        var result = await InvokeAsync(["--json", "--project", ".", "compile"], currentDirectory: projectRoot);

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(string.Empty, result.Stderr);

        var response = ParseResponse(result.Stdout);
        Assert.Equal("LIVE_UNAVAILABLE", response.error?.code);
        Assert.Equal(expectedProjectHash, response.target);
    }

    [Fact]
    public async Task RunAsync_JsonProjectOverride_ProjectName_WhenAmbiguous_ReturnsUsageError()
    {
        using var projects = new TempDirectory();
        string firstProjectRoot = CreateUnityProject(projects.Path, "SampleProjectA");
        string secondProjectRoot = CreateUnityProject(projects.Path, "SampleProjectB");
        string registryContents =
            $$"""
            {"instances":[{"projectRoot":"{{firstProjectRoot.Replace("\\", "\\\\")}}","projectName":"UnityCliBridge","projectHash":"{{ProtocolConstants.ComputeProjectHash(firstProjectRoot)}}","pipeName":"{{ProtocolConstants.BuildPipeName(ProtocolConstants.ComputeProjectHash(firstProjectRoot)).Replace("\\", "\\\\")}}","editorProcessId":1234,"unityVersion":"6000.3.10f1","state":"idle","lastSeenUtc":"2026-04-02T03:19:16.4545650+00:00","capabilities":[]},{"projectRoot":"{{secondProjectRoot.Replace("\\", "\\\\")}}","projectName":"UnityCliBridge","projectHash":"{{ProtocolConstants.ComputeProjectHash(secondProjectRoot)}}","pipeName":"{{ProtocolConstants.BuildPipeName(ProtocolConstants.ComputeProjectHash(secondProjectRoot)).Replace("\\", "\\\\")}}","editorProcessId":5678,"unityVersion":"6000.3.10f1","state":"idle","lastSeenUtc":"2026-04-02T03:19:16.4545650+00:00","capabilities":[]}]}
            """;

        var result = await InvokeAsync(["--json", "--project", "UnityCliBridge", "compile"], registryContents: registryContents);

        Assert.Equal(2, result.ExitCode);
        Assert.Equal(string.Empty, result.Stderr);

        var response = ParseResponse(result.Stdout);
        Assert.Equal("CLI_USAGE", response.error?.code);
        Assert.Contains("ambiguous", response.error?.message);
        Assert.Contains(ProtocolConstants.GetCanonicalPath(firstProjectRoot), response.error?.message);
        Assert.Contains(ProtocolConstants.GetCanonicalPath(secondProjectRoot), response.error?.message);
    }

    [Fact]
    public async Task RunAsync_JsonProjectOverride_PathTakesPrecedenceOverRegisteredProjectName()
    {
        using var projects = new TempDirectory();
        string pathProjectRoot = CreateUnityProject(projects.Path, "UnityCliBridge");
        string registeredProjectRoot = CreateUnityProject(projects.Path, "RegisteredProject");
        string pathProjectHash = ProtocolConstants.ComputeProjectHash(pathProjectRoot);
        string registeredProjectHash = ProtocolConstants.ComputeProjectHash(registeredProjectRoot);
        string registryContents =
            $$"""
            {"instances":[{"projectRoot":"{{registeredProjectRoot.Replace("\\", "\\\\")}}","projectName":"UnityCliBridge","projectHash":"{{registeredProjectHash}}","pipeName":"{{ProtocolConstants.BuildPipeName(registeredProjectHash).Replace("\\", "\\\\")}}","editorProcessId":1234,"unityVersion":"6000.3.10f1","state":"idle","lastSeenUtc":"2026-04-02T03:19:16.4545650+00:00","capabilities":[]}]}
            """;

        var result = await InvokeAsync(
            ["--json", "--project", "UnityCliBridge", "compile"],
            registryContents: registryContents,
            currentDirectory: projects.Path);

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(string.Empty, result.Stderr);

        var response = ParseResponse(result.Stdout);
        Assert.Equal("LIVE_UNAVAILABLE", response.error?.code);
        Assert.Equal(pathProjectHash, response.target);
        Assert.NotEqual(registeredProjectHash, response.target);
    }

    [Fact]
    public async Task RunAsync_JsonInstancesList_ReportsActiveAndCurrentProjectRoot()
    {
        using var projects = new TempDirectory();
        string projectRoot = CreateUnityProject(projects.Path, "SampleProject");
        string canonicalProjectRoot = ProtocolConstants.GetCanonicalPath(projectRoot);
        string projectHash = ProtocolConstants.ComputeProjectHash(canonicalProjectRoot);
        string registryContents =
            $$"""
            {"activeProjectRoot":"{{canonicalProjectRoot.Replace("\\", "\\\\")}}","instances":[{"projectRoot":"{{canonicalProjectRoot.Replace("\\", "\\\\")}}","projectName":"SampleProject","projectHash":"{{projectHash}}","pipeName":"{{ProtocolConstants.BuildPipeName(projectHash).Replace("\\", "\\\\")}}","editorProcessId":1234,"unityVersion":"6000.3.10f1","state":"idle","lastSeenUtc":"{{DateTimeOffset.UtcNow.ToString("O")}}","capabilities":[]}]}
            """;

        var result = await InvokeAsync(["--json", "instances", "list"], registryContents: registryContents, currentDirectory: projectRoot);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(string.Empty, result.Stderr);

        var response = ParseResponse(result.Stdout);
        Assert.True(response.data.HasValue);
        JsonElement data = response.data.Value;
        Assert.Equal(canonicalProjectRoot, data.GetProperty("activeProjectRoot").GetString());
        Assert.Equal(canonicalProjectRoot, data.GetProperty("currentProjectRoot").GetString());
        Assert.Equal(projectHash, data.GetProperty("currentProjectHash").GetString());
        Assert.False(data.TryGetProperty("activeProjectHash", out _));
    }

    [Fact]
    public async Task RunAsync_JsonInstancesListBrief_TrimsInternalInstanceFields()
    {
        using var projects = new TempDirectory();
        string projectRoot = CreateUnityProject(projects.Path, "SampleProject");
        string canonicalProjectRoot = ProtocolConstants.GetCanonicalPath(projectRoot);
        string projectHash = ProtocolConstants.ComputeProjectHash(canonicalProjectRoot);
        string registryContents =
            $$"""
            {"activeProjectRoot":"{{canonicalProjectRoot.Replace("\\", "\\\\")}}","instances":[{"projectRoot":"{{canonicalProjectRoot.Replace("\\", "\\\\")}}","projectName":"SampleProject","projectHash":"{{projectHash}}","pipeName":"{{ProtocolConstants.BuildPipeName(projectHash).Replace("\\", "\\\\")}}","editorProcessId":1234,"unityVersion":"6000.3.10f1","state":"idle","lastSeenUtc":"{{DateTimeOffset.UtcNow.ToString("O")}}","capabilities":["qa"]}]}
            """;

        var result = await InvokeAsync(["--json", "instances", "list", "--brief"], registryContents: registryContents, currentDirectory: projectRoot);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(string.Empty, result.Stderr);

        var response = ParseResponse(result.Stdout);
        JsonElement instance = response.data!.Value.GetProperty("instances")[0];
        Assert.Equal("SampleProject", instance.GetProperty("projectName").GetString());
        Assert.Equal(canonicalProjectRoot, instance.GetProperty("projectRoot").GetString());
        Assert.Equal(projectHash, instance.GetProperty("projectHash").GetString());
        Assert.Equal("idle", instance.GetProperty("state").GetString());
        Assert.False(instance.TryGetProperty("pipeName", out _));
        Assert.False(instance.TryGetProperty("lastSeenUtc", out _));
        Assert.False(instance.TryGetProperty("editorProcessId", out _));
        Assert.False(instance.TryGetProperty("capabilities", out _));
    }

    [Fact]
    public async Task RunAsync_JsonDoctor_WhenProtocolMismatch_ReportsLiveErrorFields()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var projects = new TempDirectory();
        string projectRoot = CreateUnityProject(projects.Path, "SampleProject");
        string canonicalProjectRoot = ProtocolConstants.GetCanonicalPath(projectRoot);
        string projectHash = ProtocolConstants.ComputeProjectHash(canonicalProjectRoot);
        string socketPath = Path.Combine("/tmp", "ucb-" + Guid.NewGuid().ToString("N") + ".sock");
        var staleResponse = ResponseEnvelope.Success(
            "req-1",
            projectHash,
            data: null,
            durationMs: 1,
            transport: ProtocolConstants.TransportLive);
        staleResponse.protocolVersion = "3";
        string registryContents =
            $$"""
            {"activeProjectRoot":"{{canonicalProjectRoot.Replace("\\", "\\\\")}}","instances":[{"projectRoot":"{{canonicalProjectRoot.Replace("\\", "\\\\")}}","projectName":"SampleProject","projectHash":"{{projectHash}}","pipeName":"{{socketPath.Replace("\\", "\\\\")}}","editorProcessId":1234,"unityVersion":"6000.3.10f1","state":"idle","lastSeenUtc":"{{DateTimeOffset.UtcNow.ToString("O")}}","capabilities":[]}]}
            """;

        using var listener = StartUnixSocketResponder(socketPath, ProtocolJson.Serialize(staleResponse));

        try
        {
            var result = await InvokeAsync(["--json", "doctor"], registryContents: registryContents, currentDirectory: projectRoot);

            Assert.Equal(0, result.ExitCode);
            Assert.Equal(string.Empty, result.Stderr);

            var response = ParseResponse(result.Stdout);
            Assert.Equal("success", response.status);
            Assert.True(response.data.HasValue);
            JsonElement data = response.data.Value;
            Assert.False(data.GetProperty("liveReachable").GetBoolean());
            Assert.Equal(ProtocolConstants.ErrorProtocolMismatch, data.GetProperty("liveErrorCode").GetString());
            Assert.Contains("incompatible", data.GetProperty("liveErrorMessage").GetString());
        }
        finally
        {
            listener.Dispose();
            DeleteSocketFile(socketPath);
        }
    }

    [Fact]
    public async Task RunAsync_JsonInstancesUse_ProjectName_WhenRegistryDoesNotMatch_ReturnsUsageError()
    {
        using var temp = new TempDirectory();
        string fallbackProjectRoot = CreateUnityProject(temp.Path, "FallbackProject");
        var result = await InvokeAsync(
            ["--json", "instances", "use", "TypoProject"],
            registryContents: "{\"instances\":[]}",
            currentDirectory: fallbackProjectRoot);

        Assert.Equal(2, result.ExitCode);
        Assert.Equal(string.Empty, result.Stderr);

        var response = ParseResponse(result.Stdout);
        Assert.Equal("CLI_USAGE", response.error?.code);
        Assert.Equal(
            "'TypoProject' is not a known project hash, a registered project name, or a valid directory path. Run 'unity-cli instances list' to see registered projects.",
            response.error?.message);
    }

    [Fact]
    public async Task RunAsync_JsonUnexpectedException_WritesCliErrorToStdoutOnly()
    {
        var result = await InvokeAsync(
            ["--json", "compile"],
            registryContents: "{\"instances\":");

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(string.Empty, result.Stderr);

        var response = ParseResponse(result.Stdout);
        Assert.Equal("CLI_ERROR", response.error?.code);
        Assert.Contains("JsonException", response.error?.details?.GetString());
    }

    [Fact]
    public async Task RunAsync_JsonRawPayloadValidation_IsNotMaskedByNoTarget()
    {
        var result = await InvokeAsync(["--json", "raw", "--json", "{"]);

        Assert.Equal(2, result.ExitCode);
        Assert.Equal(string.Empty, result.Stderr);

        var response = ParseResponse(result.Stdout);
        Assert.Equal("CLI_USAGE", response.error?.code);
        Assert.Contains("valid JSON", response.error?.message);

        var details = ParseDetails(response.error?.details);
        Assert.Equal(
            "usage: unity-cli [--json] [--output <default|json|compact>] [--project <path|name>] raw [--force] --json '{\"command\":\"status\",\"arguments\":{}}'",
            details.GetProperty("usage").GetString());
    }

    [Fact]
    public async Task RunAsync_UnknownCommand_ShowsSuggestionInStderr()
    {
        var result = await InvokeAsync(["stats"]);

        Assert.Equal(2, result.ExitCode);
        Assert.Equal(string.Empty, result.Stdout);
        Assert.Contains("Unknown command: stats", result.Stderr);
        Assert.Contains("Similar commands:", result.Stderr);
        Assert.Contains("status", result.Stderr);
    }

    [Fact]
    public async Task RunAsync_UnknownSubcommand_ShowsAvailableChoicesInStderr()
    {
        var result = await InvokeAsync(["asset", "delte"]);

        Assert.Equal(2, result.ExitCode);
        Assert.Equal(string.Empty, result.Stdout);
        Assert.Contains("Unknown asset subcommand: delte", result.Stderr);
        Assert.Contains("Similar `asset` subcommands:", result.Stderr);
        Assert.Contains("asset delete", result.Stderr);
        Assert.Contains("Available `asset` subcommands:", result.Stderr);
        Assert.Contains("asset create", result.Stderr);
    }

    [Fact]
    public async Task RunAsync_MissingRequiredOption_ShowsCommandUsageInStderr()
    {
        var result = await InvokeAsync(["asset", "delete", "--path", "Assets/Test.asset"]);

        Assert.Equal(2, result.ExitCode);
        Assert.Equal(string.Empty, result.Stdout);
        Assert.Contains("--force", result.Stderr);
        Assert.Contains(
            "usage: unity-cli [--json] [--output <default|json|compact>] [--project <path|name>] asset delete (<Assets/...> | --path <Assets/...>) --force",
            result.Stderr);
    }

    [Fact]
    public async Task RunAsync_InvalidOption_ShowsCommandUsageInStderr()
    {
        var result = await InvokeAsync(["status", "--verbose"]);

        Assert.Equal(2, result.ExitCode);
        Assert.Equal(string.Empty, result.Stdout);
        Assert.Contains("Unsupported option: --verbose", result.Stderr);
        Assert.Contains("usage: unity-cli [--json] [--output <default|json|compact>] [--project <path|name>] status", result.Stderr);
    }

    [Fact]
    public void BuildHelpText_DescribesProjectPathPrecedence()
    {
        var helpText = CliCommandMetadata.BuildHelpText();

        Assert.Contains(GlobalUsage, helpText);
        Assert.Contains("--json                Equivalent to --output json. If both --json and --output are specified, the last one wins.", helpText);
        Assert.Contains("Existing directory paths take precedence over registered project names.", helpText);
        Assert.Contains("Project-name matches are case-insensitive.", helpText);
    }

    private static ResponseEnvelope ParseResponse(string stdout)
    {
        Assert.False(string.IsNullOrWhiteSpace(stdout));
        return ProtocolJson.Deserialize<ResponseEnvelope>(stdout.Trim());
    }

    private static JsonElement ParseDetails(JsonElement? details)
    {
        Assert.True(details.HasValue);
        return details.Value;
    }

    private static JsonElement ParseJson(string json)
    {
        Assert.False(string.IsNullOrWhiteSpace(json));
        return JsonSerializer.Deserialize<JsonElement>(json.Trim());
    }

    private static async Task AssertJsonNoProjectTwoLiveInstancesReturnsAmbiguousUsageError(string[] args)
    {
        using var temp = new TempDirectory();
        string projectA = CreateUnityProject(temp.Path, "ProjectA");
        string projectB = CreateUnityProject(temp.Path, "ProjectB");
        string hashA = ProtocolConstants.ComputeProjectHash(projectA);
        string hashB = ProtocolConstants.ComputeProjectHash(projectB);
        string now = DateTimeOffset.UtcNow.ToString("O");
        string registryContents =
            $$"""
            {"activeProjectRoot":"","instances":[{"projectRoot":"{{projectA.Replace("\\", "\\\\")}}","projectName":"ProjectA","projectHash":"{{hashA}}","pipeName":"{{ProtocolConstants.BuildPipeName(hashA).Replace("\\", "\\\\")}}","editorProcessId":1234,"unityVersion":"6000.3.10f1","state":"idle","lastSeenUtc":"{{now}}","capabilities":[]},{"projectRoot":"{{projectB.Replace("\\", "\\\\")}}","projectName":"ProjectB","projectHash":"{{hashB}}","pipeName":"{{ProtocolConstants.BuildPipeName(hashB).Replace("\\", "\\\\")}}","editorProcessId":1235,"unityVersion":"6000.3.10f1","state":"idle","lastSeenUtc":"{{now}}","capabilities":[]}]}
            """;
        string currentDirectory = Path.Combine(temp.Path, "cwd");
        Directory.CreateDirectory(currentDirectory);

        var result = await InvokeAsync(args, registryContents: registryContents, currentDirectory: currentDirectory);

        Assert.Equal(2, result.ExitCode);
        var response = ParseResponse(result.Stdout);
        Assert.Equal("CLI_USAGE", response.error?.code);
        Assert.Contains("ProjectA", result.Stdout);
        Assert.Contains("ProjectB", result.Stdout);
    }

    private static async Task<CliInvocationResult> InvokeAsync(
        string[] args,
        string? registryContents = null,
        string? currentDirectory = null,
        string? registryPath = null)
    {
        await ConsoleLock.WaitAsync();

        try
        {
            using var temp = new TempDirectory();
            registryPath ??= Path.Combine(temp.Path, "instances.json");
            if (registryContents is not null)
            {
                File.WriteAllText(registryPath, registryContents);
            }

            string? originalRegistryPath = Environment.GetEnvironmentVariable("UNITY_CLI_REGISTRY_PATH");
            string originalCurrentDirectory = Environment.CurrentDirectory;
            TextWriter originalOut = Console.Out;
            TextWriter originalError = Console.Error;

            using var stdout = new StringWriter();
            using var stderr = new StringWriter();

            try
            {
                Environment.SetEnvironmentVariable("UNITY_CLI_REGISTRY_PATH", registryPath);
                Environment.CurrentDirectory = currentDirectory ?? temp.Path;
                Console.SetOut(stdout);
                Console.SetError(stderr);

                int exitCode = await UnityCli.Cli.CliApp.RunAsync(args);
                return new CliInvocationResult(exitCode, stdout.ToString(), stderr.ToString(), registryPath);
            }
            finally
            {
                Console.SetOut(originalOut);
                Console.SetError(originalError);
                Environment.CurrentDirectory = originalCurrentDirectory;
                Environment.SetEnvironmentVariable("UNITY_CLI_REGISTRY_PATH", originalRegistryPath);
            }
        }
        finally
        {
            ConsoleLock.Release();
        }
    }

    private static int StartAndKillProcess()
    {
        var start = new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sleep",
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (OperatingSystem.IsWindows())
        {
            start.ArgumentList.Add("/c");
            start.ArgumentList.Add("ping -n 30 127.0.0.1 > nul");
        }
        else
        {
            start.ArgumentList.Add("30");
        }

        using Process process = Process.Start(start)!;
        int pid = process.Id;
        process.Kill(entireProcessTree: true);
        process.WaitForExit();
        return pid;
    }

    private static string CreateUnityProject(string root, string name)
    {
        string projectRoot = Path.Combine(root, name);
        Directory.CreateDirectory(Path.Combine(projectRoot, "Assets"));
        Directory.CreateDirectory(Path.Combine(projectRoot, "Packages"));
        return projectRoot;
    }

    private static Socket StartUnixSocketResponder(string socketPath, string responseLine)
    {
        var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(socketPath));
        listener.Listen(1);

        _ = Task.Run(async () =>
        {
            try
            {
                using var client = await listener.AcceptAsync();
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

    private static void DeleteSocketFile(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private sealed record CliInvocationResult(int ExitCode, string Stdout, string Stderr, string RegistryPath);
}
