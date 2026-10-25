#nullable enable
using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace UnityCli.Protocol
{
    public static class UsingDirectiveUtility
    {
        // Note: generic type aliases (e.g. using Dict = Dictionary<string, int>) are not matched.
        // The directive group excludes a trailing // comment so it still dedupes against the wrapper usings.
        private const string UsingDirectivePattern =
            @"(?<directive>(?:global[ \t]+)?using[ \t]+(?:(?:[A-Za-z_][\w]*[ \t]*=[ \t]*(?:global::)?[A-Za-z_][\w]*(?:\.[A-Za-z_][\w]*)*)|(?:(?:static[ \t]+)?(?:global::)?[A-Za-z_][\w]*(?:\.[A-Za-z_][\w]*)*))[ \t]*;)";

        private static readonly Regex UsingDirectiveLineRegex = new Regex(
            @"^[ \t]*" + UsingDirectivePattern + @"[ \t]*(?://[^\r\n]*)?(?:\r?\n|$)",
            RegexOptions.Multiline | RegexOptions.CultureInvariant);

        // A leading using may share the line with the statement (`using UnityEngine; return 1;`).
        // Only the start of the remaining source is considered, so a using later in the body stays put.
        private static readonly Regex LeadingInlineUsingRegex = new Regex(
            @"^[ \t]*" + UsingDirectivePattern + @"[ \t]*(?://[^\r\n]*)?",
            RegexOptions.CultureInvariant);

        public static string StripUsingDirectives(string source, out string[] usings)
        {
            if (string.IsNullOrEmpty(source))
            {
                usings = Array.Empty<string>();
                return source ?? string.Empty;
            }

            if (source[0] == '\uFEFF')
            {
                source = source.Substring(1);
            }

            var extractedUsings = new List<string>();
            source = StripLeadingInlineUsings(source, extractedUsings);
            string stripped = UsingDirectiveLineRegex.Replace(source, delegate(Match match)
            {
                extractedUsings.Add(match.Groups["directive"].Value.Trim());
                return string.Empty;
            });

            usings = extractedUsings.ToArray();
            return stripped.TrimStart('\r', '\n');
        }

        private static string StripLeadingInlineUsings(string source, List<string> extractedUsings)
        {
            while (source.Length > 0)
            {
                Match match = LeadingInlineUsingRegex.Match(source);
                if (!match.Success || match.Index != 0)
                {
                    break;
                }

                extractedUsings.Add(match.Groups["directive"].Value.Trim());
                source = source.Substring(match.Length);
            }

            return source;
        }

        private static readonly Regex AliasNameRegex = new Regex(
            @"^using[ \t]+([A-Za-z_][\w]*)[ \t]*=",
            RegexOptions.CultureInvariant);

        /// <summary>
        /// Drops template alias directives (<c>using X = ...;</c>) whose alias name the user code
        /// also declares with a different target, so the user's alias wins instead of colliding.
        /// Identical directives are kept (and de-duplicated later by the caller).
        /// </summary>
        public static string[] RemoveShadowedAliases(string[] templateUsings, string[] userUsings)
        {
            var userAliases = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string directive in userUsings)
            {
                Match match = AliasNameRegex.Match(directive.Trim());
                if (match.Success)
                {
                    userAliases[match.Groups[1].Value] = directive.Trim();
                }
            }

            var kept = new List<string>(templateUsings.Length);
            foreach (string directive in templateUsings)
            {
                Match match = AliasNameRegex.Match(directive.Trim());
                if (match.Success
                    && userAliases.TryGetValue(match.Groups[1].Value, out string? userDirective)
                    && !string.Equals(userDirective, directive.Trim(), StringComparison.Ordinal))
                {
                    continue;
                }

                kept.Add(directive);
            }

            return kept.ToArray();
        }
    }

    /// <summary>
    /// Language version passed to the <c>execute</c> CodeDOM compile.
    /// Unity 2021.3 and Unity 6 compile project scripts as C# 9.0 unless
    /// <c>Assets/csc.rsp</c> or PlayerSettings say otherwise, but the CodeDOM
    /// host is Mono mcs. mcs rejects <c>/langversion:9.0</c> (CS1617) and only
    /// accepts ISO-1, ISO-2, Default, Latest, or a number from 1 through 7.2.
    /// With no option it stays on C# 5 and rejects <c>default</c> literals.
    /// <see cref="ToCompilerOption"/> maps anything mcs cannot express to
    /// <c>latest</c>, which compiles those literals on 2021.3 and Unity 6.
    /// </summary>
    public static class CSharpLanguageVersion
    {
        public const string UnityDefault = "9.0";
        public const string McsLatest = "latest";

        public static string Resolve(string? responseFileText, IReadOnlyList<string>? additionalCompilerArguments)
        {
            string version = UnityDefault;
            ApplyText(ref version, responseFileText);
            if (additionalCompilerArguments != null)
            {
                for (int index = 0; index < additionalCompilerArguments.Count; index++)
                {
                    ApplyText(ref version, additionalCompilerArguments[index]);
                }
            }

            return version;
        }

        /// <summary>
        /// Compiler option for Unity's bundled mcs. Versions above 7.2 and Roslyn-only
        /// tokens (<c>9.0</c>, <c>preview</c>, the C# 9 project default) become
        /// <c>/langversion:latest</c>. 1 through 7.2 are passed through.
        /// </summary>
        public static string ToCompilerOption(string? languageVersion)
        {
            return "/langversion:" + ToMcsLanguageVersion(languageVersion);
        }

        public static string ToMcsLanguageVersion(string? languageVersion)
        {
            if (string.IsNullOrWhiteSpace(languageVersion))
            {
                return McsLatest;
            }

            string value = languageVersion.Trim();
            if (value.Equals("latest", StringComparison.OrdinalIgnoreCase))
            {
                return McsLatest;
            }

            if (value.Equals("default", StringComparison.OrdinalIgnoreCase))
            {
                return "Default";
            }

            if (value.Equals("ISO-1", StringComparison.OrdinalIgnoreCase))
            {
                return "ISO-1";
            }

            if (value.Equals("ISO-2", StringComparison.OrdinalIgnoreCase))
            {
                return "ISO-2";
            }

            if (!TryParseMcsNumeric(value, out int major, out int minor))
            {
                return McsLatest;
            }

            if (major < 1 || major > 7 || (major == 7 && minor > 2))
            {
                return McsLatest;
            }

            return minor == 0 ? major.ToString() : major + "." + minor;
        }

        /// <summary>
        /// Builds a compile-failure message that includes mcs error text when the
        /// compiler reported any, then stdout, then <paramref name="fallback"/>.
        /// The result is never empty.
        /// </summary>
        public static string FormatCompilerFailure(
            string? compilerOptions,
            IReadOnlyList<string>? errorLines,
            IReadOnlyList<string>? outputLines,
            string? fallback)
        {
            var builder = new StringBuilder();
            builder.Append("Code compilation failed");
            if (!string.IsNullOrWhiteSpace(compilerOptions))
            {
                builder.Append(" (");
                builder.Append(compilerOptions.Trim());
                builder.Append(')');
            }

            builder.Append(':');
            bool wroteDetail = AppendDetailLines(builder, errorLines);
            if (!wroteDetail)
            {
                wroteDetail = AppendDetailLines(builder, outputLines);
            }

            if (!wroteDetail && !string.IsNullOrWhiteSpace(fallback))
            {
                builder.Append('\n');
                builder.Append(fallback.Trim());
                wroteDetail = true;
            }

            if (!wroteDetail)
            {
                builder.Append("\nCompiler failed to produce the assembly.");
            }

            return builder.ToString().TrimEnd();
        }

        private static bool AppendDetailLines(StringBuilder builder, IReadOnlyList<string>? lines)
        {
            if (lines == null || lines.Count == 0)
            {
                return false;
            }

            bool wrote = false;
            for (int index = 0; index < lines.Count; index++)
            {
                string line = lines[index];
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                builder.Append('\n');
                builder.Append(line.Trim());
                wrote = true;
            }

            return wrote;
        }

        private static bool TryParseMcsNumeric(string value, out int major, out int minor)
        {
            major = 0;
            minor = 0;
            int dot = value.IndexOf('.');
            string majorText = dot < 0 ? value : value.Substring(0, dot);
            if (!TryParseDigits(majorText, out major))
            {
                return false;
            }

            if (dot < 0)
            {
                return true;
            }

            string minorText = value.Substring(dot + 1);
            if (minorText.Length == 0 || minorText.IndexOf('.') >= 0)
            {
                return false;
            }

            return TryParseDigits(minorText, out minor);
        }

        private static bool TryParseDigits(string text, out int number)
        {
            number = 0;
            if (text.Length == 0 || text.Length > 9)
            {
                return false;
            }

            for (int index = 0; index < text.Length; index++)
            {
                char character = text[index];
                if (character < '0' || character > '9')
                {
                    return false;
                }

                number = (number * 10) + (character - '0');
            }

            return true;
        }

        public static bool TryParseLangVersionToken(string? token, out string languageVersion)
        {
            languageVersion = string.Empty;
            if (string.IsNullOrEmpty(token))
            {
                return false;
            }

            string? prefix = null;
            if (token.StartsWith("-langversion:", StringComparison.OrdinalIgnoreCase))
            {
                prefix = "-langversion:";
            }
            else if (token.StartsWith("/langversion:", StringComparison.OrdinalIgnoreCase))
            {
                prefix = "/langversion:";
            }

            if (prefix == null)
            {
                return false;
            }

            string value = token.Substring(prefix.Length).Trim();
            if (!IsSafeLanguageVersion(value))
            {
                return false;
            }

            languageVersion = value;
            return true;
        }

        private static void ApplyText(ref string version, string? text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            int index = 0;
            while (index < text.Length)
            {
                while (index < text.Length && char.IsWhiteSpace(text[index]))
                {
                    index++;
                }

                if (index >= text.Length)
                {
                    break;
                }

                if (text[index] == '/' && index + 1 < text.Length && text[index + 1] == '/')
                {
                    while (index < text.Length && text[index] != '\n')
                    {
                        index++;
                    }

                    continue;
                }

                int start = index;
                while (index < text.Length && !char.IsWhiteSpace(text[index]))
                {
                    index++;
                }

                string token = text.Substring(start, index - start);
                if (TryParseLangVersionToken(token, out string parsed))
                {
                    version = parsed;
                }
            }
        }

        private static bool IsSafeLanguageVersion(string value)
        {
            if (value.Length == 0)
            {
                return false;
            }

            for (int index = 0; index < value.Length; index++)
            {
                char character = value[index];
                bool ok = char.IsLetterOrDigit(character)
                    || character == '.'
                    || character == '_'
                    || character == '+'
                    || character == '-';
                if (!ok)
                {
                    return false;
                }
            }

            return true;
        }
    }

    /// <summary>
    /// Compiler defines for <c>execute</c> snippets. Always includes UNITY_ASSERTIONS so
    /// Debug.Assert and Debug.LogAssertion are not compiled out, then the Editor's active
    /// scripting define symbols. Invalid tokens are dropped so one bad symbol cannot
    /// break the compiler option.
    /// </summary>
    public static class ExecuteCompilerDefines
    {
        public const string Assertions = "UNITY_ASSERTIONS";

        public static string BuildDefineOption(IReadOnlyList<string>? activeDefines)
        {
            var symbols = new List<string> { Assertions };
            var seen = new HashSet<string>(StringComparer.Ordinal) { Assertions };
            if (activeDefines != null)
            {
                for (int index = 0; index < activeDefines.Count; index++)
                {
                    string? raw = activeDefines[index];
                    if (string.IsNullOrWhiteSpace(raw))
                    {
                        continue;
                    }

                    string symbol = raw.Trim();
                    if (!IsIdentifier(symbol) || !seen.Add(symbol))
                    {
                        continue;
                    }

                    symbols.Add(symbol);
                }
            }

            return "/define:" + string.Join(";", symbols);
        }

        private static bool IsIdentifier(string value)
        {
            if (value.Length == 0 || !(char.IsLetter(value[0]) || value[0] == '_'))
            {
                return false;
            }

            for (int index = 1; index < value.Length; index++)
            {
                char character = value[index];
                if (!(char.IsLetterOrDigit(character) || character == '_'))
                {
                    return false;
                }
            }

            return true;
        }
    }
}
