#nullable enable
using System;
using System.Linq;
using System.Text;
using System.Text.Json;
using UnityCli.Cli.Models;
using UnityCli.Protocol;

namespace UnityCli.Cli.Services;

/// <summary>Local drill-down over a finished capture's sidecar. No IPC.</summary>
internal static class ProfileAnalyzer
{
    private const int SimilarNameLimit = 5;

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
            ProfileGcEntry[] all = sidecar.markers
                .Where(m => m.gcBytes > 0)
                .OrderByDescending(m => m.gcBytes)
                .Select(m => new ProfileGcEntry { marker = m.m, bytesTotal = m.gcBytes, framesWithAlloc = m.frames })
                .ToArray();
            payload = new ProfileAnalyzeGcPayload
            {
                captureId = sidecar.captureId,
                entries = all.Take(limit).ToArray(),
                truncated = all.Length > limit,
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
            payload = new ProfileAnalyzeSummaryPayload
            {
                captureId = sidecar.captureId,
                summary = sidecar.summary,
            };
        }

        return ResponseEnvelope.Success(
            "local",
            null,
            JsonSerializer.SerializeToElement(payload, ProtocolJson.Default),
            0,
            "cli");
    }

    /// <summary>
    /// Marker name with the Unity profiler decoration removed: an <c>Assembly.dll!Namespace::</c> prefix,
    /// a trailing <c>() [Invoke]</c>, and empty parentheses. Used for short-name matching only.
    /// </summary>
    internal static string NormalizeMarkerName(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return string.Empty;
        }

        string value = name.Trim();
        int bang = value.IndexOf('!');
        if (bang >= 0)
        {
            value = value[(bang + 1)..];
        }

        int scope = value.LastIndexOf("::", StringComparison.Ordinal);
        if (scope >= 0)
        {
            value = value[(scope + 2)..];
        }

        const string invokeSuffix = " [Invoke]";
        if (value.EndsWith(invokeSuffix, StringComparison.Ordinal))
        {
            value = value[..^invokeSuffix.Length];
        }

        value = value.Replace("()", string.Empty, StringComparison.Ordinal);
        return value.Trim();
    }

    private static object BuildMarkerPayload(
        ProfileSidecarFile sidecar,
        string markerName,
        int limit,
        out ResponseEnvelope? failure)
    {
        failure = null;
        ProfileMarkerEntry? exact = sidecar.markers.FirstOrDefault(
            m => string.Equals(m.m, markerName, StringComparison.Ordinal));
        ProfileMarkerEntry? marker = exact;
        bool resolvedFromShortName = false;
        if (marker is null)
        {
            marker = ResolveShortName(sidecar, markerName, out failure);
            if (failure != null || marker is null)
            {
                return new object();
            }

            resolvedFromShortName = true;
        }

        string resolvedName = marker.m;
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
            resolvedMarker = resolvedFromShortName ? resolvedName : null,
            appearances = appearances,
            appearancesFromTopOnly = true,
        };
    }

    private static ProfileMarkerEntry? ResolveShortName(
        ProfileSidecarFile sidecar,
        string markerName,
        out ResponseEnvelope? failure)
    {
        failure = null;
        string query = NormalizeMarkerName(markerName);
        ProfileMarkerEntry[] matches = query.Length == 0
            ? Array.Empty<ProfileMarkerEntry>()
            : sidecar.markers
                .Where(m => MarkerMatchesQuery(NormalizeMarkerName(m.m), query))
                .GroupBy(m => m.m, StringComparer.Ordinal)
                .Select(group => group.First())
                .OrderBy(m => m.m, StringComparer.Ordinal)
                .ToArray();

        if (matches.Length == 1)
        {
            return matches[0];
        }

        if (matches.Length > 1)
        {
            var message = new StringBuilder();
            message.Append("Marker `").Append(markerName).Append("` matches more than one capture marker. Pass one of these names:");
            foreach (ProfileMarkerEntry candidate in matches)
            {
                message.Append('\n').Append(candidate.m);
            }

            failure = ResponseEnvelope.Failure(
                "local",
                null,
                ProtocolConstants.ErrorProfileMarkerAmbiguous,
                message.ToString(),
                false,
                0,
                "cli");
            return null;
        }

        string[] similar = SimilarMarkerNames(sidecar.markers, query);
        var notFound = new StringBuilder();
        notFound.Append("Marker `").Append(markerName).Append("` is not in the capture.");
        if (similar.Length == 0)
        {
            notFound.Append(" The capture has no markers.");
        }
        else
        {
            notFound.Append(" Similar names:");
            foreach (string name in similar)
            {
                notFound.Append('\n').Append(name);
            }
        }

        failure = ResponseEnvelope.Failure(
            "local",
            null,
            ProtocolConstants.ErrorProfileNotFound,
            notFound.ToString(),
            false,
            0,
            "cli");
        return null;
    }

    /// <summary>Exact normalized name, or the marker's <c>Type.Method</c> ends with the query.</summary>
    private static bool MarkerMatchesQuery(string markerNorm, string queryNorm)
    {
        if (markerNorm.Length == 0 || queryNorm.Length == 0)
        {
            return false;
        }

        if (string.Equals(markerNorm, queryNorm, StringComparison.Ordinal))
        {
            return true;
        }

        return markerNorm.EndsWith("." + queryNorm, StringComparison.Ordinal);
    }

    private static string[] SimilarMarkerNames(ProfileMarkerEntry[] markers, string queryNorm)
    {
        return markers
            .Select(m => m.m)
            .Where(name => !string.IsNullOrEmpty(name))
            .Distinct(StringComparer.Ordinal)
            .Select(name => (name, score: Similarity(queryNorm, NormalizeMarkerName(name))))
            .OrderByDescending(item => item.score)
            .ThenBy(item => item.name, StringComparer.Ordinal)
            .Take(SimilarNameLimit)
            .Select(item => item.name)
            .ToArray();
    }

    private static int Similarity(string query, string candidate)
    {
        if (query.Length == 0 || candidate.Length == 0)
        {
            return int.MinValue / 4;
        }

        int prefix = CommonRun(query, candidate, fromEnd: false);
        int suffix = CommonRun(query, candidate, fromEnd: true);
        int distance = Levenshtein(query, candidate);
        return (prefix * 4) + (suffix * 2) - distance;
    }

    private static int CommonRun(string left, string right, bool fromEnd)
    {
        int limit = Math.Min(left.Length, right.Length);
        int count = 0;
        while (count < limit)
        {
            char a = fromEnd ? left[left.Length - 1 - count] : left[count];
            char b = fromEnd ? right[right.Length - 1 - count] : right[count];
            if (a != b)
            {
                break;
            }

            count++;
        }

        return count;
    }

    private static int Levenshtein(string left, string right)
    {
        if (left.Length == 0)
        {
            return right.Length;
        }

        if (right.Length == 0)
        {
            return left.Length;
        }

        var previous = new int[right.Length + 1];
        var current = new int[right.Length + 1];
        for (int column = 0; column <= right.Length; column++)
        {
            previous[column] = column;
        }

        for (int row = 1; row <= left.Length; row++)
        {
            current[0] = row;
            for (int column = 1; column <= right.Length; column++)
            {
                int cost = left[row - 1] == right[column - 1] ? 0 : 1;
                current[column] = Math.Min(
                    Math.Min(current[column - 1] + 1, previous[column] + 1),
                    previous[column - 1] + cost);
            }

            (previous, current) = (current, previous);
        }

        return previous[right.Length];
    }
}
