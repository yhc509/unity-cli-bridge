using UnityCli.Protocol;

namespace UnityCli.Cli.Tests;

public sealed class ProfileMarkerStatTests
{
    private const string Threat = "Assembly-CSharp.dll!::ThreatField.Update() [Invoke]";

    [Fact]
    public void BuildMarkerStat_RecorderWithSamples_UsesRecorderTimeAndCaptureGc()
    {
        ProfileMarkerStat stat = ProfileStatMath.BuildMarkerStat(
            "ThreatField.Update",
            Threat,
            resolvedFromShortName: true,
            recorderMs: new[] { 0.010, 0.030, 0.020, 0.040 },
            recorderCalls: new long[] { 1, 1, 1, 1 },
            captureMs: new[] { 0.5, 0.5, 0.5, 0.5 },
            captureCalls: new long[] { 1, 1, 1, 1 },
            captureGcBytes: new long[] { 1000, 3000, 0, 4000 });

        Assert.Equal(ProfileStatMath.MarkerPathRecorder, stat.path);
        Assert.Equal(Threat, stat.marker);
        Assert.Equal(Threat, stat.resolvedMarker);
        Assert.Equal(4, stat.frames);
        Assert.Equal(4, stat.framesWithCalls);
        Assert.Equal(0.010, stat.min, precision: 9);
        Assert.Equal(0.025, stat.median, precision: 9);
        Assert.Equal(0.040, stat.p95, precision: 9);
        Assert.Equal(0.040, stat.max, precision: 9);
        Assert.Equal(1.0, stat.callsPerFrame);
        Assert.Equal(8000, stat.gcBytesTotal);
        Assert.Equal(2000, stat.gcBytesPerFrame);
        Assert.Equal(string.Empty, stat.note);
    }

    [Fact]
    public void BuildMarkerStat_RecorderSawNothing_FallsBackToCapture()
    {
        ProfileMarkerStat stat = ProfileStatMath.BuildMarkerStat(
            Threat,
            Threat,
            resolvedFromShortName: false,
            recorderMs: new[] { 0.0, 0.0, 0.0 },
            recorderCalls: new long[] { 0, 0, 0 },
            captureMs: new[] { 0.2, 0.0, 0.4 },
            captureCalls: new long[] { 2, 0, 2 },
            captureGcBytes: new long[] { 10, 0, 20 });

        Assert.Equal(ProfileStatMath.MarkerPathCapture, stat.path);
        Assert.Equal(string.Empty, stat.resolvedMarker);
        Assert.Equal(2, stat.framesWithCalls);
        Assert.Equal(0.2, stat.median, precision: 9);
        Assert.Equal(4 / 3.0, stat.callsPerFrame, precision: 9);
        Assert.Contains("ProfilerRecorder returned no samples", stat.note);
        Assert.Equal(10, stat.gcBytesPerFrame);
    }

    [Fact]
    public void BuildMarkerStat_RecorderCoversLessThanHalfTheWindow_UsesCapture()
    {
        ProfileMarkerStat stat = ProfileStatMath.BuildMarkerStat(
            "ThreatField.Update",
            Threat,
            resolvedFromShortName: true,
            recorderMs: new[] { 0.05 },
            recorderCalls: new long[] { 1 },
            captureMs: new[] { 0.1, 0.2, 0.3, 0.4 },
            captureCalls: new long[] { 1, 1, 1, 1 },
            captureGcBytes: new long[] { 0, 0, 0, 0 });

        Assert.Equal(ProfileStatMath.MarkerPathCapture, stat.path);
        Assert.Equal(4, stat.frames);
        Assert.Equal("ProfilerRecorder covered 1 of 4 frames; times come from the profiler frame hierarchy.", stat.note);
    }

    [Fact]
    public void BuildMarkerStat_NoRecorder_UsesCaptureWithoutNote()
    {
        ProfileMarkerStat stat = ProfileStatMath.BuildMarkerStat(
            "Tick", "MyGame.Tick", true, null, null,
            new[] { 1.0, 2.0 }, new long[] { 1, 1 }, new long[] { 0, 0 });

        Assert.Equal(ProfileStatMath.MarkerPathCapture, stat.path);
        Assert.Equal(string.Empty, stat.note);
        Assert.Equal(1.5, stat.median);
    }

    [Fact]
    public void BuildMarkerStat_MarkerNeverRan_ReportsZerosWithNote()
    {
        ProfileMarkerStat stat = ProfileStatMath.BuildMarkerStat(
            "Tick", "MyGame.Tick", true,
            new[] { 0.0, 0.0 }, new long[] { 0, 0 },
            new[] { 0.0, 0.0 }, new long[] { 0, 0 }, new long[] { 0, 0 });

        Assert.Equal(ProfileStatMath.MarkerPathRecorder, stat.path);
        Assert.Equal(0, stat.framesWithCalls);
        Assert.Equal(0, stat.max);
        Assert.Equal("The marker did not run during these frames.", stat.note);
    }
}
