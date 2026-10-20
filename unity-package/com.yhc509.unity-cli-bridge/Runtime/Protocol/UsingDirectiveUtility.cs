#nullable enable
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace UnityCli.Protocol
{
    public static class UsingDirectiveUtility
    {
        // Note: generic type aliases (e.g. using Dict = Dictionary<string, int>) are not matched.
        // The directive group excludes a trailing // comment so it still dedupes against the wrapper usings.
        private static readonly Regex UsingDirectiveLineRegex = new Regex(
            @"^[ \t]*(?<directive>(?:global[ \t]+)?using[ \t]+(?:(?:[A-Za-z_][\w]*[ \t]*=[ \t]*(?:global::)?[A-Za-z_][\w]*(?:\.[A-Za-z_][\w]*)*)|(?:(?:static[ \t]+)?(?:global::)?[A-Za-z_][\w]*(?:\.[A-Za-z_][\w]*)*))[ \t]*;)[ \t]*(?://[^\r\n]*)?(?:\r?\n|$)",
            RegexOptions.Multiline | RegexOptions.CultureInvariant);

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
            string stripped = UsingDirectiveLineRegex.Replace(source, delegate(Match match)
            {
                extractedUsings.Add(match.Groups["directive"].Value.Trim());
                return string.Empty;
            });

            usings = extractedUsings.ToArray();
            return stripped.TrimStart('\r', '\n');
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
    /// Unity 2021.3 and Unity 6 both default to C# 9.0 unless the project opts up
    /// in <c>Assets/csc.rsp</c> or PlayerSettings additional compiler arguments.
    /// CodeDOM itself otherwise stays on C# 5, which rejects <c>default</c> literals.
    /// </summary>
    public static class CSharpLanguageVersion
    {
        public const string UnityDefault = "9.0";

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

        public static string ToCompilerOption(string languageVersion)
        {
            return "/langversion:" + languageVersion;
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
}
