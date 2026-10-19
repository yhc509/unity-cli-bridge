using System.Text.Json;
using UnityCli.Cli.Models;
using UnityCli.Cli.Services;
using UnityCli.Protocol;
using Xunit;

namespace UnityCli.Cli.Tests;

public class ProfileMemoryComparerTests
{
    private static ProfileMemorySidecarFile Sidecar(string id, string mode, params (string Name, string Unit, double Median)[] counters)
    {
        var stats = new ProfileCounterStat[counters.Length];
        for (int i = 0; i < counters.Length; i++)
        {
            stats[i] = new ProfileCounterStat
            {
                name = counters[i].Name,
                category = "Memory",
                unit = counters[i].Unit,
                min = counters[i].Median,
                median = counters[i].Median,
                p95 = counters[i].Median,
                max = counters[i].Median,
            };
        }

        return new ProfileMemorySidecarFile
        {
            schemaVersion = 1,
            reportId = id,
            report = new ProfileMemoryPayload
            {
                reportId = id,
                mode = mode,
                frames = 30,
                unityVersion = "6000.3.10f1",
                counters = stats,
            },
        };
    }

    [Fact]
    public void Compare_TotalGrowthBeyondThreshold_IsRegression()
    {
        var baseSide = Sidecar("base", "playmode", ("Total Used Memory", "bytes", 1000));
        var headSide = Sidecar("head", "playmode", ("Total Used Memory", "bytes", 1100));

        var payload = ProfileMemoryComparer.Compare(baseSide, headSide, 5.0, 10);

        Assert.Equal("regression", payload.verdict);
        Assert.True(payload.totalUsedBytes.deltaPercentAvailable);
        Assert.Equal(10.0, payload.totalUsedBytes.deltaPercent, precision: 6);
    }

    [Fact]
    public void Compare_TotalShrinkBeyondThreshold_IsImprovement()
    {
        var baseSide = Sidecar("base", "playmode", ("Total Used Memory", "bytes", 1000));
        var headSide = Sidecar("head", "playmode", ("Total Used Memory", "bytes", 900));

        var payload = ProfileMemoryComparer.Compare(baseSide, headSide, 5.0, 10);

        Assert.Equal("improvement", payload.verdict);
    }

    [Fact]
    public void Compare_WithinThreshold_IsUnchanged()
    {
        var baseSide = Sidecar("base", "playmode", ("Total Used Memory", "bytes", 1000));
        var headSide = Sidecar("head", "playmode", ("Total Used Memory", "bytes", 1030));

        var payload = ProfileMemoryComparer.Compare(baseSide, headSide, 5.0, 10);

        Assert.Equal("unchanged", payload.verdict);
    }

    [Fact]
    public void Compare_ZeroBaseTotal_FixesVerdictUnchanged()
    {
        var baseSide = Sidecar("base", "playmode", ("Total Used Memory", "bytes", 0));
        var headSide = Sidecar("head", "playmode", ("Total Used Memory", "bytes", 5000));

        var payload = ProfileMemoryComparer.Compare(baseSide, headSide, 5.0, 10);

        Assert.Equal("unchanged", payload.verdict);
        Assert.False(payload.totalUsedBytes.deltaPercentAvailable);
        Assert.Contains(payload.notes, note => note.Contains("0 or below"));
    }

    [Fact]
    public void Compare_MissingTotalCounter_FixesVerdictUnchanged()
    {
        var baseSide = Sidecar("base", "playmode", ("GC Used Memory", "bytes", 100));
        var headSide = Sidecar("head", "playmode", ("GC Used Memory", "bytes", 500));

        var payload = ProfileMemoryComparer.Compare(baseSide, headSide, 5.0, 10);

        Assert.Equal("unchanged", payload.verdict);
        Assert.Contains(payload.notes, note => note.Contains("Total Used Memory"));
    }

    [Fact]
    public void Compare_ModeMismatch_AddsNote()
    {
        var baseSide = Sidecar("base", "editmode", ("Total Used Memory", "bytes", 1000));
        var headSide = Sidecar("head", "playmode", ("Total Used Memory", "bytes", 1000));

        var payload = ProfileMemoryComparer.Compare(baseSide, headSide, 5.0, 10);

        Assert.Contains(payload.notes, note => note.Contains("mode differs"));
    }

    [Fact]
    public void Compare_SortsByAbsoluteDelta_TieBreaksByOrdinal()
    {
        var baseSide = Sidecar(
            "base", "playmode",
            ("Total Used Memory", "bytes", 1000),
            ("Texture Memory", "bytes", 100),
            ("Mesh Memory", "bytes", 200),
            ("Material Count", "count", 10));
        var headSide = Sidecar(
            "head", "playmode",
            ("Total Used Memory", "bytes", 1000),
            ("Texture Memory", "bytes", 150),   // +50, ordinal 1
            ("Mesh Memory", "bytes", 250),      // +50, ordinal 2
            ("Material Count", "count", 110));  // +100, ordinal 3

        var payload = ProfileMemoryComparer.Compare(baseSide, headSide, 5.0, 10);

        Assert.Equal(3, payload.increases.Length);
        Assert.Equal("Material Count", payload.increases[0].name);
        Assert.Equal("Texture Memory", payload.increases[1].name); // |50| tie → ordinal 1 < 2
        Assert.Equal("Mesh Memory", payload.increases[2].name);
    }

    [Fact]
    public void Compare_LimitTruncates_AndFlags()
    {
        var baseSide = Sidecar(
            "base", "playmode",
            ("Total Used Memory", "bytes", 1000),
            ("Texture Memory", "bytes", 100),
            ("Mesh Memory", "bytes", 100));
        var headSide = Sidecar(
            "head", "playmode",
            ("Total Used Memory", "bytes", 1000),
            ("Texture Memory", "bytes", 300),
            ("Mesh Memory", "bytes", 200));

        var payload = ProfileMemoryComparer.Compare(baseSide, headSide, 5.0, 1);

        Assert.Single(payload.increases);
        Assert.Equal("Texture Memory", payload.increases[0].name);
        Assert.True(payload.truncated);
    }

    [Fact]
    public void Compare_ZeroDeltaCounters_AreExcluded()
    {
        var baseSide = Sidecar(
            "base", "playmode",
            ("Total Used Memory", "bytes", 1000),
            ("Texture Memory", "bytes", 100));
        var headSide = Sidecar(
            "head", "playmode",
            ("Total Used Memory", "bytes", 1000),
            ("Texture Memory", "bytes", 100));

        var payload = ProfileMemoryComparer.Compare(baseSide, headSide, 5.0, 10);

        Assert.Empty(payload.increases);
        Assert.Empty(payload.decreases);
    }

    [Fact]
    public void Run_NegativeThreshold_IsCliUsage()
    {
        var parsed = new ParsedCommand(CommandKind.ProfileMemoryCompare)
        {
            ProfileCompareBaseId = "a",
            ProfileCompareHeadId = "b",
            ProfileThresholdPercent = -1,
        };

        ResponseEnvelope envelope = ProfileMemoryComparer.Run(parsed, projectRoot: "/tmp/nonexistent");

        Assert.Equal(ProtocolConstants.StatusError, envelope.status);
        Assert.Equal("CLI_USAGE", envelope.error!.code);
    }

    [Fact]
    public void Run_ReadsSidecarsFromMemoryDirectory()
    {
        using var temp = new TempDirectory();
        WriteSidecar(temp.Path, Sidecar("aaa", "playmode", ("Total Used Memory", "bytes", 1000)));
        WriteSidecar(temp.Path, Sidecar("bbb", "playmode", ("Total Used Memory", "bytes", 1200)));

        var parsed = new ParsedCommand(CommandKind.ProfileMemoryCompare)
        {
            ProfileCompareBaseId = "aaa",
            ProfileCompareHeadId = "bbb",
        };

        ResponseEnvelope envelope = ProfileMemoryComparer.Run(parsed, temp.Path);

        Assert.Equal(ProtocolConstants.StatusSuccess, envelope.status);
        ProfileMemoryComparePayload payload = envelope.data!.Value
            .Deserialize<ProfileMemoryComparePayload>(ProtocolJson.Default)!;
        Assert.Equal("regression", payload.verdict);
        Assert.Equal("aaa", payload.baseReport.reportId);
        Assert.Equal("bbb", payload.headReport.reportId);
    }

    [Fact]
    public void Run_MissingBaseSidecar_ReturnsProfileNotFound()
    {
        using var temp = new TempDirectory();
        WriteSidecar(temp.Path, Sidecar("bbb", "playmode", ("Total Used Memory", "bytes", 1200)));

        var parsed = new ParsedCommand(CommandKind.ProfileMemoryCompare)
        {
            ProfileCompareBaseId = "nope",
            ProfileCompareHeadId = "bbb",
        };

        ResponseEnvelope envelope = ProfileMemoryComparer.Run(parsed, temp.Path);

        Assert.Equal(ProtocolConstants.StatusError, envelope.status);
        Assert.Equal(ProtocolConstants.ErrorProfileNotFound, envelope.error!.code);
        Assert.Contains("nope", envelope.error.message);
    }

    private static void WriteSidecar(string projectRoot, ProfileMemorySidecarFile sidecar)
    {
        string directory = Path.Combine(
            projectRoot,
            ProtocolConstants.MemoryReportsDirectoryRelative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(directory);
        File.WriteAllText(
            Path.Combine(directory, sidecar.reportId + ".json"),
            ProtocolJson.Serialize(sidecar));
    }
}
