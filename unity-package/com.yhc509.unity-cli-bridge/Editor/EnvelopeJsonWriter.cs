#nullable enable
using System;
using System.Globalization;
using System.Text;
#if !UNITY_5_3_OR_NEWER
using System.Text.Json;
#endif
using Newtonsoft.Json.Linq;
using UnityCli.Protocol;

namespace UnityCliBridge.Bridge.Editor
{
    internal static class EnvelopeJsonWriter
    {
        private const string HexDigits = "0123456789ABCDEF";

        public static string Write(ResponseEnvelope envelope)
        {
            var builder = new StringBuilder(256);
            var wroteField = false;

            builder.Append('{');
            WriteStringField(builder, ref wroteField, "requestId", envelope.requestId, true);
            WriteStringField(builder, ref wroteField, "protocolVersion", envelope.protocolVersion, false);
            WriteStringField(builder, ref wroteField, "target", envelope.target, false);
            WriteStringField(builder, ref wroteField, "status", envelope.status, true);
            WriteNumberField(builder, ref wroteField, "durationMs", envelope.durationMs);
            WriteTimingsField(builder, ref wroteField, envelope.timings);
            WriteRawField(builder, ref wroteField, "data", envelope.data);
            WriteErrorField(builder, ref wroteField, envelope.error);
            WriteBooleanField(builder, ref wroteField, "retryable", envelope.retryable);
            WriteStringField(builder, ref wroteField, "transport", envelope.transport, true);
            builder.Append('}');

            return builder.ToString();
        }

        private static void WriteStringField(
            StringBuilder builder,
            ref bool wroteField,
            string name,
            string? value,
            bool includeWhenNull)
        {
            if (value == null && !includeWhenNull)
            {
                return;
            }

            WriteFieldPrefix(builder, ref wroteField, name);
            AppendJsonString(builder, value);
        }

        private static void WriteNumberField(StringBuilder builder, ref bool wroteField, string name, long value)
        {
            WriteFieldPrefix(builder, ref wroteField, name);
            builder.Append(value.ToString(CultureInfo.InvariantCulture));
        }

        private static void WriteBooleanField(StringBuilder builder, ref bool wroteField, string name, bool value)
        {
            WriteFieldPrefix(builder, ref wroteField, name);
            builder.Append(value ? "true" : "false");
        }

        private static void WriteTimingsField(StringBuilder builder, ref bool wroteField, ResponseTimings? timings)
        {
            if (timings == null)
            {
                return;
            }

            WriteFieldPrefix(builder, ref wroteField, "timings");
            var wroteTimingField = false;
            builder.Append('{');
            WriteNumberField(builder, ref wroteTimingField, "queueWaitMs", timings.queueWaitMs);
            WriteStringField(builder, ref wroteTimingField, "compiler", timings.compiler, false);
            WriteOptionalNumberField(builder, ref wroteTimingField, "wrapMs", timings.wrapMs);
            WriteOptionalNumberField(builder, ref wroteTimingField, "compileMs", timings.compileMs);
            WriteOptionalNumberField(builder, ref wroteTimingField, "loadMs", timings.loadMs);
            WriteOptionalNumberField(builder, ref wroteTimingField, "invokeMs", timings.invokeMs);
            WriteOptionalNumberField(builder, ref wroteTimingField, "serializeMs", timings.serializeMs);
            if (timings.cacheHit.HasValue)
            {
                WriteBooleanField(builder, ref wroteTimingField, "cacheHit", timings.cacheHit.Value);
            }

            builder.Append('}');
        }

        private static void WriteOptionalNumberField(StringBuilder builder, ref bool wroteField, string name, long? value)
        {
            if (!value.HasValue)
            {
                return;
            }

            WriteNumberField(builder, ref wroteField, name, value.Value);
        }

        private static void WriteRawField(StringBuilder builder, ref bool wroteField, string name, string? rawJson)
        {
            if (rawJson == null)
            {
                return;
            }

            ValidateRawJson(rawJson);
            WriteFieldPrefix(builder, ref wroteField, name);
            builder.Append(rawJson);
        }

#if !UNITY_5_3_OR_NEWER
        private static void WriteRawField(StringBuilder builder, ref bool wroteField, string name, JsonElement? rawJson)
        {
            if (!rawJson.HasValue)
            {
                return;
            }

            WriteFieldPrefix(builder, ref wroteField, name);
            builder.Append(JsonSerializer.Serialize(rawJson.Value, ProtocolJson.Default));
        }
#endif

        internal static void ValidateRawJson(string rawJson)
        {
            try
            {
                JToken.Parse(rawJson);
            }
            catch (Exception exception)
            {
                throw new InvalidOperationException("Response envelope JSON fragment is not valid.", exception);
            }
        }

        private static void WriteErrorField(StringBuilder builder, ref bool wroteField, ProtocolError? error)
        {
            if (error == null)
            {
                return;
            }

            WriteFieldPrefix(builder, ref wroteField, "error");

            var wroteErrorField = false;
            builder.Append('{');
            WriteStringField(builder, ref wroteErrorField, "code", error.code, true);
            WriteStringField(builder, ref wroteErrorField, "message", error.message, true);
            WriteRawField(builder, ref wroteErrorField, "details", error.details);
            builder.Append('}');
        }

        private static void WriteFieldPrefix(StringBuilder builder, ref bool wroteField, string name)
        {
            if (wroteField)
            {
                builder.Append(',');
            }

            AppendJsonString(builder, name);
            builder.Append(':');
            wroteField = true;
        }

        private static void AppendJsonString(StringBuilder builder, string? value)
        {
            if (value == null)
            {
                builder.Append("null");
                return;
            }

            builder.Append('"');
            foreach (char character in value)
            {
                switch (character)
                {
                    case '"':
                        builder.Append("\\\"");
                        break;
                    case '\\':
                        builder.Append("\\\\");
                        break;
                    case '\b':
                        builder.Append("\\b");
                        break;
                    case '\f':
                        builder.Append("\\f");
                        break;
                    case '\n':
                        builder.Append("\\n");
                        break;
                    case '\r':
                        builder.Append("\\r");
                        break;
                    case '\t':
                        builder.Append("\\t");
                        break;
                    default:
                        if (character < ' ')
                        {
                            builder.Append("\\u00");
                            builder.Append(HexDigits[(character >> 4) & 0xF]);
                            builder.Append(HexDigits[character & 0xF]);
                        }
                        else
                        {
                            builder.Append(character);
                        }

                        break;
                }
            }

            builder.Append('"');
        }
    }
}
