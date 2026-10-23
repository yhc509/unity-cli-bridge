#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace UnityCli.Protocol
{
    /// <summary>
    /// Minimal JSON reader/writer for Roslyn host frame headers. Objects parse to
    /// <c>Dictionary&lt;string, object?&gt;</c>, arrays to <c>List&lt;object?&gt;</c>, numbers to
    /// <see cref="double"/>. Hand-rolled because the same source compiles into the host (bundled .NET,
    /// no reflection serializer warm-up wanted on the cold path) and into the editor (Mono, no
    /// System.Text.Json), so one implementation pins the wire format for both sides.
    /// </summary>
    public static class RoslynHostJson
    {
        public const int MaxDepth = 32;

        public static object? Parse(string json)
        {
            if (json == null)
            {
                throw new ArgumentNullException(nameof(json));
            }

            var parser = new Parser(json);
            parser.SkipWhitespace();
            object? value = parser.ReadValue(0);
            parser.SkipWhitespace();
            if (!parser.AtEnd)
            {
                throw parser.Error("unexpected trailing characters");
            }

            return value;
        }

        public static string Serialize(object? value)
        {
            var builder = new StringBuilder();
            WriteValue(builder, value, 0);
            return builder.ToString();
        }

        public static string? GetString(IDictionary<string, object?> obj, string key)
        {
            return obj.TryGetValue(key, out object? value) ? value as string : null;
        }

        public static bool GetBool(IDictionary<string, object?> obj, string key, bool fallback = false)
        {
            return obj.TryGetValue(key, out object? value) && value is bool flag ? flag : fallback;
        }

        public static double GetDouble(IDictionary<string, object?> obj, string key, double fallback = 0)
        {
            return obj.TryGetValue(key, out object? value) && value is double number ? number : fallback;
        }

        /// <summary>Integral JSON number in [<paramref name="min"/>, <paramref name="max"/>]; otherwise the fallback.</summary>
        public static long GetInt64(IDictionary<string, object?> obj, string key, long fallback = 0, long min = long.MinValue, long max = long.MaxValue)
        {
            if (!obj.TryGetValue(key, out object? value) || !(value is double number))
            {
                return fallback;
            }

            if (Math.Floor(number) != number || number < min || number > max)
            {
                return fallback;
            }

            return (long)number;
        }

        public static List<string> GetStringList(IDictionary<string, object?> obj, string key)
        {
            var result = new List<string>();
            if (obj.TryGetValue(key, out object? value) && value is List<object?> items)
            {
                foreach (object? item in items)
                {
                    if (item is string text)
                    {
                        result.Add(text);
                    }
                }
            }

            return result;
        }

        private static void WriteValue(StringBuilder builder, object? value, int depth)
        {
            if (depth > MaxDepth)
            {
                throw new InvalidOperationException("JSON value nests deeper than " + MaxDepth + " levels.");
            }

            switch (value)
            {
                case null:
                    builder.Append("null");
                    return;
                case string text:
                    WriteString(builder, text);
                    return;
                case bool flag:
                    builder.Append(flag ? "true" : "false");
                    return;
                case int number:
                    builder.Append(number.ToString(CultureInfo.InvariantCulture));
                    return;
                case long number:
                    builder.Append(number.ToString(CultureInfo.InvariantCulture));
                    return;
                case double number:
                    WriteDouble(builder, number);
                    return;
                case float number:
                    WriteDouble(builder, number);
                    return;
                case IDictionary<string, object?> obj:
                    builder.Append('{');
                    bool firstProperty = true;
                    foreach (KeyValuePair<string, object?> pair in obj)
                    {
                        if (!firstProperty)
                        {
                            builder.Append(',');
                        }

                        firstProperty = false;
                        WriteString(builder, pair.Key);
                        builder.Append(':');
                        WriteValue(builder, pair.Value, depth + 1);
                    }

                    builder.Append('}');
                    return;
                case IEnumerable items:
                    builder.Append('[');
                    bool firstItem = true;
                    foreach (object? item in items)
                    {
                        if (!firstItem)
                        {
                            builder.Append(',');
                        }

                        firstItem = false;
                        WriteValue(builder, item, depth + 1);
                    }

                    builder.Append(']');
                    return;
                default:
                    throw new InvalidOperationException("Unsupported JSON value type: " + value.GetType().FullName);
            }
        }

        private static void WriteDouble(StringBuilder builder, double number)
        {
            if (double.IsNaN(number) || double.IsInfinity(number))
            {
                builder.Append("null");
                return;
            }

            builder.Append(number.ToString("R", CultureInfo.InvariantCulture));
        }

        private static void WriteString(StringBuilder builder, string text)
        {
            builder.Append('"');
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                switch (c)
                {
                    case '"':
                        builder.Append("\\\"");
                        continue;
                    case '\\':
                        builder.Append("\\\\");
                        continue;
                    case '\n':
                        builder.Append("\\n");
                        continue;
                    case '\r':
                        builder.Append("\\r");
                        continue;
                    case '\t':
                        builder.Append("\\t");
                        continue;
                }

                if (c < 0x20)
                {
                    AppendUnicodeEscape(builder, c);
                    continue;
                }

                if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                {
                    builder.Append(c);
                    builder.Append(text[i + 1]);
                    i++;
                    continue;
                }

                if (char.IsSurrogate(c))
                {
                    // A lone surrogate cannot be encoded as UTF-8; the escape keeps the frame valid.
                    AppendUnicodeEscape(builder, c);
                    continue;
                }

                builder.Append(c);
            }

            builder.Append('"');
        }

        private static void AppendUnicodeEscape(StringBuilder builder, char c)
        {
            builder.Append("\\u");
            builder.Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
        }

        private sealed class Parser
        {
            private readonly string _text;
            private int _position;

            public Parser(string text)
            {
                _text = text;
            }

            public bool AtEnd => _position >= _text.Length;

            public FormatException Error(string message)
            {
                return new FormatException("Invalid JSON at offset " + _position + ": " + message + ".");
            }

            public void SkipWhitespace()
            {
                while (_position < _text.Length)
                {
                    char c = _text[_position];
                    if (c != ' ' && c != '\t' && c != '\n' && c != '\r')
                    {
                        return;
                    }

                    _position++;
                }
            }

            public object? ReadValue(int depth)
            {
                if (depth > MaxDepth)
                {
                    throw Error("nesting deeper than " + MaxDepth + " levels");
                }

                if (AtEnd)
                {
                    throw Error("unexpected end of input");
                }

                char c = _text[_position];
                switch (c)
                {
                    case '{':
                        return ReadObject(depth);
                    case '[':
                        return ReadArray(depth);
                    case '"':
                        return ReadString();
                    case 't':
                        ExpectLiteral("true");
                        return true;
                    case 'f':
                        ExpectLiteral("false");
                        return false;
                    case 'n':
                        ExpectLiteral("null");
                        return null;
                }

                if (c == '-' || (c >= '0' && c <= '9'))
                {
                    return ReadNumber();
                }

                throw Error("unexpected character '" + c + "'");
            }

            private Dictionary<string, object?> ReadObject(int depth)
            {
                var result = new Dictionary<string, object?>(StringComparer.Ordinal);
                _position++;
                SkipWhitespace();
                if (Peek() == '}')
                {
                    _position++;
                    return result;
                }

                while (true)
                {
                    SkipWhitespace();
                    if (Peek() != '"')
                    {
                        throw Error("expected a property name");
                    }

                    string key = ReadString();
                    SkipWhitespace();
                    Expect(':');
                    SkipWhitespace();
                    object? value = ReadValue(depth + 1);
                    if (result.ContainsKey(key))
                    {
                        throw Error("duplicate property '" + key + "'");
                    }

                    result[key] = value;
                    SkipWhitespace();
                    char next = Peek();
                    _position++;
                    if (next == '}')
                    {
                        return result;
                    }

                    if (next != ',')
                    {
                        _position--;
                        throw Error("expected ',' or '}'");
                    }
                }
            }

            private List<object?> ReadArray(int depth)
            {
                var result = new List<object?>();
                _position++;
                SkipWhitespace();
                if (Peek() == ']')
                {
                    _position++;
                    return result;
                }

                while (true)
                {
                    SkipWhitespace();
                    result.Add(ReadValue(depth + 1));
                    SkipWhitespace();
                    char next = Peek();
                    _position++;
                    if (next == ']')
                    {
                        return result;
                    }

                    if (next != ',')
                    {
                        _position--;
                        throw Error("expected ',' or ']'");
                    }
                }
            }

            private string ReadString()
            {
                _position++;
                var builder = new StringBuilder();
                while (true)
                {
                    if (AtEnd)
                    {
                        throw Error("unterminated string");
                    }

                    char c = _text[_position++];
                    if (c == '"')
                    {
                        return builder.ToString();
                    }

                    if (c < 0x20)
                    {
                        _position--;
                        throw Error("unescaped control character in string");
                    }

                    if (c != '\\')
                    {
                        builder.Append(c);
                        continue;
                    }

                    if (AtEnd)
                    {
                        throw Error("unterminated escape");
                    }

                    char escape = _text[_position++];
                    switch (escape)
                    {
                        case '"':
                            builder.Append('"');
                            break;
                        case '\\':
                            builder.Append('\\');
                            break;
                        case '/':
                            builder.Append('/');
                            break;
                        case 'b':
                            builder.Append('\b');
                            break;
                        case 'f':
                            builder.Append('\f');
                            break;
                        case 'n':
                            builder.Append('\n');
                            break;
                        case 'r':
                            builder.Append('\r');
                            break;
                        case 't':
                            builder.Append('\t');
                            break;
                        case 'u':
                            if (_position + 4 > _text.Length
                                || !int.TryParse(_text.Substring(_position, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out int code))
                            {
                                throw Error("invalid \\u escape");
                            }

                            builder.Append((char)code);
                            _position += 4;
                            break;
                        default:
                            _position--;
                            throw Error("invalid escape '\\" + escape + "'");
                    }
                }
            }

            private double ReadNumber()
            {
                int start = _position;
                if (Peek() == '-')
                {
                    _position++;
                }

                while (!AtEnd)
                {
                    char c = _text[_position];
                    if ((c >= '0' && c <= '9') || c == '.' || c == 'e' || c == 'E' || c == '+' || c == '-')
                    {
                        _position++;
                        continue;
                    }

                    break;
                }

                string token = _text.Substring(start, _position - start);
                if (!double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
                    || double.IsInfinity(value))
                {
                    _position = start;
                    throw Error("invalid number '" + token + "'");
                }

                return value;
            }

            private void ExpectLiteral(string literal)
            {
                if (string.CompareOrdinal(_text, _position, literal, 0, literal.Length) != 0)
                {
                    throw Error("invalid literal");
                }

                _position += literal.Length;
            }

            private void Expect(char expected)
            {
                if (Peek() != expected)
                {
                    throw Error("expected '" + expected + "'");
                }

                _position++;
            }

            private char Peek()
            {
                if (AtEnd)
                {
                    throw Error("unexpected end of input");
                }

                return _text[_position];
            }
        }
    }
}
