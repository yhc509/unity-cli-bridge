using System.Text.Json;
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
    public void Analyze_Marker_ExactNameOmitsResolvedMarker()
    {
        WriteSidecar(BuildScriptMarkerSidecar());
        const string fullName = "Assembly-CSharp.dll!::ThreatField.Update() [Invoke]";
        var parsed = new ParsedCommand(CommandKind.ProfileAnalyze)
        {
            ProfileCaptureId = "cap-scripts",
            ProfileAnalyzeMarker = fullName,
        };

        ResponseEnvelope response = ProfileAnalyzer.Run(parsed, _root);
        JsonElement data = response.data!.Value;

        Assert.Equal(ProtocolConstants.StatusSuccess, response.status);
        Assert.Equal(fullName, data.GetProperty("marker").GetProperty("m").GetString());
        Assert.False(data.TryGetProperty("resolvedMarker", out _));
    }

    [Theory]
    [InlineData("ThreatField.Update")]
    [InlineData("ThreatField.Update()")]
    public void Analyze_Marker_ResolvesShortScriptName(string query)
    {
        WriteSidecar(BuildScriptMarkerSidecar());
        const string fullName = "Assembly-CSharp.dll!::ThreatField.Update() [Invoke]";
        var parsed = new ParsedCommand(CommandKind.ProfileAnalyze)
        {
            ProfileCaptureId = "cap-scripts",
            ProfileAnalyzeMarker = query,
        };

        ResponseEnvelope response = ProfileAnalyzer.Run(parsed, _root);
        JsonElement data = response.data!.Value;

        Assert.Equal(ProtocolConstants.StatusSuccess, response.status);
        Assert.Equal(fullName, data.GetProperty("resolvedMarker").GetString());
        Assert.Equal(fullName, data.GetProperty("marker").GetProperty("m").GetString());
        Assert.Equal(1.23, data.GetProperty("marker").GetProperty("selfTotalMs").GetDouble());
        Assert.Equal(3, data.GetProperty("appearances")[0].GetProperty("frame").GetInt32());
        Assert.Equal(1.23, data.GetProperty("appearances")[0].GetProperty("selfMs").GetDouble());
    }

    [Fact]
    public void Analyze_Marker_ExactShortNameWinsOverNormalizedDuplicate()
    {
        ProfileSidecarFile sidecar = BuildScriptMarkerSidecar();
        sidecar.markers = sidecar.markers.Prepend(new ProfileMarkerEntry
        {
            m = "ThreatField.Update",
            selfTotalMs = 9,
            calls = 1,
            frames = 1,
        }).ToArray();
        WriteSidecar(sidecar);
        var parsed = new ParsedCommand(CommandKind.ProfileAnalyze)
        {
            ProfileCaptureId = "cap-scripts",
            ProfileAnalyzeMarker = "ThreatField.Update",
        };

        ResponseEnvelope response = ProfileAnalyzer.Run(parsed, _root);
        JsonElement data = response.data!.Value;

        Assert.Equal("ThreatField.Update", data.GetProperty("marker").GetProperty("m").GetString());
        Assert.False(data.TryGetProperty("resolvedMarker", out _));
    }

    [Fact]
    public void Analyze_Marker_SeveralShortMatches_ReturnsAmbiguous()
    {
        WriteSidecar(BuildScriptMarkerSidecar());
        var parsed = new ParsedCommand(CommandKind.ProfileAnalyze)
        {
            ProfileCaptureId = "cap-scripts",
            ProfileAnalyzeMarker = "Update",
        };

        ResponseEnvelope response = ProfileAnalyzer.Run(parsed, _root);

        Assert.Equal(ProtocolConstants.ErrorProfileMarkerAmbiguous, response.error!.code);
        Assert.Contains("Assembly-CSharp.dll!::ThreatField.Update() [Invoke]", response.error.message);
        Assert.Contains("Assembly-CSharp.dll!Game.Ui::MinimapView.Update() [Invoke]", response.error.message);
        Assert.DoesNotContain("SkirmishHud.LateUpdate", response.error.message);
        int threat = response.error.message.IndexOf("ThreatField.Update", StringComparison.Ordinal);
        int minimap = response.error.message.IndexOf("MinimapView.Update", StringComparison.Ordinal);
        Assert.True(threat < minimap);
    }

    [Fact]
    public void Analyze_Marker_NotFound_ListsUpToFiveSimilarNames()
    {
        WriteSidecar(BuildScriptMarkerSidecar());
        var parsed = new ParsedCommand(CommandKind.ProfileAnalyze)
        {
            ProfileCaptureId = "cap-scripts",
            ProfileAnalyzeMarker = "ThreatField.Late",
        };

        ResponseEnvelope response = ProfileAnalyzer.Run(parsed, _root);

        Assert.Equal(ProtocolConstants.ErrorProfileNotFound, response.error!.code);
        string message = response.error.message;
        Assert.Contains("Similar names:", message);
        string similar = message[(message.IndexOf("Similar names:", StringComparison.Ordinal) + "Similar names:".Length)..].Trim();
        string[] names = similar.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(5, names.Length);
        Assert.Equal("Assembly-CSharp.dll!::ThreatField.Update() [Invoke]", names[0]);
    }

    [Fact]
    public void Analyze_NoMode_ReturnsSummaryHotspotsGcAndSpikes()
    {
        WriteSidecar(BuildScriptMarkerSidecar());
        var parsed = new ParsedCommand(CommandKind.ProfileAnalyze)
        {
            ProfileCaptureId = "cap-scripts",
        };

        ResponseEnvelope response = ProfileAnalyzer.Run(parsed, _root);
        JsonElement summary = response.data!.Value.GetProperty("summary");

        Assert.Equal(ProtocolConstants.StatusSuccess, response.status);
        Assert.Equal(
            "Assembly-CSharp.dll!::ThreatField.Update() [Invoke]",
            summary.GetProperty("hotspots")[0].GetProperty("marker").GetString());
        // The stored gcTop only had GC.Alloc; summary mode rebuilds it without the containers.
        Assert.Equal(0, summary.GetProperty("gcTop").GetArrayLength());
        Assert.Equal(4, summary.GetProperty("spikes")[0].GetProperty("frame").GetInt32());
        Assert.Contains("schemaVersion 1", response.data!.Value.GetProperty("notes")[0].GetString());
    }

    [Fact]
    public void Analyze_Gc_LeavesOutContainersAndListsGcByScript()
    {
        WriteSidecar(BuildGcSidecar(schemaVersion: ProtocolConstants.ProfileSidecarSchemaVersion));
        var parsed = new ParsedCommand(CommandKind.ProfileAnalyze)
        {
            ProfileCaptureId = "cap-gc",
            ProfileAnalyzeGc = true,
        };

        ResponseEnvelope response = ProfileAnalyzer.Run(parsed, _root);
        JsonElement data = response.data!.Value;

        Assert.Equal(ProtocolConstants.StatusSuccess, response.status);
        string[] markers = data.GetProperty("entries").EnumerateArray().Select(e => e.GetProperty("marker").GetString()!).ToArray();
        Assert.Equal(new[] { ThreatMarker, HudMarker }, markers);
        foreach (string container in ProfileGcAttribution.GcTopExcludedMarkers)
        {
            Assert.DoesNotContain(container, markers);
        }

        JsonElement byScript = data.GetProperty("gcByScript");
        Assert.Equal(ThreatMarker, byScript[0].GetProperty("marker").GetString());
        Assert.Equal(3_000_000, byScript[0].GetProperty("bytesTotal").GetInt64());
        Assert.Equal(15_000, byScript[0].GetProperty("bytesPerFrame").GetDouble());
        Assert.Equal(1234, data.GetProperty("gcUnattributedBytes").GetInt64());
        Assert.False(data.TryGetProperty("notes", out _));
    }

    [Fact]
    public void Analyze_Gc_LimitAppliesToBothLists()
    {
        WriteSidecar(BuildGcSidecar(schemaVersion: ProtocolConstants.ProfileSidecarSchemaVersion));
        var parsed = new ParsedCommand(CommandKind.ProfileAnalyze)
        {
            ProfileCaptureId = "cap-gc",
            ProfileAnalyzeGc = true,
            ProfileLimit = 1,
        };

        JsonElement data = ProfileAnalyzer.Run(parsed, _root).data!.Value;

        Assert.Equal(1, data.GetProperty("entries").GetArrayLength());
        Assert.True(data.GetProperty("truncated").GetBoolean());
        Assert.Equal(1, data.GetProperty("gcByScript").GetArrayLength());
        Assert.True(data.GetProperty("gcByScriptTruncated").GetBoolean());
    }

    [Fact]
    public void Analyze_Gc_SchemaOneSidecarWithoutGcByScript_ReadsWithANote()
    {
        // A 0.6.0 sidecar, written as raw JSON so the new fields are truly absent.
        string path = Path.Combine(_root, ProtocolConstants.ProfilesDirectoryRelative, "cap-old.json");
        File.WriteAllText(path, """
            {"schemaVersion":1,"captureId":"cap-old","createdUtc":"2026-10-24T00:00:00Z",
             "summary":{"captureId":"cap-old","status":"Completed","capturedFrames":200,"requestedFrames":200,
               "gcTop":[{"marker":"GC.Alloc","bytesTotal":3270000,"framesWithAlloc":200},{"marker":"PlayerLoop","bytesTotal":3270000,"framesWithAlloc":200}]},
             "frames":[],
             "markers":[{"m":"GC.Alloc","gcBytes":3270000,"frames":200},{"m":"PlayerLoop","gcBytes":3270000,"frames":200},
               {"m":"UpdateScene","gcBytes":3270000,"frames":200},{"m":"BehaviourUpdate","gcBytes":3270000,"frames":200},
               {"m":"Update.ScriptRunBehaviourUpdate","gcBytes":3270000,"frames":200},
               {"m":"Assembly-CSharp.dll!::MinimapView.Update() [Invoke]","gcBytes":2100000,"frames":200}]}
            """);

        var gc = ProfileAnalyzer.Run(new ParsedCommand(CommandKind.ProfileAnalyze) { ProfileCaptureId = "cap-old", ProfileAnalyzeGc = true }, _root);
        JsonElement data = gc.data!.Value;

        Assert.Equal(ProtocolConstants.StatusSuccess, gc.status);
        Assert.Equal("Assembly-CSharp.dll!::MinimapView.Update() [Invoke]", data.GetProperty("entries")[0].GetProperty("marker").GetString());
        Assert.Equal(1, data.GetProperty("entries").GetArrayLength());
        Assert.Equal(0, data.GetProperty("gcByScript").GetArrayLength());
        Assert.Contains("schemaVersion 1", data.GetProperty("notes")[0].GetString());

        var summary = ProfileAnalyzer.Run(new ParsedCommand(CommandKind.ProfileAnalyze) { ProfileCaptureId = "cap-old" }, _root);
        JsonElement summaryData = summary.data!.Value;
        Assert.Equal(ProtocolConstants.StatusSuccess, summary.status);
        Assert.Equal(
            "Assembly-CSharp.dll!::MinimapView.Update() [Invoke]",
            summaryData.GetProperty("summary").GetProperty("gcTop")[0].GetProperty("marker").GetString());
        Assert.Equal(0, summaryData.GetProperty("summary").GetProperty("gcByScript").GetArrayLength());
        Assert.True(summaryData.TryGetProperty("notes", out _));
    }

    private const string ThreatMarker = "Assembly-CSharp.dll!::ThreatField.Update() [Invoke]";
    private const string HudMarker = "Assembly-CSharp.dll!::SkirmishHud.LateUpdate() [Invoke]";

    private static ProfileSidecarFile BuildGcSidecar(int schemaVersion)
    {
        return new ProfileSidecarFile
        {
            schemaVersion = schemaVersion,
            captureId = "cap-gc",
            summary = new ProfileSummaryPayload { captureId = "cap-gc", status = "Completed", capturedFrames = 200 },
            markers = new[]
            {
                new ProfileMarkerEntry { m = "PlayerLoop", gcBytes = 4_000_000, frames = 200 },
                new ProfileMarkerEntry { m = "UpdateScene", gcBytes = 4_000_000, frames = 200 },
                new ProfileMarkerEntry { m = "BehaviourUpdate", gcBytes = 4_000_000, frames = 200 },
                new ProfileMarkerEntry { m = "Update.ScriptRunBehaviourUpdate", gcBytes = 3_000_000, frames = 200 },
                new ProfileMarkerEntry { m = "GC.Alloc", gcBytes = 4_000_000, frames = 200 },
                new ProfileMarkerEntry { m = ThreatMarker, gcBytes = 3_000_000, frames = 200 },
                new ProfileMarkerEntry { m = HudMarker, gcBytes = 1_000_000, frames = 200 },
            },
            gcByScript = new[]
            {
                new ProfileGcScriptEntry { marker = ThreatMarker, bytesTotal = 3_000_000, bytesPerFrame = 15_000, allocations = 400, framesWithAlloc = 200 },
                new ProfileGcScriptEntry { marker = HudMarker, bytesTotal = 1_000_000, bytesPerFrame = 5_000, allocations = 200, framesWithAlloc = 200 },
            },
            gcUnattributedBytes = 1234,
        };
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

    private static ProfileSidecarFile BuildScriptMarkerSidecar()
    {
        const string threat = "Assembly-CSharp.dll!::ThreatField.Update() [Invoke]";
        const string minimap = "Assembly-CSharp.dll!Game.Ui::MinimapView.Update() [Invoke]";
        const string hud = "Assembly-CSharp.dll!::SkirmishHud.LateUpdate() [Invoke]";
        return new ProfileSidecarFile
        {
            captureId = "cap-scripts",
            summary = new ProfileSummaryPayload
            {
                captureId = "cap-scripts",
                status = "Completed",
                hotspots = new[] { new ProfileHotspot { marker = threat, selfTotalMs = 1.23 } },
                gcTop = new[] { new ProfileGcEntry { marker = "GC.Alloc", bytesTotal = 100 } },
                spikes = new[] { new ProfileSpike { frame = 4, ms = 40, topMarker = threat } },
            },
            frames = new[]
            {
                new ProfileFrameEntry
                {
                    i = 3,
                    ms = 12,
                    top = new[] { new ProfileFrameMarker { m = threat, self = 1.23, gc = 0, calls = 1 } },
                },
            },
            markers = new[]
            {
                new ProfileMarkerEntry { m = threat, selfTotalMs = 1.23, calls = 1, frames = 1 },
                new ProfileMarkerEntry { m = minimap, selfTotalMs = 0.4, calls = 1, frames = 1 },
                new ProfileMarkerEntry { m = hud, selfTotalMs = 0.2, calls = 1, frames = 1 },
                new ProfileMarkerEntry { m = "PlayerLoop", selfTotalMs = 8, calls = 1, frames = 1 },
                new ProfileMarkerEntry { m = "GC.Alloc", selfTotalMs = 0.1, gcBytes = 100, calls = 1, frames = 1 },
                new ProfileMarkerEntry { m = "Update.ScriptRunBehaviourUpdate", selfTotalMs = 2, calls = 1, frames = 1 },
            },
        };
    }
}
