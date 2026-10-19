using UnityCli.Cli.Models;
using UnityCli.Cli.Services;
using UnityCli.Protocol;

namespace UnityCli.Cli.Tests;

public sealed class ProfileComparerTests : IDisposable
{
    private readonly string _root;

    public ProfileComparerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ucb-profile-cmp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, ProtocolConstants.ProfilesDirectoryRelative));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private void WriteSidecar(ProfileSidecarFile sidecar)
    {
        string path = Path.Combine(_root, ProtocolConstants.ProfilesDirectoryRelative, sidecar.captureId + ".json");
        File.WriteAllText(path, ProtocolJson.Serialize(sidecar));
    }

    private static ProfileSidecarFile BuildSidecar(
        string captureId,
        double medianMs,
        double p95Ms = 0,
        double worstMs = 0,
        int overBudget = 0,
        int capturedFrames = 100,
        ProfileMarkerEntry[]? markers = null,
        string status = "Completed")
    {
        return new ProfileSidecarFile
        {
            captureId = captureId,
            summary = new ProfileSummaryPayload
            {
                captureId = captureId,
                status = status,
                capturedFrames = capturedFrames,
                mode = "playmode",
                unityVersion = "6000.0.30f1",
                budgetMs = 16.67f,
                frameTime = new ProfileFrameTimeStats
                {
                    medianMs = medianMs,
                    p95Ms = p95Ms,
                    worstMs = worstMs,
                    overBudgetCount = overBudget,
                },
                verdict = new ProfileVerdict { bound = "cpu", basis = "gpuMedian" },
            },
            markers = markers ?? Array.Empty<ProfileMarkerEntry>(),
        };
    }

    private static ProfileMarkerEntry Marker(string name, double selfMedianMs, long gcBytes = 0)
    {
        return new ProfileMarkerEntry { m = name, selfMedianMs = selfMedianMs, gcBytes = gcBytes, frames = 10 };
    }

    private ProfileComparePayload RunCompare(string baseId, string headId, double? threshold = null, int? limit = null)
    {
        var parsed = new ParsedCommand(CommandKind.ProfileCompare)
        {
            ProfileCompareBaseId = baseId,
            ProfileCompareHeadId = headId,
            ProfileThresholdPercent = threshold,
            ProfileLimit = limit,
        };

        ResponseEnvelope response = ProfileComparer.Run(parsed, _root);
        Assert.Equal(ProtocolConstants.StatusSuccess, response.status);
        return CliApp.DeserializeData<ProfileComparePayload>(response)!;
    }

    private ResponseEnvelope RunCompareRaw(string baseId, string headId)
    {
        var parsed = new ParsedCommand(CommandKind.ProfileCompare)
        {
            ProfileCompareBaseId = baseId,
            ProfileCompareHeadId = headId,
        };

        return ProfileComparer.Run(parsed, _root);
    }

    [Fact]
    public void Compare_SlowerHead_ReportsRegression()
    {
        WriteSidecar(BuildSidecar("base", medianMs: 10.0, p95Ms: 12.0, worstMs: 20.0, overBudget: 2));
        WriteSidecar(BuildSidecar("head", medianMs: 13.0, p95Ms: 15.0, worstMs: 30.0, overBudget: 9));

        ProfileComparePayload payload = RunCompare("base", "head");

        Assert.Equal("regression", payload.verdict);
        Assert.Equal(5.0, payload.thresholdPercent);
        Assert.Equal("base", payload.baseCapture.captureId);
        Assert.Equal("head", payload.headCapture.captureId);
        Assert.Equal("cpu", payload.baseCapture.bound);
        Assert.Equal(3.0, payload.frameTimeMedianMs.delta, precision: 6);
        Assert.Equal(30.0, payload.frameTimeMedianMs.deltaPercent, precision: 6);
        Assert.Equal(3.0, payload.frameTimeP95Ms.delta, precision: 6);
        Assert.Equal(10.0, payload.frameTimeWorstMs.delta, precision: 6);
        Assert.Equal(2.0, payload.overBudgetFrames.baseValue);
        Assert.Equal(9.0, payload.overBudgetFrames.headValue);
        Assert.Empty(payload.notes);
        Assert.False(payload.truncated);
    }

    [Fact]
    public void Compare_FasterHead_ReportsImprovement()
    {
        WriteSidecar(BuildSidecar("base", medianMs: 20.0));
        WriteSidecar(BuildSidecar("head", medianMs: 10.0));

        ProfileComparePayload payload = RunCompare("base", "head");

        Assert.Equal("improvement", payload.verdict);
        Assert.Equal(-50.0, payload.frameTimeMedianMs.deltaPercent, precision: 6);
    }

    [Fact]
    public void Compare_WithinThreshold_ReportsUnchanged()
    {
        WriteSidecar(BuildSidecar("base", medianMs: 10.0));
        WriteSidecar(BuildSidecar("head", medianMs: 10.4));

        ProfileComparePayload payload = RunCompare("base", "head");

        Assert.Equal("unchanged", payload.verdict);
        Assert.Equal(4.0, payload.frameTimeMedianMs.deltaPercent, precision: 6);
    }

    [Fact]
    public void Compare_CustomThreshold_ChangesVerdict()
    {
        WriteSidecar(BuildSidecar("base", medianMs: 10.0));
        WriteSidecar(BuildSidecar("head", medianMs: 10.4));

        Assert.Equal("regression", RunCompare("base", "head", threshold: 1.0).verdict);
        Assert.Equal("unchanged", RunCompare("base", "head", threshold: 10.0).verdict);
    }

    [Fact]
    public void Compare_Markers_SplitsRegressionsImprovementsAddedRemoved()
    {
        WriteSidecar(BuildSidecar(
            "base",
            medianMs: 10.0,
            markers: new[] { Marker("Slower", 1.0), Marker("Faster", 5.0), Marker("Gone", 2.0) }));
        WriteSidecar(BuildSidecar(
            "head",
            medianMs: 10.0,
            markers: new[] { Marker("Slower", 4.0), Marker("Faster", 1.0), Marker("New", 3.0) }));

        ProfileComparePayload payload = RunCompare("base", "head");

        ProfileMarkerDelta regression = Assert.Single(payload.regressions);
        Assert.Equal("Slower", regression.marker);
        Assert.Equal(3.0, regression.deltaMs, precision: 6);
        Assert.Equal(300.0, regression.deltaPercent, precision: 6);

        ProfileMarkerDelta improvement = Assert.Single(payload.improvements);
        Assert.Equal("Faster", improvement.marker);
        Assert.Equal(-4.0, improvement.deltaMs, precision: 6);
        Assert.Equal(-80.0, improvement.deltaPercent, precision: 6);

        Assert.Equal(new[] { "New" }, payload.markersAdded);
        Assert.Equal(new[] { "Gone" }, payload.markersRemoved);
        Assert.False(payload.truncated);
    }

    [Fact]
    public void Compare_ZeroBaseMarkerMedian_MarksPercentUnavailable()
    {
        WriteSidecar(BuildSidecar("base", medianMs: 10.0, markers: new[] { Marker("Fresh", 0.0) }));
        WriteSidecar(BuildSidecar("head", medianMs: 10.0, markers: new[] { Marker("Fresh", 2.0) }));

        ProfileComparePayload payload = RunCompare("base", "head");

        ProfileMarkerDelta regression = Assert.Single(payload.regressions);
        Assert.Equal(2.0, regression.deltaMs, precision: 6);
        Assert.False(regression.deltaPercentAvailable);
        Assert.Equal(0.0, regression.deltaPercent);
    }

    [Fact]
    public void Compare_ZeroBaseOverBudgetFrames_MarksPercentUnavailableButKeepsDelta()
    {
        WriteSidecar(BuildSidecar("base", medianMs: 10.0, overBudget: 0));
        WriteSidecar(BuildSidecar("head", medianMs: 10.0, overBudget: 40));

        ProfileComparePayload payload = RunCompare("base", "head");

        Assert.False(payload.overBudgetFrames.deltaPercentAvailable);
        Assert.Equal(0.0, payload.overBudgetFrames.deltaPercent);
        Assert.Equal(40.0, payload.overBudgetFrames.delta, precision: 6);
    }

    [Fact]
    public void Compare_ZeroBaseGcBytes_MarksPercentUnavailableButKeepsDelta()
    {
        WriteSidecar(BuildSidecar("base", medianMs: 10.0, markers: new[] { Marker("A", 1.0, gcBytes: 0) }));
        WriteSidecar(BuildSidecar("head", medianMs: 10.0, markers: new[] { Marker("A", 1.0, gcBytes: 5_000_000) }));

        ProfileComparePayload payload = RunCompare("base", "head");

        Assert.False(payload.gcBytesTotal.deltaPercentAvailable);
        Assert.Equal(0.0, payload.gcBytesTotal.deltaPercent);
        Assert.Equal(5_000_000.0, payload.gcBytesTotal.delta, precision: 6);
    }

    [Fact]
    public void Compare_NonZeroBase_KeepsPercentAvailable()
    {
        WriteSidecar(BuildSidecar("base", medianMs: 10.0, overBudget: 2));
        WriteSidecar(BuildSidecar("head", medianMs: 13.0, overBudget: 4));

        ProfileComparePayload payload = RunCompare("base", "head");

        Assert.True(payload.frameTimeMedianMs.deltaPercentAvailable);
        Assert.True(payload.overBudgetFrames.deltaPercentAvailable);
        Assert.Equal(100.0, payload.overBudgetFrames.deltaPercent, precision: 6);
    }

    [Fact]
    public void Compare_GcBytes_TotalsAndPerMarkerDeltas()
    {
        WriteSidecar(BuildSidecar(
            "base",
            medianMs: 10.0,
            markers: new[] { Marker("A", 1.0, gcBytes: 1000), Marker("B", 1.0, gcBytes: 500) }));
        WriteSidecar(BuildSidecar(
            "head",
            medianMs: 10.0,
            markers: new[] { Marker("A", 2.0, gcBytes: 4000), Marker("B", 1.0, gcBytes: 500) }));

        ProfileComparePayload payload = RunCompare("base", "head");

        Assert.Equal(1500.0, payload.gcBytesTotal.baseValue);
        Assert.Equal(4500.0, payload.gcBytesTotal.headValue);
        Assert.Equal(3000.0, payload.gcBytesTotal.delta);
        Assert.Equal(200.0, payload.gcBytesTotal.deltaPercent, precision: 6);

        ProfileMarkerDelta regression = Assert.Single(payload.regressions);
        Assert.Equal("A", regression.marker);
        Assert.Equal(1000, regression.baseGcBytes);
        Assert.Equal(4000, regression.headGcBytes);
        Assert.Equal(3000, regression.gcBytesDelta);
    }

    [Fact]
    public void Compare_Limit_TruncatesListsAndSetsFlag()
    {
        WriteSidecar(BuildSidecar(
            "base",
            medianMs: 10.0,
            markers: new[] { Marker("R1", 1.0), Marker("R2", 1.0), Marker("R3", 1.0) }));
        WriteSidecar(BuildSidecar(
            "head",
            medianMs: 10.0,
            markers: new[] { Marker("R1", 9.0), Marker("R2", 5.0), Marker("R3", 3.0) }));

        ProfileComparePayload payload = RunCompare("base", "head", limit: 2);

        Assert.True(payload.truncated);
        Assert.Equal(2, payload.regressions.Length);
        // Sorted by deltaMs descending — the marker that slowed the most comes first.
        Assert.Equal("R1", payload.regressions[0].marker);
        Assert.Equal("R2", payload.regressions[1].marker);
    }

    [Fact]
    public void Compare_Limit_TruncatesAddedAndRemovedLists()
    {
        WriteSidecar(BuildSidecar("base", medianMs: 10.0, markers: new[] { Marker("G1", 1.0), Marker("G2", 1.0) }));
        WriteSidecar(BuildSidecar("head", medianMs: 10.0, markers: new[] { Marker("N1", 1.0), Marker("N2", 1.0) }));

        ProfileComparePayload payload = RunCompare("base", "head", limit: 1);

        Assert.True(payload.truncated);
        Assert.Single(payload.markersAdded);
        Assert.Single(payload.markersRemoved);
    }

    [Fact]
    public void Compare_DifferentBudget_AddsNote()
    {
        ProfileSidecarFile head = BuildSidecar("head", medianMs: 10.0);
        head.summary.budgetMs = 33.33f;
        WriteSidecar(BuildSidecar("base", medianMs: 10.0));
        WriteSidecar(head);

        ProfileComparePayload payload = RunCompare("base", "head");

        Assert.Contains(payload.notes, n => n.Contains("budgetMs"));
    }

    [Fact]
    public void Compare_EqualBudget_AddsNoNoteDespiteFloatRoundTrip()
    {
        ProfileSidecarFile head = BuildSidecar("head", medianMs: 10.0);
        // One ULP at 16.67 is ~1e-6 — well inside the tolerance, so this must not be reported as a mismatch.
        head.summary.budgetMs = 16.67f + 1e-6f;
        WriteSidecar(BuildSidecar("base", medianMs: 10.0));
        WriteSidecar(head);

        ProfileComparePayload payload = RunCompare("base", "head");

        Assert.Empty(payload.notes);
    }

    [Fact]
    public void Compare_DifferentUnityVersion_AddsNote()
    {
        ProfileSidecarFile head = BuildSidecar("head", medianMs: 10.0);
        head.summary.unityVersion = "6000.1.0f1";
        WriteSidecar(BuildSidecar("base", medianMs: 10.0));
        WriteSidecar(head);

        ProfileComparePayload payload = RunCompare("base", "head");

        Assert.Contains(payload.notes, n => n.Contains("unityVersion"));
    }

    [Fact]
    public void Compare_TruncatedCapture_AddsNote()
    {
        ProfileSidecarFile head = BuildSidecar("head", medianMs: 10.0);
        head.summary.truncated = true;
        WriteSidecar(BuildSidecar("base", medianMs: 10.0));
        WriteSidecar(head);

        ProfileComparePayload payload = RunCompare("base", "head");

        Assert.Contains(payload.notes, n => n.Contains("truncated"));
    }

    [Fact]
    public void Compare_FrameCountGapOverQuarter_AddsNote()
    {
        WriteSidecar(BuildSidecar("base", medianMs: 10.0, capturedFrames: 100));
        WriteSidecar(BuildSidecar("head", medianMs: 10.0, capturedFrames: 70));

        ProfileComparePayload payload = RunCompare("base", "head");

        Assert.Contains(payload.notes, n => n.Contains("frame count"));
    }

    [Fact]
    public void Compare_FrameCountGapWithinQuarter_HasNoNote()
    {
        WriteSidecar(BuildSidecar("base", medianMs: 10.0, capturedFrames: 100));
        WriteSidecar(BuildSidecar("head", medianMs: 10.0, capturedFrames: 80));

        ProfileComparePayload payload = RunCompare("base", "head");

        Assert.Empty(payload.notes);
    }

    [Fact]
    public void Compare_FrameCountGapExactlyQuarter_HasNoNote()
    {
        // The boundary is exclusive on purpose: a gap of exactly 25% of the larger count still compares cleanly.
        WriteSidecar(BuildSidecar("base", medianMs: 10.0, capturedFrames: 100));
        WriteSidecar(BuildSidecar("head", medianMs: 10.0, capturedFrames: 75));

        ProfileComparePayload payload = RunCompare("base", "head");

        Assert.Empty(payload.notes);
    }

    [Fact]
    public void Compare_FrameCountGapJustOverQuarter_AddsNote()
    {
        WriteSidecar(BuildSidecar("base", medianMs: 10.0, capturedFrames: 100));
        WriteSidecar(BuildSidecar("head", medianMs: 10.0, capturedFrames: 74));

        ProfileComparePayload payload = RunCompare("base", "head");

        Assert.Contains(payload.notes, n => n.Contains("frame count"));
    }

    [Fact]
    public void Compare_BaseMedianZero_ForcesUnchangedWithNote()
    {
        WriteSidecar(BuildSidecar("base", medianMs: 0.0));
        WriteSidecar(BuildSidecar("head", medianMs: 25.0));

        ProfileComparePayload payload = RunCompare("base", "head");

        Assert.Equal("unchanged", payload.verdict);
        Assert.False(payload.frameTimeMedianMs.deltaPercentAvailable);
        Assert.Equal(0.0, payload.frameTimeMedianMs.deltaPercent);
        Assert.Equal(25.0, payload.frameTimeMedianMs.delta, precision: 6);
        Assert.Contains(payload.notes, n => n.Contains("base capture has no usable frame-time"));
    }

    [Fact]
    public void Compare_HeadMedianZero_ForcesUnchangedWithNote()
    {
        WriteSidecar(BuildSidecar("base", medianMs: 25.0));
        WriteSidecar(BuildSidecar("head", medianMs: 0.0));

        ProfileComparePayload payload = RunCompare("base", "head");

        Assert.Equal("unchanged", payload.verdict);
        Assert.Equal(-25.0, payload.frameTimeMedianMs.delta, precision: 6);
        Assert.Contains(payload.notes, n => n.Contains("head capture has no usable frame-time"));
    }

    [Fact]
    public void Compare_InterruptedHeadCapture_ReturnsProfileFailed()
    {
        WriteSidecar(BuildSidecar("base", medianMs: 10.0));
        WriteSidecar(BuildSidecar("head", medianMs: 0.0, capturedFrames: 0, status: "Interrupted"));

        ResponseEnvelope response = RunCompareRaw("base", "head");

        Assert.Equal(ProtocolConstants.StatusError, response.status);
        Assert.Equal(ProtocolConstants.ErrorProfileFailed, response.error!.code);
        Assert.Contains("head", response.error.message);
        Assert.Contains("Interrupted", response.error.message);
    }

    [Fact]
    public void Compare_InterruptedBaseCapture_ReturnsProfileFailed()
    {
        WriteSidecar(BuildSidecar("base", medianMs: 0.0, capturedFrames: 0, status: "Interrupted"));
        WriteSidecar(BuildSidecar("head", medianMs: 10.0));

        ResponseEnvelope response = RunCompareRaw("base", "head");

        Assert.Equal(ProtocolConstants.StatusError, response.status);
        Assert.Equal(ProtocolConstants.ErrorProfileFailed, response.error!.code);
        Assert.Contains("base", response.error.message);
        Assert.Contains("Interrupted", response.error.message);
    }

    [Fact]
    public void Compare_FailedCapture_ReturnsProfileFailed()
    {
        WriteSidecar(BuildSidecar("base", medianMs: 10.0));
        WriteSidecar(BuildSidecar("head", medianMs: 10.0, status: "Failed"));

        ResponseEnvelope response = RunCompareRaw("base", "head");

        Assert.Equal(ProtocolConstants.StatusError, response.status);
        Assert.Equal(ProtocolConstants.ErrorProfileFailed, response.error!.code);
        Assert.Contains("Failed", response.error.message);
    }

    [Fact]
    public void Compare_CompletedCaptureWithZeroFrames_ReturnsProfileFailed()
    {
        WriteSidecar(BuildSidecar("base", medianMs: 10.0));
        WriteSidecar(BuildSidecar("head", medianMs: 10.0, capturedFrames: 0));

        ResponseEnvelope response = RunCompareRaw("base", "head");

        Assert.Equal(ProtocolConstants.StatusError, response.status);
        Assert.Equal(ProtocolConstants.ErrorProfileFailed, response.error!.code);
        Assert.Contains("capturedFrames=0", response.error.message);
    }

    [Fact]
    public void Compare_BothSidesUnfinished_ReportsBaseFirst()
    {
        WriteSidecar(BuildSidecar("base", medianMs: 0.0, capturedFrames: 0, status: "Interrupted"));
        WriteSidecar(BuildSidecar("head", medianMs: 0.0, capturedFrames: 0, status: "Failed"));

        ResponseEnvelope response = RunCompareRaw("base", "head");

        Assert.Equal(ProtocolConstants.ErrorProfileFailed, response.error!.code);
        Assert.Contains("base", response.error.message);
        Assert.DoesNotContain("Failed", response.error.message);
    }

    [Fact]
    public void Compare_FlatMarkerWithGcGrowth_LandsInRegressions()
    {
        WriteSidecar(BuildSidecar("base", medianMs: 10.0, markers: new[] { Marker("Alloc", 1.0, gcBytes: 0) }));
        WriteSidecar(BuildSidecar("head", medianMs: 10.0, markers: new[] { Marker("Alloc", 1.0, gcBytes: 5_000_000) }));

        ProfileComparePayload payload = RunCompare("base", "head");

        ProfileMarkerDelta regression = Assert.Single(payload.regressions);
        Assert.Equal("Alloc", regression.marker);
        Assert.Equal(0.0, regression.deltaMs, precision: 6);
        Assert.Equal(5_000_000, regression.gcBytesDelta);
        Assert.Empty(payload.improvements);
    }

    [Fact]
    public void Compare_FlatMarkerWithGcDrop_LandsInImprovements()
    {
        WriteSidecar(BuildSidecar("base", medianMs: 10.0, markers: new[] { Marker("Alloc", 1.0, gcBytes: 5_000_000) }));
        WriteSidecar(BuildSidecar("head", medianMs: 10.0, markers: new[] { Marker("Alloc", 1.0, gcBytes: 0) }));

        ProfileComparePayload payload = RunCompare("base", "head");

        ProfileMarkerDelta improvement = Assert.Single(payload.improvements);
        Assert.Equal("Alloc", improvement.marker);
        Assert.Equal(0.0, improvement.deltaMs, precision: 6);
        Assert.Equal(-5_000_000, improvement.gcBytesDelta);
        Assert.Empty(payload.regressions);
    }

    [Fact]
    public void Compare_FlatMarkerWithFlatGc_LandsInNeitherList()
    {
        WriteSidecar(BuildSidecar("base", medianMs: 10.0, markers: new[] { Marker("Idle", 1.0, gcBytes: 128) }));
        WriteSidecar(BuildSidecar("head", medianMs: 10.0, markers: new[] { Marker("Idle", 1.0, gcBytes: 128) }));

        ProfileComparePayload payload = RunCompare("base", "head");

        Assert.Empty(payload.regressions);
        Assert.Empty(payload.improvements);
    }

    [Fact]
    public void Compare_TiedRegressions_BreakTiesOrdinallyUnderLimit()
    {
        WriteSidecar(BuildSidecar(
            "base",
            medianMs: 10.0,
            markers: new[] { Marker("Charlie", 1.0), Marker("Alpha", 1.0), Marker("Bravo", 1.0) }));
        WriteSidecar(BuildSidecar(
            "head",
            medianMs: 10.0,
            markers: new[] { Marker("Charlie", 3.0), Marker("Alpha", 3.0), Marker("Bravo", 3.0) }));

        ProfileComparePayload payload = RunCompare("base", "head", limit: 2);

        Assert.True(payload.truncated);
        Assert.Equal(new[] { "Alpha", "Bravo" }, payload.regressions.Select(d => d.marker).ToArray());
    }

    [Fact]
    public void Compare_TiedImprovements_BreakTiesOrdinally()
    {
        WriteSidecar(BuildSidecar(
            "base",
            medianMs: 10.0,
            markers: new[] { Marker("Charlie", 3.0), Marker("Alpha", 3.0), Marker("Bravo", 3.0) }));
        WriteSidecar(BuildSidecar(
            "head",
            medianMs: 10.0,
            markers: new[] { Marker("Charlie", 1.0), Marker("Alpha", 1.0), Marker("Bravo", 1.0) }));

        ProfileComparePayload payload = RunCompare("base", "head");

        Assert.Equal(new[] { "Alpha", "Bravo", "Charlie" }, payload.improvements.Select(d => d.marker).ToArray());
    }

    [Fact]
    public void Compare_MissingBaseSidecar_ReturnsProfileNotFound()
    {
        WriteSidecar(BuildSidecar("head", medianMs: 10.0));
        var parsed = new ParsedCommand(CommandKind.ProfileCompare)
        {
            ProfileCompareBaseId = "nope",
            ProfileCompareHeadId = "head",
        };

        ResponseEnvelope response = ProfileComparer.Run(parsed, _root);

        Assert.Equal(ProtocolConstants.StatusError, response.status);
        Assert.Equal(ProtocolConstants.ErrorProfileNotFound, response.error!.code);
        Assert.Contains("nope", response.error.message);
    }

    [Fact]
    public void Compare_MissingHeadSidecar_ReturnsProfileNotFound()
    {
        WriteSidecar(BuildSidecar("base", medianMs: 10.0));
        var parsed = new ParsedCommand(CommandKind.ProfileCompare)
        {
            ProfileCompareBaseId = "base",
            ProfileCompareHeadId = "nope",
        };

        ResponseEnvelope response = ProfileComparer.Run(parsed, _root);

        Assert.Equal(ProtocolConstants.StatusError, response.status);
        Assert.Equal(ProtocolConstants.ErrorProfileNotFound, response.error!.code);
        Assert.Contains("nope", response.error.message);
    }

    [Fact]
    public void Compare_UnreadableSidecar_ReturnsProfileFailed()
    {
        WriteSidecar(BuildSidecar("base", medianMs: 10.0));
        File.WriteAllText(
            Path.Combine(_root, ProtocolConstants.ProfilesDirectoryRelative, "head.json"),
            "{ this is not json");
        var parsed = new ParsedCommand(CommandKind.ProfileCompare)
        {
            ProfileCompareBaseId = "base",
            ProfileCompareHeadId = "head",
        };

        ResponseEnvelope response = ProfileComparer.Run(parsed, _root);

        Assert.Equal(ProtocolConstants.StatusError, response.status);
        Assert.Equal(ProtocolConstants.ErrorProfileFailed, response.error!.code);
    }

    [Fact]
    public void Compare_NoProjectRoot_ReturnsUsageError()
    {
        var parsed = new ParsedCommand(CommandKind.ProfileCompare)
        {
            ProfileCompareBaseId = "base",
            ProfileCompareHeadId = "head",
        };

        ResponseEnvelope response = ProfileComparer.Run(parsed, null);

        Assert.Equal(ProtocolConstants.StatusError, response.status);
        Assert.Equal("CLI_USAGE", response.error!.code);
    }
}
