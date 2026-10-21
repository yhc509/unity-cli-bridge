#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace UnityCli.Protocol
{
    /// <summary>A frame that cannot be decoded. The stream is out of sync afterwards and must be closed.</summary>
    public sealed class RoslynHostFrameException : Exception
    {
        public RoslynHostFrameException(string message)
            : base(message)
        {
        }

        public RoslynHostFrameException(string message, Exception inner)
            : base(message, inner)
        {
        }
    }

    /// <summary>Upper bounds for one message. A length prefix above a bound is rejected before any allocation.</summary>
    public sealed class RoslynHostFrameLimits
    {
        public const int DefaultMaxHeaderBytes = 16 * 1024 * 1024;
        public const int DefaultMaxPayloadBytes = 64 * 1024 * 1024;

        public static readonly RoslynHostFrameLimits Default = new RoslynHostFrameLimits(DefaultMaxHeaderBytes, DefaultMaxPayloadBytes);

        public RoslynHostFrameLimits(int maxHeaderBytes, int maxPayloadBytes)
        {
            if (maxHeaderBytes <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maxHeaderBytes));
            }

            if (maxPayloadBytes < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maxPayloadBytes));
            }

            MaxHeaderBytes = maxHeaderBytes;
            MaxPayloadBytes = maxPayloadBytes;
        }

        public int MaxHeaderBytes { get; }

        public int MaxPayloadBytes { get; }
    }

    /// <summary>One decoded message: a JSON object header (always has <c>type</c>) and an optional binary payload.</summary>
    public sealed class RoslynHostMessage
    {
        public RoslynHostMessage(string type, Dictionary<string, object?>? header = null, byte[]? payload = null)
        {
            if (string.IsNullOrEmpty(type))
            {
                throw new ArgumentException("Message type is required.", nameof(type));
            }

            Header = header ?? new Dictionary<string, object?>(StringComparer.Ordinal);
            Header[RoslynHostFrameCodec.TypeKey] = type;
            Payload = payload;
        }

        public string Type => (string)Header[RoslynHostFrameCodec.TypeKey]!;

        public Dictionary<string, object?> Header { get; }

        public byte[]? Payload { get; }
    }

    /// <summary>
    /// Length-prefixed framing for the editor ↔ Roslyn host pipe (stdin/stdout of the host process).
    /// A message is <c>[u32 LE length][UTF-8 JSON header]</c>, optionally followed by
    /// <c>[u32 LE length][binary payload]</c>. The header announces a payload with
    /// <c>payloadBytes</c>; the second prefix must repeat that length. Callers use the raw streams
    /// (<c>Process.StandardInput.BaseStream</c> and so on) so no text encoding or newline translation
    /// touches the bytes.
    ///
    /// Compiled from this folder into the CLI, the editor bridge, and the on-demand host build
    /// (<see cref="RoslynHostBuilder.HostSourceRelativePaths"/>), so both ends share one codec.
    /// </summary>
    public static class RoslynHostFrameCodec
    {
        public const string TypeKey = "type";
        public const string PayloadBytesKey = "payloadBytes";

        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);

        public static void WriteMessage(Stream output, RoslynHostMessage message, RoslynHostFrameLimits? limits = null)
        {
            if (output == null)
            {
                throw new ArgumentNullException(nameof(output));
            }

            if (message == null)
            {
                throw new ArgumentNullException(nameof(message));
            }

            RoslynHostFrameLimits bounds = limits ?? RoslynHostFrameLimits.Default;
            byte[]? payload = message.Payload;
            var header = new Dictionary<string, object?>(message.Header, StringComparer.Ordinal);
            if (payload != null)
            {
                header[PayloadBytesKey] = payload.Length;
            }
            else
            {
                header.Remove(PayloadBytesKey);
            }

            byte[] headerBytes = StrictUtf8.GetBytes(RoslynHostJson.Serialize(header));
            if (headerBytes.Length > bounds.MaxHeaderBytes)
            {
                throw new RoslynHostFrameException(
                    "Header of '" + message.Type + "' is " + headerBytes.Length + " bytes; the limit is " + bounds.MaxHeaderBytes + ".");
            }

            if (payload != null && payload.Length > bounds.MaxPayloadBytes)
            {
                throw new RoslynHostFrameException(
                    "Payload of '" + message.Type + "' is " + payload.Length + " bytes; the limit is " + bounds.MaxPayloadBytes + ".");
            }

            int total = 4 + headerBytes.Length + (payload == null ? 0 : 4 + payload.Length);
            var buffer = new byte[total];
            WriteLength(buffer, 0, headerBytes.Length);
            Buffer.BlockCopy(headerBytes, 0, buffer, 4, headerBytes.Length);
            if (payload != null)
            {
                int offset = 4 + headerBytes.Length;
                WriteLength(buffer, offset, payload.Length);
                Buffer.BlockCopy(payload, 0, buffer, offset + 4, payload.Length);
            }

            // One write per message so a reader never sees a header without its payload.
            output.Write(buffer, 0, buffer.Length);
            output.Flush();
        }

        /// <summary>
        /// Reads one message. Returns null on a clean end of stream (EOF exactly at a message
        /// boundary). Throws <see cref="RoslynHostFrameException"/> for a truncated, oversized, or
        /// malformed frame.
        /// </summary>
        public static RoslynHostMessage? ReadMessage(Stream input, RoslynHostFrameLimits? limits = null)
        {
            if (input == null)
            {
                throw new ArgumentNullException(nameof(input));
            }

            RoslynHostFrameLimits bounds = limits ?? RoslynHostFrameLimits.Default;
            var prefix = new byte[4];
            int read = ReadUpTo(input, prefix, 0, 4);
            if (read == 0)
            {
                return null;
            }

            if (read < 4)
            {
                throw new RoslynHostFrameException("Stream ended inside a header length prefix (" + read + " of 4 bytes).");
            }

            uint headerLength = ReadLength(prefix);
            if (headerLength == 0)
            {
                throw new RoslynHostFrameException("Header frame is empty.");
            }

            if (headerLength > (uint)bounds.MaxHeaderBytes)
            {
                throw new RoslynHostFrameException(
                    "Header frame announces " + headerLength + " bytes; the limit is " + bounds.MaxHeaderBytes + ".");
            }

            byte[] headerBytes = ReadExactly(input, (int)headerLength, "header");
            string json;
            try
            {
                json = StrictUtf8.GetString(headerBytes);
            }
            catch (ArgumentException ex)
            {
                throw new RoslynHostFrameException("Header frame is not valid UTF-8.", ex);
            }

            object? parsed;
            try
            {
                parsed = RoslynHostJson.Parse(json);
            }
            catch (FormatException ex)
            {
                throw new RoslynHostFrameException("Header frame is not valid JSON: " + ex.Message, ex);
            }

            if (!(parsed is Dictionary<string, object?> header))
            {
                throw new RoslynHostFrameException("Header frame must be a JSON object.");
            }

            string? type = RoslynHostJson.GetString(header, TypeKey);
            if (string.IsNullOrEmpty(type))
            {
                throw new RoslynHostFrameException("Header frame has no 'type' string.");
            }

            byte[]? payload = null;
            if (header.TryGetValue(PayloadBytesKey, out object? announcedValue))
            {
                if (!(announcedValue is double announced) || Math.Floor(announced) != announced || announced < 0)
                {
                    throw new RoslynHostFrameException("'" + PayloadBytesKey + "' must be a non-negative integer.");
                }

                if (announced > bounds.MaxPayloadBytes)
                {
                    throw new RoslynHostFrameException(
                        "Payload of '" + type + "' announces " + announced + " bytes; the limit is " + bounds.MaxPayloadBytes + ".");
                }

                read = ReadUpTo(input, prefix, 0, 4);
                if (read < 4)
                {
                    throw new RoslynHostFrameException("Stream ended before the payload frame of '" + type + "'.");
                }

                uint payloadLength = ReadLength(prefix);
                if (payloadLength != (uint)announced)
                {
                    throw new RoslynHostFrameException(
                        "Payload frame of '" + type + "' is " + payloadLength + " bytes but the header announced " + announced + ".");
                }

                payload = ReadExactly(input, (int)payloadLength, "payload");
            }

            return new RoslynHostMessage(type!, header, payload);
        }

        private static byte[] ReadExactly(Stream input, int length, string what)
        {
            var buffer = new byte[length];
            int read = ReadUpTo(input, buffer, 0, length);
            if (read < length)
            {
                throw new RoslynHostFrameException(
                    "Stream ended inside a " + what + " frame (" + read + " of " + length + " bytes).");
            }

            return buffer;
        }

        private static int ReadUpTo(Stream input, byte[] buffer, int offset, int count)
        {
            int total = 0;
            while (total < count)
            {
                int read = input.Read(buffer, offset + total, count - total);
                if (read <= 0)
                {
                    break;
                }

                total += read;
            }

            return total;
        }

        private static void WriteLength(byte[] buffer, int offset, int length)
        {
            uint value = (uint)length;
            buffer[offset] = (byte)value;
            buffer[offset + 1] = (byte)(value >> 8);
            buffer[offset + 2] = (byte)(value >> 16);
            buffer[offset + 3] = (byte)(value >> 24);
        }

        private static uint ReadLength(byte[] prefix)
        {
            return prefix[0] | ((uint)prefix[1] << 8) | ((uint)prefix[2] << 16) | ((uint)prefix[3] << 24);
        }
    }
}
