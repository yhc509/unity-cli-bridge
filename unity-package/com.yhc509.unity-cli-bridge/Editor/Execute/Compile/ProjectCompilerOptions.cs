#nullable enable
using System;
using System.Collections.Generic;
using System.Text;

namespace UnityCliBridge.Bridge.Editor
{
    /// <summary>
    /// The part of a project's <c>Assets/csc.rsp</c> (or <c>mcs.rsp</c>) that the Roslyn host
    /// applies to <c>execute</c> snippets: <c>-define</c>/<c>-d</c>, <c>-unsafe</c>, and
    /// <c>-nowarn</c>, with either <c>-</c> or <c>/</c>. Everything else is listed in
    /// <see cref="Ignored"/> (shown by doctor) and not applied. The CodeDOM backend does not use this;
    /// it keeps its existing options. Unity-free so the parsing is unit-tested.
    /// </summary>
    internal sealed class ProjectCompilerOptions
    {
        public static readonly ProjectCompilerOptions Empty = new ProjectCompilerOptions(
            Array.Empty<string>(),
            false,
            Array.Empty<string>(),
            Array.Empty<string>());

        private ProjectCompilerOptions(IReadOnlyList<string> defines, bool allowUnsafe, IReadOnlyList<string> noWarn, IReadOnlyList<string> ignored)
        {
            Defines = defines;
            AllowUnsafe = allowUnsafe;
            NoWarn = noWarn;
            Ignored = ignored;
        }

        public IReadOnlyList<string> Defines { get; }

        public bool AllowUnsafe { get; }

        /// <summary>Warning ids as written (<c>0168</c>, <c>CS0168</c>); the host normalizes them.</summary>
        public IReadOnlyList<string> NoWarn { get; }

        /// <summary>Options found in the file that are not applied, as written.</summary>
        public IReadOnlyList<string> Ignored { get; }

        public static ProjectCompilerOptions Parse(string? responseFileText)
        {
            if (string.IsNullOrWhiteSpace(responseFileText))
            {
                return Empty;
            }

            var defines = new List<string>();
            var noWarn = new List<string>();
            var ignored = new List<string>();
            bool allowUnsafe = false;
            foreach (string token in Tokenize(responseFileText!))
            {
                if (token.Length < 2 || (token[0] != '-' && token[0] != '/'))
                {
                    ignored.Add(token);
                    continue;
                }

                string body = token.Substring(1);
                int colon = body.IndexOf(':');
                string name = (colon >= 0 ? body.Substring(0, colon) : body).ToLowerInvariant();
                string value = colon >= 0 ? body.Substring(colon + 1) : string.Empty;
                switch (name)
                {
                    case "define":
                    case "d":
                        AddList(defines, value);
                        break;
                    case "unsafe":
                    case "unsafe+":
                        allowUnsafe = true;
                        break;
                    case "unsafe-":
                        allowUnsafe = false;
                        break;
                    case "nowarn":
                        AddList(noWarn, value);
                        break;
                    default:
                        ignored.Add(token);
                        break;
                }
            }

            return new ProjectCompilerOptions(defines.ToArray(), allowUnsafe, noWarn.ToArray(), ignored.ToArray());
        }

        private static void AddList(List<string> target, string value)
        {
            foreach (string part in value.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string trimmed = part.Trim().Trim('"');
                if (trimmed.Length > 0 && !target.Contains(trimmed))
                {
                    target.Add(trimmed);
                }
            }
        }

        /// <summary>Whitespace-separated tokens; double quotes group (and are dropped); <c>#</c> starts a comment line.</summary>
        private static IEnumerable<string> Tokenize(string text)
        {
            foreach (string rawLine in text.Split('\n'))
            {
                string line = rawLine.Trim();
                if (line.Length == 0 || line[0] == '#')
                {
                    continue;
                }

                var current = new StringBuilder();
                bool quoted = false;
                foreach (char c in line)
                {
                    if (c == '"')
                    {
                        quoted = !quoted;
                        continue;
                    }

                    if (!quoted && char.IsWhiteSpace(c))
                    {
                        if (current.Length > 0)
                        {
                            yield return current.ToString();
                            current.Clear();
                        }

                        continue;
                    }

                    current.Append(c);
                }

                if (current.Length > 0)
                {
                    yield return current.ToString();
                }
            }
        }
    }
}
