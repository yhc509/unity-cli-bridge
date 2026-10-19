#nullable enable
using System;

namespace UnityCliBridge.Bridge.Editor
{
    internal static class InspectorPathParserUtility
    {
        internal static string RequireNodeName(string? name, string commandName, string errorPrefix)
        {
            string normalized = string.IsNullOrWhiteSpace(name) ? string.Empty : name.Trim();
            if (string.IsNullOrWhiteSpace(normalized))
            {
                throw new CommandFailureException(errorPrefix + "_NODE_INVALID", commandName + " node name is empty.");
            }

            return normalized;
        }

        internal static bool IsRootTraversalSentinel(string? path)
        {
            string normalized = path == null ? string.Empty : path.Trim();
            return normalized.Length == 0 || string.Equals(normalized, "/", StringComparison.Ordinal);
        }

        internal static (string name, int index) ParsePathSegment(string segment, string commandName, string errorPrefix)
        {
            ParsePathSegment(segment.AsSpan(), commandName, errorPrefix, out int nameLength, out int index);
            string name = nameLength == segment.Length
                ? segment
                : segment.Substring(0, nameLength);
            return (name, index);
        }

        internal static void ParsePathSegment(ReadOnlySpan<char> segment, string commandName, string errorPrefix, out int nameLength, out int index)
        {
            int bracketIndex = segment.LastIndexOf('[');
            if (bracketIndex > 0 && segment[segment.Length - 1] == ']')
            {
                string indexText = segment.Slice(bracketIndex + 1, segment.Length - bracketIndex - 2).ToString();
                if (int.TryParse(indexText, out index))
                {
                    nameLength = bracketIndex;
                    return;
                }
            }

            if (IsWhiteSpace(segment))
            {
                throw new CommandFailureException(errorPrefix + "_NODE_INVALID", commandName + " path segment is empty.");
            }

            nameLength = segment.Length;
            index = 0;
        }

        internal static bool TryGetNextPathSegment(string path, ref int position, out int segmentStart, out int segmentLength)
        {
            int pathLength = path.Length;
            while (position < pathLength && path[position] == '/')
            {
                position++;
            }

            if (position >= pathLength)
            {
                segmentStart = 0;
                segmentLength = 0;
                return false;
            }

            segmentStart = position;
            while (position < pathLength && path[position] != '/')
            {
                position++;
            }

            segmentLength = position - segmentStart;
            return true;
        }

        private static bool IsWhiteSpace(ReadOnlySpan<char> value)
        {
            for (int index = 0; index < value.Length; index++)
            {
                if (!char.IsWhiteSpace(value[index]))
                {
                    return false;
                }
            }

            return true;
        }
    }
}
