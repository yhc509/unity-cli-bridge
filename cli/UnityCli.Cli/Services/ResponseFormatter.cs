using System.Text.Json;
using UnityCli.Cli.Models;
using UnityCli.Protocol;

namespace UnityCli.Cli.Services;

public static class ResponseFormatter
{
    private static readonly JsonSerializerOptions PrettyPrintOptions = new(ProtocolJson.Default)
    {
        WriteIndented = true,
    };

    private static readonly JsonSerializerOptions CompactPrintOptions = new(ProtocolJson.Default)
    {
        WriteIndented = false,
    };

    public static string Format(ParsedCommand parsed, ResponseEnvelope response)
    {
        return Format(parsed.OutputMode, response);
    }

    public static string Format(OutputMode outputMode, ResponseEnvelope response)
    {
        if (outputMode == OutputMode.Json)
        {
            return ProtocolJson.Serialize(response);
        }

        if (outputMode == OutputMode.Compact)
        {
            return FormatCompact(response);
        }

        return FormatDefault(response);
    }

    private static string FormatDefault(ResponseEnvelope response)
    {
        if (response.status != "success")
        {
            return BuildErrorText(response);
        }

        var lines = new List<string>
        {
            $"status: {response.status}",
            $"transport: {response.transport}",
        };

        if (!string.IsNullOrWhiteSpace(response.target))
        {
            lines.Add($"target: {response.target}");
        }

        if (response.durationMs > 0)
        {
            lines.Add($"durationMs: {response.durationMs}");
        }

        if (response.timings is { } timings)
        {
            lines.Add("timings: " + FormatTimings(timings));
        }

        if (response.data is { } data)
        {
            lines.Add("data:");
            lines.Add(PrettyData(data));
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static string FormatCompact(ResponseEnvelope response)
    {
        if (response.status != "success")
        {
            return JsonSerializer.Serialize(
                new
                {
                    error = response.error?.code ?? "UNKNOWN_ERROR",
                    message = response.error?.message ?? "Unknown error.",
                },
                CompactPrintOptions);
        }

        if (response.data is { } data)
        {
            return CompactData(data);
        }

        return "{}";
    }

    private static string BuildErrorText(ResponseEnvelope response)
    {
        var lines = new List<string>
        {
            $"status: {response.status}",
            $"transport: {response.transport}",
        };

        if (!string.IsNullOrWhiteSpace(response.target))
        {
            lines.Add($"target: {response.target}");
        }

        if (response.error is not null)
        {
            lines.Add($"errorCode: {response.error.code}");
            lines.Add($"message: {response.error.message}");
            if (response.error.details is JsonElement details && details.ValueKind != JsonValueKind.Null)
            {
                lines.Add("details:");
                lines.Add(details.ValueKind == JsonValueKind.String
                    ? details.GetString() ?? string.Empty
                    : JsonSerializer.Serialize(details, PrettyPrintOptions));
            }
        }

        lines.Add($"retryable: {response.retryable.ToString().ToLowerInvariant()}");
        if (response.timings is { } timings)
        {
            lines.Add("timings: " + FormatTimings(timings));
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static string FormatTimings(ResponseTimings timings)
    {
        var parts = new List<string>
        {
            "queueWaitMs=" + timings.queueWaitMs.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
        if (!string.IsNullOrEmpty(timings.compiler))
        {
            parts.Add("compiler=" + timings.compiler);
        }

        AppendTiming(parts, "wrapMs", timings.wrapMs);
        AppendTiming(parts, "compileMs", timings.compileMs);
        AppendTiming(parts, "loadMs", timings.loadMs);
        AppendTiming(parts, "invokeMs", timings.invokeMs);
        AppendTiming(parts, "serializeMs", timings.serializeMs);
        if (timings.cacheHit is { } cacheHit)
        {
            parts.Add("cacheHit=" + (cacheHit ? "true" : "false"));
        }

        return string.Join(" ", parts);
    }

    private static void AppendTiming(List<string> parts, string name, long? value)
    {
        if (!value.HasValue)
        {
            return;
        }

        parts.Add(name + "=" + value.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    private static string CompactData(JsonElement data)
    {
        return SerializeData(data, CompactPrintOptions);
    }

    private static string PrettyData(JsonElement data)
    {
        return SerializeData(data, PrettyPrintOptions);
    }

    private static string SerializeData(JsonElement data, JsonSerializerOptions options)
    {
        return JsonSerializer.Serialize(data, options);
    }

}
