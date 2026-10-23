using System.Text;
using System.Text.Json;
using UnityCli.Protocol;

namespace UnityCli.Cli.Tests;

public sealed class CscSharedStatusTests
{
    private const string ProjectHash = "d6887d898597";

    [Fact]
    public void PipeName_IsShortDeterministicAndProjectScoped()
    {
        string pipe = CscSharedPipe.BuildName(ProjectHash, "2021.3.45f2");

        Assert.Equal(pipe, CscSharedPipe.BuildName(ProjectHash, "2021.3.45f2"));
        Assert.Matches("^ucb-d6887d89-[0-9a-f]{6}$", pipe);
        Assert.Equal(19, pipe.Length);
        Assert.NotEqual(pipe, CscSharedPipe.BuildName(ProjectHash, "6000.3.10f1"));
        Assert.NotEqual(pipe, CscSharedPipe.BuildName("86c6e1d6e8e5", "2021.3.45f2"));
        Assert.True(CscSharedPipe.IsProjectPipe(pipe, ProjectHash));
        Assert.True(CscSharedPipe.IsProjectPipe(CscSharedPipe.BuildName(ProjectHash, "6000.3.10f1"), ProjectHash));
        Assert.False(CscSharedPipe.IsProjectPipe(pipe, "86c6e1d6e8e5"));
    }

    [Fact]
    public void PipeName_VersionPartIsTheSha256Prefix()
    {
        byte[] hash = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes("6000.3.10f1"));
        string expected = "ucb-d6887d89-" + Convert.ToHexString(hash, 0, 3).ToLowerInvariant();

        Assert.Equal(expected, CscSharedPipe.BuildName(ProjectHash, "6000.3.10f1"));
    }

    [Fact]
    public void PipeName_FitsTheUnixSocketLimitUnderTmp()
    {
        // Roslyn puts the server socket at /tmp/<pipe> on macOS and Linux; sun_path holds 104 bytes on macOS.
        string pipe = CscSharedPipe.BuildName(ProjectHash, "6000.3.10f1-very-long-custom-build-name");

        Assert.True(Encoding.UTF8.GetByteCount("/tmp/" + pipe) < 104);
    }

    [Theory]
    [InlineData("ucb-d6887d89-abc12")]
    [InlineData("ucb-d6887d89-abc1234")]
    [InlineData("ucb-d6887d89-ABC123")]
    [InlineData("ucb-d6887d8-abc123")]
    [InlineData("vbcs-d6887d89-abc123")]
    [InlineData("")]
    [InlineData(null)]
    public void IsProjectPipe_RejectsAnythingElse(string? pipe)
    {
        Assert.False(CscSharedPipe.IsProjectPipe(pipe, ProjectHash));
    }

    [Theory]
    [InlineData("/x/dotnet exec /x/VBCSCompiler.dll -pipename:ucb-d6887d89-abc123", true)]
    [InlineData("/x/dotnet exec \"/x/VBCSCompiler.dll\" \"-pipename:ucb-d6887d89-abc123\"", true)]
    [InlineData("C:\\U\\dotnet.exe exec \"C:\\U\\VBCSCompiler.dll\" -pipename:ucb-d6887d89-abc123 ", true)]
    [InlineData("/x/dotnet exec /x/VBCSCompiler.dll -pipename:ucb-d6887d89-abc1234", false)]
    [InlineData("/x/dotnet exec /x/VBCSCompiler.dll -pipename:xucb-d6887d89-abc123", false)]
    [InlineData("/x/dotnet exec /x/VBCSCompiler.dll -pipename:Aq3bTL0ZJ9ExP4jQn2N1wSGx+Ko", false)]
    [InlineData("/x/dotnet exec /x/VBCSCompiler.dll", false)]
    [InlineData("/x/dotnet exec /x/csc.dll /shared:ucb-d6887d89-abc123 a.cs", false)]
    [InlineData("/x/dotnet exec /x/VBCSCompiler.dll -shutdown -pipename:ucb-d6887d89-abc123", false)]
    [InlineData("", false)]
    public void IsServerCommandLine_MatchesOnlyOurServer(string commandLine, bool expected)
    {
        Assert.Equal(expected, CscSharedPipe.IsServerCommandLine(commandLine, "ucb-d6887d89-abc123"));
    }

    [Fact]
    public void Status_RoundTripsThroughTheFile()
    {
        using var temp = new TempDirectory();
        var status = new CscSharedStatus
        {
            UpdatedUtc = "2026-10-23T01:00:00.0000000Z",
            EditorPid = 500,
            ConfiguredCompiler = "csc-shared",
            PipeName = "ucb-d6887d89-abc123",
            State = "ready",
            ServerPid = 600,
            DotnetPath = "/U/NetCoreRuntime/dotnet",
            CscPath = "/U/DotNetSdkRoslyn/csc.dll",
            RoslynVersion = "4.3.1-3.22526.13",
            CompileTimeoutMs = 10000,
            Compiles = 3,
            FirstCompileMs = 412.34,
            LastShutdownMs = 151.25,
            LastError = "csc-shared-timeout: x",
            LastErrorUtc = "2026-10-23T01:00:01.0000000Z",
        };
        string path = CscSharedStatus.GetStatusPath(temp.Path);

        status.Write(path);
        CscSharedStatus? read = CscSharedStatus.TryRead(path);

        Assert.Equal(Path.Combine(temp.Path, "Library", "com.yhc509.unity-cli-bridge", "csc-shared", "status.json"), path);
        Assert.NotNull(read);
        Assert.Equal(status.ToJson().Replace("412.34", "412.3").Replace("151.25", "151.2"), read!.ToJson());
        Assert.Equal(151.2, read.LastShutdownMs);
        Assert.Equal("ucb-d6887d89-abc123", read.PipeName);
        Assert.Equal(600, read.ServerPid);
        Assert.Equal(412.3, read.FirstCompileMs);
        Assert.Null(CscSharedStatus.Parse("not json"));
        Assert.Null(CscSharedStatus.TryRead(Path.Combine(temp.Path, "missing.json")));
    }

    [Fact]
    public void Doctor_NoStatusFile_SaysWhenCscSharedIsUsed()
    {
        using var temp = new TempDirectory();

        JsonElement data = Serialize(UnityCli.Cli.CliApp.BuildCscSharedData(temp.Path, _ => Array.Empty<int>(), _ => false));

        Assert.False(data.GetProperty("present").GetBoolean());
        Assert.Contains("UCB_EXECUTE_COMPILER=roslyn-host or csc-shared", data.GetProperty("note").GetString());
        Assert.Null(UnityCli.Cli.CliApp.BuildCscSharedData(null));
    }

    [Fact]
    public void Doctor_ReportsPipeServerAliveAndLastError()
    {
        using var temp = new TempDirectory();
        new CscSharedStatus
        {
            EditorPid = 500,
            ConfiguredCompiler = "roslyn-host",
            PipeName = "ucb-d6887d89-abc123",
            State = "ready",
            ServerPid = 600,
            LastError = "csc-shared-failed: exit 1",
        }.Write(CscSharedStatus.GetStatusPath(temp.Path));
        string? askedPipe = null;

        JsonElement data = Serialize(UnityCli.Cli.CliApp.BuildCscSharedData(
            temp.Path,
            pipe =>
            {
                askedPipe = pipe;
                return new[] { 700 };
            },
            pid => pid == 500));

        Assert.Equal("ucb-d6887d89-abc123", askedPipe);
        Assert.True(data.GetProperty("present").GetBoolean());
        Assert.False(data.GetProperty("stale").GetBoolean());
        Assert.Equal("ucb-d6887d89-abc123", data.GetProperty("pipeName").GetString());
        Assert.True(data.GetProperty("serverAlive").GetBoolean());
        Assert.Equal(700, data.GetProperty("serverPid").GetInt32());
        Assert.Equal("csc-shared-failed: exit 1", data.GetProperty("lastError").GetString());
    }

    [Fact]
    public void Doctor_NoServerOnThePipe_IsNotAlive()
    {
        using var temp = new TempDirectory();
        new CscSharedStatus { EditorPid = 500, PipeName = "ucb-d6887d89-abc123", State = "stopped", Note = "editor-quit", ServerPid = 600 }
            .Write(CscSharedStatus.GetStatusPath(temp.Path));

        JsonElement data = Serialize(UnityCli.Cli.CliApp.BuildCscSharedData(temp.Path, _ => Array.Empty<int>(), _ => false));

        Assert.False(data.GetProperty("serverAlive").GetBoolean());
        Assert.True(data.GetProperty("stale").GetBoolean());
        Assert.Equal("editor-quit", data.GetProperty("note").GetString());
    }

    private static JsonElement Serialize(object? value)
    {
        return JsonDocument.Parse(JsonSerializer.Serialize(value)).RootElement.Clone();
    }
}
