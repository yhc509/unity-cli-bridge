#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using UnityCli.Protocol;

namespace UnityCliBridge.Bridge.Editor
{
    /// <summary>
    /// Builds the <c>csc /shared</c> invocation and reads its output. The command line stays short and
    /// fixed (<c>exec csc.dll /noconfig /nologo /nostdlib+ /target:library /langversion:9
    /// /preferreduilang:en-US /shared:&lt;pipe&gt; @&lt;rsp&gt; &lt;src&gt;</c>); references, defines, and
    /// options go in the response file, so the length does not grow with the reference count (#220).
    /// The compile inputs are the Roslyn host's (<see cref="RoslynHostBackend.BuildHostRequest"/>), so
    /// both Roslyn backends see the same source, polyfill, defines, unsafe flag, and suppressed warnings.
    /// Unity-free so it is unit-tested.
    /// </summary>
    internal static class CscSharedCommandLine
    {
        /// <summary>English diagnostics whatever the machine's language, matching the host's invariant-culture messages.</summary>
        public const string UiLanguage = "en-US";

        private static readonly Regex LocatedDiagnostic = new Regex(
            @"^(?<file>.+?)\((?<line>\d+),(?<col>\d+)(?:,\d+,\d+)?\): (?<sev>error|warning) (?<id>[A-Za-z]+\d+): (?<msg>.*)$",
            RegexOptions.CultureInvariant);

        private static readonly Regex UnlocatedDiagnostic = new Regex(
            @"^(?:.*? : )?(?<sev>error|warning) (?<id>[A-Za-z]+\d+): (?<msg>.*)$",
            RegexOptions.CultureInvariant);

        /// <summary>Arguments after the muxer (<c>dotnet</c>).</summary>
        public static List<string> BuildArguments(string cscPath, string pipeName, string responseFilePath, string sourcePath)
        {
            return new List<string>
            {
                "exec",
                cscPath,
                "/noconfig",
                "/nologo",
                "/nostdlib+",
                "/target:library",
                "/langversion:" + RoslynHostBackend.LanguageVersion,
                "/preferreduilang:" + UiLanguage,
                "/shared:" + pipeName,
                "@" + responseFilePath,
                sourcePath,
            };
        }

        /// <summary>The <c>VBCSCompiler.dll -shutdown</c> client for <paramref name="pipeName"/> (arguments after the muxer).</summary>
        public static List<string> BuildShutdownArguments(string vbcsCompilerPath, string pipeName)
        {
            return new List<string> { "exec", vbcsCompilerPath, "-shutdown", CscSharedPipe.PipeNameOption + pipeName };
        }

        /// <summary>
        /// Response-file lines: the host's options (Release, no pdb, defines, unsafe, suppressed
        /// warnings), the output path, then one <c>/r:</c> per reference.
        /// </summary>
        public static List<string> BuildResponseFile(RoslynHostCompileRequest request, IReadOnlyList<string> referencePaths, string outputPath)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            var lines = new List<string>
            {
                "/optimize+",
                "/debug-",
                "/utf8output",
                "/codepage:65001",
            };

            if (request.Defines.Count > 0)
            {
                lines.Add("/define:" + string.Join(";", request.Defines));
            }

            if (request.AllowUnsafe)
            {
                lines.Add("/unsafe+");
            }

            var noWarn = new List<string>();
            foreach (string id in RoslynHostAlwaysSuppressed)
            {
                AddWarning(noWarn, id);
            }

            foreach (string id in request.NoWarn)
            {
                AddWarning(noWarn, id);
            }

            lines.Add("/nowarn:" + string.Join(",", noWarn));
            lines.Add("/out:" + Quote(outputPath));
            if (referencePaths != null)
            {
                foreach (string reference in referencePaths)
                {
                    if (!string.IsNullOrEmpty(reference))
                    {
                        lines.Add("/r:" + Quote(reference));
                    }
                }
            }

            return lines;
        }

        /// <summary>The host suppresses these on top of the request's list (assembly-unification noise).</summary>
        public static readonly string[] RoslynHostAlwaysSuppressed = { "CS1701", "CS1702" };

        /// <summary>
        /// Always double-quoted (csc splits an unquoted <c>/r:</c> value on <c>,</c> and <c>;</c>), with
        /// the command-line rules csc applies to response files: <c>\"</c> is a literal quote and
        /// backslashes are literal unless they precede a quote.
        /// </summary>
        public static string Quote(string value)
        {
            if (value == null)
            {
                throw new ArgumentNullException(nameof(value));
            }

            var builder = new StringBuilder(value.Length + 2);
            builder.Append('"');
            int backslashes = 0;
            foreach (char c in value)
            {
                if (c == '\\')
                {
                    backslashes++;
                    continue;
                }

                if (c == '"')
                {
                    builder.Append('\\', backslashes * 2 + 1);
                    builder.Append('"');
                }
                else
                {
                    builder.Append('\\', backslashes);
                    builder.Append(c);
                }

                backslashes = 0;
            }

            builder.Append('\\', backslashes * 2);
            builder.Append('"');
            return builder.ToString();
        }

        /// <summary>
        /// <c>file(line,col): error CS0029: text</c> lines, plus location-less <c>error CS2001: text</c>
        /// ones (file empty, line 0). Lines that are not diagnostics are skipped; at most
        /// <paramref name="max"/> are kept, like the host.
        /// </summary>
        public static List<RoslynHostDiagnostic> ParseDiagnostics(string? output, int max = 200)
        {
            var diagnostics = new List<RoslynHostDiagnostic>();
            if (string.IsNullOrEmpty(output))
            {
                return diagnostics;
            }

            foreach (string rawLine in output!.Split('\n'))
            {
                if (diagnostics.Count >= max)
                {
                    break;
                }

                string line = rawLine.TrimEnd('\r');
                Match located = LocatedDiagnostic.Match(line);
                if (located.Success)
                {
                    diagnostics.Add(new RoslynHostDiagnostic
                    {
                        Id = located.Groups["id"].Value.ToUpperInvariant(),
                        Severity = located.Groups["sev"].Value,
                        Line = int.Parse(located.Groups["line"].Value, CultureInfo.InvariantCulture),
                        Column = int.Parse(located.Groups["col"].Value, CultureInfo.InvariantCulture),
                        File = located.Groups["file"].Value,
                        Message = located.Groups["msg"].Value,
                    });
                    continue;
                }

                Match unlocated = UnlocatedDiagnostic.Match(line);
                if (unlocated.Success)
                {
                    diagnostics.Add(new RoslynHostDiagnostic
                    {
                        Id = unlocated.Groups["id"].Value.ToUpperInvariant(),
                        Severity = unlocated.Groups["sev"].Value,
                        Message = unlocated.Groups["msg"].Value,
                    });
                }
            }

            return diagnostics;
        }

        /// <summary>
        /// A failed run is the user's compile error only when csc reported at least one error located
        /// in the source; anything else (bad option, missing reference, no output, crash) is an
        /// infrastructure failure that moves down the chain.
        /// </summary>
        public static bool IsUserCompileError(IReadOnlyList<RoslynHostDiagnostic> diagnostics)
        {
            foreach (RoslynHostDiagnostic diagnostic in diagnostics)
            {
                if (diagnostic.IsError && diagnostic.Line > 0 && diagnostic.File.Length > 0)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>The first lines of the output, for a fallback reason.</summary>
        public static string Summarize(string? output, int maxLength = 400)
        {
            string text = (output ?? string.Empty).Trim().Replace("\r", string.Empty).Replace('\n', ' ');
            return text.Length > maxLength ? text.Substring(0, maxLength) + "..." : text;
        }

        private static void AddWarning(List<string> target, string id)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                return;
            }

            // Only letters and digits reach the response file, so an rsp value cannot inject an option.
            var builder = new StringBuilder();
            foreach (char c in id.Trim())
            {
                if ((c >= '0' && c <= '9') || (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z'))
                {
                    builder.Append(c);
                }
            }

            string normalized = builder.ToString();
            if (normalized.Length > 0 && normalized.Length == id.Trim().Length && !target.Contains(normalized))
            {
                target.Add(normalized);
            }
        }
    }
}
