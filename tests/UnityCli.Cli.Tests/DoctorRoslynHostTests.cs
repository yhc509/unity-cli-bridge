using System.Text.Json;
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
        Assert.Equal("codedom", data.GetProperty("activeBackend").GetString());
        Assert.Equal(RoslynHostStatus.GetStatusPath(temp.Path), data.GetProperty("statusPath").GetString());
        Assert.Contains("UCB_EXECUTE_COMPILER=roslyn-host", data.GetProperty("note").GetString());
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
            started.AddSeconds(90)));

        Assert.True(data.GetProperty("present").GetBoolean());
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

        Assert.True(data.GetProperty("stale").GetBoolean());
        Assert.False(data.GetProperty("hostAlive").GetBoolean());
        Assert.Equal(JsonValueKind.Null, data.GetProperty("uptimeSeconds").ValueKind);
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
