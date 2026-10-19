#nullable enable
using System;

namespace UnityCli.Protocol
{
    [Serializable]
    public sealed class ProfileStatsArgs
    {
        public int frames;
        public string preset = string.Empty;
    }

    [Serializable]
    public sealed class ProfileCounterStat
    {
        public string name = string.Empty;
        public string category = string.Empty;
        public string unit = string.Empty;
        public double min;
        public double median;
        public double p95;
        public double max;
    }

    [Serializable]
    public sealed class ProfileStatsPayload
    {
        public int frames;
        public string preset = string.Empty;
        public string mode = string.Empty;
        public ProfileCounterStat[] counters = Array.Empty<ProfileCounterStat>();
        public string[] unavailable = Array.Empty<string>();
    }

    [Serializable]
    public sealed class ProfileCaptureStartArgs
    {
        public int frames;
        public int durationSeconds;
        public float budgetMs;
    }

    [Serializable]
    public sealed class ProfileStatusArgs
    {
        public string? captureId;
    }

    [Serializable]
    public sealed class ProfileCaptureStartedPayload
    {
        public string captureId = string.Empty;
        public string status = string.Empty;
        public string startedAt = string.Empty;
        public int requestedFrames;
        public int durationSeconds;
        public float budgetMs;
    }

    [Serializable]
    public sealed class ProfileFrameTimeStats
    {
        public double medianMs;
        public double p95Ms;
        public double worstMs;
        public int worstFrame = -1;
        public int overBudgetCount;
    }

    [Serializable]
    public sealed class ProfileVerdict
    {
        public string bound = "unknown";
        public string basis = "none";
        public double gpuMedianMs = -1;
    }

    [Serializable]
    public sealed class ProfileSpike
    {
        public int frame;
        public double ms;
        public string topMarker = string.Empty;
        public double topMarkerSelfMs;
    }

    [Serializable]
    public sealed class ProfileHotspot
    {
        public string marker = string.Empty;
        public double selfMedianMs;
        public double selfP95Ms;
        public double selfTotalMs;
        public long calls;
    }

    [Serializable]
    public sealed class ProfileGcEntry
    {
        public string marker = string.Empty;
        public long bytesTotal;
        public int framesWithAlloc;
    }

    [Serializable]
    public sealed class ProfileSummaryPayload
    {
        public string captureId = string.Empty;
        public string status = string.Empty;
        public int capturedFrames;
        public int requestedFrames;
        public bool truncated;
        public string mode = string.Empty;
        public string unityVersion = string.Empty;
        public float budgetMs;
        public ProfileFrameTimeStats frameTime = new ProfileFrameTimeStats();
        public ProfileVerdict verdict = new ProfileVerdict();
        public ProfileSpike[] spikes = Array.Empty<ProfileSpike>();
        public ProfileHotspot[] hotspots = Array.Empty<ProfileHotspot>();
        public ProfileGcEntry[] gcTop = Array.Empty<ProfileGcEntry>();
        public string sidecarPath = string.Empty;
    }

    [Serializable]
    public sealed class ProfileFrameMarker
    {
        public string m = string.Empty;
        public double self;
        public long gc;
        public int calls;
    }

    [Serializable]
    public sealed class ProfileFrameEntry
    {
        public int i;
        public double ms;
        public double gpuMs = -1;
        public ProfileFrameMarker[] top = Array.Empty<ProfileFrameMarker>();
    }

    [Serializable]
    public sealed class ProfileMarkerEntry
    {
        public string m = string.Empty;
        public double selfTotalMs;
        public double selfMedianMs;
        public double selfP95Ms;
        public long gcBytes;
        public long calls;
        public int frames;
    }

    [Serializable]
    public sealed class ProfileSidecarFile
    {
        public int schemaVersion = 1;
        public string captureId = string.Empty;
        public string createdUtc = string.Empty;
        public ProfileSummaryPayload summary = new ProfileSummaryPayload();
        public ProfileFrameEntry[] frames = Array.Empty<ProfileFrameEntry>();
        public ProfileMarkerEntry[] markers = Array.Empty<ProfileMarkerEntry>();
    }

    [Serializable]
    public sealed class ProfileFrameAppearance
    {
        public int frame;
        public double selfMs;
        public long gcBytes;
        public int calls;
    }

    [Serializable]
    public sealed class ProfileAnalyzeMarkerPayload
    {
        public string captureId = string.Empty;
        public ProfileMarkerEntry marker = new ProfileMarkerEntry();
        public ProfileFrameAppearance[] appearances = Array.Empty<ProfileFrameAppearance>();
        public bool appearancesFromTopOnly = true;
    }

    [Serializable]
    public sealed class ProfileAnalyzeFramePayload
    {
        public string captureId = string.Empty;
        public float budgetMs;
        public bool overBudget;
        public ProfileFrameEntry frame = new ProfileFrameEntry();
    }

    [Serializable]
    public sealed class ProfileAnalyzeGcPayload
    {
        public string captureId = string.Empty;
        public ProfileGcEntry[] entries = Array.Empty<ProfileGcEntry>();
        public bool truncated;
    }

    [Serializable]
    public sealed class ProfileAnalyzeSpikesPayload
    {
        public string captureId = string.Empty;
        public float budgetMs;
        public ProfileFrameEntry[] frames = Array.Empty<ProfileFrameEntry>();
    }

    /// <summary>One side (base or head) of a `profile compare` run, for comparability checks.</summary>
    [Serializable]
    public sealed class ProfileCompareSide
    {
        public string captureId = string.Empty;
        public int capturedFrames;
        public float budgetMs;
        public string mode = string.Empty;
        public string unityVersion = string.Empty;
        public string bound = "unknown";
    }

    /// <summary>A single scalar metric compared across two captures.</summary>
    [Serializable]
    public sealed class ProfileCompareDelta
    {
        public double baseValue;
        public double headValue;
        public double delta;
        public double deltaPercent;

        /// <summary>False when the base value is zero or negative, which leaves the percentage undefined; deltaPercent is 0 then and must be ignored.</summary>
        public bool deltaPercentAvailable = true;
    }

    /// <summary>A marker present in both captures, with its self-time and GC movement.</summary>
    [Serializable]
    public sealed class ProfileMarkerDelta
    {
        public string marker = string.Empty;
        public double baseSelfMedianMs;
        public double headSelfMedianMs;
        public double deltaMs;
        public double deltaPercent;

        /// <summary>False when the base self-time is zero or negative, which leaves the percentage undefined; deltaPercent is 0 then and must be ignored.</summary>
        public bool deltaPercentAvailable = true;
        public long baseGcBytes;
        public long headGcBytes;
        public long gcBytesDelta;
    }

    [Serializable]
    public sealed class ProfileComparePayload
    {
        public ProfileCompareSide baseCapture = new ProfileCompareSide();
        public ProfileCompareSide headCapture = new ProfileCompareSide();
        public double thresholdPercent;
        public string verdict = "unchanged";
        public ProfileCompareDelta frameTimeMedianMs = new ProfileCompareDelta();
        public ProfileCompareDelta frameTimeP95Ms = new ProfileCompareDelta();
        public ProfileCompareDelta frameTimeWorstMs = new ProfileCompareDelta();
        public ProfileCompareDelta overBudgetFrames = new ProfileCompareDelta();
        public ProfileCompareDelta gcBytesTotal = new ProfileCompareDelta();
        public ProfileMarkerDelta[] regressions = Array.Empty<ProfileMarkerDelta>();
        public ProfileMarkerDelta[] improvements = Array.Empty<ProfileMarkerDelta>();
        public string[] markersAdded = Array.Empty<string>();
        public string[] markersRemoved = Array.Empty<string>();
        public bool truncated;
        public string[] notes = Array.Empty<string>();
    }

    [Serializable]
    public sealed class ProfileMemoryArgs
    {
        public int frames; // 0 = DefaultProfileMemoryFrames
    }

    [Serializable]
    public sealed class ProfileMemoryPayload
    {
        public string reportId = string.Empty;
        public string mode = string.Empty; // "editmode" | "playmode"
        public int frames;
        public string unityVersion = string.Empty;
        public string capturedAtUtc = string.Empty; // ISO-8601 ("O")
        public ProfileCounterStat[] counters = Array.Empty<ProfileCounterStat>();
        public string[] unavailable = Array.Empty<string>();
    }

    [Serializable]
    public sealed class ProfileMemorySidecarFile
    {
        public int schemaVersion = 1;
        public string reportId = string.Empty;
        public ProfileMemoryPayload report = new ProfileMemoryPayload();
    }

    [Serializable]
    public sealed class ProfileMemoryCounterDelta
    {
        public string name = string.Empty;
        public string unit = string.Empty; // "bytes" | "count"
        public double baseMedian;
        public double headMedian;
        public double delta;
        public double deltaPercent;

        /// <summary>False when the base median is zero or negative, which leaves the percentage undefined; deltaPercent is 0 then and must be ignored.</summary>
        public bool deltaPercentAvailable = true;
    }

    [Serializable]
    public sealed class ProfileMemoryCompareSide
    {
        public string reportId = string.Empty;
        public int frames;
        public string mode = string.Empty;
        public string unityVersion = string.Empty;
        public string capturedAtUtc = string.Empty;
    }

    [Serializable]
    public sealed class ProfileMemoryComparePayload
    {
        public ProfileMemoryCompareSide baseReport = new ProfileMemoryCompareSide();
        public ProfileMemoryCompareSide headReport = new ProfileMemoryCompareSide();
        public double thresholdPercent;
        public string verdict = "unchanged"; // regression | improvement | unchanged
        public ProfileCompareDelta totalUsedBytes = new ProfileCompareDelta();
        public ProfileCompareDelta gcUsedBytes = new ProfileCompareDelta();
        public ProfileMemoryCounterDelta[] increases = Array.Empty<ProfileMemoryCounterDelta>();
        public ProfileMemoryCounterDelta[] decreases = Array.Empty<ProfileMemoryCounterDelta>();
        public bool truncated;
        public string[] notes = Array.Empty<string>();
    }

    [Serializable]
    public sealed class ProfileMemorySnapshotArgs
    {
        // Reserved option. CaptureFlags uses the bridge's fixed value.
    }

    [Serializable]
    public sealed class ProfileMemorySnapshotPayload
    {
        public string snapshotId = string.Empty;
        public string path = string.Empty; // Absolute path. The file itself is not transferred.
        public long sizeBytes;
        public string captureFlags = string.Empty;
        public long elapsedMs;
        public string guidance = string.Empty;
    }
}
