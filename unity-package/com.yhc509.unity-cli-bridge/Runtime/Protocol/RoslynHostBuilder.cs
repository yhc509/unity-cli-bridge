#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace UnityCli.Protocol
{
    public sealed class RoslynHostBuildRequest
    {
        public RoslynHostBuildRequest(
            BundledToolchainReport toolchain,
            IReadOnlyList<string> hostSourcePaths,
            string cacheRoot,
            string unityVersion)
        {
            Toolchain = toolchain;
            HostSourcePaths = hostSourcePaths;
            CacheRoot = cacheRoot;
            UnityVersion = unityVersion;
        }

        public BundledToolchainReport Toolchain { get; }

        /// <summary>Usually <see cref="RoslynHostBuilder.GetHostSourcePaths"/>.</summary>
        public IReadOnlyList<string> HostSourcePaths { get; }

        /// <summary>Parent of the per-key output folders (the editor will pass <c>Library/com.yhc509.unity-cli-bridge/roslyn-host</c>).</summary>
        public string CacheRoot { get; }

        public string UnityVersion { get; }

        public TimeSpan CompilerTimeout { get; set; } = TimeSpan.FromSeconds(120);
    }

    public sealed class RoslynHostBuildResult
    {
        internal RoslynHostBuildResult(
            bool success,
            bool cacheHit,
            string? cacheKey,
            string? outputDirectory,
            string? hostAssemblyPath,
            string? runtimeConfigPath,
            double buildMs,
            double totalMs,
            int? compilerExitCode,
            string? compilerOutput,
            string? error)
        {
            Success = success;
            CacheHit = cacheHit;
            CacheKey = cacheKey;
            OutputDirectory = outputDirectory;
            HostAssemblyPath = hostAssemblyPath;
            RuntimeConfigPath = runtimeConfigPath;
            BuildMs = buildMs;
            TotalMs = totalMs;
            CompilerExitCode = compilerExitCode;
            CompilerOutput = compilerOutput;
            Error = error;
        }

        public bool Success { get; }

        /// <summary>The output already existed (or a concurrent build finished first); csc did not run for this call.</summary>
        public bool CacheHit { get; }

        public string? CacheKey { get; }

        public string? OutputDirectory { get; }

        public string? HostAssemblyPath { get; }

        public string? RuntimeConfigPath { get; }

        /// <summary>Wall time of the csc process; 0 on a cache hit.</summary>
        public double BuildMs { get; }

        /// <summary>Wall time of the whole call, including hashing and the cache check.</summary>
        public double TotalMs { get; }

        public int? CompilerExitCode { get; }

        /// <summary>csc stdout + stderr (trimmed to 16 KB) when it ran.</summary>
        public string? CompilerOutput { get; }

        public string? Error { get; }
    }

    /// <summary>
    /// Builds the Roslyn host from source with the editor's bundled csc, once per
    /// <c>&lt;unityVersion&gt;-&lt;sha256(host sources + runtime version + Roslyn version + recipe)[:12]&gt;</c>.
    /// Building in place (instead of shipping a binary) keeps the host bound to exactly the runtime
    /// and Roslyn that this editor install bundles.
    ///
    /// The compile runs <c>dotnet exec csc.dll @rsp</c> on the bundled muxer only
    /// (<see cref="BundledDotnetProcess"/>), against every managed assembly in
    /// <c>shared/Microsoft.NETCore.App/&lt;ver&gt;/</c> plus the two Roslyn assemblies, into a temporary
    /// folder that is moved into place only after the runtimeconfig and build marker are written.
    /// Unity-free so the editor (PR 5) and tools can share it.
    /// </summary>
    public static class RoslynHostBuilder
    {
        public const string HostAssemblyName = "UcbRoslynHost";
        public const string HostAssemblyFileName = HostAssemblyName + ".dll";
        public const string RuntimeConfigFileName = HostAssemblyName + ".runtimeconfig.json";
        public const string BuildMarkerFileName = "build.json";
        public const string ResponseFileName = "csc.rsp";

        /// <summary>
        /// Bumped when the build recipe itself (compiler options, runtimeconfig content) changes so
        /// old cache folders are not reused with a different meaning.
        /// </summary>
        public const string RecipeVersion = "1";

        /// <summary>Host sources relative to the package root: the host program plus the shared wire files it compiles in.</summary>
        public static readonly string[] HostSourceRelativePaths =
        {
            "RoslynHost~/UcbRoslynHost.cs",
            "Runtime/Protocol/RoslynHostArguments.cs",
            "Runtime/Protocol/RoslynHostFrameCodec.cs",
            "Runtime/Protocol/RoslynHostJson.cs",
            "Runtime/Protocol/RoslynHostMessages.cs",
        };

        private const int MaxCompilerOutputChars = 16 * 1024;
        private static readonly TimeSpan StaleTempAge = TimeSpan.FromHours(1);

        public static List<string> GetHostSourcePaths(string packageRoot)
        {
            var paths = new List<string>(HostSourceRelativePaths.Length);
            foreach (string relative in HostSourceRelativePaths)
            {
                string path = packageRoot;
                foreach (string part in relative.Split('/'))
                {
                    path = Path.Combine(path, part);
                }

                paths.Add(path);
            }

            return paths;
        }

        /// <summary>
        /// <c>&lt;unityVersion&gt;-&lt;12 hex&gt;</c>. Sources are hashed by file name and content in
        /// ordinal file-name order, so the key does not depend on where the package is checked out.
        /// </summary>
        public static string ComputeCacheKey(
            string unityVersion,
            IEnumerable<KeyValuePair<string, byte[]>> sources,
            string runtimeVersion,
            string roslynVersion)
        {
            var ordered = new List<KeyValuePair<string, byte[]>>(sources);
            ordered.Sort((left, right) => string.CompareOrdinal(left.Key, right.Key));
            using (SHA256 sha = SHA256.Create())
            {
                var buffer = new MemoryStream();
                AppendText(buffer, "ucb-roslyn-host\nrecipe:" + RecipeVersion + "\n");
                foreach (KeyValuePair<string, byte[]> source in ordered)
                {
                    AppendText(buffer, "source:" + source.Key + ":" + source.Value.Length.ToString(CultureInfo.InvariantCulture) + "\n");
                    buffer.Write(source.Value, 0, source.Value.Length);
                    AppendText(buffer, "\n");
                }

                AppendText(buffer, "runtime:" + runtimeVersion + "\nroslyn:" + roslynVersion + "\n");
                byte[] hash = sha.ComputeHash(buffer.ToArray());
                var hex = new StringBuilder(12);
                for (int i = 0; i < 6; i++)
                {
                    hex.Append(hash[i].ToString("x2", CultureInfo.InvariantCulture));
                }

                return SanitizeKeyPart(unityVersion) + "-" + hex;
            }
        }

        /// <summary>
        /// Exact detected framework version, no roll-forward, workstation non-concurrent GC (the host
        /// stays resident, so memory beats throughput), and TieredPGO off. Tiered compilation stays on:
        /// turning it off made startup slower, not faster.
        /// </summary>
        public static string BuildRuntimeConfigJson(string runtimeVersion)
        {
            return "{\n"
                + "  \"runtimeOptions\": {\n"
                + "    \"tfm\": \"" + TargetFrameworkMoniker(runtimeVersion) + "\",\n"
                + "    \"framework\": {\n"
                + "      \"name\": \"Microsoft.NETCore.App\",\n"
                + "      \"version\": \"" + runtimeVersion + "\"\n"
                + "    },\n"
                + "    \"rollForward\": \"Disable\",\n"
                + "    \"configProperties\": {\n"
                + "      \"System.GC.Server\": false,\n"
                + "      \"System.GC.Concurrent\": false,\n"
                + "      \"System.Runtime.TieredPGO\": false\n"
                + "    }\n"
                + "  }\n"
                + "}\n";
        }

        /// <summary>Response file lines; every path is quoted. References are sorted for a stable file.</summary>
        public static List<string> BuildResponseFileLines(
            IEnumerable<string> frameworkAssemblies,
            string roslynDirectory,
            string outputAssemblyPath,
            IEnumerable<string> sourcePaths)
        {
            var lines = new List<string>
            {
                "/noconfig",
                "/nologo",
                "/nostdlib+",
                "/target:exe",
                "/langversion:9",
                "/optimize+",
                "/deterministic+",
                "/debug-",
                "/utf8output",
                "/out:" + QuoteRspPath(outputAssemblyPath),
            };

            var framework = new List<string>(frameworkAssemblies);
            framework.Sort(StringComparer.Ordinal);
            foreach (string reference in framework)
            {
                lines.Add("/r:" + QuoteRspPath(reference));
            }

            lines.Add("/r:" + QuoteRspPath(Path.Combine(roslynDirectory, BundledToolchainLocator.CodeAnalysisFileName)));
            lines.Add("/r:" + QuoteRspPath(Path.Combine(roslynDirectory, BundledToolchainLocator.CodeAnalysisCSharpFileName)));
            foreach (string source in sourcePaths)
            {
                lines.Add(QuoteRspPath(source));
            }

            return lines;
        }

        /// <summary>
        /// Managed assemblies in the shared framework folder. Windows framework folders also hold
        /// native DLLs (coreclr.dll, clrjit.dll, ...), which csc rejects as references.
        /// </summary>
        public static List<string> ListFrameworkAssemblies(string sharedFrameworkDirectory)
        {
            var result = new List<string>();
            foreach (string file in Directory.GetFiles(sharedFrameworkDirectory, "*.dll"))
            {
                if (IsManagedAssembly(file))
                {
                    result.Add(file);
                }
            }

            result.Sort(StringComparer.Ordinal);
            return result;
        }

        /// <summary>True when the PE file has a CLI header (data directory 14). Reads headers only.</summary>
        public static bool IsManagedAssembly(string path)
        {
            try
            {
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var reader = new BinaryReader(stream))
                {
                    if (stream.Length < 0x40 || reader.ReadUInt16() != 0x5A4D)
                    {
                        return false;
                    }

                    stream.Position = 0x3C;
                    int peOffset = reader.ReadInt32();
                    if (peOffset <= 0 || peOffset > stream.Length - 24)
                    {
                        return false;
                    }

                    stream.Position = peOffset;
                    if (reader.ReadUInt32() != 0x00004550)
                    {
                        return false;
                    }

                    stream.Position = peOffset + 4 + 16;
                    ushort optionalHeaderSize = reader.ReadUInt16();
                    long optionalHeader = peOffset + 24;
                    if (optionalHeaderSize == 0 || optionalHeader + optionalHeaderSize > stream.Length)
                    {
                        return false;
                    }

                    stream.Position = optionalHeader;
                    ushort magic = reader.ReadUInt16();
                    int directoriesOffset;
                    if (magic == 0x10B)
                    {
                        directoriesOffset = 96;
                    }
                    else if (magic == 0x20B)
                    {
                        directoriesOffset = 112;
                    }
                    else
                    {
                        return false;
                    }

                    const int CliHeaderDirectory = 14;
                    int cliEntry = directoriesOffset + CliHeaderDirectory * 8;
                    if (cliEntry + 8 > optionalHeaderSize)
                    {
                        return false;
                    }

                    stream.Position = optionalHeader + directoriesOffset - 4;
                    uint directoryCount = reader.ReadUInt32();
                    if (directoryCount <= CliHeaderDirectory)
                    {
                        return false;
                    }

                    stream.Position = optionalHeader + cliEntry;
                    uint rva = reader.ReadUInt32();
                    uint size = reader.ReadUInt32();
                    return rva != 0 && size != 0;
                }
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// True when <paramref name="request"/> already has a complete host image in the cache.
        /// Does not compile and does not start a process. <paramref name="keyed"/> is false when
        /// the toolchain, sources, or cache root cannot name an output folder (a start would fail
        /// before csc). Otherwise a false result means the next start has to compile the host.
        /// </summary>
        public static bool IsImageReady(RoslynHostBuildRequest request, out bool keyed)
        {
            if (request == null)
            {
                keyed = false;
                return false;
            }

            PreparedHostBuild prepared = Prepare(request);
            keyed = prepared.Error == null;
            return keyed && prepared.OutputDirectory != null && IsComplete(prepared.OutputDirectory);
        }

        public static RoslynHostBuildResult EnsureBuilt(RoslynHostBuildRequest request)
        {
            return EnsureBuilt(request, BundledDotnetProcess.Run);
        }

        /// <summary>Test seam: <paramref name="runCompiler"/> stands in for running the csc process.</summary>
        public static RoslynHostBuildResult EnsureBuilt(
            RoslynHostBuildRequest request,
            Func<ProcessStartInfo, TimeSpan, BundledDotnetRunResult> runCompiler)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            if (runCompiler == null)
            {
                throw new ArgumentNullException(nameof(runCompiler));
            }

            Stopwatch total = Stopwatch.StartNew();
            PreparedHostBuild prepared = Prepare(request);
            if (prepared.Error != null)
            {
                return Failed(total, prepared.Key, prepared.Error);
            }

            BundledToolComponent runtime = prepared.Runtime!;
            BundledToolComponent roslyn = prepared.Roslyn!;
            List<KeyValuePair<string, byte[]>> sources = prepared.Sources!;
            string key = prepared.Key!;
            string outputDirectory = prepared.OutputDirectory!;
            string runtimeDirectory = runtime.Directory ?? string.Empty;
            string runtimePath = runtime.Path ?? string.Empty;
            string runtimeVersion = runtime.Version ?? string.Empty;
            string roslynDirectory = roslyn.Directory ?? string.Empty;
            string roslynPath = roslyn.Path ?? string.Empty;
            string roslynVersion = roslyn.Version ?? "unknown";
            if (IsComplete(outputDirectory))
            {
                return Hit(total, key, outputDirectory);
            }

            string sharedFramework = Path.Combine(Path.Combine(Path.Combine(runtimeDirectory, "shared"), "Microsoft.NETCore.App"), runtimeVersion);
            List<string> frameworkAssemblies;
            try
            {
                frameworkAssemblies = ListFrameworkAssemblies(sharedFramework);
            }
            catch (Exception ex)
            {
                return Failed(total, key, "Cannot list the shared framework at " + sharedFramework + ": " + ex.Message);
            }

            if (frameworkAssemblies.Count == 0)
            {
                return Failed(total, key, "No managed assemblies under " + sharedFramework + ".");
            }

            string tempDirectory = Path.Combine(
                request.CacheRoot,
                ".build-" + key + "-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                Directory.CreateDirectory(request.CacheRoot);
                DeleteStaleTempDirectories(request.CacheRoot);
                string sourceDirectory = Path.Combine(tempDirectory, "src");
                Directory.CreateDirectory(sourceDirectory);

                // Compile the exact bytes that were hashed, not whatever is on disk a moment later.
                var tempSources = new List<string>(sources.Count);
                foreach (KeyValuePair<string, byte[]> source in sources)
                {
                    string copy = Path.Combine(sourceDirectory, source.Key);
                    File.WriteAllBytes(copy, source.Value);
                    tempSources.Add(copy);
                }

                string assemblyPath = Path.Combine(tempDirectory, HostAssemblyFileName);
                string responseFile = Path.Combine(tempDirectory, ResponseFileName);
                List<string> lines = BuildResponseFileLines(frameworkAssemblies, roslynDirectory, assemblyPath, tempSources);
                File.WriteAllText(responseFile, string.Join("\n", lines) + "\n", new UTF8Encoding(false));

                ProcessStartInfo startInfo = BundledDotnetProcess.CreateStartInfo(
                    runtimePath,
                    new[] { "exec", roslynPath, "@" + responseFile },
                    tempDirectory);
                Stopwatch build = Stopwatch.StartNew();
                BundledDotnetRunResult run = runCompiler(startInfo, request.CompilerTimeout);
                build.Stop();
                string output = Trim(run.StandardOutput + run.StandardError);
                if (run.TimedOut)
                {
                    return Cleanup(tempDirectory, Failed(total, key, "csc did not finish within " + request.CompilerTimeout.TotalSeconds + " s.", build.Elapsed.TotalMilliseconds, null, output));
                }

                if (run.ExitCode != 0 || !File.Exists(assemblyPath))
                {
                    return Cleanup(tempDirectory, Failed(total, key, "csc failed with exit code " + run.ExitCode + ".", build.Elapsed.TotalMilliseconds, run.ExitCode, output));
                }

                File.WriteAllText(Path.Combine(tempDirectory, RuntimeConfigFileName), BuildRuntimeConfigJson(runtimeVersion), new UTF8Encoding(false));
                File.WriteAllText(
                    Path.Combine(tempDirectory, BuildMarkerFileName),
                    BuildMarkerJson(key, request.UnityVersion, runtimeVersion, roslynVersion, build.Elapsed.TotalMilliseconds),
                    new UTF8Encoding(false));

                try
                {
                    Directory.Move(tempDirectory, outputDirectory);
                }
                catch (IOException)
                {
                    // Another build of the same key won the race; its output is equivalent.
                    if (IsComplete(outputDirectory))
                    {
                        TryDeleteDirectory(tempDirectory);
                        return Hit(total, key, outputDirectory);
                    }

                    // A half-written folder from an interrupted older build: replace it.
                    TryDeleteDirectory(outputDirectory);
                    Directory.Move(tempDirectory, outputDirectory);
                }

                total.Stop();
                return new RoslynHostBuildResult(
                    true,
                    false,
                    key,
                    outputDirectory,
                    Path.Combine(outputDirectory, HostAssemblyFileName),
                    Path.Combine(outputDirectory, RuntimeConfigFileName),
                    build.Elapsed.TotalMilliseconds,
                    total.Elapsed.TotalMilliseconds,
                    run.ExitCode,
                    output,
                    null);
            }
            catch (Exception ex)
            {
                return Cleanup(tempDirectory, Failed(total, key, "Host build failed: " + ex.GetType().Name + ": " + ex.Message));
            }
        }

        private sealed class PreparedHostBuild
        {
            public BundledToolComponent? Runtime { get; set; }

            public BundledToolComponent? Roslyn { get; set; }

            public List<KeyValuePair<string, byte[]>>? Sources { get; set; }

            public string? Key { get; set; }

            public string? OutputDirectory { get; set; }

            public string? Error { get; set; }
        }

        /// <summary>Names the cache folder, or sets <see cref="PreparedHostBuild.Error"/> with the same text <see cref="EnsureBuilt"/> used to return.</summary>
        private static PreparedHostBuild Prepare(RoslynHostBuildRequest request)
        {
            var prepared = new PreparedHostBuild();
            BundledToolchainReport toolchain = request.Toolchain;
            BundledToolComponent? runtime = toolchain == null ? null : toolchain.Runtime;
            BundledToolComponent? roslyn = toolchain == null ? null : toolchain.Roslyn;
            if (runtime == null || !runtime.Found || runtime.Path == null || runtime.Directory == null || runtime.Version == null)
            {
                prepared.Error = "Bundled .NET runtime is not available: " + (runtime == null ? toolchain?.Error : runtime.Error);
                return prepared;
            }

            if (roslyn == null || !roslyn.Found || roslyn.Path == null || roslyn.Directory == null)
            {
                prepared.Error = "Bundled Roslyn is not available: " + (roslyn == null ? toolchain?.Error : roslyn.Error);
                return prepared;
            }

            if (string.IsNullOrWhiteSpace(request.CacheRoot) || !Path.IsPathRooted(request.CacheRoot))
            {
                prepared.Error = "Cache root must be an absolute path: " + request.CacheRoot;
                return prepared;
            }

            var sources = new List<KeyValuePair<string, byte[]>>();
            var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string sourcePath in request.HostSourcePaths)
            {
                string name = Path.GetFileName(sourcePath);
                if (!seenNames.Add(name))
                {
                    prepared.Error = "Two host sources share the file name " + name + ".";
                    return prepared;
                }

                try
                {
                    sources.Add(new KeyValuePair<string, byte[]>(name, File.ReadAllBytes(sourcePath)));
                }
                catch (Exception ex)
                {
                    prepared.Error = "Cannot read host source " + sourcePath + ": " + ex.Message;
                    return prepared;
                }
            }

            if (sources.Count == 0)
            {
                prepared.Error = "No host sources were given.";
                return prepared;
            }

            string roslynVersion = roslyn.Version ?? "unknown";
            prepared.Runtime = runtime;
            prepared.Roslyn = roslyn;
            prepared.Sources = sources;
            prepared.Key = ComputeCacheKey(request.UnityVersion, sources, runtime.Version, roslynVersion);
            prepared.OutputDirectory = Path.Combine(request.CacheRoot, prepared.Key);
            return prepared;
        }

        public static bool IsComplete(string outputDirectory)
        {
            return File.Exists(Path.Combine(outputDirectory, HostAssemblyFileName))
                && File.Exists(Path.Combine(outputDirectory, RuntimeConfigFileName))
                && File.Exists(Path.Combine(outputDirectory, BuildMarkerFileName));
        }

        private static string TargetFrameworkMoniker(string runtimeVersion)
        {
            int dot = runtimeVersion.IndexOf('.');
            string major = dot > 0 ? runtimeVersion.Substring(0, dot) : runtimeVersion;
            return "net" + major + ".0";
        }

        private static string BuildMarkerJson(string key, string unityVersion, string runtimeVersion, string roslynVersion, double buildMs)
        {
            var marker = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["key"] = key,
                ["recipe"] = RecipeVersion,
                ["unityVersion"] = unityVersion,
                ["runtimeVersion"] = runtimeVersion,
                ["roslynVersion"] = roslynVersion,
                ["buildMs"] = Math.Round(buildMs, 1),
                ["builtUtc"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
            };
            return RoslynHostJson.Serialize(marker) + "\n";
        }

        private static string QuoteRspPath(string path)
        {
            // csc response files split on whitespace outside quotes; a path cannot contain '"'.
            return "\"" + path + "\"";
        }

        private static string SanitizeKeyPart(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return "unknown";
            }

            var builder = new StringBuilder(value.Length);
            foreach (char c in value)
            {
                bool safe = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '.' || c == '_' || c == '-';
                builder.Append(safe ? c : '_');
            }

            return builder.ToString();
        }

        private static void AppendText(MemoryStream buffer, string text)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(text);
            buffer.Write(bytes, 0, bytes.Length);
        }

        private static string Trim(string output)
        {
            return output.Length <= MaxCompilerOutputChars ? output : output.Substring(0, MaxCompilerOutputChars) + "\n...(truncated)";
        }

        private static void DeleteStaleTempDirectories(string cacheRoot)
        {
            try
            {
                foreach (string directory in Directory.GetDirectories(cacheRoot, ".build-*"))
                {
                    if (DateTime.UtcNow - Directory.GetLastWriteTimeUtc(directory) > StaleTempAge)
                    {
                        TryDeleteDirectory(directory);
                    }
                }
            }
            catch (Exception)
            {
                // Best effort; a leftover temp folder only costs disk space.
            }
        }

        private static void TryDeleteDirectory(string directory)
        {
            try
            {
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, true);
                }
            }
            catch (Exception)
            {
                // Best effort.
            }
        }

        private static RoslynHostBuildResult Cleanup(string tempDirectory, RoslynHostBuildResult result)
        {
            TryDeleteDirectory(tempDirectory);
            return result;
        }

        private static RoslynHostBuildResult Hit(Stopwatch total, string key, string outputDirectory)
        {
            total.Stop();
            return new RoslynHostBuildResult(
                true,
                true,
                key,
                outputDirectory,
                Path.Combine(outputDirectory, HostAssemblyFileName),
                Path.Combine(outputDirectory, RuntimeConfigFileName),
                0,
                total.Elapsed.TotalMilliseconds,
                null,
                null,
                null);
        }

        private static RoslynHostBuildResult Failed(
            Stopwatch total,
            string? key,
            string error,
            double buildMs = 0,
            int? exitCode = null,
            string? output = null)
        {
            total.Stop();
            return new RoslynHostBuildResult(false, false, key, null, null, null, buildMs, total.Elapsed.TotalMilliseconds, exitCode, output, error);
        }
    }
}
