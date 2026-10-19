using UnityCli.Cli.Models;
using UnityCli.Cli.Services;
using UnityCli.Protocol;

namespace UnityCli.Cli.Tests;

public sealed class ProfileAnalyzerTests : IDisposable
{
    private readonly string _root;

    public ProfileAnalyzerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ucb-profile-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, ProtocolConstants.ProfilesDirectoryRelative));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string WriteSidecar(ProfileSidecarFile sidecar)
    {
        string path = Path.Combine(_root, ProtocolConstants.ProfilesDirectoryRelative, sidecar.captureId + ".json");
        File.WriteAllText(path, ProtocolJson.Serialize(sidecar));
        return path;
    }

    private static ProfileSidecarFile BuildSidecar()
    {
        return new ProfileSidecarFile
        {
            captureId = "cap1",
            summary = new ProfileSummaryPayload
            {
                captureId = "cap1",
                status = "Completed",
                budgetMs = 16.67f,
                spikes = new[] { new ProfileSpike { frame = 1, ms = 41.2, topMarker = "MyGame.Update", topMarkerSelfMs = 33.0 } },
            },
            frames = new[]
            {
                new ProfileFrameEntry { i = 0, ms = 10.0, top = new[] { new ProfileFrameMarker { m = "MyGame.Update", self = 3.0, gc = 128, calls = 1 } } },
                new ProfileFrameEntry { i = 1, ms = 41.2, top = new[] { new ProfileFrameMarker { m = "MyGame.Update", self = 33.0, gc = 4096, calls = 1 } } },
            },
            markers = new[]
            {
                new ProfileMarkerEntry { m = "MyGame.Update", selfTotalMs = 36.0, selfMedianMs = 18.0, selfP95Ms = 33.0, gcBytes = 4224, calls = 2, frames = 2 },
                new ProfileMarkerEntry { m = "Quiet.Marker", selfTotalMs = 1.0, gcBytes = 0, calls = 2, frames = 2 },
            },
        };
    }

    [Fact]
    public void Analyze_Marker_ReturnsAggregateAndAppearances()
    {
        WriteSidecar(BuildSidecar());
        var parsed = new ParsedCommand(CommandKind.ProfileAnalyze)
        {
            ProfileCaptureId = "cap1",
            ProfileAnalyzeMarker = "MyGame.Update",
        };

        ResponseEnvelope response = ProfileAnalyzer.Run(parsed, _root);

        Assert.Equal(ProtocolConstants.StatusSuccess, response.status);
        var payload = CliApp.DeserializeData<ProfileAnalyzeMarkerPayload>(response);
        Assert.Equal("MyGame.Update", payload!.marker.m);
        Assert.Equal(2, payload.appearances.Length);
        // appearances are sorted by selfMs descending — [0] is the spike frame (33.0ms)
        Assert.Equal(33.0, payload.appearances[0].selfMs);
        Assert.Equal(1, payload.appearances[0].frame);
    }

    [Fact]
    public void Analyze_Frame_ReturnsFrameTop()
    {
        WriteSidecar(BuildSidecar());
        var parsed = new ParsedCommand(CommandKind.ProfileAnalyze)
        {
            ProfileCaptureId = "cap1",
            ProfileAnalyzeFrame = 1,
        };

        ResponseEnvelope response = ProfileAnalyzer.Run(parsed, _root);

        var payload = CliApp.DeserializeData<ProfileAnalyzeFramePayload>(response);
        Assert.True(payload!.overBudget);
        Assert.Equal(41.2, payload.frame.ms);
    }

    [Fact]
    public void Analyze_Gc_SortsByBytesAndSkipsZero()
    {
        WriteSidecar(BuildSidecar());
        var parsed = new ParsedCommand(CommandKind.ProfileAnalyze)
        {
            ProfileCaptureId = "cap1",
            ProfileAnalyzeGc = true,
        };

        ResponseEnvelope response = ProfileAnalyzer.Run(parsed, _root);

        var payload = CliApp.DeserializeData<ProfileAnalyzeGcPayload>(response);
        Assert.Single(payload!.entries);
        Assert.Equal("MyGame.Update", payload.entries[0].marker);
    }

    [Fact]
    public void Analyze_MissingCaptureId_ReturnsProfileNotFound()
    {
        var parsed = new ParsedCommand(CommandKind.ProfileAnalyze)
        {
            ProfileCaptureId = "nope",
            ProfileAnalyzeGc = true,
        };

        ResponseEnvelope response = ProfileAnalyzer.Run(parsed, _root);

        Assert.Equal(ProtocolConstants.StatusError, response.status);
        Assert.Equal(ProtocolConstants.ErrorProfileNotFound, response.error!.code);
    }

    [Fact]
    public void Analyze_NoProjectRoot_ReturnsUsageError()
    {
        var parsed = new ParsedCommand(CommandKind.ProfileAnalyze)
        {
            ProfileCaptureId = "cap1",
            ProfileAnalyzeGc = true,
        };

        ResponseEnvelope response = ProfileAnalyzer.Run(parsed, null);

        Assert.Equal(ProtocolConstants.StatusError, response.status);
        Assert.Equal("CLI_USAGE", response.error!.code);
    }
}
