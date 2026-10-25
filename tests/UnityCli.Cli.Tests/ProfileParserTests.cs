using UnityCli.Cli.Models;
using UnityCli.Cli.Services;
using UnityCli.Protocol;
using Xunit;

namespace UnityCli.Cli.Tests;

public class ProfileParserTests
{
    [Fact]
    public void Parse_ProfileStats_AcceptsOptions()
    {
        var parsed = CliArgumentParser.Parse(["profile", "stats", "--frames", "120", "--preset", "render"]);
        Assert.Equal(CommandKind.ProfileStats, parsed.Kind);
        Assert.Equal(120, parsed.ProfileFrames);
        Assert.Equal("render", parsed.ProfilePreset);
    }

    [Fact]
    public void Parse_ProfileCaptureStart_AcceptsOptions()
    {
        var parsed = CliArgumentParser.Parse(["profile", "capture", "start", "--frames", "300", "--budget-ms", "33.33"]);
        Assert.Equal(CommandKind.ProfileCaptureStart, parsed.Kind);
        Assert.Equal(300, parsed.ProfileFrames);
        Assert.Equal(33.33, parsed.ProfileBudgetMs!.Value, precision: 2);
    }

    [Fact]
    public void Parse_ProfileCaptureStart_FramesAndDurationAreExclusive()
    {
        Assert.Throws<CliUsageException>(() =>
            CliArgumentParser.Parse(["profile", "capture", "start", "--frames", "300", "--duration", "10"]));
    }

    [Fact]
    public void Parse_ProfileCaptureStop_Wait()
    {
        var parsed = CliArgumentParser.Parse(["profile", "capture", "stop", "--wait"]);
        Assert.Equal(CommandKind.ProfileCaptureStop, parsed.Kind);
        Assert.True(parsed.ProfileWait);
    }

    [Fact]
    public void Parse_ProfileStatus_PositionalCaptureId()
    {
        var parsed = CliArgumentParser.Parse(["profile", "status", "0123456789abcdef0123456789abcdef"]);
        Assert.Equal(CommandKind.ProfileStatus, parsed.Kind);
        Assert.Equal("0123456789abcdef0123456789abcdef", parsed.ProfileCaptureId);
    }

    [Fact]
    public void Parse_ProfileAnalyze_MarkerQuery()
    {
        var parsed = CliArgumentParser.Parse(["profile", "analyze", "0123456789abcdef0123456789abcdef", "--marker", "GC.Alloc", "--limit", "10"]);
        Assert.Equal(CommandKind.ProfileAnalyze, parsed.Kind);
        Assert.Equal("0123456789abcdef0123456789abcdef", parsed.ProfileCaptureId);
        Assert.Equal("GC.Alloc", parsed.ProfileAnalyzeMarker);
        Assert.Equal(10, parsed.ProfileLimit);
    }

    [Fact]
    public void Parse_ProfileAnalyze_RequiresCaptureId()
    {
        Assert.Throws<CliUsageException>(() => CliArgumentParser.Parse(["profile", "analyze", "--gc"]));
    }

    [Fact]
    public void Parse_ProfileAnalyze_NoMode_IsTheSummary()
    {
        ParsedCommand parsed = CliArgumentParser.Parse(["profile", "analyze", "0123456789abcdef0123456789abcdef"]);

        Assert.Equal(CommandKind.ProfileAnalyze, parsed.Kind);
        Assert.Equal("0123456789abcdef0123456789abcdef", parsed.ProfileCaptureId);
        Assert.True(string.IsNullOrEmpty(parsed.ProfileAnalyzeMarker));
        Assert.False(parsed.ProfileAnalyzeGc);
        Assert.False(parsed.ProfileAnalyzeSpikes);
        Assert.False(parsed.ProfileAnalyzeFrame.HasValue);
    }

    [Fact]
    public void Parse_ProfileAnalyze_RejectsMoreThanOneMode()
    {
        CliUsageException ex = Assert.Throws<CliUsageException>(() =>
            CliArgumentParser.Parse(["profile", "analyze", "0123456789abcdef0123456789abcdef", "--gc", "--spikes"]));

        Assert.Contains("at most one", ex.Message);
    }

    [Fact]
    public void Parse_ProfileCompare_TwoPositionalIdsAndOptions()
    {
        var parsed = CliArgumentParser.Parse(["profile", "compare", "capA", "capB", "--threshold", "2.5", "--limit", "8"]);
        Assert.Equal(CommandKind.ProfileCompare, parsed.Kind);
        Assert.Equal("capA", parsed.ProfileCompareBaseId);
        Assert.Equal("capB", parsed.ProfileCompareHeadId);
        Assert.Equal(2.5, parsed.ProfileThresholdPercent!.Value, precision: 3);
        Assert.Equal(8, parsed.ProfileLimit);
    }

    [Fact]
    public void Parse_ProfileCompare_DefaultsLeaveOptionsUnset()
    {
        var parsed = CliArgumentParser.Parse(["profile", "compare", "capA", "capB"]);
        Assert.Equal(CommandKind.ProfileCompare, parsed.Kind);
        Assert.Null(parsed.ProfileThresholdPercent);
        Assert.Null(parsed.ProfileLimit);
    }

    [Fact]
    public void Parse_ProfileCompare_RequiresBothCaptureIds()
    {
        Assert.Throws<CliUsageException>(() => CliArgumentParser.Parse(["profile", "compare"]));
        Assert.Throws<CliUsageException>(() => CliArgumentParser.Parse(["profile", "compare", "--threshold", "5"]));
        Assert.Throws<CliUsageException>(() => CliArgumentParser.Parse(["profile", "compare", "capA"]));
        Assert.Throws<CliUsageException>(() => CliArgumentParser.Parse(["profile", "compare", "capA", "--limit", "3"]));
    }

    [Fact]
    public void Parse_ProfileCompare_RejectsNegativeThreshold()
    {
        Assert.Throws<CliUsageException>(() =>
            CliArgumentParser.Parse(["profile", "compare", "capA", "capB", "--threshold", "-1"]));
    }

    [Fact]
    public void Parse_ProfileCompare_AcceptsZeroThreshold()
    {
        var parsed = CliArgumentParser.Parse(["profile", "compare", "capA", "capB", "--threshold", "0"]);
        Assert.Equal(0.0, parsed.ProfileThresholdPercent!.Value);
    }

    [Theory]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("-Infinity")]
    [InlineData("1e400")]
    public void Parse_ProfileCompare_RejectsNonFiniteThreshold(string value)
    {
        // double.TryParse accepts these (1e400 overflows to +Infinity); the serializer cannot write them.
        Assert.Throws<CliUsageException>(() =>
            CliArgumentParser.Parse(["profile", "compare", "capA", "capB", "--threshold", value]));
    }

    [Fact]
    public void Parse_ProfileCompare_RejectsNonPositiveLimit()
    {
        Assert.Throws<CliUsageException>(() =>
            CliArgumentParser.Parse(["profile", "compare", "capA", "capB", "--limit", "0"]));
        Assert.Throws<CliUsageException>(() =>
            CliArgumentParser.Parse(["profile", "compare", "capA", "capB", "--limit", "-3"]));
    }

    [Fact]
    public void Parse_ProfileCompare_RejectsNonNumericThreshold()
    {
        Assert.Throws<CliUsageException>(() =>
            CliArgumentParser.Parse(["profile", "compare", "capA", "capB", "--threshold", "fast"]));
    }

    [Fact]
    public void Parse_QaRunSequence_ProfileFlag()
    {
        var parsed = CliArgumentParser.Parse([
            "qa", "run-sequence",
            "--spec-json", "{\"steps\":[{\"actions\":[{\"wait\":100}]}]}",
            "--profile",
        ]);
        Assert.Equal(CommandKind.QaRunSequence, parsed.Kind);
        Assert.True(parsed.QaSequenceProfile);
    }

    [Fact]
    public void Parse_Profile_RequiresSubcommand()
    {
        Assert.Throws<CliUsageException>(() => CliArgumentParser.Parse(["profile"]));
    }

    [Fact]
    public void Parse_ProfileMemory_DefaultsAndFrames()
    {
        var parsed = CliArgumentParser.Parse(["profile", "memory"]);
        Assert.Equal(CommandKind.ProfileMemory, parsed.Kind);
        Assert.Null(parsed.ProfileFrames);

        var withFrames = CliArgumentParser.Parse(["profile", "memory", "--frames", "10"]);
        Assert.Equal(CommandKind.ProfileMemory, withFrames.Kind);
        Assert.Equal(10, withFrames.ProfileFrames);
    }

    [Fact]
    public void Parse_ProfileMemoryCompare_ParsesIdsAndOptions()
    {
        var parsed = CliArgumentParser.Parse(
            ["profile", "memory", "compare", "aaa", "bbb", "--threshold", "10", "--limit", "3"]);
        Assert.Equal(CommandKind.ProfileMemoryCompare, parsed.Kind);
        Assert.Equal("aaa", parsed.ProfileCompareBaseId);
        Assert.Equal("bbb", parsed.ProfileCompareHeadId);
        Assert.Equal(10.0, parsed.ProfileThresholdPercent!.Value, precision: 6);
        Assert.Equal(3, parsed.ProfileLimit);
    }

    [Fact]
    public void Parse_ProfileMemoryCompare_RequiresBothIds()
    {
        Assert.Throws<CliUsageException>(() => CliArgumentParser.Parse(["profile", "memory", "compare"]));
        Assert.Throws<CliUsageException>(() => CliArgumentParser.Parse(["profile", "memory", "compare", "aaa"]));
    }

    [Fact]
    public void Parse_ProfileMemory_UnknownSub_Throws()
    {
        Assert.Throws<CliUsageException>(() => CliArgumentParser.Parse(["profile", "memory", "bogus"]));
    }

    [Fact]
    public void Parse_ProfileMemoryTop_Defaults()
    {
        var parsed = CliArgumentParser.Parse(["profile", "memory", "top"]);
        Assert.Equal(CommandKind.ProfileMemoryTop, parsed.Kind);
        Assert.Null(parsed.ProfileMemoryTopType);
        Assert.Null(parsed.ProfileMemoryTopScope);
        Assert.Null(parsed.ProfileLimit);

        ProfileMemoryTopArgs args = ProtocolJson.Deserialize<ProfileMemoryTopArgs>(parsed.ToEnvelope().argumentsJson);
        Assert.Equal(ProtocolConstants.CommandProfileMemoryTop, parsed.ToEnvelope().command);
        Assert.Equal(string.Empty, args.type);
        Assert.Equal(0, args.limit);
        Assert.Equal(string.Empty, args.scope);
    }

    [Fact]
    public void Parse_ProfileMemoryTop_NormalizesTypeScopeAndLimit()
    {
        var parsed = CliArgumentParser.Parse(
            ["profile", "memory", "top", "--type", "texture2d", "--limit", "5", "--scope", "ASSETS"]);
        Assert.Equal("Texture2D", parsed.ProfileMemoryTopType);
        Assert.Equal(5, parsed.ProfileLimit);
        Assert.Equal("assets", parsed.ProfileMemoryTopScope);

        ProfileMemoryTopArgs args = ProtocolJson.Deserialize<ProfileMemoryTopArgs>(parsed.ToEnvelope().argumentsJson);
        Assert.Equal("Texture2D", args.type);
        Assert.Equal(5, args.limit);
        Assert.Equal("assets", args.scope);
    }

    [Theory]
    [InlineData("bogus")]
    [InlineData("GameObject")]
    [InlineData("")]
    public void Parse_ProfileMemoryTop_RejectsUnknownType(string type)
    {
        var ex = Assert.Throws<CliUsageException>(() => CliArgumentParser.Parse(
            ["profile", "memory", "top", "--type", type]));
        Assert.Contains("Texture2D", ex.Message);
    }

    [Fact]
    public void Parse_ProfileMemoryTop_RejectsUnknownScopeAndLimit()
    {
        Assert.Throws<CliUsageException>(() => CliArgumentParser.Parse(
            ["profile", "memory", "top", "--scope", "all"]));
        Assert.Throws<CliUsageException>(() => CliArgumentParser.Parse(
            ["profile", "memory", "top", "--limit", "0"]));
        Assert.Throws<CliUsageException>(() => CliArgumentParser.Parse(
            ["profile", "memory", "top", "--limit", "101"]));
    }

    [Fact]
    public void Parse_ProfileMemorySnapshot()
    {
        var parsed = CliArgumentParser.Parse(["profile", "memory", "snapshot"]);
        Assert.Equal(CommandKind.ProfileMemorySnapshot, parsed.Kind);
    }
}
