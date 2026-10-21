#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;

namespace UnityCli.Protocol
{
    /// <summary>
    /// Command line of the Roslyn host process (after <c>dotnet exec UcbRoslynHost.dll</c>).
    /// <code>
    /// --ucb-roslyn-host &lt;projectHash&gt;   marker; orphan cleanup only kills processes carrying it
    /// --roslyn-dir &lt;dir&gt;                bundled DotNetSdkRoslyn folder Roslyn is loaded from
    /// --parent-pid &lt;pid&gt;                editor process; the host exits within ~2 s of it going away
    /// --parent-start-ms &lt;unixMs&gt;         optional editor start time (UTC ms) to catch pid reuse
    /// --prewarm                          compile two dummies (before hello, after the first setRefs)
    /// </code>
    /// Formatted by the editor and parsed by the host, so both share this file.
    /// </summary>
    public sealed class RoslynHostArguments
    {
        public const string MarkerOption = "--ucb-roslyn-host";
        public const string RoslynDirectoryOption = "--roslyn-dir";
        public const string ParentPidOption = "--parent-pid";
        public const string ParentStartOption = "--parent-start-ms";
        public const string PrewarmOption = "--prewarm";

        public RoslynHostArguments(string projectHash, string roslynDirectory, int parentProcessId, long? parentStartUnixMs, bool prewarm)
        {
            ProjectHash = projectHash;
            RoslynDirectory = roslynDirectory;
            ParentProcessId = parentProcessId;
            ParentStartUnixMs = parentStartUnixMs;
            Prewarm = prewarm;
        }

        public string ProjectHash { get; }

        public string RoslynDirectory { get; }

        public int ParentProcessId { get; }

        public long? ParentStartUnixMs { get; }

        public bool Prewarm { get; }

        /// <summary>The marker comes first so it is visible in truncated <c>ps</c> output.</summary>
        public List<string> ToArgumentList()
        {
            var args = new List<string>
            {
                MarkerOption,
                ProjectHash,
                RoslynDirectoryOption,
                RoslynDirectory,
                ParentPidOption,
                ParentProcessId.ToString(CultureInfo.InvariantCulture),
            };
            if (ParentStartUnixMs.HasValue)
            {
                args.Add(ParentStartOption);
                args.Add(ParentStartUnixMs.Value.ToString(CultureInfo.InvariantCulture));
            }

            if (Prewarm)
            {
                args.Add(PrewarmOption);
            }

            return args;
        }

        /// <summary>True when <paramref name="commandLine"/> carries the marker for <paramref name="projectHash"/>.</summary>
        public static bool CommandLineHasMarker(string commandLine, string projectHash)
        {
            if (string.IsNullOrEmpty(commandLine) || string.IsNullOrEmpty(projectHash))
            {
                return false;
            }

            string[] tokens = commandLine.Split(new[] { ' ', '\t', '"' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i + 1 < tokens.Length; i++)
            {
                if (string.Equals(tokens[i], MarkerOption, StringComparison.Ordinal)
                    && string.Equals(tokens[i + 1], projectHash, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        public static bool TryParse(IList<string> args, out RoslynHostArguments? result, out string? error)
        {
            result = null;
            error = null;
            string? projectHash = null;
            string? roslynDirectory = null;
            int? parentPid = null;
            long? parentStart = null;
            bool prewarm = false;

            for (int i = 0; i < args.Count; i++)
            {
                string option = args[i];
                if (string.Equals(option, PrewarmOption, StringComparison.Ordinal))
                {
                    prewarm = true;
                    continue;
                }

                if (i + 1 >= args.Count)
                {
                    error = "Missing value for " + option + ".";
                    return false;
                }

                string value = args[++i];
                switch (option)
                {
                    case MarkerOption:
                        projectHash = value;
                        break;
                    case RoslynDirectoryOption:
                        roslynDirectory = value;
                        break;
                    case ParentPidOption:
                        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int pid) || pid <= 0)
                        {
                            error = "Invalid " + ParentPidOption + ": " + value;
                            return false;
                        }

                        parentPid = pid;
                        break;
                    case ParentStartOption:
                        if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out long start) || start <= 0)
                        {
                            error = "Invalid " + ParentStartOption + ": " + value;
                            return false;
                        }

                        parentStart = start;
                        break;
                    default:
                        error = "Unknown option: " + option;
                        return false;
                }
            }

            if (string.IsNullOrWhiteSpace(projectHash) || projectHash!.IndexOfAny(new[] { ' ', '\t', '"' }) >= 0)
            {
                error = MarkerOption + " <projectHash> is required and must not contain spaces or quotes.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(roslynDirectory))
            {
                error = RoslynDirectoryOption + " <dir> is required.";
                return false;
            }

            if (!parentPid.HasValue)
            {
                error = ParentPidOption + " <pid> is required.";
                return false;
            }

            result = new RoslynHostArguments(projectHash, roslynDirectory!, parentPid.Value, parentStart, prewarm);
            return true;
        }
    }
}
