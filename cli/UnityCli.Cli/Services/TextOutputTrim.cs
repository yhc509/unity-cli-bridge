#nullable enable
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using UnityCli.Cli.Models;
using UnityCli.Protocol;

namespace UnityCli.Cli.Services;

/// <summary>
/// Display-only trims for the default and compact text modes. <c>--json</c> keeps the wire payload.
/// </summary>
internal static class TextOutputTrim
{
    internal static JsonElement PresentData(JsonElement data, OutputMode mode)
    {
        if (data.ValueKind != JsonValueKind.Object)
        {
            return data;
        }

        JsonNode? node = JsonNode.Parse(data.GetRawText());
        if (node is not JsonObject obj)
        {
            return data;
        }

        bool changed = false;
        if (obj["output"] is JsonValue outputValue
            && outputValue.TryGetValue<string>(out string? output)
            && output != null
            && obj.ContainsKey("cacheHit"))
        {
            string collapsed = CollapseRepeatedLines(output);
            if (!string.Equals(collapsed, output, StringComparison.Ordinal))
            {
                obj["output"] = collapsed;
                changed = true;
            }
        }

        if (mode == OutputMode.Compact && IsEditorStatus(obj))
        {
            changed |= obj.Remove("pipeName");
            changed |= obj.Remove("domainId");
        }

        if (!changed)
        {
            return data;
        }

        return JsonSerializer.SerializeToElement(obj, ProtocolJson.Default);
    }

    internal static string TrimCompileErrorMessage(string message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return message;
        }

        const string Prefix = "Code compilation failed (";
        int start = message.IndexOf(Prefix, StringComparison.Ordinal);
        string rebuilt = message;
        if (start >= 0)
        {
            int optionsStart = start + Prefix.Length;
            int close = message.IndexOf("):", optionsStart, StringComparison.Ordinal);
            if (close >= 0)
            {
                string options = message.Substring(optionsStart, close - optionsStart);
                string kept = StripCompilerOptionTokens(options);
                string tail = message[(close + 2)..];
                string head = message[..start];
                rebuilt = string.IsNullOrWhiteSpace(kept)
                    ? head + "Code compilation failed:" + tail
                    : head + "Code compilation failed (" + kept.Trim() + "):" + tail;
            }
        }

        return StripReferenceListLines(rebuilt);
    }

    /// <summary>Consecutive identical lines become one line with <c>(×N)</c>. Order is preserved.</summary>
    internal static string CollapseRepeatedLines(string text)
    {
        if (string.IsNullOrEmpty(text) || text.IndexOf('\n') < 0)
        {
            return text;
        }

        string[] lines = text.Split('\n');
        bool trailingNewline = text.EndsWith("\n", StringComparison.Ordinal);
        int count = lines.Length;
        if (trailingNewline && count > 0 && lines[^1].Length == 0)
        {
            count--;
        }

        var builder = new StringBuilder(text.Length);
        int index = 0;
        bool first = true;
        while (index < count)
        {
            string line = lines[index].TrimEnd('\r');
            int run = 1;
            while (index + run < count && string.Equals(lines[index + run].TrimEnd('\r'), line, StringComparison.Ordinal))
            {
                run++;
            }

            if (!first)
            {
                builder.Append('\n');
            }

            first = false;
            builder.Append(line);
            if (run > 1)
            {
                builder.Append(" (×").Append(run).Append(')');
            }

            index += run;
        }

        if (trailingNewline && count > 0)
        {
            builder.Append('\n');
        }

        return builder.ToString();
    }

    private static bool IsEditorStatus(JsonObject obj)
    {
        // `status` carries isCompiling plus the internal handshake fields. A play payload
        // also has isPlaying and domainId, and is left alone.
        return obj.ContainsKey("isCompiling")
            && (obj.ContainsKey("pipeName") || obj.ContainsKey("domainId"));
    }

    internal static string StripCompilerOptionTokens(string options)
    {
        var kept = new List<string>();
        int index = 0;
        while (index < options.Length)
        {
            while (index < options.Length && char.IsWhiteSpace(options[index]))
            {
                index++;
            }

            if (index >= options.Length)
            {
                break;
            }

            string token = ReadOptionToken(options, ref index);
            if (!IsDroppedCompilerToken(token))
            {
                kept.Add(token);
            }
        }

        return string.Join(" ", kept);
    }

    private static string ReadOptionToken(string text, ref int index)
    {
        int start = index;
        while (index < text.Length && !char.IsWhiteSpace(text[index]))
        {
            if (text[index] == '"')
            {
                index++;
                while (index < text.Length && text[index] != '"')
                {
                    index++;
                }

                if (index < text.Length)
                {
                    index++;
                }

                continue;
            }

            index++;
        }

        return text[start..index];
    }

    private static bool IsDroppedCompilerToken(string token)
    {
        string flag = CompilerFlag(token);
        return flag.Equals("define", StringComparison.OrdinalIgnoreCase)
            || flag.Equals("reference", StringComparison.OrdinalIgnoreCase)
            || flag.Equals("r", StringComparison.OrdinalIgnoreCase);
    }

    private static string CompilerFlag(string token)
    {
        string value = token.Trim();
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
        {
            value = value[1..^1];
        }

        int colon = value.IndexOf(':');
        string head = colon >= 0 ? value[..colon] : value;
        int slash = 0;
        while (slash < head.Length && (head[slash] == '/' || head[slash] == '-'))
        {
            slash++;
        }

        return head[slash..];
    }

    private static string StripReferenceListLines(string message)
    {
        string[] lines = message.Split('\n');
        var kept = new List<string>(lines.Length);
        foreach (string raw in lines)
        {
            string line = raw.TrimEnd('\r');
            if (IsReferenceListLine(line.Trim()))
            {
                continue;
            }

            kept.Add(line);
        }

        return string.Join("\n", kept);
    }

    private static bool IsReferenceListLine(string line)
    {
        if (line.Length == 0)
        {
            return false;
        }

        if (line.Contains("CS", StringComparison.Ordinal) && line.Contains(':'))
        {
            return false;
        }

        string[] tokens = line.Split(new[] { ' ', '\t', ';', ',' }, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
        {
            return false;
        }

        bool sawReference = false;
        foreach (string token in tokens)
        {
            if (IsDroppedCompilerToken(token))
            {
                sawReference = true;
                continue;
            }

            if (token.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                || token.EndsWith(".dll\"", StringComparison.OrdinalIgnoreCase)
                || token.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                sawReference = true;
                continue;
            }

            return false;
        }

        return sawReference;
    }
}
