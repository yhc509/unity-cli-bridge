using System.Text.Json;
using UnityCli.Protocol;

namespace UnityCli.Cli.Tests;

public sealed class ResponseTimingsTests
{
    [Fact]
    public void ProtocolVersion_StaysAtEightWhenTimingsAreAdded()
    {
        Assert.Equal("8", ProtocolConstants.ProtocolVersion);
    }

    [Fact]
    public void QueueWaitMilliseconds_IsZeroWhenDispatchIsNotAfterReceive()
    {
        Assert.Equal(0, ResponseTimings.QueueWaitMilliseconds(100, 100));
        Assert.Equal(0, ResponseTimings.QueueWaitMilliseconds(100, 50));
    }

    [Fact]
    public void QueueWaitMilliseconds_ConvertsTimestampDeltaToWholeMilliseconds()
    {
        long frequency = System.Diagnostics.Stopwatch.Frequency;
        long received = 1_000;
        long dispatch = received + frequency + (frequency / 2);

        Assert.Equal(1500, ResponseTimings.QueueWaitMilliseconds(received, dispatch));
    }

    [Fact]
    public void Deserialize_LegacyEnvelopeWithoutTimings_LeavesTimingsNullAndDurationMsIntact()
    {
        const string json =
            "{\"requestId\":\"req-1\",\"protocolVersion\":\"8\",\"target\":\"hash\"," +
            "\"status\":\"success\",\"durationMs\":12,\"data\":{\"message\":\"hello\"}," +
            "\"retryable\":false,\"transport\":\"live\"}";

        var response = ProtocolJson.Deserialize<ResponseEnvelope>(json);

        Assert.Null(response.timings);
        Assert.Equal(12, response.durationMs);
        Assert.Equal("success", response.status);
        Assert.Equal("hello", response.data!.Value.GetProperty("message").GetString());

        string rewritten = ProtocolJson.Serialize(response);
        Assert.DoesNotContain("timings", rewritten);
        Assert.Contains("\"durationMs\":12", rewritten);
    }

    [Fact]
    public void Deserialize_EnvelopeWithTimings_ReadsAdditiveFields()
    {
        const string json =
            "{\"requestId\":\"req-1\",\"protocolVersion\":\"8\",\"status\":\"success\",\"durationMs\":12," +
            "\"timings\":{\"queueWaitMs\":40,\"compiler\":\"codedom\",\"wrapMs\":1,\"compileMs\":341,\"loadMs\":2,\"invokeMs\":3,\"serializeMs\":0,\"cacheHit\":false}," +
            "\"data\":{\"cacheHit\":false,\"compileMs\":343},\"retryable\":false,\"transport\":\"live\"}";

        var response = ProtocolJson.Deserialize<ResponseEnvelope>(json);

        Assert.NotNull(response.timings);
        Assert.Equal(12, response.durationMs);
        Assert.Equal(40, response.timings!.queueWaitMs);
        Assert.Equal("codedom", response.timings.compiler);
        Assert.Equal(1, response.timings.wrapMs);
        Assert.Equal(341, response.timings.compileMs);
        Assert.Equal(2, response.timings.loadMs);
        Assert.Equal(3, response.timings.invokeMs);
        Assert.Equal(0, response.timings.serializeMs);
        Assert.False(response.timings.cacheHit);
        Assert.Equal(343, response.data!.Value.GetProperty("compileMs").GetInt64());
    }

    [Fact]
    public void Deserialize_NonExecuteTimings_LeavesExecutePhasesNull()
    {
        const string json =
            "{\"requestId\":\"req-1\",\"protocolVersion\":\"8\",\"status\":\"success\",\"durationMs\":4," +
            "\"timings\":{\"queueWaitMs\":9},\"retryable\":false,\"transport\":\"live\"}";

        var response = ProtocolJson.Deserialize<ResponseEnvelope>(json);

        Assert.Equal(9, response.timings!.queueWaitMs);
        Assert.Null(response.timings.compiler);
        Assert.Null(response.timings.wrapMs);
        Assert.Null(response.timings.compileMs);
        Assert.Null(response.timings.loadMs);
        Assert.Null(response.timings.invokeMs);
        Assert.Null(response.timings.serializeMs);
        Assert.Null(response.timings.cacheHit);

        string rewritten = ProtocolJson.Serialize(response);
        Assert.Contains("\"queueWaitMs\":9", rewritten);
        Assert.DoesNotContain("compiler", rewritten);
        Assert.DoesNotContain("wrapMs", rewritten);
        Assert.DoesNotContain("cacheHit", rewritten);
    }

    [Fact]
    public void LegacyClientShape_IgnoresUnknownTimingsField()
    {
        const string json =
            "{\"requestId\":\"req-1\",\"protocolVersion\":\"8\",\"status\":\"success\",\"durationMs\":12," +
            "\"timings\":{\"queueWaitMs\":40,\"cacheHit\":true},\"data\":{\"message\":\"한글\"}," +
            "\"retryable\":false,\"transport\":\"live\",\"futureField\":1}";

        var legacy = JsonSerializer.Deserialize<LegacyResponseEnvelope>(json, ProtocolJson.Default)!;

        Assert.Equal("req-1", legacy.requestId);
        Assert.Equal("8", legacy.protocolVersion);
        Assert.Equal("success", legacy.status);
        Assert.Equal(12, legacy.durationMs);
        Assert.False(legacy.retryable);
        Assert.Equal("live", legacy.transport);
        Assert.Equal("한글", legacy.data!.Value.GetProperty("message").GetString());
    }

    /// <summary>
    /// The response fields a CLI from before <c>timings</c> knew about. Extra JSON must not fail the parse.
    /// </summary>
    private sealed class LegacyResponseEnvelope
    {
        public string requestId { get; set; } = string.Empty;
        public string? protocolVersion { get; set; }
        public string? target { get; set; }
        public string status { get; set; } = string.Empty;
        public long durationMs { get; set; }
        public JsonElement? data { get; set; }
        public bool retryable { get; set; }
        public string transport { get; set; } = string.Empty;
    }
}
