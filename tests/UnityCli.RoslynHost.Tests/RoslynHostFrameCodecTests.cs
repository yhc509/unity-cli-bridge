using System.Text;
using UnityCli.Protocol;

namespace UnityCli.RoslynHost.Tests;

public sealed class RoslynHostFrameCodecTests
{
    [Fact]
    public void HeaderOnlyMessage_HasLittleEndianPrefixAndNoPayload()
    {
        byte[] bytes = HostTestSupport.Encode(HostTestSupport.Simple("ping"));

        string json = Encoding.UTF8.GetString(bytes, 4, bytes.Length - 4);
        Assert.Equal("{\"type\":\"ping\"}", json);
        Assert.Equal(new byte[] { (byte)json.Length, 0, 0, 0 }, bytes.Take(4).ToArray());

        RoslynHostMessage decoded = Assert.Single(HostTestSupport.DecodeAll(bytes));
        Assert.Equal("ping", decoded.Type);
        Assert.Null(decoded.Payload);
    }

    [Fact]
    public void PayloadMessage_RoundTripsAndAnnouncesItsLength()
    {
        byte[] payload = Enumerable.Range(0, 300).Select(i => (byte)i).ToArray();
        var message = new RoslynHostMessage("result", new Dictionary<string, object?> { ["id"] = "1" }, payload);

        byte[] bytes = HostTestSupport.Encode(message);
        RoslynHostMessage decoded = Assert.Single(HostTestSupport.DecodeAll(bytes));

        Assert.Equal(payload, decoded.Payload);
        Assert.Equal(300d, decoded.Header[RoslynHostFrameCodec.PayloadBytesKey]);
        int headerLength = BitConverter.ToInt32(bytes, 0);
        Assert.Equal(300, BitConverter.ToInt32(bytes, 4 + headerLength));
    }

    [Fact]
    public void EmptyPayload_IsStillAFrame()
    {
        var message = new RoslynHostMessage("result", payload: Array.Empty<byte>());

        RoslynHostMessage decoded = Assert.Single(HostTestSupport.DecodeAll(HostTestSupport.Encode(message)));

        Assert.NotNull(decoded.Payload);
        Assert.Empty(decoded.Payload!);
    }

    [Fact]
    public void BackToBackMessages_DecodeInOrder()
    {
        byte[] bytes = HostTestSupport.Encode(
            HostTestSupport.Simple("a"),
            new RoslynHostMessage("b", payload: new byte[] { 1, 2, 3 }),
            HostTestSupport.Simple("c"));

        Assert.Equal(new[] { "a", "b", "c" }, HostTestSupport.DecodeAll(bytes).Select(m => m.Type));
    }

    [Fact]
    public void PartialReads_OneByteAtATime_StillDecode()
    {
        byte[] payload = new byte[] { 9, 8, 7, 6, 5 };
        byte[] bytes = HostTestSupport.Encode(
            new RoslynHostMessage("result", new Dictionary<string, object?> { ["text"] = "héllo 한글" }, payload),
            HostTestSupport.Simple("quit"));
        var stream = new TrickleStream(bytes);

        RoslynHostMessage first = RoslynHostFrameCodec.ReadMessage(stream)!;
        RoslynHostMessage second = RoslynHostFrameCodec.ReadMessage(stream)!;

        Assert.Equal("héllo 한글", first.Header["text"]);
        Assert.Equal(payload, first.Payload);
        Assert.Equal("quit", second.Type);
        Assert.Null(RoslynHostFrameCodec.ReadMessage(stream));
        Assert.True(stream.ReadCalls >= bytes.Length);
    }

    [Fact]
    public void CleanEndOfStream_ReturnsNull()
    {
        Assert.Null(RoslynHostFrameCodec.ReadMessage(new MemoryStream()));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void EndInsideLengthPrefix_IsRejected(int length)
    {
        var ex = Assert.Throws<RoslynHostFrameException>(() => RoslynHostFrameCodec.ReadMessage(new MemoryStream(new byte[length])));
        Assert.Contains("length prefix", ex.Message);
    }

    [Fact]
    public void EndInsideHeader_IsRejected()
    {
        byte[] bytes = HostTestSupport.Encode(HostTestSupport.Simple("ping"));

        var ex = Assert.Throws<RoslynHostFrameException>(() => RoslynHostFrameCodec.ReadMessage(new MemoryStream(bytes, 0, bytes.Length - 2)));
        Assert.Contains("inside a header frame", ex.Message);
    }

    [Fact]
    public void EndInsidePayload_IsRejected()
    {
        byte[] bytes = HostTestSupport.Encode(new RoslynHostMessage("result", payload: new byte[10]));

        var ex = Assert.Throws<RoslynHostFrameException>(() => RoslynHostFrameCodec.ReadMessage(new MemoryStream(bytes, 0, bytes.Length - 1)));
        Assert.Contains("inside a payload frame", ex.Message);
    }

    [Fact]
    public void EndBeforePayloadPrefix_IsRejected()
    {
        byte[] bytes = HostTestSupport.Encode(new RoslynHostMessage("result", payload: new byte[10]));
        int headerEnd = 4 + BitConverter.ToInt32(bytes, 0);

        var ex = Assert.Throws<RoslynHostFrameException>(() => RoslynHostFrameCodec.ReadMessage(new MemoryStream(bytes, 0, headerEnd + 2)));
        Assert.Contains("before the payload frame", ex.Message);
    }

    [Fact]
    public void ZeroLengthHeader_IsRejected()
    {
        var ex = Assert.Throws<RoslynHostFrameException>(() => RoslynHostFrameCodec.ReadMessage(new MemoryStream(new byte[4])));
        Assert.Contains("empty", ex.Message);
    }

    [Fact]
    public void OversizedHeaderPrefix_IsRejectedWithoutReadingTheBody()
    {
        // 0xFFFFFFFF: a desynchronised stream must not turn into a 4 GB allocation.
        var stream = new MemoryStream(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 1, 2, 3 });

        var ex = Assert.Throws<RoslynHostFrameException>(() => RoslynHostFrameCodec.ReadMessage(stream));
        Assert.Contains("4294967295", ex.Message);
        Assert.Equal(4, stream.Position);
    }

    [Fact]
    public void HeaderAtTheLimit_IsAccepted_OneByteOver_IsRejected()
    {
        byte[] bytes = HostTestSupport.Encode(HostTestSupport.Simple("ping"));
        int headerLength = BitConverter.ToInt32(bytes, 0);

        Assert.NotNull(RoslynHostFrameCodec.ReadMessage(new MemoryStream(bytes), new RoslynHostFrameLimits(headerLength, 0)));
        Assert.Throws<RoslynHostFrameException>(
            () => RoslynHostFrameCodec.ReadMessage(new MemoryStream(bytes), new RoslynHostFrameLimits(headerLength - 1, 0)));
    }

    [Fact]
    public void PayloadOverTheLimit_IsRejectedOnReadAndWrite()
    {
        var message = new RoslynHostMessage("result", payload: new byte[17]);
        byte[] bytes = HostTestSupport.Encode(message);
        var limits = new RoslynHostFrameLimits(1024, 16);

        var read = Assert.Throws<RoslynHostFrameException>(() => RoslynHostFrameCodec.ReadMessage(new MemoryStream(bytes), limits));
        Assert.Contains("limit is 16", read.Message);
        var write = Assert.Throws<RoslynHostFrameException>(() => RoslynHostFrameCodec.WriteMessage(new MemoryStream(), message, limits));
        Assert.Contains("limit is 16", write.Message);
    }

    [Fact]
    public void HeaderOverTheLimit_IsRejectedOnWrite()
    {
        var message = new RoslynHostMessage("compile", new Dictionary<string, object?> { ["source"] = new string('x', 100) });

        Assert.Throws<RoslynHostFrameException>(() => RoslynHostFrameCodec.WriteMessage(new MemoryStream(), message, new RoslynHostFrameLimits(50, 0)));
    }

    [Fact]
    public void PayloadLengthMismatch_IsRejected()
    {
        byte[] bytes = HostTestSupport.Encode(new RoslynHostMessage("result", payload: new byte[10]));
        int payloadPrefix = 4 + BitConverter.ToInt32(bytes, 0);
        bytes[payloadPrefix] = 9;

        var ex = Assert.Throws<RoslynHostFrameException>(() => RoslynHostFrameCodec.ReadMessage(new MemoryStream(bytes)));
        Assert.Contains("announced 10", ex.Message);
    }

    [Theory]
    [InlineData("{\"type\":\"x\",\"payloadBytes\":-1}")]
    [InlineData("{\"type\":\"x\",\"payloadBytes\":1.5}")]
    [InlineData("{\"type\":\"x\",\"payloadBytes\":\"3\"}")]
    public void InvalidPayloadAnnouncement_IsRejected(string header)
    {
        var ex = Assert.Throws<RoslynHostFrameException>(() => RoslynHostFrameCodec.ReadMessage(new MemoryStream(RawFrame(header))));
        Assert.Contains("payloadBytes", ex.Message);
    }

    [Theory]
    [InlineData("not json", "not valid JSON")]
    [InlineData("[1,2]", "JSON object")]
    [InlineData("{\"id\":\"1\"}", "'type'")]
    [InlineData("{\"type\":\"\"}", "'type'")]
    [InlineData("{\"type\":7}", "'type'")]
    [InlineData("{\"type\":\"a\"} trailing", "not valid JSON")]
    public void MalformedHeader_IsRejectedWithAClearReason(string header, string expected)
    {
        var ex = Assert.Throws<RoslynHostFrameException>(() => RoslynHostFrameCodec.ReadMessage(new MemoryStream(RawFrame(header))));
        Assert.Contains(expected, ex.Message);
    }

    [Fact]
    public void InvalidUtf8Header_IsRejected()
    {
        byte[] body = { (byte)'{', 0xC3, 0x28, (byte)'}' };
        byte[] bytes = BitConverter.GetBytes(body.Length).Concat(body).ToArray();

        var ex = Assert.Throws<RoslynHostFrameException>(() => RoslynHostFrameCodec.ReadMessage(new MemoryStream(bytes)));
        Assert.Contains("UTF-8", ex.Message);
    }

    [Fact]
    public void Writer_ReplacesAStalePayloadAnnouncement()
    {
        var header = new Dictionary<string, object?> { ["payloadBytes"] = 99 };

        RoslynHostMessage decoded = Assert.Single(HostTestSupport.DecodeAll(HostTestSupport.Encode(new RoslynHostMessage("x", header))));

        Assert.False(decoded.Header.ContainsKey(RoslynHostFrameCodec.PayloadBytesKey));
        Assert.Null(decoded.Payload);
    }

    private static byte[] RawFrame(string header)
    {
        byte[] body = Encoding.UTF8.GetBytes(header);
        return BitConverter.GetBytes(body.Length).Concat(body).ToArray();
    }
}
