#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace UnityCli.Protocol
{
    /// <summary>Which editor install layout family to probe. Explicit so every layout is testable on any host.</summary>
    public enum BundledToolchainPlatform
    {
        MacOS,
        Windows,
        Linux,
    }

    /// <summary>One located tool. <see cref="Error"/> is null exactly when the tool was found.</summary>
    public sealed class BundledToolComponent
    {
        public BundledToolComponent(string? path, string? directory, string? version, string? error)
        {
            Path = path;
            Directory = directory;
            Version = version;
            Error = error;
        }

        /// <summary>The executable or entry assembly (dotnet muxer, csc.dll, mcs.exe).</summary>
        public string? Path { get; }

        /// <summary>The tool's root directory (NetCoreRuntime, DotNetSdkRoslyn, mono/4.5).</summary>
        public string? Directory { get; }

        /// <summary>
        /// Microsoft.NETCore.App directory name for the runtime; product version for csc; assembly
        /// version for mcs (what <c>mcs --version</c> prints).
        /// </summary>
        public string? Version { get; }

        public string? Error { get; }

        public bool Found => Error == null;
    }

    /// <summary>Result of probing one editor install. Partial results are kept so doctor can show what is missing.</summary>
    public sealed class BundledToolchainReport
    {
        public BundledToolchainReport(
            string? editorPath,
            string? contentsPath,
            string? layout,
            string? toolchainRoot,
            BundledToolComponent? runtime,
            BundledToolComponent? roslyn,
            BundledToolComponent? mcs,
            string? error)
        {
            EditorPath = editorPath;
            ContentsPath = contentsPath;
            Layout = layout;
            ToolchainRoot = toolchainRoot;
            Runtime = runtime;
            Roslyn = roslyn;
            Mcs = mcs;
            Error = error;
        }

        /// <summary>The path the caller passed in (editor executable, app bundle, or contents directory).</summary>
        public string? EditorPath { get; }

        /// <summary>Equivalent of <c>EditorApplication.applicationContentsPath</c>.</summary>
        public string? ContentsPath { get; }

        /// <summary>Layout id (A–E, see <see cref="BundledToolchainLocator"/>) of the candidate reported below.</summary>
        public string? Layout { get; }

        /// <summary>Directory the layout's tools hang off: the contents path itself or its Resources/Scripting.</summary>
        public string? ToolchainRoot { get; }

        public BundledToolComponent? Runtime { get; }

        public BundledToolComponent? Roslyn { get; }

        public BundledToolComponent? Mcs { get; }

        /// <summary>Why nothing could be probed at all (unknown editor path, missing contents directory).</summary>
        public string? Error { get; }

        public bool Complete =>
            Error == null
            && Runtime != null && Runtime.Found
            && Roslyn != null && Roslyn.Found
            && Mcs != null && Mcs.Found;
    }

    /// <summary>
    /// Finds the .NET runtime, Roslyn compiler, and mcs that ship inside a Unity editor install.
    /// Candidate layouts, relative to the editor contents path
    /// (<c>Unity.app/Contents</c> on macOS, <c>Editor/Data</c> on Windows and Linux):
    /// <code>
    /// A  macOS 2021.3   NetCoreRuntime/dotnet                         DotNetSdkRoslyn/   MonoBleedingEdge/
    /// B  macOS 6000.x   Resources/Scripting/NetCoreRuntime/dotnet     (same under Resources/Scripting)
    /// C  Windows        NetCoreRuntime/dotnet.exe                     DotNetSdkRoslyn/   MonoBleedingEdge/
    /// D  Windows 6000.x Resources/Scripting/NetCoreRuntime/dotnet.exe (same under Resources/Scripting)
    /// E  Linux          both relative roots above, unverified
    /// </code>
    /// The first candidate with every required file wins. When none is complete, the candidate with
    /// the most tools found is reported, with a reason on each missing one.
    ///
    /// Only the editor install is consulted — never PATH, DOTNET_ROOT, or a system .NET install —
    /// and no process is started: versions come from directory names and file version metadata.
    /// Kept free of Unity types so the CLI's doctor and the bridge share it, and so every layout is
    /// unit-testable against a fake tree.
    /// </summary>
    public static class BundledToolchainLocator
    {
        public const string RuntimeDirectoryName = "NetCoreRuntime";
        public const string RoslynDirectoryName = "DotNetSdkRoslyn";
        public const string CscFileName = "csc.dll";
        public const string CodeAnalysisFileName = "Microsoft.CodeAnalysis.dll";
        public const string CodeAnalysisCSharpFileName = "Microsoft.CodeAnalysis.CSharp.dll";
        public const string McsFileName = "mcs.exe";

        private const string ScriptingRelativeRoot = "Resources/Scripting";
        private const string CoreLibFileName = "System.Private.CoreLib.dll";

        private static readonly string[] SharedFrameworkRelativePath = { "shared", "Microsoft.NETCore.App" };
        private static readonly string[] McsRelativePath = { "MonoBleedingEdge", "lib", "mono", "4.5" };

        public static BundledToolchainPlatform CurrentPlatform
        {
            get
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    return BundledToolchainPlatform.Windows;
                }

                return RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
                    ? BundledToolchainPlatform.MacOS
                    : BundledToolchainPlatform.Linux;
            }
        }

        public static BundledToolchainReport Locate(string? editorPath)
        {
            return Locate(editorPath, CurrentPlatform);
        }

        /// <summary>
        /// Probes the install that owns <paramref name="editorPath"/>: the editor executable
        /// (<c>Unity.app/Contents/MacOS/Unity</c>, <c>Editor/Unity.exe</c>, <c>Editor/Unity</c>),
        /// the <c>.app</c> bundle, the <c>Editor</c> directory, or the contents directory itself.
        /// </summary>
        /// <param name="versionReader">Returns a file's product version; defaults to <see cref="ReadProductVersion"/>.</param>
        public static BundledToolchainReport Locate(
            string? editorPath,
            BundledToolchainPlatform platform,
            Func<string, string?>? versionReader = null)
        {
            if (string.IsNullOrWhiteSpace(editorPath))
            {
                return Failure(null, null, "Unity editor path is unknown, so the bundled toolchain was not probed.");
            }

            string trimmed = editorPath!.Trim();
            string? contentsPath = ResolveContentsPath(trimmed, platform);
            if (contentsPath == null)
            {
                return Failure(trimmed, null, "Unity editor path does not exist: " + trimmed);
            }

            if (!System.IO.Directory.Exists(contentsPath))
            {
                return Failure(trimmed, contentsPath, "Unity editor contents directory does not exist: " + contentsPath);
            }

            Func<string, string?> reader = versionReader ?? ReadProductVersion;
            ProbeResult? best = null;
            var checkedRoots = new List<string>();
            foreach (LayoutCandidate candidate in GetCandidates(platform))
            {
                string root = CombineRelative(contentsPath, candidate.RelativeRoot);
                checkedRoots.Add(root);
                ProbeResult probe = Probe(candidate, root, platform, reader);
                if (probe.FoundCount == 3)
                {
                    return probe.ToReport(trimmed, contentsPath);
                }

                if (probe.FoundCount > 0 && (best == null || probe.FoundCount > best.FoundCount))
                {
                    best = probe;
                }
            }

            if (best != null)
            {
                return best.ToReport(trimmed, contentsPath);
            }

            string checkedList = string.Join(", ", checkedRoots);
            return new BundledToolchainReport(
                trimmed,
                contentsPath,
                layout: null,
                toolchainRoot: null,
                runtime: Missing("No bundled " + RuntimeDirectoryName + " found; checked " + checkedList),
                roslyn: Missing("No bundled " + RoslynDirectoryName + " found; checked " + checkedList),
                mcs: Missing("No bundled " + McsFileName + " found; checked " + checkedList),
                error: null);
        }

        /// <summary>
        /// Maps an editor path to the contents directory (what <c>EditorApplication.applicationContentsPath</c>
        /// returns inside the editor). Null when the path does not exist.
        /// </summary>
        public static string? ResolveContentsPath(string editorPath, BundledToolchainPlatform platform)
        {
            string path = TrimTrailingSeparators(editorPath);
            if (System.IO.Directory.Exists(path))
            {
                if (platform == BundledToolchainPlatform.MacOS)
                {
                    return path.EndsWith(".app", StringComparison.OrdinalIgnoreCase)
                        ? System.IO.Path.Combine(path, "Contents")
                        : path;
                }

                // Editor/ -> Editor/Data; Editor/Data stays as is.
                string data = System.IO.Path.Combine(path, "Data");
                return System.IO.Directory.Exists(data) ? data : path;
            }

            if (!File.Exists(path))
            {
                return null;
            }

            string? directory = System.IO.Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(directory))
            {
                return null;
            }

            if (platform == BundledToolchainPlatform.MacOS)
            {
                // Unity.app/Contents/MacOS/Unity -> Unity.app/Contents
                return string.Equals(System.IO.Path.GetFileName(directory), "MacOS", StringComparison.Ordinal)
                    ? System.IO.Path.GetDirectoryName(directory)
                    : directory;
            }

            // Editor/Unity(.exe) -> Editor/Data
            return System.IO.Path.Combine(directory, "Data");
        }

        /// <summary>
        /// Product version with SemVer build metadata (<c>+commit</c>) dropped, falling back to the
        /// file version. Reads PE metadata only; never runs the file. Null when unreadable.
        /// </summary>
        public static string? ReadProductVersion(string filePath)
        {
            try
            {
                FileVersionInfo info = FileVersionInfo.GetVersionInfo(filePath);
                string? version = NormalizeVersion(info.ProductVersion);
                return version ?? NormalizeVersion(info.FileVersion);
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// The mcs version as <c>mcs --version</c> reports it: the assembly version in the metadata,
        /// falling back to <paramref name="versionReader"/> when the file is not a readable assembly.
        /// mcs.exe's Win32 version resource says 4.6.57.0 while its assembly says 6.13.0.0, and
        /// <see cref="FileVersionInfo"/> reads the resource under Mono and on Windows but the assembly
        /// attributes under .NET on macOS/Linux. The metadata reads the same everywhere, so doctor
        /// (CLI, .NET) and the editor (Mono) agree.
        /// </summary>
        public static string? ReadMcsVersion(string filePath, Func<string, string?>? versionReader = null)
        {
            string? version = ReadAssemblyVersion(filePath);
            if (version != null)
            {
                return version;
            }

            return (versionReader ?? ReadProductVersion)(filePath);
        }

        /// <summary>Assembly version from the file's metadata (the file is not loaded or run). Null when unreadable.</summary>
        public static string? ReadAssemblyVersion(string filePath)
        {
            try
            {
                Version? version = AssemblyName.GetAssemblyName(filePath).Version;
                return version == null ? null : version.ToString();
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// Picks the highest Microsoft.NETCore.App version directory that holds a CoreLib. Release
        /// versions sort above prereleases of the same number; unparseable names are ignored.
        /// </summary>
        public static string? SelectSharedFrameworkVersion(string sharedFrameworkDirectory)
        {
            if (!System.IO.Directory.Exists(sharedFrameworkDirectory))
            {
                return null;
            }

            string? bestName = null;
            Version? bestVersion = null;
            string? bestPrerelease = null;
            foreach (string directory in System.IO.Directory.GetDirectories(sharedFrameworkDirectory))
            {
                string name = System.IO.Path.GetFileName(directory);
                if (!TryParseFrameworkVersion(name, out Version? version, out string? prerelease)
                    || !File.Exists(System.IO.Path.Combine(directory, CoreLibFileName)))
                {
                    continue;
                }

                if (bestVersion == null || CompareFrameworkVersions(version!, prerelease, bestVersion, bestPrerelease) > 0)
                {
                    bestName = name;
                    bestVersion = version;
                    bestPrerelease = prerelease;
                }
            }

            return bestName;
        }

        private static ProbeResult Probe(
            LayoutCandidate candidate,
            string root,
            BundledToolchainPlatform platform,
            Func<string, string?> versionReader)
        {
            return new ProbeResult(
                candidate.Id,
                root,
                ProbeRuntime(root, platform),
                ProbeRoslyn(root, versionReader),
                ProbeMcs(root, versionReader));
        }

        private static BundledToolComponent ProbeRuntime(string root, BundledToolchainPlatform platform)
        {
            string runtimeDirectory = System.IO.Path.Combine(root, RuntimeDirectoryName);
            string muxer = System.IO.Path.Combine(
                runtimeDirectory,
                platform == BundledToolchainPlatform.Windows ? "dotnet.exe" : "dotnet");
            if (!File.Exists(muxer))
            {
                return new BundledToolComponent(null, null, null, "dotnet muxer not found: " + muxer);
            }

            string sharedFramework = CombineParts(runtimeDirectory, SharedFrameworkRelativePath);
            string? version = SelectSharedFrameworkVersion(sharedFramework);
            if (version == null)
            {
                return new BundledToolComponent(
                    muxer,
                    runtimeDirectory,
                    null,
                    "No Microsoft.NETCore.App version with " + CoreLibFileName + " under " + sharedFramework);
            }

            return new BundledToolComponent(muxer, runtimeDirectory, version, null);
        }

        private static BundledToolComponent ProbeRoslyn(string root, Func<string, string?> versionReader)
        {
            string roslynDirectory = System.IO.Path.Combine(root, RoslynDirectoryName);
            string csc = System.IO.Path.Combine(roslynDirectory, CscFileName);
            var missing = new List<string>();
            foreach (string fileName in new[] { CscFileName, CodeAnalysisFileName, CodeAnalysisCSharpFileName })
            {
                if (!File.Exists(System.IO.Path.Combine(roslynDirectory, fileName)))
                {
                    missing.Add(fileName);
                }
            }

            if (missing.Count == 3)
            {
                return new BundledToolComponent(null, null, null, "Roslyn not found: " + roslynDirectory);
            }

            if (missing.Count > 0)
            {
                return new BundledToolComponent(
                    null,
                    roslynDirectory,
                    null,
                    "Roslyn incomplete; missing " + string.Join(", ", missing) + " in " + roslynDirectory);
            }

            return new BundledToolComponent(csc, roslynDirectory, versionReader(csc), null);
        }

        private static BundledToolComponent ProbeMcs(string root, Func<string, string?> versionReader)
        {
            string monoDirectory = CombineParts(root, McsRelativePath);
            string mcs = System.IO.Path.Combine(monoDirectory, McsFileName);
            if (!File.Exists(mcs))
            {
                return new BundledToolComponent(null, null, null, "mcs not found: " + mcs);
            }

            return new BundledToolComponent(mcs, monoDirectory, ReadMcsVersion(mcs, versionReader), null);
        }

        private static IEnumerable<LayoutCandidate> GetCandidates(BundledToolchainPlatform platform)
        {
            switch (platform)
            {
                case BundledToolchainPlatform.Windows:
                    yield return new LayoutCandidate("C", string.Empty);
                    yield return new LayoutCandidate("D", ScriptingRelativeRoot);
                    break;
                case BundledToolchainPlatform.Linux:
                    yield return new LayoutCandidate("E", string.Empty);
                    yield return new LayoutCandidate("E", ScriptingRelativeRoot);
                    break;
                default:
                    yield return new LayoutCandidate("A", string.Empty);
                    yield return new LayoutCandidate("B", ScriptingRelativeRoot);
                    break;
            }
        }

        private static bool TryParseFrameworkVersion(string name, out Version? version, out string? prerelease)
        {
            version = null;
            prerelease = null;
            if (string.IsNullOrEmpty(name))
            {
                return false;
            }

            string core = name;
            int dash = name.IndexOf('-');
            if (dash >= 0)
            {
                core = name.Substring(0, dash);
                prerelease = name.Substring(dash + 1);
                if (prerelease.Length == 0)
                {
                    return false;
                }
            }

            // Shared framework folders are always major.minor.patch.
            if (core.Split('.').Length != 3 || !Version.TryParse(core, out Version? parsed))
            {
                return false;
            }

            version = parsed;
            return true;
        }

        private static int CompareFrameworkVersions(Version left, string? leftPrerelease, Version right, string? rightPrerelease)
        {
            int compare = left.CompareTo(right);
            if (compare != 0)
            {
                return compare;
            }

            if (leftPrerelease == null || rightPrerelease == null)
            {
                // Release (no suffix) outranks any prerelease of the same number.
                return (leftPrerelease == null ? 1 : 0) - (rightPrerelease == null ? 1 : 0);
            }

            return string.CompareOrdinal(leftPrerelease, rightPrerelease);
        }

        private static string? NormalizeVersion(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return null;
            }

            string value = raw!.Trim();
            int plus = value.IndexOf('+');
            if (plus >= 0)
            {
                value = value.Substring(0, plus);
            }

            return value.Length == 0 ? null : value;
        }

        private static string CombineRelative(string basePath, string relative)
        {
            if (relative.Length == 0)
            {
                return basePath;
            }

            return CombineParts(basePath, relative.Split('/'));
        }

        private static string CombineParts(string basePath, string[] parts)
        {
            string result = basePath;
            foreach (string part in parts)
            {
                result = System.IO.Path.Combine(result, part);
            }

            return result;
        }

        private static string TrimTrailingSeparators(string path)
        {
            string trimmed = path.TrimEnd('/', '\\');
            return trimmed.Length == 0 ? path : trimmed;
        }

        private static BundledToolchainReport Failure(string? editorPath, string? contentsPath, string error)
        {
            return new BundledToolchainReport(editorPath, contentsPath, null, null, null, null, null, error);
        }

        private static BundledToolComponent Missing(string error)
        {
            return new BundledToolComponent(null, null, null, error);
        }

        private sealed class LayoutCandidate
        {
            public LayoutCandidate(string id, string relativeRoot)
            {
                Id = id;
                RelativeRoot = relativeRoot;
            }

            public string Id { get; }

            public string RelativeRoot { get; }
        }

        private sealed class ProbeResult
        {
            public ProbeResult(
                string layout,
                string root,
                BundledToolComponent runtime,
                BundledToolComponent roslyn,
                BundledToolComponent mcs)
            {
                Layout = layout;
                Root = root;
                Runtime = runtime;
                Roslyn = roslyn;
                Mcs = mcs;
            }

            public string Layout { get; }

            public string Root { get; }

            public BundledToolComponent Runtime { get; }

            public BundledToolComponent Roslyn { get; }

            public BundledToolComponent Mcs { get; }

            public int FoundCount => (Runtime.Found ? 1 : 0) + (Roslyn.Found ? 1 : 0) + (Mcs.Found ? 1 : 0);

            public BundledToolchainReport ToReport(string editorPath, string contentsPath)
            {
                return new BundledToolchainReport(editorPath, contentsPath, Layout, Root, Runtime, Roslyn, Mcs, null);
            }
        }
    }
}
