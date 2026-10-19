#nullable enable
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace UnityCli.Protocol
{
    public static class UsingDirectiveUtility
    {
        // Note: generic type aliases (e.g. using Dict = Dictionary<string, int>) are not matched.
        private static readonly Regex UsingDirectiveLineRegex = new Regex(
            @"^[ \t]*using[ \t]+(?:(?:[A-Za-z_][\w]*[ \t]*=[ \t]*(?:global::)?[A-Za-z_][\w]*(?:\.[A-Za-z_][\w]*)*)|(?:(?:static[ \t]+)?(?:global::)?[A-Za-z_][\w]*(?:\.[A-Za-z_][\w]*)*))[ \t]*;[ \t]*(?:\r?\n|$)",
            RegexOptions.Multiline | RegexOptions.CultureInvariant);

        public static string StripUsingDirectives(string source, out string[] usings)
        {
            var extractedUsings = new List<string>();
            string stripped = UsingDirectiveLineRegex.Replace(source, delegate(Match match)
            {
                extractedUsings.Add(match.Value.Trim());
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
}
