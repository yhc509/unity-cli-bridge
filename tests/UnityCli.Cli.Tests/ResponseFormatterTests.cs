using System.Text.Json;
using UnityCli.Cli.Models;
using UnityCli.Cli.Services;
using UnityCli.Protocol;

namespace UnityCli.Cli.Tests;

public sealed class ResponseFormatterTests
{
    [Fact]
    public void Format_PrettyTextOutput_UsesDataFieldAndPreservesUnicodeCharacters()
    {
        var parsed = new ParsedCommand(CommandKind.Refresh);
        var response = ResponseEnvelope.Success(
            requestId: "req-1",
            target: "target-1",
            data: ToDataElement(new
            {
                message = "AssetDatabase.Refresh completed",
            }),
            durationMs: 12,
            transport: ProtocolConstants.TransportLive);

        var text = ResponseFormatter.Format(parsed, response);

        Assert.Contains("AssetDatabase.Refresh completed", text);
        Assert.DoesNotContain("\\uC644\\uB8CC", text);
    }

    [Fact]
    public void Format_JsonOutput_UsesDataFieldWithoutDoubleEscaping()
    {
        var response = ResponseEnvelope.Success(
            requestId: "req-1",
            target: "target-1",
            data: ToDataElement(new
            {
                path = "Assets/Prefab.prefab",
                type = "GameObject",
            }),
            durationMs: 42,
            transport: ProtocolConstants.TransportLive);

        var text = ResponseFormatter.Format(OutputMode.Json, response);
        var payload = JsonSerializer.Deserialize<JsonElement>(text);
        Assert.True(payload.TryGetProperty("data", out var data));

        Assert.Equal("Assets/Prefab.prefab", data.GetProperty("path").GetString());
        Assert.Equal("GameObject", data.GetProperty("type").GetString());
        Assert.False(payload.TryGetProperty("dataJson", out _));
    }

    [Fact]
    public void Format_JsonOutput_UsesWireDataWithoutDoubleEscaping()
    {
        var response = ProtocolJson.Deserialize<ResponseEnvelope>(
            "{\"requestId\":\"req-1\",\"target\":\"target-1\",\"status\":\"success\",\"durationMs\":42,\"data\":{\"path\":\"Assets/Prefab.prefab\",\"type\":\"GameObject\"},\"retryable\":false,\"transport\":\"live\"}");

        var text = ResponseFormatter.Format(OutputMode.Json, response);
        var payload = JsonSerializer.Deserialize<JsonElement>(text);
        Assert.True(payload.TryGetProperty("data", out var data));

        Assert.Equal("Assets/Prefab.prefab", data.GetProperty("path").GetString());
        Assert.Equal("GameObject", data.GetProperty("type").GetString());
        Assert.False(payload.TryGetProperty("dataJson", out _));
    }

    [Fact]
    public void Format_CompactOutput_UsesDataFieldWhenPresent()
    {
        var response = ResponseEnvelope.Success(
            requestId: "req-1",
            target: "target-1",
            data: ToDataElement(new
            {
                path = "Assets/Prefab.prefab",
                type = "GameObject",
            }),
            durationMs: 42,
            transport: ProtocolConstants.TransportLive);

        var text = ResponseFormatter.Format(OutputMode.Compact, response);

        Assert.Equal("{\"path\":\"Assets/Prefab.prefab\",\"type\":\"GameObject\"}", text);
    }

    [Fact]
    public void Format_DefaultOutput_UsesDataField()
    {
        var response = new ResponseEnvelope
        {
            requestId = "req-1",
            target = "target-1",
            status = ProtocolConstants.StatusSuccess,
            durationMs = 42,
            data = ParseData("{\"path\":\"Assets/Prefab.prefab\",\"type\":\"GameObject\"}"),
            transport = ProtocolConstants.TransportLive,
        };

        var text = ResponseFormatter.Format(OutputMode.Default, response);

        Assert.Contains("data:", text);
        Assert.Contains("\"path\": \"Assets/Prefab.prefab\"", text);
        Assert.Contains("\"type\": \"GameObject\"", text);
    }

    [Fact]
    public void Format_CompactOutput_UsesDataField()
    {
        var response = new ResponseEnvelope
        {
            requestId = "req-1",
            target = "target-1",
            status = ProtocolConstants.StatusSuccess,
            durationMs = 42,
            data = ParseData("{\"path\":\"Assets/Prefab.prefab\",\"type\":\"GameObject\"}"),
            transport = ProtocolConstants.TransportLive,
        };

        var text = ResponseFormatter.Format(OutputMode.Compact, response);

        Assert.Equal("{\"path\":\"Assets/Prefab.prefab\",\"type\":\"GameObject\"}", text);
    }

    [Fact]
    public void Format_CompactOutput_WithoutPayload_ReturnsEmptyObject()
    {
        var response = ResponseEnvelope.Success(
            requestId: "req-1",
            target: "target-1",
            data: null,
            durationMs: 42);

        var text = ResponseFormatter.Format(OutputMode.Compact, response);

        Assert.Equal("{}", text);
    }

    [Fact]
    public void Format_JsonOutput_WithoutPayload_OmitsDataField()
    {
        var response = ResponseEnvelope.Success(
            requestId: "req-1",
            target: "target-1",
            data: null,
            durationMs: 42);

        var text = ResponseFormatter.Format(OutputMode.Json, response);
        var payload = JsonSerializer.Deserialize<JsonElement>(text);

        Assert.False(payload.TryGetProperty("data", out _));
    }

    [Fact]
    public void Format_CompactError_ReturnsReducedErrorJson()
    {
        var response = ResponseEnvelope.Failure(
            requestId: "req-1",
            target: "target-1",
            code: "LIVE_UNAVAILABLE",
            message: "The bridge is not ready yet.",
            retryable: true,
            details: "{\"hint\":\"retry\"}");

        var text = ResponseFormatter.Format(OutputMode.Compact, response);

        Assert.Equal("{\"error\":\"LIVE_UNAVAILABLE\",\"message\":\"The bridge is not ready yet.\"}", text);
    }

    [Fact]
    public void Format_ErrorWithObjectDetails_PrettyPrintsAsIndentedJson()
    {
        var json = "{\"requestId\":\"r\",\"protocolVersion\":\"3\",\"status\":\"error\"," +
                   "\"durationMs\":0,\"error\":{\"code\":\"E\",\"message\":\"m\"," +
                   "\"details\":{\"path\":\"/Root\",\"reason\":\"x\"}}," +
                   "\"retryable\":false,\"transport\":\"live\"}";
        var env = JsonSerializer.Deserialize<ResponseEnvelope>(json, ProtocolJson.Default)!;

        var output = ResponseFormatter.Format(OutputMode.Default, env);

        Assert.Contains("details:", output);
        Assert.Contains("\"path\": \"/Root\"", output);
        Assert.DoesNotContain("\\\"path\\\"", output);
    }

    [Fact]
    public void Format_ErrorWithStringDetails_PrintsRawString()
    {
        var env = NewErrorEnvelopeWithStringDetails("plain text");

        var output = ResponseFormatter.Format(OutputMode.Default, env);

        Assert.Contains("plain text", output);
    }

    [Fact]
    public void Format_ErrorWithNullDetails_OmitsDetailsLine()
    {
        var env = NewErrorEnvelopeWithoutDetails();

        var output = ResponseFormatter.Format(OutputMode.Default, env);

        Assert.DoesNotContain("details:", output);
    }

    private static JsonElement ParseData(string json)
    {
        return JsonSerializer.Deserialize<JsonElement>(json, ProtocolJson.Default);
    }

    private static JsonElement ToDataElement<T>(T value)
    {
        return JsonSerializer.SerializeToElement(value, ProtocolJson.Default);
    }

    private static ResponseEnvelope NewErrorEnvelopeWithStringDetails(string details)
    {
        return ResponseEnvelope.Failure(
            requestId: "req-1",
            target: "target-1",
            code: "E",
            message: "m",
            retryable: false,
            details: details);
    }

    private static ResponseEnvelope NewErrorEnvelopeWithoutDetails()
    {
        return ResponseEnvelope.Failure(
            requestId: "req-1",
            target: "target-1",
            code: "E",
            message: "m",
            retryable: false);
    }
}
