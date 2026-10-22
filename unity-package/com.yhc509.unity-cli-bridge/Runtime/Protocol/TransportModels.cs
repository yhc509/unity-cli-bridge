#nullable enable
using System;
#if !UNITY_5_3_OR_NEWER
using System.Text.Json;
#endif

namespace UnityCli.Protocol
{
    [Serializable]
    public sealed class CommandEnvelope
    {
        public string requestId = string.Empty;
        public string? protocolVersion;
        public string token = string.Empty;
        public string command = string.Empty;
        public string argumentsJson = "{}";
    }

    [Serializable]
    public sealed class ResponseEnvelope
    {
        public string requestId = string.Empty;
        public string? protocolVersion;
        public string? target;
        public string status = ProtocolConstants.StatusSuccess;
        // Handler time only. Does not include queue wait; that is timings.queueWaitMs when the
        // request was queued for the editor main thread.
        public long durationMs;
        // Optional and additive. Null on responses that never entered the editor queue, and absent
        // from older packages. Older clients ignore the field. Protocol version stays 8.
        public ResponseTimings? timings;
#if UNITY_5_3_OR_NEWER
        public string? data;
#else
        public JsonElement? data;
#endif
        public ProtocolError? error;
        public bool retryable;
        public string transport = ProtocolConstants.TransportLive;

#if UNITY_5_3_OR_NEWER
        public static ResponseEnvelope Success(
            string requestId,
            string? target,
            string? data,
            long durationMs,
            string transport = ProtocolConstants.TransportLive)
        {
            return new ResponseEnvelope
            {
                requestId = requestId,
                protocolVersion = ProtocolConstants.ProtocolVersion,
                target = target,
                status = ProtocolConstants.StatusSuccess,
                durationMs = durationMs,
                data = data,
                transport = transport,
            };
        }
#else
        public static ResponseEnvelope Success(
            string requestId,
            string? target,
            JsonElement? data,
            long durationMs,
            string transport = ProtocolConstants.TransportLive)
        {
            return new ResponseEnvelope
            {
                requestId = requestId,
                protocolVersion = ProtocolConstants.ProtocolVersion,
                target = target,
                status = ProtocolConstants.StatusSuccess,
                durationMs = durationMs,
                data = data,
                transport = transport,
            };
        }
#endif

        public static ResponseEnvelope Failure(
            string requestId,
            string? target,
            string code,
            string message,
            bool retryable,
            long durationMs = 0,
            string transport = ProtocolConstants.TransportLive,
            string? details = null)
        {
            return new ResponseEnvelope
            {
                requestId = requestId,
                protocolVersion = ProtocolConstants.ProtocolVersion,
                target = target,
                status = ProtocolConstants.StatusError,
                durationMs = durationMs,
                retryable = retryable,
                transport = transport,
                error = new ProtocolError
                {
                    code = code,
                    message = message,
#if UNITY_5_3_OR_NEWER
                    details = details,
#else
                    details = ParseDetails(details),
#endif
                },
            };
        }

        public static ResponseEnvelope WithTimings(ResponseEnvelope envelope, ResponseTimings? timings)
        {
            envelope.timings = timings;
            return envelope;
        }

#if !UNITY_5_3_OR_NEWER
        private static JsonElement? ParseDetails(string? details)
        {
            if (string.IsNullOrWhiteSpace(details))
            {
                return null;
            }

            try
            {
                return JsonSerializer.Deserialize<JsonElement>(details, ProtocolJson.Default);
            }
            catch (JsonException)
            {
                return JsonSerializer.Deserialize<JsonElement>(
                    JsonSerializer.Serialize(details),
                    ProtocolJson.Default);
            }
        }
#endif
    }

    [Serializable]
    public sealed class ProtocolError
    {
        public string code = string.Empty;
        public string message = string.Empty;
#if UNITY_5_3_OR_NEWER
        public string? details;
#else
        public JsonElement? details;
#endif
    }

    /// <summary>
    /// Optional breakdown attached to a live response. <see cref="queueWaitMs"/> is present for every
    /// queued command. Execute phases are set only for <c>execute</c>, and only for phases that ran.
    /// <see cref="compiler"/> is the compile-backend id when <c>execute</c> selected one (<c>codedom</c>
    /// for CodeDOM). Whole milliseconds. A phase under 1 ms is 0.
    /// </summary>
    [Serializable]
    public sealed class ResponseTimings
    {
        public long queueWaitMs;
        public string? compiler;
        public long? wrapMs;
        public long? compileMs;
        public long? loadMs;
        public long? invokeMs;
        public long? serializeMs;
        public bool? cacheHit;

        public static void ApplyQueueWait(ResponseEnvelope response, long queueWaitMs)
        {
            if (response.timings == null)
            {
                response.timings = new ResponseTimings();
            }

            response.timings.queueWaitMs = queueWaitMs;
        }

        public static long QueueWaitMilliseconds(long receivedTimestamp, long dispatchTimestamp)
        {
            if (dispatchTimestamp <= receivedTimestamp)
            {
                return 0;
            }

            long frequency = System.Diagnostics.Stopwatch.Frequency;
            if (frequency <= 0)
            {
                return 0;
            }

            double elapsedMs = (dispatchTimestamp - receivedTimestamp) * 1000.0 / frequency;
            if (double.IsNaN(elapsedMs) || elapsedMs <= 0)
            {
                return 0;
            }

            if (elapsedMs >= long.MaxValue)
            {
                return long.MaxValue;
            }

            return (long)elapsedMs;
        }
    }
}
