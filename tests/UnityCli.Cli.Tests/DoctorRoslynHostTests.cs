using System.Text.Json;
using UnityCli.DocGen;
using UnityCli.Protocol;

namespace UnityCli.Cli.Tests;

public sealed class DoctorRoslynHostTests
{
    [Fact]
    public void NoStatusFile_ReportsTheDefaultCompiler()
    {
        using var temp = new TempDirectory();

        JsonElement data = Serialize(UnityCli.Cli.CliApp.BuildRoslynHostData(temp.Path));

        Assert.False(data.GetProperty("present").GetBoolean());
        Assert.Equal("roslyn-host", data.GetProperty("activeBackend").GetString());
        Assert.Equal(RoslynHostStatus.GetStatusPath(temp.Path), data.GetProperty("statusPath").GetString());
        Assert.Contains("default compiler is the Roslyn host", data.GetProperty("note").GetString());
        Assert.Contains("UCB_EXECUTE_COMPILER=codedom", data.GetProperty("note").GetString());
    }

    [Fact]
    public void NoProjectRoot_OmitsTheSection()
    {
        Assert.Null(UnityCli.Cli.CliApp.BuildRoslynHostData(null));
        Assert.Null(UnityCli.Cli.CliApp.BuildRoslynHostData(" "));
    }

    [Fact]
    public void LiveHost_ReportsPidUptimePrewarmAndStderrTail()
    {
        using var temp = new TempDirectory();
        DateTime started = new DateTime(2026, 10, 22, 8, 0, 0, DateTimeKind.Utc);
        WriteStatus(temp.Path, started);

        JsonElement data = Serialize(UnityCli.Cli.CliApp.BuildRoslynHostData(
            temp.Path,
            pid => pid == 4100 || pid == 500,
            started.AddSeconds(90),
            RunningEditor(4100)));

        Assert.True(data.GetProperty("present").GetBoolean());
        Assert.False(data.GetProperty("oldSession").GetBoolean());
        Assert.False(data.GetProperty("stale").GetBoolean());
        Assert.True(data.GetProperty("editorAlive").GetBoolean());
        Assert.True(data.GetProperty("hostAlive").GetBoolean());
        Assert.Equal("roslyn-host", data.GetProperty("activeBackend").GetString());
        Assert.Equal("ready", data.GetProperty("state").GetString());
        Assert.Equal(500, data.GetProperty("pid").GetInt32());
        Assert.Equal(90.0, data.GetProperty("uptimeSeconds").GetDouble());
        Assert.Equal(280.0, data.GetProperty("prewarmMs").GetDouble());
        Assert.Equal("6.0.21", data.GetProperty("runtimeVersion").GetString());
        Assert.True(data.GetProperty("build").GetProperty("cacheHit").GetBoolean());
        Assert.Equal("[ucb-roslyn-host] ready", data.GetProperty("stderrTail")[0].GetString());
        Assert.Equal(string.Empty, data.GetProperty("sessionDisabledReason").GetString());
    }

    [Fact]
    public void DeadEditor_IsStaleAndHasNoUptime()
    {
        using var temp = new TempDirectory();
        WriteStatus(temp.Path, DateTime.UtcNow);

        JsonElement data = Serialize(UnityCli.Cli.CliApp.BuildRoslynHostData(temp.Path, _ => false));

        Assert.True(data.GetProperty("oldSession").GetBoolean());
        Assert.True(data.GetProperty("stale").GetBoolean());
        Assert.Equal(string.Empty, data.GetProperty("activeBackend").GetString());
        Assert.Contains("Old-session record", data.GetProperty("note").GetString());
        Assert.False(data.GetProperty("hostAlive").GetBoolean());
        Assert.Equal(JsonValueKind.Null, data.GetProperty("uptimeSeconds").ValueKind);
    }

    [Fact]
    public void DifferentEditorPid_IsAnOldSession_AndClearsActiveBackend()
    {
        using var temp = new TempDirectory();
        WriteStatus(temp.Path, DateTime.UtcNow);

        JsonElement data = Serialize(UnityCli.Cli.CliApp.BuildRoslynHostData(
            temp.Path,
            pid => pid == 4100 || pid == 9999,
            runningEditor: RunningEditor(9999)));

        Assert.True(data.GetProperty("oldSession").GetBoolean());
        Assert.Equal(string.Empty, data.GetProperty("activeBackend").GetString());
        Assert.Equal(4100, data.GetProperty("editorPid").GetInt32());
        Assert.Equal(9999, data.GetProperty("runningEditorPid").GetInt32());
        Assert.Contains("Old-session record", data.GetProperty("note").GetString());
    }

    [Fact]
    public void StatusWrittenBeforeTheEditorStarted_IsAnOldSession_EvenWhenThePidMatches()
    {
        using var temp = new TempDirectory();
        WriteStatus(temp.Path, DateTime.UtcNow);
        string path = RoslynHostStatus.GetStatusPath(temp.Path);
        DateTime started = DateTime.UtcNow;
        File.SetLastWriteTimeUtc(path, started.AddMinutes(-30));

        JsonElement data = Serialize(UnityCli.Cli.CliApp.BuildRoslynHostData(
            temp.Path,
            pid => pid == 4100,
            runningEditor: new UnityCli.Cli.CliApp.DoctorRunningEditor(4100, started)));

        Assert.True(data.GetProperty("oldSession").GetBoolean());
        Assert.Equal(string.Empty, data.GetProperty("activeBackend").GetString());
    }

    [Fact]
    public void CodedomOffStatus_MatchesTheRunningEditor()
    {
        using var temp = new TempDirectory();
        new RoslynHostStatus
        {
            EditorPid = 100,
            ActiveBackend = "roslyn-host",
            State = "ready",
            Note = "previous session",
        }.Write(RoslynHostStatus.GetStatusPath(temp.Path));

        RoslynHostStatus.WriteCodedomOff(temp.Path, 4100);

        RoslynHostStatus? written = RoslynHostStatus.TryRead(RoslynHostStatus.GetStatusPath(temp.Path));
        Assert.NotNull(written);
        Assert.Equal("off", written!.State);
        Assert.Equal("codedom", written.ActiveBackend);
        Assert.Equal("codedom", written.ConfiguredCompiler);
        Assert.Equal(4100, written.EditorPid);

        JsonElement data = Serialize(UnityCli.Cli.CliApp.BuildRoslynHostData(
            temp.Path,
            pid => pid == 4100,
            runningEditor: RunningEditor(4100)));

        Assert.False(data.GetProperty("oldSession").GetBoolean());
        Assert.Equal("codedom", data.GetProperty("activeBackend").GetString());
        Assert.Equal("off", data.GetProperty("state").GetString());
        Assert.Contains("UCB_EXECUTE_COMPILER=codedom", data.GetProperty("note").GetString());
    }

    [Fact]
    public void CodedomDomainLoad_QueuesTheOffStatusBeforeTheHost()
    {
        string repoRoot = RepositoryPaths.FindRepoRoot(AppContext.BaseDirectory);
        string source = File.ReadAllText(Path.Combine(
            repoRoot,
            "unity-package",
            "com.yhc509.unity-cli-bridge",
            "Editor",
            "Execute",
            "Compile",
            "RoslynHostEditorLifecycle.cs"));

        int ctor = source.IndexOf("static RoslynHostEditorLifecycle()", StringComparison.Ordinal);
        int codedom = source.IndexOf("ExecuteCompilerMode.CodeDom", ctor, StringComparison.Ordinal);
        int queue = source.IndexOf("QueueCodedomOffStatus()", ctor, StringComparison.Ordinal);
        int host = source.IndexOf("if (!_settings.UsesRoslynHost)", ctor, StringComparison.Ordinal);
        int writeHost = source.IndexOf("RoslynHostStatus.WriteCodedomOff", StringComparison.Ordinal);
        int writeCsc = source.IndexOf("CscSharedStatus.WriteCodedomOff", StringComparison.Ordinal);

        Assert.True(ctor >= 0 && codedom > ctor && queue > codedom && host > queue, "codedom domain load must queue the off status and return before the host starts");
        Assert.True(writeHost > queue && writeCsc > writeHost, "the queued write must cover the Roslyn host and csc /shared status files");
    }

    private static UnityCli.Cli.CliApp.DoctorRunningEditor RunningEditor(int pid)
    {
        return new UnityCli.Cli.CliApp.DoctorRunningEditor(pid, DateTime.UtcNow.AddHours(-1));
    }

    private static void WriteStatus(string projectRoot, DateTime started)
    {
        new RoslynHostStatus
        {
            UpdatedUtc = RoslynHostStatus.FormatUtc(started),
            EditorPid = 4100,
            ConfiguredCompiler = "roslyn-host",
            ActiveBackend = "roslyn-host",
            PrewarmPolicy = "auto",
            State = "ready",
            Pid = 500,
            HostStartedUtc = RoslynHostStatus.FormatUtc(started),
            StartMs = 300,
            PrewarmMs = 280,
            RuntimeVersion = "6.0.21",
            BuildCacheHit = true,
            StderrTail = { "[ucb-roslyn-host] ready" },
        }.Write(RoslynHostStatus.GetStatusPath(projectRoot));
    }

    private static JsonElement Serialize(object? value)
    {
        return JsonDocument.Parse(JsonSerializer.Serialize(value)).RootElement.Clone();
    }
}
