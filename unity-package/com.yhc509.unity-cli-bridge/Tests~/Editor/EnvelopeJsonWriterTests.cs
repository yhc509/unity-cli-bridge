#nullable enable
using System;
using NUnit.Framework;
using UnityCli.Protocol;

namespace UnityCliBridge.Bridge.Editor.Tests
{
    public sealed class EnvelopeJsonWriterTests
    {
        [TestCase("{\"message\":\"hello\"}", "{\"message\":\"hello\"}")]
        [TestCase("[1,true,null]", "[1,true,null]")]
        [TestCase("\"hello\"", "\"hello\"")]
        [TestCase("12.5", "12.5")]
        [TestCase("true", "true")]
        [TestCase("null", "null")]
        [TestCase("{}", "{}")]
        public void Write_InlinesValidDataFragments(string rawData, string expectedData)
        {
            var response = ResponseEnvelope.Success(
                "req-1",
                "target-1",
                rawData,
                12,
                ProtocolConstants.TransportLive);

            string json = EnvelopeJsonWriter.Write(response);

            Assert.That(json, Is.EqualTo(
                "{\"requestId\":\"req-1\",\"protocolVersion\":\"" + ProtocolConstants.ProtocolVersion + "\",\"target\":\"target-1\",\"status\":\"success\",\"durationMs\":12,\"data\":"
                + expectedData
                + ",\"retryable\":false,\"transport\":\"live\"}"));
        }

        [Test]
        public void Write_WithNullData_OmitsDataField()
        {
            var response = ResponseEnvelope.Success(
                "req-1",
                null,
                null,
                0,
                ProtocolConstants.TransportLive);

            string json = EnvelopeJsonWriter.Write(response);

            Assert.That(json, Is.EqualTo(
                "{\"requestId\":\"req-1\",\"protocolVersion\":\"" + ProtocolConstants.ProtocolVersion + "\",\"status\":\"success\",\"durationMs\":0,\"retryable\":false,\"transport\":\"live\"}"));
        }

        [Test]
        public void Write_WithNullProtocolVersion_OmitsProtocolVersionField()
        {
            var response = ResponseEnvelope.Success(
                "req-1",
                null,
                null,
                0,
                ProtocolConstants.TransportLive);
            response.protocolVersion = null;

            string json = EnvelopeJsonWriter.Write(response);

            Assert.That(json, Is.EqualTo(
                "{\"requestId\":\"req-1\",\"status\":\"success\",\"durationMs\":0,\"retryable\":false,\"transport\":\"live\"}"));
        }

        [Test]
        public void Write_WithError_IncludesDetails()
        {
            var response = ResponseEnvelope.Failure(
                "req-1",
                null,
                "BOOM",
                "Something failed.",
                false,
                34,
                ProtocolConstants.TransportLive,
                "{\"hint\":\"retry\"}");

            string json = EnvelopeJsonWriter.Write(response);

            Assert.That(json, Is.EqualTo(
                "{\"requestId\":\"req-1\",\"protocolVersion\":\"" + ProtocolConstants.ProtocolVersion + "\",\"status\":\"error\",\"durationMs\":34,\"error\":{\"code\":\"BOOM\",\"message\":\"Something failed.\",\"details\":\"{\\\"hint\\\":\\\"retry\\\"}\"},\"retryable\":false,\"transport\":\"live\"}"));
        }

        [Test]
        public void Write_WithErrorAndNullDetails_OmitsDetails()
        {
            var response = ResponseEnvelope.Failure(
                "req-1",
                null,
                "BOOM",
                "Something failed.",
                true,
                34,
                ProtocolConstants.TransportLive,
                null);

            string json = EnvelopeJsonWriter.Write(response);

            Assert.That(json, Is.EqualTo(
                "{\"requestId\":\"req-1\",\"protocolVersion\":\"" + ProtocolConstants.ProtocolVersion + "\",\"status\":\"error\",\"durationMs\":34,\"error\":{\"code\":\"BOOM\",\"message\":\"Something failed.\"},\"retryable\":true,\"transport\":\"live\"}"));
        }

        [Test]
        public void Write_EscapesStringFields()
        {
            string emoji = char.ConvertFromUtf32(0x1F600);
            string lineSeparator = ((char)0x2028).ToString();
            string value = "plain \" \\ \b \f \n \r \t \u0001 " + emoji + lineSeparator;
            string escaped = "plain \\\" \\\\ \\b \\f \\n \\r \\t \\u0001 " + emoji + lineSeparator;
            var response = ResponseEnvelope.Failure(
                "req-1",
                value,
                "ESCAPE",
                value,
                false,
                1,
                ProtocolConstants.TransportLive,
                value);

            string json = EnvelopeJsonWriter.Write(response);

            Assert.That(json, Is.EqualTo(
                "{\"requestId\":\"req-1\",\"protocolVersion\":\"" + ProtocolConstants.ProtocolVersion + "\",\"target\":\""
                + escaped
                + "\",\"status\":\"error\",\"durationMs\":1,\"error\":{\"code\":\"ESCAPE\",\"message\":\""
                + escaped
                + "\",\"details\":\""
                + escaped
                + "\"},\"retryable\":false,\"transport\":\"live\"}"));
        }

        [Test]
        public void Write_OmitsTimingsWhenNull()
        {
            var response = ResponseEnvelope.Success(
                "req-1",
                "target-1",
                "{\"ok\":true}",
                12,
                ProtocolConstants.TransportLive);

            string json = EnvelopeJsonWriter.Write(response);

            Assert.That(json, Does.Not.Contain("timings"));
            Assert.That(json, Does.Contain("\"durationMs\":12"));
        }

        [Test]
        public void Write_EmitsQueueWaitOnly_WhenExecutePhasesAreUnset()
        {
            var response = ResponseEnvelope.Success(
                "req-1",
                "target-1",
                "{\"ok\":true}",
                12,
                ProtocolConstants.TransportLive);
            response.timings = new ResponseTimings { queueWaitMs = 40 };

            string json = EnvelopeJsonWriter.Write(response);

            Assert.That(json, Does.Contain("\"durationMs\":12,\"timings\":{\"queueWaitMs\":40},\"data\":{\"ok\":true}"));
            Assert.That(json, Does.Not.Contain("wrapMs"));
            Assert.That(json, Does.Not.Contain("cacheHit"));
        }

        [Test]
        public void Write_EmitsExecutePhases_WhenSet()
        {
            var response = ResponseEnvelope.Failure(
                "req-1",
                "target-1",
                "COMPILE_FAILED",
                "boom",
                false,
                341,
                ProtocolConstants.TransportLive);
            response.timings = new ResponseTimings
            {
                queueWaitMs = 8,
                wrapMs = 1,
                compileMs = 340,
                cacheHit = false,
            };

            string json = EnvelopeJsonWriter.Write(response);

            Assert.That(json, Does.Contain("\"timings\":{\"queueWaitMs\":8,\"wrapMs\":1,\"compileMs\":340,\"cacheHit\":false}"));
            Assert.That(json, Does.Not.Contain("loadMs"));
            Assert.That(json, Does.Not.Contain("invokeMs"));
            Assert.That(json, Does.Contain("\"durationMs\":341"));
        }

        [TestCase("")]
        [TestCase(" ")]
        [TestCase("{not valid")]
        public void Write_WithInvalidDataFragment_ThrowsInvalidOperationException(string rawData)
        {
            var response = ResponseEnvelope.Success(
                "req-1",
                "target-1",
                rawData,
                12,
                ProtocolConstants.TransportLive);

            InvalidOperationException? exception = Assert.Throws<InvalidOperationException>(() => EnvelopeJsonWriter.Write(response));
            Assert.That(exception!.Message, Does.Contain("valid JSON fragment"));
        }
    }
}
