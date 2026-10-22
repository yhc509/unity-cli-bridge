using UnityCli.Protocol;
using UnityCliBridge.Bridge.Editor;

namespace UnityCli.Cli.Tests;

/// <summary>rsp option parsing, the pidfile, the orphan-kill rule, and the doctor status file.</summary>
public sealed class RoslynHostEditorPolicyTests
{
    private const string Hash = "0123456789ab";
    private const int Editor = 4100;

    [Fact]
    public void ProjectOptions_EmptyOrMissing_IsEmpty()
    {
        Assert.Same(ProjectCompilerOptions.Empty, ProjectCompilerOptions.Parse(null));
        Assert.Same(ProjectCompilerOptions.Empty, ProjectCompilerOptions.Parse("  \n "));
        Assert.Empty(ProjectCompilerOptions.Empty.Defines);
        Assert.False(ProjectCompilerOptions.Empty.AllowUnsafe);
    }

    [Fact]
    public void ProjectOptions_ReadsDefinesUnsafeAndNoWarnInEitherSpelling()
    {
        ProjectCompilerOptions options = ProjectCompilerOptions.Parse(
            "-define:UCB_ONE;UCB_TWO\n/d:UCB_THREE,UCB_ONE\n-unsafe\n-nowarn:0168,CS0219\n/nowarn:618\n");

        Assert.Equal(new[] { "UCB_ONE", "UCB_TWO", "UCB_THREE" }, options.Defines);
        Assert.True(options.AllowUnsafe);
        Assert.Equal(new[] { "0168", "CS0219", "618" }, options.NoWarn);
        Assert.Empty(options.Ignored);
    }

    [Theory]
    [InlineData("-unsafe+", true)]
    [InlineData("/unsafe", true)]
    [InlineData("-unsafe\n-unsafe-", false)]
    [InlineData("-define:X", false)]
    public void ProjectOptions_UnsafeFollowsTheLastSwitch(string text, bool expected)
    {
        Assert.Equal(expected, ProjectCompilerOptions.Parse(text).AllowUnsafe);
    }

    [Fact]
    public void ProjectOptions_ListsEverythingElseAsIgnored_AndSkipsComments()
    {
        ProjectCompilerOptions options = ProjectCompilerOptions.Parse(
            "# project options\n-langversion:latest -warnaserror+ \"-r:/path with space/Lib.dll\" stray -define:\"QUOTED\"\n");

        Assert.Equal(new[] { "QUOTED" }, options.Defines);
        Assert.Equal(new[] { "-langversion:latest", "-warnaserror+", "-r:/path with space/Lib.dll", "stray" }, options.Ignored);
    }

    [Fact]
    public void PidRecord_RoundTripsAndExposesTheMarkerHash()
    {
        var record = new RoslynHostPidRecord
        {
            Pid = 321,
            EditorPid = Editor,
            StartTimeUnixMs = 1_760_000_000_123,
            Marker = RoslynHostPidRecord.FormatMarker(Hash),
        };

        RoslynHostPidRecord? parsed = RoslynHostPidRecord.Parse(record.ToJson());

        Assert.NotNull(parsed);
        Assert.Equal(321, parsed!.Pid);
        Assert.Equal(Editor, parsed.EditorPid);
        Assert.Equal(1_760_000_000_123, parsed.StartTimeUnixMs);
        Assert.Equal("--ucb-roslyn-host " + Hash, parsed.Marker);
        Assert.Equal(Hash, parsed.MarkerProjectHash);
        Assert.Null(RoslynHostPidRecord.Parse("not json"));
        Assert.Null(RoslynHostPidRecord.Parse("[1,2]"));
    }

    [Fact]
    public void PidRecord_WriteReadDelete()
    {
        using var temp = new TempDirectory();
        string path = Path.Combine(temp.Path, "roslyn-host", "host.pid");
        new RoslynHostPidRecord { Pid = 9, EditorPid = 8, Marker = RoslynHostPidRecord.FormatMarker(Hash) }.Write(path);

        Assert.Equal(9, RoslynHostPidRecord.TryRead(path)!.Pid);
        RoslynHostPidRecord.TryDelete(path);
        Assert.False(File.Exists(path));
        Assert.Null(RoslynHostPidRecord.TryRead(path));
        RoslynHostPidRecord.TryDelete(path);
    }

    [Theory]
    [InlineData("--ucb-roslyn-host")]
    [InlineData("--ucb-roslyn-host ")]
    [InlineData("--other 0123456789ab")]
    [InlineData("--ucb-roslyn-host a b")]
    public void PidRecord_MalformedMarker_HasNoHash(string marker)
    {
        Assert.Equal(string.Empty, new RoslynHostPidRecord { Marker = marker }.MarkerProjectHash);
    }

    private static string HostCommandLine(string hash) =>
        "/Unity.app/Contents/NetCoreRuntime/dotnet exec /p/Library/com.yhc509.unity-cli-bridge/roslyn-host/k/UcbRoslynHost.dll --ucb-roslyn-host "
        + hash + " --roslyn-dir /Unity.app/Contents/DotNetSdkRoslyn --parent-pid " + Editor + " --prewarm";

    private static OrphanVerdict Evaluate(
        RoslynHostPidRecord? record,
        HashSet<int> alive,
        string? commandLine,
        out string reason,
        List<int>? commandLineReads = null)
    {
        return RoslynHostOrphanPolicy.Evaluate(
            record,
            Hash,
            Editor,
            alive.Contains,
            pid =>
            {
                commandLineReads?.Add(pid);
                return commandLine;
            },
            out reason);
    }

    private static RoslynHostPidRecord Record(int pid = 500, int editorPid = Editor, string? marker = null) => new()
    {
        Pid = pid,
        EditorPid = editorPid,
        Marker = marker ?? RoslynHostPidRecord.FormatMarker(Hash),
    };

    [Fact]
    public void Orphan_NoRecordOrDeadPid_IsGone()
    {
        Assert.Equal(OrphanVerdict.Gone, Evaluate(null, new HashSet<int>(), null, out _));
        Assert.Equal(OrphanVerdict.Gone, Evaluate(Record(pid: 0), new HashSet<int>(), null, out _));
        Assert.Equal(OrphanVerdict.Gone, Evaluate(Record(), new HashSet<int> { Editor }, HostCommandLine(Hash), out string reason));
        Assert.Contains("not running", reason);
    }

    [Fact]
    public void Orphan_AliveWithMarkerAndCurrentEditor_IsKilled()
    {
        Assert.Equal(OrphanVerdict.Kill, Evaluate(Record(), new HashSet<int> { 500, Editor }, HostCommandLine(Hash), out _));
    }

    [Fact]
    public void Orphan_AliveWithMarkerAndDeadEditor_IsKilled()
    {
        Assert.Equal(OrphanVerdict.Kill, Evaluate(Record(editorPid: 777), new HashSet<int> { 500, Editor }, HostCommandLine(Hash), out _));
        Assert.Equal(OrphanVerdict.Kill, Evaluate(Record(editorPid: 0), new HashSet<int> { 500 }, HostCommandLine(Hash), out _));
    }

    [Fact]
    public void Orphan_OtherLiveEditor_IsSpared()
    {
        Assert.Equal(
            OrphanVerdict.Spare,
            Evaluate(Record(editorPid: 777), new HashSet<int> { 500, 777, Editor }, HostCommandLine(Hash), out string reason));
        Assert.Contains("still running", reason);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("/usr/bin/vim notes.txt")]
    [InlineData("dotnet exec UcbRoslynHost.dll --ucb-roslyn-host ffffffffffff --parent-pid 1")]
    [InlineData("dotnet exec UcbRoslynHost.dll --ucb-roslyn-host")]
    public void Orphan_WithoutThisProjectsMarkerOnTheCommandLine_IsNeverKilled(string? commandLine)
    {
        Assert.Equal(OrphanVerdict.Spare, Evaluate(Record(), new HashSet<int> { 500, Editor }, commandLine, out _));
    }

    [Fact]
    public void Orphan_PidfileOfAnotherProject_IsSparedWithoutReadingTheCommandLine()
    {
        var reads = new List<int>();

        OrphanVerdict verdict = Evaluate(
            Record(marker: RoslynHostPidRecord.FormatMarker("ffffffffffff")),
            new HashSet<int> { 500, Editor },
            HostCommandLine("ffffffffffff"),
            out _,
            reads);

        Assert.Equal(OrphanVerdict.Spare, verdict);
        Assert.Empty(reads);
    }

    [Fact]
    public void Orphan_PidThatIsThisEditor_IsSpared()
    {
        Assert.Equal(OrphanVerdict.Spare, Evaluate(Record(pid: Editor), new HashSet<int> { Editor }, HostCommandLine(Hash), out _));
    }

    [Fact]
    public void ProcessInspector_SeesThisProcessAndNotABogusPid()
    {
        int self = Environment.ProcessId;

        Assert.True(RoslynHostProcessInspector.IsAlive(self));
        Assert.False(RoslynHostProcessInspector.IsAlive(0));
        Assert.False(RoslynHostProcessInspector.IsAlive(-5));
        Assert.Null(RoslynHostProcessInspector.ReadCommandLine(0));
        if (!OperatingSystem.IsWindows())
        {
            Assert.False(RoslynHostProcessInspector.IsAlive(int.MaxValue - 7));
            Assert.False(string.IsNullOrEmpty(RoslynHostProcessInspector.ReadCommandLine(self)));
        }
    }

    [Fact]
    public void Status_RoundTripsThroughTheFile()
    {
        using var temp = new TempDirectory();
        string projectRoot = Path.Combine(temp.Path, "Proj");
        string path = RoslynHostStatus.GetStatusPath(projectRoot);
        var status = new RoslynHostStatus
        {
            UpdatedUtc = RoslynHostStatus.FormatUtc(new DateTime(2026, 10, 22, 1, 2, 3, DateTimeKind.Utc)),
            EditorPid = Editor,
            ConfiguredCompiler = "roslyn-host",
            ActiveBackend = "roslyn-host",
            PrewarmPolicy = "auto",
            HostWaitMs = 1500,
            CompileTimeoutMs = 10000,
            State = "ready",
            Pid = 500,
            StartMs = 301.26,
            PrewarmMs = 280.04,
            BuildKey = "2021.3.45f2-914553587507",
            BuildCacheHit = true,
            LastCompiler = "roslyn-host",
            IgnoredProjectOptions = { "-langversion:latest" },
            StderrTail = { "[ucb-roslyn-host] ready in 230 ms" },
        };

        status.Write(path);
        RoslynHostStatus? read = RoslynHostStatus.TryRead(path);

        Assert.Equal(Path.Combine(projectRoot, "Library", "com.yhc509.unity-cli-bridge", "roslyn-host", "status.json"), path);
        Assert.NotNull(read);
        Assert.Equal(RoslynHostStatus.CurrentSchema, read!.Schema);
        Assert.Equal("ready", read.State);
        Assert.Equal(500, read.Pid);
        Assert.Equal(301.3, read.StartMs);
        Assert.Equal(280.0, read.PrewarmMs);
        Assert.True(read.BuildCacheHit);
        Assert.Equal(new[] { "-langversion:latest" }, read.IgnoredProjectOptions);
        Assert.Equal(new[] { "[ucb-roslyn-host] ready in 230 ms" }, read.StderrTail);
        Assert.Equal(new DateTime(2026, 10, 22, 1, 2, 3, DateTimeKind.Utc), RoslynHostStatus.ParseUtc(read.UpdatedUtc));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp"));
        Assert.Null(RoslynHostStatus.Parse("{broken"));
        Assert.Null(RoslynHostStatus.TryRead(Path.Combine(temp.Path, "missing.json")));
    }
}
