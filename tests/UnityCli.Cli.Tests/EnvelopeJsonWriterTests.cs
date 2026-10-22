using System.Text.Json;
using UnityCli.Protocol;
using UnityCliBridge.Bridge.Editor;

namespace UnityCli.Cli.Tests;

public sealed class EnvelopeJsonWriterTests
{
    [Fact]
    public void Write_EmitsErrorDetailsAsInlineJsonObject()
    {
        var envelope = NewErrorEnvelope(details: "{\"path\":\"/Root[0]\",\"reason\":\"missing\"}");

        var json = EnvelopeJsonWriter.Write(envelope);

        Assert.Contains("\"details\":{\"path\":\"/Root[0]\",\"reason\":\"missing\"}", json);
        Assert.DoesNotContain("\"details\":\"{", json);
    }

    [Fact]
    public void Write_EmitsErrorDetailsAsInlineJsonString_WhenFreeformText()
    {
        var envelope = NewErrorEnvelope(details: "\"plain text\"");

        var json = EnvelopeJsonWriter.Write(envelope);

        Assert.Contains("\"details\":\"plain text\"", json);
    }

    [Fact]
    public void ValidateRawJson_ThrowsOnInvalidFragment()
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => EnvelopeJsonWriter.ValidateRawJson("{not valid json"));

        Assert.Equal("Response envelope JSON fragment is not valid.", exception.Message);
    }

    [Fact]
    public void ProtocolErrorDetails_FromString_EncodesFreeformTextAsJsonStringFragment()
    {
        var details = ProtocolErrorDetails.FromString("line\nquoted \"value\"");
        var envelope = NewErrorEnvelope(details);

        var json = EnvelopeJsonWriter.Write(envelope);

        Assert.Contains("\"details\":\"line\\nquoted \\\"value\\\"\"", json);
    }

    [Fact]
    public void Write_OmitsTimingsWhenNull()
    {
        var envelope = ResponseEnvelope.Success(
            "req-1",
            "target-1",
            Parse("{\"ok\":true}"),
            12,
            ProtocolConstants.TransportLive);

        var json = EnvelopeJsonWriter.Write(envelope);

        Assert.DoesNotContain("timings", json);
        Assert.Contains("\"durationMs\":12", json);
    }

    [Fact]
    public void Write_EmitsQueueWaitOnly_WhenExecutePhasesAreUnset()
    {
        var envelope = ResponseEnvelope.Success(
            "req-1",
            "target-1",
            Parse("{\"ok\":true}"),
            12,
            ProtocolConstants.TransportLive);
        envelope.timings = new ResponseTimings { queueWaitMs = 40 };

        var json = EnvelopeJsonWriter.Write(envelope);

        Assert.Contains("\"durationMs\":12,\"timings\":{\"queueWaitMs\":40},\"data\":{\"ok\":true}", json);
        Assert.DoesNotContain("wrapMs", json);
        Assert.DoesNotContain("cacheHit", json);
    }

    [Fact]
    public void Write_EmitsExecutePhases_WhenSet()
    {
        var envelope = ResponseEnvelope.Failure(
            "req-1",
            "target-1",
            "COMPILE_FAILED",
            "boom",
            false,
            341,
            ProtocolConstants.TransportLive);
        envelope.timings = new ResponseTimings
        {
            queueWaitMs = 8,
            wrapMs = 1,
            compileMs = 340,
            cacheHit = false,
        };

        var json = EnvelopeJsonWriter.Write(envelope);

        Assert.Contains(
            "\"timings\":{\"queueWaitMs\":8,\"wrapMs\":1,\"compileMs\":340,\"cacheHit\":false}",
            json);
        Assert.DoesNotContain("compiler", json);
        Assert.DoesNotContain("loadMs", json);
        Assert.DoesNotContain("invokeMs", json);
        Assert.Contains("\"durationMs\":341", json);
    }

    private static JsonElement Parse(string json)
    {
        return JsonSerializer.Deserialize<JsonElement>(json, ProtocolJson.Default);
    }

    private static ResponseEnvelope NewErrorEnvelope(string? details)
    {
        return ResponseEnvelope.Failure(
            requestId: "r1",
            target: null,
            code: "E_X",
            message: "boom",
            retryable: false,
            transport: ProtocolConstants.TransportLive,
            details: details);
    }
}
