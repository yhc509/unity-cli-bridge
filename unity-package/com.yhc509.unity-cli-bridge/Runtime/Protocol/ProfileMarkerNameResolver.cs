#nullable enable
using System;
using System.Collections.Generic;
using System.Text;

namespace UnityCli.Protocol
{
    public enum ProfileMarkerMatchKind
    {
        Exact,
        Resolved,
        Ambiguous,
        NotFound,
    }

    /// <summary>Outcome of matching one user-supplied marker name against a list of profiler marker names.</summary>
    public sealed class ProfileMarkerResolution
    {
        public ProfileMarkerMatchKind Kind;
        public string Query = string.Empty;

        /// <summary>The full marker name for <see cref="ProfileMarkerMatchKind.Exact"/> and <see cref="ProfileMarkerMatchKind.Resolved"/>.</summary>
        public string? Marker;

        /// <summary>Every matching full name, ordinal order, for <see cref="ProfileMarkerMatchKind.Ambiguous"/>.</summary>
        public string[] Candidates = Array.Empty<string>();

        /// <summary>Up to five similar names for <see cref="ProfileMarkerMatchKind.NotFound"/>.</summary>
        public string[] Similar = Array.Empty<string>();

        public bool IsMatch
        {
            get { return Kind == ProfileMarkerMatchKind.Exact || Kind == ProfileMarkerMatchKind.Resolved; }
        }

        public string ErrorCode
        {
            get
            {
                return Kind == ProfileMarkerMatchKind.Ambiguous
                    ? ProtocolConstants.ErrorProfileMarkerAmbiguous
                    : ProtocolConstants.ErrorProfileNotFound;
            }
        }

        /// <param name="markerNoun">What the names are, for the ambiguous case (e.g. "capture marker").</param>
        /// <param name="source">Where the names came from, for the not-found case (e.g. "the capture").</param>
        public string BuildMessage(string markerNoun, string source)
        {
            var message = new StringBuilder();
            if (Kind == ProfileMarkerMatchKind.Ambiguous)
            {
                message.Append("Marker `").Append(Query).Append("` matches more than one ").Append(markerNoun).Append(". Pass one of these names:");
                foreach (string candidate in Candidates)
                {
                    message.Append('\n').Append(candidate);
                }

                return message.ToString();
            }

            message.Append("Marker `").Append(Query).Append("` is not in ").Append(source).Append('.');
            if (Similar.Length == 0)
            {
                message.Append(' ').Append(char.ToUpperInvariant(source[0])).Append(source.Substring(1)).Append(" has no markers.");
            }
            else
            {
                message.Append(" Similar names:");
                foreach (string name in Similar)
                {
                    message.Append('\n').Append(name);
                }
            }

            return message.ToString();
        }
    }

    /// <summary>
    /// Matches a marker name the way an agent types it against real profiler marker names.
    /// An exact name always wins. Otherwise names are compared after <see cref="Normalize"/>,
    /// so <c>ThreatField.Update</c> finds <c>Assembly-CSharp.dll!::ThreatField.Update() [Invoke]</c>.
    /// Shared by <c>profile analyze --marker</c> (sidecar names) and <c>profile stats --marker</c> (registered names).
    /// </summary>
    public static class ProfileMarkerNameResolver
    {
        public const int SimilarNameLimit = 5;

        /// <summary>
        /// Marker name with the Unity profiler decoration removed: an <c>Assembly.dll!Namespace::</c> prefix,
        /// a trailing <c>() [Invoke]</c>, and empty parentheses. Used for short-name matching only.
        /// </summary>
        public static string Normalize(string? name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return string.Empty;
            }

            string value = name!.Trim();
            int bang = value.IndexOf('!');
            if (bang >= 0)
            {
                value = value.Substring(bang + 1);
            }

            int scope = value.LastIndexOf("::", StringComparison.Ordinal);
            if (scope >= 0)
            {
                value = value.Substring(scope + 2);
            }

            const string invokeSuffix = " [Invoke]";
            if (value.EndsWith(invokeSuffix, StringComparison.Ordinal))
            {
                value = value.Substring(0, value.Length - invokeSuffix.Length);
            }

            value = value.Replace("()", string.Empty);
            return value.Trim();
        }

        public static ProfileMarkerResolution Resolve(IEnumerable<string> names, string query)
        {
            var distinct = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (string name in names)
            {
                if (!string.IsNullOrEmpty(name) && seen.Add(name))
                {
                    distinct.Add(name);
                }
            }

            var resolution = new ProfileMarkerResolution { Query = query ?? string.Empty };
            if (seen.Contains(resolution.Query))
            {
                resolution.Kind = ProfileMarkerMatchKind.Exact;
                resolution.Marker = resolution.Query;
                return resolution;
            }

            string normalizedQuery = Normalize(resolution.Query);
            var matches = new List<string>();
            if (normalizedQuery.Length > 0)
            {
                foreach (string name in distinct)
                {
                    if (Matches(Normalize(name), normalizedQuery))
                    {
                        matches.Add(name);
                    }
                }
            }

            matches.Sort(StringComparer.Ordinal);
            if (matches.Count == 1)
            {
                resolution.Kind = ProfileMarkerMatchKind.Resolved;
                resolution.Marker = matches[0];
                return resolution;
            }

            if (matches.Count > 1)
            {
                resolution.Kind = ProfileMarkerMatchKind.Ambiguous;
                resolution.Candidates = matches.ToArray();
                return resolution;
            }

            resolution.Kind = ProfileMarkerMatchKind.NotFound;
            resolution.Similar = SimilarNames(distinct, normalizedQuery);
            return resolution;
        }

        /// <summary>Exact normalized name, or the marker's <c>Type.Method</c> ends with the query.</summary>
        private static bool Matches(string markerNorm, string queryNorm)
        {
            if (markerNorm.Length == 0 || queryNorm.Length == 0)
            {
                return false;
            }

            if (string.Equals(markerNorm, queryNorm, StringComparison.Ordinal))
            {
                return true;
            }

            return markerNorm.EndsWith("." + queryNorm, StringComparison.Ordinal);
        }

        private static string[] SimilarNames(List<string> names, string queryNorm)
        {
            var scored = new List<KeyValuePair<string, int>>(names.Count);
            foreach (string name in names)
            {
                scored.Add(new KeyValuePair<string, int>(name, Similarity(queryNorm, Normalize(name))));
            }

            scored.Sort((left, right) =>
            {
                int byScore = right.Value.CompareTo(left.Value);
                return byScore != 0 ? byScore : string.CompareOrdinal(left.Key, right.Key);
            });

            int count = Math.Min(SimilarNameLimit, scored.Count);
            var result = new string[count];
            for (int index = 0; index < count; index++)
            {
                result[index] = scored[index].Key;
            }

            return result;
        }

        private static int Similarity(string query, string candidate)
        {
            if (query.Length == 0 || candidate.Length == 0)
            {
                return int.MinValue / 4;
            }

            int prefix = CommonRun(query, candidate, false);
            int suffix = CommonRun(query, candidate, true);
            int distance = Levenshtein(query, candidate);
            return (prefix * 4) + (suffix * 2) - distance;
        }

        private static int CommonRun(string left, string right, bool fromEnd)
        {
            int limit = Math.Min(left.Length, right.Length);
            int count = 0;
            while (count < limit)
            {
                char a = fromEnd ? left[left.Length - 1 - count] : left[count];
                char b = fromEnd ? right[right.Length - 1 - count] : right[count];
                if (a != b)
                {
                    break;
                }

                count++;
            }

            return count;
        }

        private static int Levenshtein(string left, string right)
        {
            if (left.Length == 0)
            {
                return right.Length;
            }

            if (right.Length == 0)
            {
                return left.Length;
            }

            var previous = new int[right.Length + 1];
            var current = new int[right.Length + 1];
            for (int column = 0; column <= right.Length; column++)
            {
                previous[column] = column;
            }

            for (int row = 1; row <= left.Length; row++)
            {
                current[0] = row;
                for (int column = 1; column <= right.Length; column++)
                {
                    int cost = left[row - 1] == right[column - 1] ? 0 : 1;
                    current[column] = Math.Min(
                        Math.Min(current[column - 1] + 1, previous[column] + 1),
                        previous[column - 1] + cost);
                }

                int[] swap = previous;
                previous = current;
                current = swap;
            }

            return previous[right.Length];
        }
    }
}
