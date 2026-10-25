#nullable enable
using System;
using System.Linq;
using System.Text.Json;
using UnityCli.Cli.Models;
using UnityCli.Protocol;

namespace UnityCli.Cli.Services;

/// <summary>Local drill-down over a finished capture's sidecar. No IPC.</summary>
internal static class ProfileAnalyzer
{
    internal static ResponseEnvelope Run(ParsedCommand parsed, string? projectRoot)
    {
        if (!ProfileSidecarLoader.TryLoad(
                projectRoot, parsed.ProfileCaptureId, out ProfileSidecarFile sidecar, out ResponseEnvelope? loadFailure))
        {
            return loadFailure!;
        }

        int limit = parsed.ProfileLimit ?? ProtocolConstants.DefaultProfileListLimit;
        object payload;
        if (!string.IsNullOrEmpty(parsed.ProfileAnalyzeMarker))
        {
            payload = BuildMarkerPayload(sidecar, parsed.ProfileAnalyzeMarker!, limit, out ResponseEnvelope? failure);
            if (failure != null)
            {
                return failure;
            }
        }
        else if (parsed.ProfileAnalyzeFrame.HasValue)
        {
            ProfileFrameEntry? frame = sidecar.frames.FirstOrDefault(f => f.i == parsed.ProfileAnalyzeFrame.Value);
            if (frame is null)
            {
                return ResponseEnvelope.Failure(
                    "local",
                    null,
                    ProtocolConstants.ErrorProfileNotFound,
                    $"Frame {parsed.ProfileAnalyzeFrame.Value} is outside the capture range (0..{(sidecar.frames.Length == 0 ? 0 : sidecar.frames[^1].i)}).",
                    false,
                    0,
                    "cli");
            }

            payload = new ProfileAnalyzeFramePayload
            {
                captureId = sidecar.captureId,
                budgetMs = sidecar.summary.budgetMs,
                overBudget = frame.ms > sidecar.summary.budgetMs,
                frame = frame,
            };
        }
        else if (parsed.ProfileAnalyzeGc)
        {
            string? note = LegacyGcByScriptNote(sidecar);
            long unattributed = sidecar.gcUnattributedBytes;
            ProfileGcScriptEntry[] scripts = ProfileGcAttribution.OmitEngineMarkers(sidecar.gcByScript, ref unattributed);
            payload = new ProfileAnalyzeGcPayload
            {
                captureId = sidecar.captureId,
                entries = ProfileGcAttribution.BuildGcTop(sidecar.markers, limit, out bool truncated),
                truncated = truncated,
                gcByScript = ProfileGcAttribution.Take(scripts, limit, out bool scriptsTruncated),
                gcByScriptTruncated = scriptsTruncated,
                gcUnattributedBytes = unattributed,
                notes = note is null ? null : new[] { note },
            };
        }
        else if (parsed.ProfileAnalyzeSpikes)
        {
            int[] spikeFrames = sidecar.summary.spikes.Select(s => s.frame).ToArray();
            payload = new ProfileAnalyzeSpikesPayload
            {
                captureId = sidecar.captureId,
                budgetMs = sidecar.summary.budgetMs,
                frames = sidecar.frames.Where(f => spikeFrames.Contains(f.i)).Take(limit).ToArray(),
            };
        }
        else
        {
            // Older sidecars stored a gcTop that still ranked the player-loop containers; rebuild it.
            // Engine phase markers stored in gcByScript are moved to gcUnattributedBytes.
            ProfileSummaryPayload summary = sidecar.summary;
            summary.gcTop = ProfileGcAttribution.BuildGcTop(
                sidecar.markers, ProtocolConstants.DefaultProfileListLimit, out _);
            long summaryUnattributed = summary.gcUnattributedBytes;
            summary.gcByScript = ProfileGcAttribution.OmitEngineMarkers(summary.gcByScript, ref summaryUnattributed);
            summary.gcUnattributedBytes = summaryUnattributed;
            string? note = LegacyGcByScriptNote(sidecar);
            payload = new ProfileAnalyzeSummaryPayload
            {
                captureId = sidecar.captureId,
                summary = summary,
                notes = note is null ? null : new[] { note },
            };
        }

        return ResponseEnvelope.Success(
            "local",
            null,
            JsonSerializer.SerializeToElement(payload, ProtocolJson.Default),
            0,
            "cli");
    }

    private static object BuildMarkerPayload(
        ProfileSidecarFile sidecar,
        string markerName,
        int limit,
        out ResponseEnvelope? failure)
    {
        failure = null;
        ProfileMarkerResolution resolution = ProfileMarkerNameResolver.Resolve(
            sidecar.markers.Select(m => m.m),
            markerName);
        if (!resolution.IsMatch)
        {
            failure = ResponseEnvelope.Failure(
                "local",
                null,
                resolution.ErrorCode,
                resolution.BuildMessage("capture marker", "the capture"),
                false,
                0,
                "cli");
            return new object();
        }

        string resolvedName = resolution.Marker!;
        ProfileMarkerEntry marker = sidecar.markers.First(m => string.Equals(m.m, resolvedName, StringComparison.Ordinal));
        ProfileFrameAppearance[] appearances = sidecar.frames
            .SelectMany(f => f.top
                .Where(t => string.Equals(t.m, resolvedName, StringComparison.Ordinal))
                .Select(t => new ProfileFrameAppearance { frame = f.i, selfMs = t.self, gcBytes = t.gc, calls = t.calls }))
            .OrderByDescending(a => a.selfMs)
            .Take(limit)
            .ToArray();

        return new ProfileAnalyzeMarkerPayload
        {
            captureId = sidecar.captureId,
            marker = marker,
            resolvedMarker = resolution.Kind == ProfileMarkerMatchKind.Resolved ? resolvedName : null,
            appearances = appearances,
            appearancesFromTopOnly = true,
        };
    }

    /// <summary>Note for sidecars written before GC attribution existed; null when the sidecar has it.</summary>
    internal static string? LegacyGcByScriptNote(ProfileSidecarFile sidecar)
    {
        if (sidecar.schemaVersion >= ProtocolConstants.ProfileSidecarSchemaVersion)
        {
            return null;
        }

        return $"gcByScript is empty: this capture was recorded before GC attribution (sidecar schemaVersion {sidecar.schemaVersion}, needs {ProtocolConstants.ProfileSidecarSchemaVersion}). Capture again to see allocations per script.";
    }
}
