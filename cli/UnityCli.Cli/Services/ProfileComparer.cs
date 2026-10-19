#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using UnityCli.Cli.Models;
using UnityCli.Protocol;

namespace UnityCli.Cli.Services;

/// <summary>Local diff of two finished captures' sidecars. No IPC.</summary>
internal static class ProfileComparer
{
    private const double DefaultThresholdPercent = 5.0;

    // Frame counts further apart than this fraction of the larger count make the comparison shaky.
    // The check is exclusive: a gap of exactly this fraction still counts as comparable.
    private const double FrameCountMismatchRatio = 0.25;

    // budgetMs is a float, so equal budgets can differ by an ULP after a JSON round-trip.
    private const float BudgetToleranceMs = 1e-4f;

    // The only sidecar status a finished capture carries.
    private const string CompletedStatus = "Completed";

    internal static ResponseEnvelope Run(ParsedCommand parsed, string? projectRoot)
    {
        if (!ProfileSidecarLoader.TryLoad(
                projectRoot, parsed.ProfileCompareBaseId, out ProfileSidecarFile baseSidecar, out ResponseEnvelope? baseFailure))
        {
            return baseFailure!;
        }

        if (!ProfileSidecarLoader.TryLoad(
                projectRoot, parsed.ProfileCompareHeadId, out ProfileSidecarFile headSidecar, out ResponseEnvelope? headFailure))
        {
            return headFailure!;
        }

        // Check base first, then head, so the reported side is deterministic when both are unusable.
        if (!TryEnsureFinished(baseSidecar, "base", out ResponseEnvelope? baseUnfinished))
        {
            return baseUnfinished!;
        }

        if (!TryEnsureFinished(headSidecar, "head", out ResponseEnvelope? headUnfinished))
        {
            return headUnfinished!;
        }

        int limit = parsed.ProfileLimit ?? ProtocolConstants.DefaultProfileListLimit;
        double threshold = parsed.ProfileThresholdPercent ?? DefaultThresholdPercent;
        ProfileComparePayload payload = Compare(baseSidecar, headSidecar, threshold, limit);

        return ResponseEnvelope.Success(
            "local",
            null,
            JsonSerializer.SerializeToElement(payload, ProtocolJson.Default),
            0,
            "cli");
    }

    /// <summary>
    /// Refuses a capture that never finished. A domain reload mid-capture writes a terminal sidecar with
    /// <c>status = "Interrupted"</c> and zeroed stats, which would otherwise read as a 100% improvement.
    /// </summary>
    private static bool TryEnsureFinished(ProfileSidecarFile sidecar, string side, out ResponseEnvelope? failure)
    {
        ProfileSummaryPayload summary = sidecar.summary;
        bool completed = string.Equals(summary.status, CompletedStatus, StringComparison.Ordinal);
        if (completed && summary.capturedFrames > 0)
        {
            failure = null;
            return true;
        }

        string reason = completed
            ? $"status={summary.status}, capturedFrames={summary.capturedFrames}"
            : $"status={summary.status}";
        failure = ResponseEnvelope.Failure(
            "local",
            null,
            ProtocolConstants.ErrorProfileFailed,
            $"{side} capture `{sidecar.captureId}` did not finish cleanly and cannot be compared ({reason}). Capture it again.",
            false,
            0,
            "cli");
        return false;
    }

    internal static ProfileComparePayload Compare(
        ProfileSidecarFile baseSidecar,
        ProfileSidecarFile headSidecar,
        double thresholdPercent,
        int limit)
    {
        ProfileSummaryPayload baseSummary = baseSidecar.summary;
        ProfileSummaryPayload headSummary = headSidecar.summary;

        var payload = new ProfileComparePayload
        {
            baseCapture = BuildSide(baseSidecar),
            headCapture = BuildSide(headSidecar),
            thresholdPercent = thresholdPercent,
            frameTimeMedianMs = BuildDelta(baseSummary.frameTime.medianMs, headSummary.frameTime.medianMs),
            frameTimeP95Ms = BuildDelta(baseSummary.frameTime.p95Ms, headSummary.frameTime.p95Ms),
            frameTimeWorstMs = BuildDelta(baseSummary.frameTime.worstMs, headSummary.frameTime.worstMs),
            overBudgetFrames = BuildDelta(baseSummary.frameTime.overBudgetCount, headSummary.frameTime.overBudgetCount),
            gcBytesTotal = BuildDelta(SumGcBytes(baseSidecar), SumGcBytes(headSidecar)),
        };

        bool baseMedianUnusable = baseSummary.frameTime.medianMs <= 0;
        bool headMedianUnusable = headSummary.frameTime.medianMs <= 0;

        // An unusable base leaves deltaPercent undefined, and an unusable head makes any movement meaningless;
        // in both cases the verdict must not be derived from the percentage.
        bool verdictUndecidable =
            baseMedianUnusable || headMedianUnusable || !payload.frameTimeMedianMs.deltaPercentAvailable;
        payload.verdict = verdictUndecidable
            ? "unchanged"
            : ResolveVerdict(payload.frameTimeMedianMs.deltaPercent, thresholdPercent);

        bool truncated = false;
        BuildMarkerDeltas(baseSidecar, headSidecar, limit, payload, ref truncated);
        payload.truncated = truncated;
        payload.notes = BuildNotes(baseSidecar, headSidecar, baseMedianUnusable, headMedianUnusable);
        return payload;
    }

    private static ProfileCompareSide BuildSide(ProfileSidecarFile sidecar)
    {
        return new ProfileCompareSide
        {
            captureId = sidecar.captureId,
            capturedFrames = sidecar.summary.capturedFrames,
            budgetMs = sidecar.summary.budgetMs,
            mode = sidecar.summary.mode,
            unityVersion = sidecar.summary.unityVersion,
            bound = sidecar.summary.verdict.bound,
        };
    }

    private static ProfileCompareDelta BuildDelta(double baseValue, double headValue)
    {
        double delta = headValue - baseValue;
        bool available = IsPercentAvailable(baseValue);
        return new ProfileCompareDelta
        {
            baseValue = baseValue,
            headValue = headValue,
            delta = delta,
            deltaPercent = Percent(baseValue, delta),
            deltaPercentAvailable = available,
        };
    }

    /// <summary>A zero (or negative) base has no percentage to move by, so 0 would be indistinguishable from "no change".</summary>
    private static bool IsPercentAvailable(double baseValue) => baseValue > 0;

    private static double Percent(double baseValue, double delta) =>
        baseValue <= 0 ? 0 : delta / baseValue * 100.0;

    private static long SumGcBytes(ProfileSidecarFile sidecar) => sidecar.markers.Sum(m => m.gcBytes);

    private static string ResolveVerdict(double deltaPercent, double thresholdPercent)
    {
        if (deltaPercent > thresholdPercent)
        {
            return "regression";
        }

        return deltaPercent < -thresholdPercent ? "improvement" : "unchanged";
    }

    private static void BuildMarkerDeltas(
        ProfileSidecarFile baseSidecar,
        ProfileSidecarFile headSidecar,
        int limit,
        ProfileComparePayload payload,
        ref bool truncated)
    {
        Dictionary<string, ProfileMarkerEntry> baseMarkers = ToLookup(baseSidecar.markers);
        Dictionary<string, ProfileMarkerEntry> headMarkers = ToLookup(headSidecar.markers);

        var shared = new List<ProfileMarkerDelta>();
        foreach (KeyValuePair<string, ProfileMarkerEntry> pair in headMarkers)
        {
            if (!baseMarkers.TryGetValue(pair.Key, out ProfileMarkerEntry? baseMarker))
            {
                continue;
            }

            ProfileMarkerEntry headMarker = pair.Value;
            double deltaMs = headMarker.selfMedianMs - baseMarker.selfMedianMs;
            shared.Add(new ProfileMarkerDelta
            {
                marker = pair.Key,
                baseSelfMedianMs = baseMarker.selfMedianMs,
                headSelfMedianMs = headMarker.selfMedianMs,
                deltaMs = deltaMs,
                deltaPercent = Percent(baseMarker.selfMedianMs, deltaMs),
                deltaPercentAvailable = IsPercentAvailable(baseMarker.selfMedianMs),
                baseGcBytes = baseMarker.gcBytes,
                headGcBytes = headMarker.gcBytes,
                gcBytesDelta = headMarker.gcBytes - baseMarker.gcBytes,
            });
        }

        // A flat self-time with a GC swing is still a real regression/improvement, so classify on both axes.
        // The ordinal marker tie-break keeps the order (and therefore --limit) reproducible across runs.
        ProfileMarkerDelta[] regressions = shared
            .Where(d => d.deltaMs > 0 || (d.deltaMs == 0 && d.gcBytesDelta > 0))
            .OrderByDescending(d => d.deltaMs)
            .ThenByDescending(d => d.gcBytesDelta)
            .ThenBy(d => d.marker, StringComparer.Ordinal)
            .ToArray();
        ProfileMarkerDelta[] improvements = shared
            .Where(d => d.deltaMs < 0 || (d.deltaMs == 0 && d.gcBytesDelta < 0))
            .OrderBy(d => d.deltaMs)
            .ThenBy(d => d.gcBytesDelta)
            .ThenBy(d => d.marker, StringComparer.Ordinal)
            .ToArray();
        string[] added = headMarkers.Keys.Where(m => !baseMarkers.ContainsKey(m)).OrderBy(m => m, StringComparer.Ordinal).ToArray();
        string[] removed = baseMarkers.Keys.Where(m => !headMarkers.ContainsKey(m)).OrderBy(m => m, StringComparer.Ordinal).ToArray();

        payload.regressions = Cap(regressions, limit, ref truncated);
        payload.improvements = Cap(improvements, limit, ref truncated);
        payload.markersAdded = Cap(added, limit, ref truncated);
        payload.markersRemoved = Cap(removed, limit, ref truncated);
    }

    private static Dictionary<string, ProfileMarkerEntry> ToLookup(ProfileMarkerEntry[] markers)
    {
        var lookup = new Dictionary<string, ProfileMarkerEntry>(StringComparer.Ordinal);
        foreach (ProfileMarkerEntry marker in markers)
        {
            lookup[marker.m] = marker;
        }

        return lookup;
    }

    private static T[] Cap<T>(T[] source, int limit, ref bool truncated)
    {
        if (source.Length <= limit)
        {
            return source;
        }

        truncated = true;
        return source.Take(limit).ToArray();
    }

    private static string[] BuildNotes(
        ProfileSidecarFile baseSidecar,
        ProfileSidecarFile headSidecar,
        bool baseMedianUnusable,
        bool headMedianUnusable)
    {
        ProfileSummaryPayload baseSummary = baseSidecar.summary;
        ProfileSummaryPayload headSummary = headSidecar.summary;
        var notes = new List<string>();

        if (Math.Abs(baseSummary.budgetMs - headSummary.budgetMs) > BudgetToleranceMs)
        {
            notes.Add($"budgetMs differs ({baseSummary.budgetMs} vs {headSummary.budgetMs}). overBudgetFrames cannot be compared directly.");
        }

        if (!string.Equals(baseSummary.unityVersion, headSummary.unityVersion, StringComparison.Ordinal))
        {
            notes.Add($"unityVersion differs ({baseSummary.unityVersion} vs {headSummary.unityVersion}).");
        }

        if (baseSummary.truncated || headSummary.truncated)
        {
            notes.Add("A truncated capture is included, so marker totals may be incomplete.");
        }

        int larger = Math.Max(baseSummary.capturedFrames, headSummary.capturedFrames);
        int gap = Math.Abs(baseSummary.capturedFrames - headSummary.capturedFrames);
        if (larger > 0 && gap > larger * FrameCountMismatchRatio)
        {
            notes.Add($"Captured frame counts differ widely ({baseSummary.capturedFrames} vs {headSummary.capturedFrames}). Sample sizes differ, so the comparison is less reliable.");
        }

        if (baseMedianUnusable)
        {
            notes.Add("base capture has no usable frame-time data (median is 0 or below), so the verdict is fixed to unchanged.");
        }

        if (headMedianUnusable)
        {
            notes.Add("head capture has no usable frame-time data (median is 0 or below), so the verdict is fixed to unchanged.");
        }

        return notes.ToArray();
    }
}
