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
            return ProfileClampJson.OmitUnclamped(ProtocolJson.Serialize(response));
        }

        response = PresentForText(outputMode, response);
        if (outputMode == OutputMode.Compact)
        {
            return FormatCompact(response);
        }

        return FormatDefault(response);
    }

    /// <summary>
    /// Default and compact text drop repeated execute log lines, the compile-error define and
    /// reference lists, and (compact only) internal status fields. The caller's envelope is unchanged.
    /// </summary>
    private static ResponseEnvelope PresentForText(OutputMode outputMode, ResponseEnvelope response)
    {
        JsonElement? data = response.data;
        if (data.HasValue)
        {
            data = TextOutputTrim.PresentData(data.Value, outputMode);
        }

        ProtocolError? error = response.error;
        if (error != null && !string.IsNullOrEmpty(error.message))
        {
            string trimmed = TextOutputTrim.TrimCompileErrorMessage(error.message);
            if (!string.Equals(trimmed, error.message, StringComparison.Ordinal))
            {
                error = new ProtocolError
                {
                    code = error.code,
                    message = trimmed,
                    details = error.details,
                };
            }
        }

        return new ResponseEnvelope
        {
            requestId = response.requestId,
            protocolVersion = response.protocolVersion,
            target = response.target,
            status = response.status,
            durationMs = response.durationMs,
            data = data,
            error = error,
            retryable = response.retryable,
            transport = response.transport,
        };
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
        return string.Join(Environment.NewLine, lines);
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
        return ProfileClampJson.OmitUnclamped(JsonSerializer.Serialize(data, options));
    }

}
