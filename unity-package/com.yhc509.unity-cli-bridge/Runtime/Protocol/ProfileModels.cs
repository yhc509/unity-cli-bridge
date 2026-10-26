#nullable enable
using System;
using System.Globalization;
using System.Text;

namespace UnityCli.Protocol
{
    public sealed class ProfileCounterName
    {
        public ProfileCounterName(string name, string unit)
        {
            Name = name;
            Unit = unit;
        }

        public string Name { get; }
        public string Unit { get; }
    }

    /// <summary>
    /// ProfilerRecorder names sampled by profile stats and profile memory.
    /// A name the running Unity version does not expose is reported as unavailable; it must not throw.
    /// </summary>
    public static class ProfileCounterCatalog
    {
        public static readonly ProfileCounterName[] RenderStats =
        {
            new ProfileCounterName("Draw Calls Count", "count"),
            new ProfileCounterName("SetPass Calls Count", "count"),
            new ProfileCounterName("Batches Count", "count"),
            new ProfileCounterName("Triangles Count", "count"),
            new ProfileCounterName("Vertices Count", "count"),
            new ProfileCounterName("Shadow Casters Count", "count"),
            new ProfileCounterName("Visible Skinned Meshes Count", "count"),
            new ProfileCounterName("Render Textures Count", "count"),
            new ProfileCounterName("Render Textures Bytes", "bytes"),
            new ProfileCounterName("Used Textures Count", "count"),
            new ProfileCounterName("Used Textures Bytes", "bytes"),
        };

        public static readonly ProfileCounterName[] MemoryStats =
        {
            new ProfileCounterName("Total Used Memory", "bytes"),
            new ProfileCounterName("System Used Memory", "bytes"),
            new ProfileCounterName("Texture Memory", "bytes"),
            new ProfileCounterName("Mesh Memory", "bytes"),
            new ProfileCounterName("App Resident Memory", "bytes"),
            new ProfileCounterName("App Committed Memory", "bytes"),
            new ProfileCounterName("AudioClip Count", "count"),
            new ProfileCounterName("AudioClip Memory", "bytes"),
            new ProfileCounterName("Physics Used Memory", "bytes"),
            new ProfileCounterName("Profiler Used Memory", "bytes"),
            new ProfileCounterName("Profiler Reserved Memory", "bytes"),
            new ProfileCounterName("System Total Used Memory", "bytes"),
        };

        public static readonly ProfileCounterName[] MemoryReport =
        {
            new ProfileCounterName("Total Used Memory", "bytes"),
            new ProfileCounterName("Total Reserved Memory", "bytes"),
            new ProfileCounterName("System Used Memory", "bytes"),
            new ProfileCounterName("GC Used Memory", "bytes"),
            new ProfileCounterName("GC Reserved Memory", "bytes"),
            new ProfileCounterName("Gfx Used Memory", "bytes"),
            new ProfileCounterName("Gfx Reserved Memory", "bytes"),
            new ProfileCounterName("Audio Used Memory", "bytes"),
            new ProfileCounterName("Video Used Memory", "bytes"),
            new ProfileCounterName("Texture Count", "count"),
            new ProfileCounterName("Texture Memory", "bytes"),
            new ProfileCounterName("Mesh Count", "count"),
            new ProfileCounterName("Mesh Memory", "bytes"),
            new ProfileCounterName("Material Count", "count"),
            new ProfileCounterName("Material Memory", "bytes"),
            new ProfileCounterName("AnimationClip Count", "count"),
            new ProfileCounterName("AnimationClip Memory", "bytes"),
            new ProfileCounterName("Asset Count", "count"),
            new ProfileCounterName("Scene Object Count", "count"),
            new ProfileCounterName("Object Count", "count"),
            new ProfileCounterName("App Resident Memory", "bytes"),
            new ProfileCounterName("App Committed Memory", "bytes"),
            new ProfileCounterName("AudioClip Count", "count"),
            new ProfileCounterName("AudioClip Memory", "bytes"),
            new ProfileCounterName("Physics Used Memory", "bytes"),
            new ProfileCounterName("Profiler Used Memory", "bytes"),
            new ProfileCounterName("Profiler Reserved Memory", "bytes"),
            new ProfileCounterName("System Total Used Memory", "bytes"),
        };
    }

    [Serializable]
    public sealed class ProfileStatsArgs
    {
        public int frames;
        public string preset = string.Empty;

        /// <summary>Marker names for per-marker timing. Empty = counter presets only.</summary>
        public string[] markers = Array.Empty<string>();
    }

    /// <summary>One `profile stats --marker` result: per-frame time and GC for a resolved profiler marker.</summary>
    [Serializable]
    public sealed class ProfileMarkerStat
    {
        public string query = string.Empty;
        public string marker = string.Empty;

        /// <summary>The full marker name when <see cref="query"/> was a short name; empty on an exact match.</summary>
        public string resolvedMarker = string.Empty;

        /// <summary>Where the time came from: "recorder" (ProfilerRecorder) or "capture" (profiler frame hierarchy).</summary>
        public string path = string.Empty;
        public string unit = "ms";
        public int frames;
        public int framesWithCalls;
        public double min;
        public double median;
        public double p95;
        public double max;
        public double callsPerFrame;

        /// <summary>GC.Alloc bytes below this marker, mean per frame. Always from the frame hierarchy.</summary>
        public double gcBytesPerFrame;
        public long gcBytesTotal;
        public string note = string.Empty;
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

        /// <summary>Per-marker results for --marker. A bridge that predates --marker omits the field entirely.</summary>
        public ProfileMarkerStat[] markerStats = Array.Empty<ProfileMarkerStat>();
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

    /// <summary>
    /// Set when <c>--frames</c> was above the profiler buffer. <c>requestedFrames</c> on the payload stays the
    /// clamped cap. <c>capturedFrames</c> is how many frames the walk kept, which can be slightly under that cap
    /// because the profiler ring buffer rotates. Omitted entirely when the request was not clamped.
    /// </summary>
    [Serializable]
    public sealed class ProfileFrameClamp
    {
        public int requested;
        public int clamped;
        public int bufferLimit;

        /// <summary>Null when <paramref name="requested"/> fits in the buffer, or no frame count was asked for.</summary>
        public static ProfileFrameClamp? TryCreate(int requested, int bufferLimit)
        {
            if (requested <= 0 || bufferLimit <= 0 || requested <= bufferLimit)
            {
                return null;
            }

            return new ProfileFrameClamp
            {
                requested = requested,
                clamped = bufferLimit,
                bufferLimit = bufferLimit,
            };
        }

        public string WarningText()
        {
            return "Requested " + requested + " frames, clamped to " + clamped
                + " (profiler buffer limit " + bufferLimit + ").";
        }
    }

    /// <summary>
    /// JsonUtility writes a null string as <c>""</c> and a null object as zeros, so an unclamped capture would
    /// otherwise always carry <c>warning</c> and <c>frameClamp</c>. Those fields are part of the response only
    /// when the request was clamped.
    /// </summary>
    public static class ProfileClampJson
    {
        public static string? NormalizeWarning(string? warning)
        {
            return string.IsNullOrEmpty(warning) ? null : warning;
        }

        public static ProfileFrameClamp? NormalizeClamp(ProfileFrameClamp? frameClamp)
        {
            if (frameClamp == null
                || (frameClamp.requested == 0 && frameClamp.clamped == 0 && frameClamp.bufferLimit == 0))
            {
                return null;
            }

            return frameClamp;
        }

        public static void Normalize(ProfileSummaryPayload? summary)
        {
            if (summary == null)
            {
                return;
            }

            summary.warning = NormalizeWarning(summary.warning);
            summary.frameClamp = NormalizeClamp(summary.frameClamp);
        }

        public static void Normalize(ProfileCaptureStartedPayload? started)
        {
            if (started == null)
            {
                return;
            }

            started.warning = NormalizeWarning(started.warning);
            started.frameClamp = NormalizeClamp(started.frameClamp);
        }

        public static void Normalize(ProfileCaptureResultPayload? result)
        {
            if (result == null)
            {
                return;
            }

            result.warning = NormalizeWarning(result.warning);
            result.frameClamp = NormalizeClamp(result.frameClamp);
            Normalize(result.summary);
        }

        /// <summary>True when <paramref name="json"/> has an object property with this name.</summary>
        public static bool HasProperty(string json, string propertyName)
        {
            if (string.IsNullOrEmpty(json)
                || json.IndexOf("\"" + propertyName + "\"", StringComparison.Ordinal) < 0)
            {
                return false;
            }

            int index = 0;
            SkipWs(json, ref index);
            return ValueHasProperty(json, ref index, propertyName);
        }

        /// <summary>
        /// Drops empty <c>warning</c> strings (and JSON null) and all-zero or null <c>frameClamp</c> objects,
        /// including inside a nested <c>summary</c>. A real clamp is left as written.
        /// </summary>
        public static string OmitUnclamped(string json)
        {
            if (string.IsNullOrEmpty(json)
                || (json.IndexOf("warning", StringComparison.Ordinal) < 0
                    && json.IndexOf("frameClamp", StringComparison.Ordinal) < 0))
            {
                return json;
            }

            try
            {
                var builder = new StringBuilder(json.Length);
                int index = 0;
                SkipWs(json, ref index);
                builder.Append(json, 0, index);
                CopyValue(json, ref index, builder);
                int tail = index;
                SkipWs(json, ref index);
                if (index != json.Length)
                {
                    return json;
                }

                builder.Append(json, tail, index - tail);
                return builder.ToString();
            }
            catch (FormatException)
            {
                return json;
            }
        }

        private static bool ValueHasProperty(string json, ref int index, string propertyName)
        {
            if (index >= json.Length)
            {
                return false;
            }

            char kind = json[index];
            if (kind == '{')
            {
                index++;
                while (true)
                {
                    SkipWs(json, ref index);
                    if (index >= json.Length)
                    {
                        throw new FormatException();
                    }

                    if (json[index] == '}')
                    {
                        index++;
                        return false;
                    }

                    if (json[index] == ',')
                    {
                        index++;
                        SkipWs(json, ref index);
                    }

                    string name = ReadString(json, ref index);
                    SkipWs(json, ref index);
                    if (index >= json.Length || json[index] != ':')
                    {
                        throw new FormatException();
                    }

                    index++;
                    SkipWs(json, ref index);
                    if (string.Equals(name, propertyName, StringComparison.Ordinal) || ValueHasProperty(json, ref index, propertyName))
                    {
                        return true;
                    }
                }
            }

            if (kind == '[')
            {
                index++;
                while (true)
                {
                    SkipWs(json, ref index);
                    if (index >= json.Length)
                    {
                        throw new FormatException();
                    }

                    if (json[index] == ']')
                    {
                        index++;
                        return false;
                    }

                    if (json[index] == ',')
                    {
                        index++;
                        SkipWs(json, ref index);
                    }

                    if (ValueHasProperty(json, ref index, propertyName))
                    {
                        return true;
                    }
                }
            }

            SkipValue(json, ref index);
            return false;
        }

        private static void CopyValue(string json, ref int index, StringBuilder builder)
        {
            if (index >= json.Length)
            {
                throw new FormatException();
            }

            char kind = json[index];
            if (kind == '{')
            {
                CopyObject(json, ref index, builder);
                return;
            }

            if (kind == '[')
            {
                CopyArray(json, ref index, builder);
                return;
            }

            int start = index;
            SkipValue(json, ref index);
            builder.Append(json, start, index - start);
        }

        private static void CopyObject(string json, ref int index, StringBuilder builder)
        {
            builder.Append('{');
            index++;
            bool emitted = false;
            while (true)
            {
                int whitespaceStart = index;
                SkipWs(json, ref index);
                if (index >= json.Length)
                {
                    throw new FormatException();
                }

                if (json[index] == '}')
                {
                    builder.Append(json, whitespaceStart, index - whitespaceStart);
                    builder.Append('}');
                    index++;
                    return;
                }

                bool comma = false;
                int separatorWhitespace = whitespaceStart;
                if (json[index] == ',')
                {
                    comma = true;
                    index++;
                    separatorWhitespace = index;
                    SkipWs(json, ref index);
                }

                int nameStart = index;
                string name = ReadString(json, ref index);
                int afterName = index;
                SkipWs(json, ref index);
                if (index >= json.Length || json[index] != ':')
                {
                    throw new FormatException();
                }

                index++;
                int afterColon = index;
                SkipWs(json, ref index);
                int valueStart = index;
                if (ShouldOmit(name, json, valueStart))
                {
                    SkipValue(json, ref index);
                    continue;
                }

                if (emitted && comma)
                {
                    builder.Append(',');
                }

                builder.Append(json, comma ? separatorWhitespace : whitespaceStart, nameStart - (comma ? separatorWhitespace : whitespaceStart));
                builder.Append(json, nameStart, afterName - nameStart);
                builder.Append(':');
                builder.Append(json, afterColon, valueStart - afterColon);
                CopyValue(json, ref index, builder);
                emitted = true;
            }
        }

        private static void CopyArray(string json, ref int index, StringBuilder builder)
        {
            builder.Append('[');
            index++;
            bool emitted = false;
            while (true)
            {
                int whitespaceStart = index;
                SkipWs(json, ref index);
                if (index >= json.Length)
                {
                    throw new FormatException();
                }

                if (json[index] == ']')
                {
                    builder.Append(json, whitespaceStart, index - whitespaceStart);
                    builder.Append(']');
                    index++;
                    return;
                }

                bool comma = false;
                int separatorWhitespace = whitespaceStart;
                if (json[index] == ',')
                {
                    comma = true;
                    index++;
                    separatorWhitespace = index;
                    SkipWs(json, ref index);
                }

                int valueStart = index;
                if (emitted && comma)
                {
                    builder.Append(',');
                }

                builder.Append(json, comma ? separatorWhitespace : whitespaceStart, valueStart - (comma ? separatorWhitespace : whitespaceStart));
                CopyValue(json, ref index, builder);
                emitted = true;
            }
        }

        private static bool ShouldOmit(string name, string json, int index)
        {
            if (string.Equals(name, "warning", StringComparison.Ordinal))
            {
                return IsEmptyStringOrNull(json, index);
            }

            if (string.Equals(name, "frameClamp", StringComparison.Ordinal))
            {
                return IsNullOrZeroClamp(json, index);
            }

            return false;
        }

        private static bool IsEmptyStringOrNull(string json, int index)
        {
            SkipWs(json, ref index);
            if (index >= json.Length)
            {
                return false;
            }

            if (Match(json, index, "null"))
            {
                return true;
            }

            return json[index] == '"' && index + 1 < json.Length && json[index + 1] == '"';
        }

        private static bool IsNullOrZeroClamp(string json, int index)
        {
            SkipWs(json, ref index);
            if (index >= json.Length)
            {
                return false;
            }

            if (Match(json, index, "null"))
            {
                return true;
            }

            if (json[index] != '{')
            {
                return false;
            }

            index++;
            while (true)
            {
                SkipWs(json, ref index);
                if (index >= json.Length)
                {
                    throw new FormatException();
                }

                if (json[index] == '}')
                {
                    return true;
                }

                if (json[index] == ',')
                {
                    index++;
                    SkipWs(json, ref index);
                }

                ReadString(json, ref index);
                SkipWs(json, ref index);
                if (index >= json.Length || json[index] != ':')
                {
                    throw new FormatException();
                }

                index++;
                SkipWs(json, ref index);
                if (!IsZeroValue(json, ref index))
                {
                    return false;
                }
            }
        }

        private static bool IsZeroValue(string json, ref int index)
        {
            if (index >= json.Length)
            {
                throw new FormatException();
            }

            char kind = json[index];
            if (kind == '"')
            {
                int start = index;
                SkipString(json, ref index);
                return index == start + 2;
            }

            if (kind == '{')
            {
                return IsNullOrZeroClamp(json, index) && AdvancePastValue(json, ref index);
            }

            if (kind == '[')
            {
                SkipValue(json, ref index);
                return false;
            }

            if (kind == 'n' && Match(json, index, "null"))
            {
                index += 4;
                return true;
            }

            if (kind == 't' || kind == 'f')
            {
                SkipLiteral(json, ref index);
                return false;
            }

            int numberStart = index;
            SkipNumber(json, ref index);
            return IsZeroNumber(json.Substring(numberStart, index - numberStart));
        }

        private static bool AdvancePastValue(string json, ref int index)
        {
            SkipValue(json, ref index);
            return true;
        }

        private static bool IsZeroNumber(string literal)
        {
            return double.TryParse(literal, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
                && value == 0d;
        }

        private static void SkipValue(string json, ref int index)
        {
            if (index >= json.Length)
            {
                throw new FormatException();
            }

            char kind = json[index];
            if (kind == '{')
            {
                index++;
                while (true)
                {
                    SkipWs(json, ref index);
                    if (index >= json.Length)
                    {
                        throw new FormatException();
                    }

                    if (json[index] == '}')
                    {
                        index++;
                        return;
                    }

                    if (json[index] == ',')
                    {
                        index++;
                        SkipWs(json, ref index);
                    }

                    SkipString(json, ref index);
                    SkipWs(json, ref index);
                    if (index >= json.Length || json[index] != ':')
                    {
                        throw new FormatException();
                    }

                    index++;
                    SkipWs(json, ref index);
                    SkipValue(json, ref index);
                }
            }

            if (kind == '[')
            {
                index++;
                while (true)
                {
                    SkipWs(json, ref index);
                    if (index >= json.Length)
                    {
                        throw new FormatException();
                    }

                    if (json[index] == ']')
                    {
                        index++;
                        return;
                    }

                    if (json[index] == ',')
                    {
                        index++;
                        SkipWs(json, ref index);
                    }

                    SkipValue(json, ref index);
                }
            }

            if (kind == '"')
            {
                SkipString(json, ref index);
                return;
            }

            if (kind == 't' || kind == 'f' || kind == 'n')
            {
                SkipLiteral(json, ref index);
                return;
            }

            SkipNumber(json, ref index);
        }

        private static string ReadString(string json, ref int index)
        {
            int start = index;
            SkipString(json, ref index);
            return Unescape(json, start + 1, index - 1);
        }

        private static string Unescape(string json, int start, int end)
        {
            if (json.IndexOf('\\', start, end - start) < 0)
            {
                return json.Substring(start, end - start);
            }

            var builder = new StringBuilder(end - start);
            for (int i = start; i < end; i++)
            {
                char character = json[i];
                if (character != '\\' || i + 1 >= end)
                {
                    builder.Append(character);
                    continue;
                }

                char escaped = json[++i];
                switch (escaped)
                {
                    case '"':
                    case '\\':
                    case '/':
                        builder.Append(escaped);
                        break;
                    case 'b':
                        builder.Append('\b');
                        break;
                    case 'f':
                        builder.Append('\f');
                        break;
                    case 'n':
                        builder.Append('\n');
                        break;
                    case 'r':
                        builder.Append('\r');
                        break;
                    case 't':
                        builder.Append('\t');
                        break;
                    case 'u':
                        if (i + 4 >= end)
                        {
                            throw new FormatException();
                        }

                        builder.Append((char)int.Parse(json.Substring(i + 1, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                        i += 4;
                        break;
                    default:
                        builder.Append(escaped);
                        break;
                }
            }

            return builder.ToString();
        }

        private static void SkipString(string json, ref int index)
        {
            if (index >= json.Length || json[index] != '"')
            {
                throw new FormatException();
            }

            index++;
            while (index < json.Length)
            {
                char character = json[index++];
                if (character == '\\')
                {
                    if (index >= json.Length)
                    {
                        throw new FormatException();
                    }

                    char escaped = json[index++];
                    if (escaped == 'u')
                    {
                        if (index + 4 > json.Length)
                        {
                            throw new FormatException();
                        }

                        index += 4;
                    }
                }
                else if (character == '"')
                {
                    return;
                }
            }

            throw new FormatException();
        }

        private static void SkipNumber(string json, ref int index)
        {
            int start = index;
            if (index < json.Length && json[index] == '-')
            {
                index++;
            }

            while (index < json.Length && char.IsDigit(json[index]))
            {
                index++;
            }

            if (index < json.Length && json[index] == '.')
            {
                index++;
                while (index < json.Length && char.IsDigit(json[index]))
                {
                    index++;
                }
            }

            if (index < json.Length && (json[index] == 'e' || json[index] == 'E'))
            {
                index++;
                if (index < json.Length && (json[index] == '+' || json[index] == '-'))
                {
                    index++;
                }

                while (index < json.Length && char.IsDigit(json[index]))
                {
                    index++;
                }
            }

            if (index == start || (json[start] == '-' && index == start + 1))
            {
                throw new FormatException();
            }
        }

        private static void SkipLiteral(string json, ref int index)
        {
            if (Match(json, index, "true"))
            {
                index += 4;
                return;
            }

            if (Match(json, index, "false"))
            {
                index += 5;
                return;
            }

            if (Match(json, index, "null"))
            {
                index += 4;
                return;
            }

            throw new FormatException();
        }

        private static void SkipWs(string json, ref int index)
        {
            while (index < json.Length && char.IsWhiteSpace(json[index]))
            {
                index++;
            }
        }

        private static bool Match(string json, int index, string literal)
        {
            return index + literal.Length <= json.Length
                && string.CompareOrdinal(json, index, literal, 0, literal.Length) == 0;
        }
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

        /// <summary>Set only when --frames was clamped. States requested, clamped, and the limit. Omitted otherwise.</summary>
        public string? warning;

        public ProfileFrameClamp? frameClamp;
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

    /// <summary>GC.Alloc bytes attributed to the nearest script marker above the allocation.</summary>
    [Serializable]
    public sealed class ProfileGcScriptEntry
    {
        public string marker = string.Empty;
        public long bytesTotal;
        public double bytesPerFrame;
        public long allocations;
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
        public ProfileGcScriptEntry[] gcByScript = Array.Empty<ProfileGcScriptEntry>();

        /// <summary>GC.Alloc bytes with no script marker above them (engine or Editor allocations).</summary>
        public long gcUnattributedBytes;
        public string sidecarPath = string.Empty;

        /// <summary>Set only when --frames was clamped. Same text as the start response. Omitted otherwise. capturedFrames is the actual count and can be under frameClamp.clamped.</summary>
        public string? warning;

        public ProfileFrameClamp? frameClamp;
    }

    /// <summary>
    /// A finished capture. <c>profile capture start --wait</c>, <c>profile capture stop --wait</c>, and
    /// <c>profile capture stop</c> after the capture already finished on its own all return this shape.
    /// <see cref="notice"/> is PROFILE_ALREADY_COMPLETED only for that last case.
    /// <see cref="warning"/> is set only when --frames was clamped to the profiler buffer.
    /// <see cref="capturedFrames"/> is the number of frames actually kept and can be slightly under the clamp.
    /// </summary>
    [Serializable]
    public sealed class ProfileCaptureResultPayload
    {
        public string captureId = string.Empty;
        public string status = string.Empty;
        public int capturedFrames;
        public int requestedFrames;
        public string? notice;
        public string? message;
        public string? warning;
        public ProfileFrameClamp? frameClamp;
        public ProfileSummaryPayload summary = new ProfileSummaryPayload();

        public static ProfileCaptureResultPayload FromSummary(ProfileSummaryPayload summary, string? notice, string? message)
        {
            ProfileClampJson.Normalize(summary);
            return new ProfileCaptureResultPayload
            {
                captureId = summary.captureId,
                status = summary.status,
                capturedFrames = summary.capturedFrames,
                requestedFrames = summary.requestedFrames,
                notice = notice,
                message = message,
                warning = summary.warning,
                frameClamp = summary.frameClamp,
                summary = summary,
            };
        }
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

        /// <summary>Schema 2+. Every script that allocated, largest first. Absent (empty) in schema 1 sidecars.</summary>
        public ProfileGcScriptEntry[] gcByScript = Array.Empty<ProfileGcScriptEntry>();
        public long gcUnattributedBytes;
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
        /// <summary>
        /// Set when <c>--marker</c> did not match a name exactly and resolved to one normalized candidate.
        /// Null on an exact match so the field stays off the response.
        /// </summary>
        public string? resolvedMarker;
        public ProfileFrameAppearance[] appearances = Array.Empty<ProfileFrameAppearance>();
        public bool appearancesFromTopOnly = true;
    }

    /// <summary>Default <c>profile analyze</c> payload: the capture summary, with hotspots under <c>summary</c>.</summary>
    [Serializable]
    public sealed class ProfileAnalyzeSummaryPayload
    {
        public string captureId = string.Empty;
        public ProfileSummaryPayload summary = new ProfileSummaryPayload();
        public string[]? notes;
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

        /// <summary>Markers by inclusive GC bytes, with the player-loop containers and GC.Alloc left out.</summary>
        public ProfileGcEntry[] entries = Array.Empty<ProfileGcEntry>();
        public bool truncated;
        public ProfileGcScriptEntry[] gcByScript = Array.Empty<ProfileGcScriptEntry>();
        public bool gcByScriptTruncated;
        public long gcUnattributedBytes;
        public string[]? notes;
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

    [Serializable]
    public sealed class ProfileMemoryTopArgs
    {
        public string type = string.Empty;
        public int limit;
        public string scope = string.Empty;
    }

    /// <summary>One measured object passed into <see cref="ProfileMemoryTopRanking"/>. Not a wire type.</summary>
    public sealed class ProfileMemoryTopMeasurement
    {
        public string type = string.Empty;
        public string name = string.Empty;
        public string? path;
        public long bytes;
        public int? width;
        public int? height;
        public string? format;
        public int? mipCount;
        public int? vertexCount;
        public int? subMeshCount;
    }

    [Serializable]
    public sealed class ProfileMemoryTopPayload
    {
        public string mode = string.Empty;
        public string unityVersion = string.Empty;
        public string scope = string.Empty;
        public string? type;
        public string note = string.Empty;
        public ProfileMemoryTopTotal[] totals = Array.Empty<ProfileMemoryTopTotal>();
        public ProfileMemoryTopEntry[] top = Array.Empty<ProfileMemoryTopEntry>();
        public bool truncated;
        public string[]? warnings;
    }

    [Serializable]
    public sealed class ProfileMemoryTopTotal
    {
        public string type = string.Empty;
        public int count;
        public long bytes;
    }

    [Serializable]
    public sealed class ProfileMemoryTopEntry
    {
        public string name = string.Empty;
        public string type = string.Empty;
        public string? path;
        public long bytes;
        public ProfileMemoryTopDetail? detail;
    }

    [Serializable]
    public sealed class ProfileMemoryTopDetail
    {
        public int? width;
        public int? height;
        public string? format;
        public int? mipCount;
        public int? vertexCount;
        public int? subMeshCount;
    }

    public static class ProfileMemoryTopCatalog
    {
        public const string EditorSizeNote =
            "Editor-measured sizes include editor-only copies and differ from player builds. Compare results taken in the same mode.";

        public static readonly string[] AcceptedTypeNames =
        {
            "Texture",
            "Texture2D",
            "RenderTexture",
            "Cubemap",
            "Mesh",
            "AudioClip",
            "Material",
            "AnimationClip",
            "Shader",
            "Sprite",
        };

        public static readonly string[] DefaultTypeNames =
        {
            "Texture2D",
            "RenderTexture",
            "Cubemap",
            "Mesh",
            "AudioClip",
            "Material",
            "AnimationClip",
            "Shader",
        };

        public static string AcceptedTypeList()
        {
            return string.Join(", ", AcceptedTypeNames);
        }

        public static bool TryNormalizeType(string? type, out string normalized)
        {
            normalized = string.Empty;
            if (string.IsNullOrWhiteSpace(type))
            {
                return false;
            }

            string trimmed = type.Trim();
            for (int index = 0; index < AcceptedTypeNames.Length; index++)
            {
                if (string.Equals(AcceptedTypeNames[index], trimmed, StringComparison.OrdinalIgnoreCase))
                {
                    normalized = AcceptedTypeNames[index];
                    return true;
                }
            }

            return false;
        }

        public static bool TryNormalizeScope(string? scope, out string normalized)
        {
            if (string.IsNullOrWhiteSpace(scope))
            {
                normalized = "loaded";
                return true;
            }

            string trimmed = scope.Trim();
            if (trimmed.Equals("loaded", StringComparison.OrdinalIgnoreCase)
                || trimmed.Equals("scene", StringComparison.OrdinalIgnoreCase)
                || trimmed.Equals("assets", StringComparison.OrdinalIgnoreCase))
            {
                normalized = trimmed.ToLowerInvariant();
                return true;
            }

            normalized = string.Empty;
            return false;
        }
    }

    public static class ProfileMemoryTopFilter
    {
        public static bool IsInternalAssetPath(string? path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return false;
            }

            return path.IndexOf("unity editor resources", StringComparison.OrdinalIgnoreCase) >= 0
                || path.IndexOf("unity default resources", StringComparison.OrdinalIgnoreCase) >= 0
                || path.IndexOf("unity_builtin_extra", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public static bool IsProjectAssetPath(string? path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return false;
            }

            return path.StartsWith("Assets/", StringComparison.Ordinal)
                || path.StartsWith("Packages/", StringComparison.Ordinal)
                || string.Equals(path, "Assets", StringComparison.Ordinal)
                || string.Equals(path, "Packages", StringComparison.Ordinal);
        }

        public static bool Include(string scope, bool hideAndDontSave, string? path, bool persistent)
        {
            if (hideAndDontSave || IsInternalAssetPath(path))
            {
                return false;
            }

            if (string.Equals(scope, "scene", StringComparison.Ordinal))
            {
                return !persistent;
            }

            if (string.Equals(scope, "assets", StringComparison.Ordinal))
            {
                return persistent && IsProjectAssetPath(path);
            }

            return true;
        }
    }

    public static class ProfileMemoryTopRanking
    {
        public static ProfileMemoryTopPayload Build(
            System.Collections.Generic.IReadOnlyList<ProfileMemoryTopMeasurement>? measurements,
            int limit,
            string mode,
            string unityVersion,
            string scope,
            string? type,
            bool scanTruncated,
            System.Collections.Generic.IReadOnlyList<string>? warnings)
        {
            if (limit < 1)
            {
                limit = ProtocolConstants.DefaultProfileMemoryTopLimit;
            }
            else if (limit > ProtocolConstants.MaxProfileMemoryTopLimit)
            {
                limit = ProtocolConstants.MaxProfileMemoryTopLimit;
            }

            int count = measurements?.Count ?? 0;
            var totals = new System.Collections.Generic.List<ProfileMemoryTopTotal>();
            for (int index = 0; index < count; index++)
            {
                ProfileMemoryTopMeasurement measurement = measurements![index];
                ProfileMemoryTopTotal? total = null;
                for (int totalIndex = 0; totalIndex < totals.Count; totalIndex++)
                {
                    if (string.Equals(totals[totalIndex].type, measurement.type, StringComparison.Ordinal))
                    {
                        total = totals[totalIndex];
                        break;
                    }
                }

                if (total == null)
                {
                    total = new ProfileMemoryTopTotal { type = measurement.type ?? string.Empty };
                    totals.Add(total);
                }

                total.count++;
                total.bytes += measurement.bytes;
            }

            totals.Sort(CompareTotals);

            var ordered = new ProfileMemoryTopMeasurement[count];
            for (int index = 0; index < count; index++)
            {
                ordered[index] = measurements![index];
            }

            Array.Sort(ordered, CompareMeasurements);
            int topCount = Math.Min(limit, count);
            var top = new ProfileMemoryTopEntry[topCount];
            for (int index = 0; index < topCount; index++)
            {
                top[index] = ToEntry(ordered[index]);
            }

            string[]? warningArray = null;
            if (warnings != null && warnings.Count > 0)
            {
                warningArray = new string[warnings.Count];
                for (int index = 0; index < warnings.Count; index++)
                {
                    warningArray[index] = warnings[index];
                }
            }

            return new ProfileMemoryTopPayload
            {
                mode = mode ?? string.Empty,
                unityVersion = unityVersion ?? string.Empty,
                scope = string.IsNullOrEmpty(scope) ? "loaded" : scope,
                type = string.IsNullOrEmpty(type) ? null : type,
                note = ProfileMemoryTopCatalog.EditorSizeNote,
                totals = totals.ToArray(),
                top = top,
                truncated = scanTruncated || count > limit,
                warnings = warningArray,
            };
        }

        private static ProfileMemoryTopEntry ToEntry(ProfileMemoryTopMeasurement measurement)
        {
            ProfileMemoryTopDetail? detail = null;
            if (measurement.width.HasValue
                || measurement.height.HasValue
                || !string.IsNullOrEmpty(measurement.format)
                || measurement.mipCount.HasValue
                || measurement.vertexCount.HasValue
                || measurement.subMeshCount.HasValue)
            {
                detail = new ProfileMemoryTopDetail
                {
                    width = measurement.width,
                    height = measurement.height,
                    format = measurement.format,
                    mipCount = measurement.mipCount,
                    vertexCount = measurement.vertexCount,
                    subMeshCount = measurement.subMeshCount,
                };
            }

            return new ProfileMemoryTopEntry
            {
                name = measurement.name ?? string.Empty,
                type = measurement.type ?? string.Empty,
                path = string.IsNullOrEmpty(measurement.path) ? null : measurement.path,
                bytes = measurement.bytes,
                detail = detail,
            };
        }

        private static int CompareTotals(ProfileMemoryTopTotal left, ProfileMemoryTopTotal right)
        {
            int byBytes = right.bytes.CompareTo(left.bytes);
            if (byBytes != 0)
            {
                return byBytes;
            }

            return string.CompareOrdinal(left.type, right.type);
        }

        private static int CompareMeasurements(ProfileMemoryTopMeasurement left, ProfileMemoryTopMeasurement right)
        {
            int byBytes = right.bytes.CompareTo(left.bytes);
            if (byBytes != 0)
            {
                return byBytes;
            }

            int byName = string.CompareOrdinal(left.name, right.name);
            if (byName != 0)
            {
                return byName;
            }

            return string.CompareOrdinal(left.type, right.type);
        }
    }
}
