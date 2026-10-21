// Roslyn compile host for `execute`. Not imported by Unity (the folder ends in '~'): the editor
// builds this file, together with the shared Runtime/Protocol/RoslynHost*.cs wire files, with the
// csc that ships inside the editor install, and runs it on the editor's bundled .NET runtime.
// See RoslynHostBuilder for the build and RoslynHostArguments for the command line.
//
// Pipe: stdin/stdout carry RoslynHostFrameCodec frames only; stderr is a free-form log.
// Lifetime: exits on `quit`, on stdin EOF, and within ~2 s of the parent (editor) process going away.
// Prewarm is opt-in (`--prewarm`) so the editor's prewarm policy decides when the CPU is spent: one
// dummy compile against the host's own framework before `hello` (hello.prewarmMs), one against the
// editor references after the first `setRefs` (refsSet.prewarmMs). Without it, both report 0.
// Roslyn API use is kept to what Roslyn 3.x also has, so an older bundled compiler still builds it.
#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Text;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.Text;
using UnityCli.Protocol;

namespace UnityCliBridge.RoslynHost
{
    public static class UcbRoslynHost
    {
        public const int ExitClean = 0;
        public const int ExitStartupFailed = 1;
        public const int ExitProtocolError = 2;
        public const int ExitParentGone = 3;

        public static int Main(string[] args)
        {
            Stream input = new BufferedStream(Console.OpenStandardInput(), 64 * 1024);
            Stream output = Console.OpenStandardOutput();
            TextWriter log = Console.Error;

            // Anything that writes to Console.Out by accident must not corrupt the frame stream.
            Console.SetOut(log);
            return Run(args, input, output, log, ParentProcessWatch.DefaultInterval, code => Environment.Exit(code));
        }

        /// <summary>
        /// Everything <see cref="Main"/> does, on explicit streams. <paramref name="exit"/> is called
        /// from the watch thread when the parent goes away; the returned code covers every other exit.
        /// Nothing here touches a Roslyn type before the resolver is registered.
        /// </summary>
        public static int Run(string[] args, Stream input, Stream output, TextWriter log, TimeSpan parentPollInterval, Action<int> exit)
        {
            if (!RoslynHostArguments.TryParse(args, out RoslynHostArguments? options, out string? error) || options == null)
            {
                Log(log, "invalid arguments: " + error);
                return ExitStartupFailed;
            }

            if (!Directory.Exists(options.RoslynDirectory))
            {
                Log(log, "Roslyn directory does not exist: " + options.RoslynDirectory);
                return ExitStartupFailed;
            }

            RoslynAssemblyResolver.Register(options.RoslynDirectory);
            using (ParentProcessWatch watch = ParentProcessWatch.Start(
                options.ParentProcessId,
                options.ParentStartUnixMs,
                parentPollInterval,
                ParentProcessWatch.ProbeProcess,
                reason =>
                {
                    Log(log, reason + "; exiting");
                    exit(ExitParentGone);
                }))
            {
                if (watch.IsGone)
                {
                    return ExitParentGone;
                }

                try
                {
                    return Serve(options, input, output, log);
                }
                catch (Exception ex)
                {
                    // Typically Roslyn failing to load from --roslyn-dir.
                    Log(log, "startup failed: " + ex);
                    return ExitStartupFailed;
                }
            }
        }

        // Separate, non-inlined method: the JIT resolves Roslyn types used by RoslynHostServer only
        // when this runs, which is after the Resolving handler is in place.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int Serve(RoslynHostArguments options, Stream input, Stream output, TextWriter log)
        {
            var server = new RoslynHostServer(input, output, log, options.ProjectHash, options.Prewarm);
            return server.Run() == RoslynHostExitReason.ProtocolError ? ExitProtocolError : ExitClean;
        }

        internal static void Log(TextWriter log, string message)
        {
            try
            {
                log.WriteLine("[ucb-roslyn-host] " + message);
                log.Flush();
            }
            catch (Exception)
            {
                // stderr gone: nothing left to tell.
            }
        }
    }

    /// <summary>Loads Roslyn (and anything else the default context cannot find) from the bundled compiler folder.</summary>
    public static class RoslynAssemblyResolver
    {
        private static string? _directory;
        private static int _registered;

        public static void Register(string directory)
        {
            _directory = Path.GetFullPath(directory);
            if (Interlocked.Exchange(ref _registered, 1) == 0)
            {
                AssemblyLoadContext.Default.Resolving += Resolve;
            }
        }

        private static Assembly? Resolve(AssemblyLoadContext context, AssemblyName name)
        {
            string? directory = _directory;
            if (directory == null || string.IsNullOrEmpty(name.Name))
            {
                return null;
            }

            string candidate = string.IsNullOrEmpty(name.CultureName)
                ? Path.Combine(directory, name.Name + ".dll")
                : Path.Combine(directory, name.CultureName, name.Name + ".dll");
            return File.Exists(candidate) ? context.LoadFromAssemblyPath(candidate) : null;
        }
    }

    public readonly struct ParentProbe
    {
        public static readonly ParentProbe Dead = new ParentProbe(false, null);

        public ParentProbe(bool alive, DateTime? startTimeUtc)
        {
            Alive = alive;
            StartTimeUtc = startTimeUtc;
        }

        public bool Alive { get; }

        public DateTime? StartTimeUtc { get; }
    }

    /// <summary>
    /// Polls the parent pid. Fires once when the process is gone, or when the pid now belongs to a
    /// process with a different start time (pid reuse, mainly a Windows concern). The start time seen
    /// at startup is the baseline; an optional expected start time from the command line also
    /// catches reuse that happened before the host started.
    /// </summary>
    public sealed class ParentProcessWatch : IDisposable
    {
        public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(2);

        private const double BaselineToleranceMs = 1000;
        private const double ExpectedStartToleranceMs = 2000;
        private static readonly DateTime UnixEpoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        private readonly int _pid;
        private readonly Func<int, ParentProbe> _probe;
        private readonly Action<string> _onGone;
        private DateTime? _baseline;
        private Timer? _timer;
        private int _fired;
        private int _disposed;

        private ParentProcessWatch(int pid, Func<int, ParentProbe> probe, Action<string> onGone)
        {
            _pid = pid;
            _probe = probe;
            _onGone = onGone;
        }

        public bool IsGone => Volatile.Read(ref _fired) != 0;

        public string? GoneReason { get; private set; }

        /// <summary>Checks once synchronously (firing immediately if the parent is already gone), then every <paramref name="interval"/>.</summary>
        public static ParentProcessWatch Start(
            int pid,
            long? expectedStartUnixMs,
            TimeSpan interval,
            Func<int, ParentProbe> probe,
            Action<string> onGone)
        {
            var watch = new ParentProcessWatch(pid, probe, onGone);
            ParentProbe first = probe(pid);
            if (!first.Alive)
            {
                watch.Fire("parent process " + pid + " is not running");
                return watch;
            }

            watch._baseline = first.StartTimeUtc;
            if (expectedStartUnixMs.HasValue && first.StartTimeUtc.HasValue)
            {
                double actual = (first.StartTimeUtc.Value - UnixEpoch).TotalMilliseconds;
                if (Math.Abs(actual - expectedStartUnixMs.Value) > ExpectedStartToleranceMs)
                {
                    watch.Fire(
                        "parent pid " + pid + " started at " + actual.ToString("F0", CultureInfo.InvariantCulture)
                        + " ms, expected " + expectedStartUnixMs.Value.ToString(CultureInfo.InvariantCulture) + " ms (pid reused)");
                    return watch;
                }
            }

            watch._timer = new Timer(_ => watch.Check(), null, interval, interval);
            return watch;
        }

        public static ParentProbe ProbeProcess(int pid)
        {
            try
            {
                using (Process process = Process.GetProcessById(pid))
                {
                    if (process.HasExited)
                    {
                        return ParentProbe.Dead;
                    }

                    DateTime? start = null;
                    try
                    {
                        start = process.StartTime.ToUniversalTime();
                    }
                    catch (Exception)
                    {
                        // Start time unavailable on this platform or for this process: existence only.
                    }

                    return new ParentProbe(true, start);
                }
            }
            catch (ArgumentException)
            {
                return ParentProbe.Dead;
            }
            catch (InvalidOperationException)
            {
                return ParentProbe.Dead;
            }
            catch (Exception)
            {
                // Access denied and similar: the process exists but cannot be inspected.
                return new ParentProbe(true, null);
            }
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref _disposed, 1);
            Interlocked.Exchange(ref _timer, null)?.Dispose();
        }

        private void Check()
        {
            if (Volatile.Read(ref _disposed) != 0 || IsGone)
            {
                return;
            }

            ParentProbe probe = _probe(_pid);
            if (!probe.Alive)
            {
                Fire("parent process " + _pid + " exited");
                return;
            }

            if (_baseline.HasValue && probe.StartTimeUtc.HasValue
                && Math.Abs((probe.StartTimeUtc.Value - _baseline.Value).TotalMilliseconds) > BaselineToleranceMs)
            {
                Fire("parent pid " + _pid + " now belongs to a different process");
            }
        }

        private void Fire(string reason)
        {
            if (Volatile.Read(ref _disposed) != 0 || Interlocked.Exchange(ref _fired, 1) != 0)
            {
                return;
            }

            GoneReason = reason;
            Interlocked.Exchange(ref _timer, null)?.Dispose();
            _onGone(reason);
        }
    }

    public enum RoslynHostExitReason
    {
        Quit,
        EndOfInput,
        ProtocolError,
        OutputClosed,
    }

    /// <summary>The request loop: hello, then one reply per request until quit, EOF, or a corrupt frame.</summary>
    public sealed class RoslynHostServer
    {
        private readonly Stream _input;
        private readonly Stream _output;
        private readonly TextWriter _log;
        private readonly string _marker;
        private readonly bool _prewarm;
        private readonly RoslynHostFrameLimits _limits;
        private bool _referencesPrewarmed;

        public RoslynHostServer(Stream input, Stream output, TextWriter log, string marker, bool prewarm, RoslynHostFrameLimits? limits = null)
        {
            _input = input;
            _output = output;
            _log = log;
            _marker = marker;
            _prewarm = prewarm;
            _limits = limits ?? RoslynHostFrameLimits.Default;
            Compiler = new RoslynCompilerService();
        }

        public RoslynCompilerService Compiler { get; }

        public RoslynHostExitReason Run()
        {
            Stopwatch startup = Stopwatch.StartNew();
            double prewarmMs = _prewarm ? Prewarm(Compiler.PrewarmFramework) : 0;
            var hello = new RoslynHostHello
            {
                RuntimeDirectory = RuntimeEnvironment.GetRuntimeDirectory(),
                CoreLibraryPath = typeof(object).Assembly.Location,
                RuntimeVersion = Environment.Version.ToString(),
                RoslynVersion = Compiler.RoslynVersion,
                ProcessId = Environment.ProcessId,
                PrewarmMs = Math.Round(prewarmMs, 3),
                Marker = _marker,
            };
            if (!TrySend(hello.ToMessage()))
            {
                return RoslynHostExitReason.OutputClosed;
            }

            UcbRoslynHost.Log(_log, "ready in " + startup.ElapsedMilliseconds + " ms (roslyn " + hello.RoslynVersion
                + ", runtime " + hello.RuntimeVersion + ", prewarm " + prewarmMs.ToString("F0", CultureInfo.InvariantCulture) + " ms)");

            while (true)
            {
                RoslynHostMessage? message;
                try
                {
                    message = RoslynHostFrameCodec.ReadMessage(_input, _limits);
                }
                catch (RoslynHostFrameException ex)
                {
                    UcbRoslynHost.Log(_log, "corrupt frame, exiting: " + ex.Message);
                    TrySend(new RoslynHostError { Message = ex.Message, Fatal = true }.ToMessage());
                    return RoslynHostExitReason.ProtocolError;
                }
                catch (IOException ex)
                {
                    UcbRoslynHost.Log(_log, "stdin failed, exiting: " + ex.Message);
                    return RoslynHostExitReason.EndOfInput;
                }

                if (message == null)
                {
                    UcbRoslynHost.Log(_log, "stdin closed, exiting");
                    return RoslynHostExitReason.EndOfInput;
                }

                if (string.Equals(message.Type, RoslynHostMessageTypes.Quit, StringComparison.Ordinal))
                {
                    UcbRoslynHost.Log(_log, "quit received, exiting");
                    return RoslynHostExitReason.Quit;
                }

                RoslynHostMessage reply;
                try
                {
                    reply = Handle(message);
                }
                catch (Exception ex)
                {
                    reply = new RoslynHostError { Message = "Failed to handle '" + message.Type + "': " + ex.Message }.ToMessage();
                }

                if (!TrySend(reply))
                {
                    return RoslynHostExitReason.OutputClosed;
                }
            }
        }

        private RoslynHostMessage Handle(RoslynHostMessage message)
        {
            switch (message.Type)
            {
                case RoslynHostMessageTypes.Ping:
                    var pong = new Dictionary<string, object?>(StringComparer.Ordinal);
                    string? id = RoslynHostJson.GetString(message.Header, "id");
                    if (id != null)
                    {
                        pong["id"] = id;
                    }

                    return new RoslynHostMessage(RoslynHostMessageTypes.Pong, pong);
                case RoslynHostMessageTypes.SetRefs:
                    RoslynHostSetRefs setRefs = RoslynHostSetRefs.FromMessage(message);
                    RoslynHostRefsSet ack = Compiler.SetReferences(setRefs.RefsHash, setRefs.Paths);
                    if (_prewarm && !_referencesPrewarmed && ack.Count > 0)
                    {
                        _referencesPrewarmed = true;
                        ack.PrewarmMs = Math.Round(Prewarm(Compiler.PrewarmReferences), 3);
                    }

                    UcbRoslynHost.Log(_log, "setRefs " + ack.RefsHash + ": " + ack.Count + " refs (" + ack.Loaded + " read, "
                        + ack.Reused + " cached, " + ack.Skipped.Count + " skipped) in "
                        + ack.LoadMs.ToString("F0", CultureInfo.InvariantCulture) + " ms");
                    return ack.ToMessage();
                case RoslynHostMessageTypes.Compile:
                    RoslynHostCompileRequest request = RoslynHostCompileRequest.FromMessage(message);
                    return Compiler.Compile(request).ToMessage();
                default:
                    return new RoslynHostError { Message = "Unknown message type '" + message.Type + "'." }.ToMessage();
            }
        }

        /// <summary>Prewarm only speeds up the next compile; a failure is logged and never fails the host or the request.</summary>
        private double Prewarm(Func<double> step)
        {
            try
            {
                return step();
            }
            catch (Exception ex)
            {
                UcbRoslynHost.Log(_log, "prewarm failed (ignored): " + ex.GetType().Name + ": " + ex.Message);
                return 0;
            }
        }

        private bool TrySend(RoslynHostMessage message)
        {
            try
            {
                RoslynHostFrameCodec.WriteMessage(_output, message, _limits);
                return true;
            }
            catch (RoslynHostFrameException ex)
            {
                // The reply itself is too large; say so instead.
                return TrySend(new RoslynHostError { Message = ex.Message }.ToMessage());
            }
            catch (IOException ex)
            {
                UcbRoslynHost.Log(_log, "stdout closed, exiting: " + ex.Message);
                return false;
            }
            catch (ObjectDisposedException)
            {
                return false;
            }
        }
    }

    /// <summary>
    /// Roslyn-facing half of the host: the metadata reference cache and the compile itself.
    /// Options mirror the execute pipeline: DLL, Release, no concurrent build, C# 9 unless the
    /// request says otherwise, and defines / unsafe / suppressed warnings from the request.
    /// </summary>
    public sealed class RoslynCompilerService
    {
        public const string DefaultFilePath = "execute.cs";
        public const int MaxDiagnostics = 200;

        /// <summary>
        /// Assembly-unification notices (UnityEngine modules reference mscorlib 2.0.0.0, the editor
        /// passes 4.0.0.0). Every compile against editor references would carry them; the .NET SDK
        /// suppresses the same pair by default.
        /// </summary>
        public static readonly string[] AlwaysSuppressed = { "CS1701", "CS1702" };

        private const string FrameworkPrewarmSource =
            "using System;\n"
            + "using System.Collections.Generic;\n"
            + "using System.Linq;\n"
            + "namespace UcbRoslynHostPrewarm\n"
            + "{\n"
            + "    public static class FrameworkProbe\n"
            + "    {\n"
            + "        public static int Run(int n)\n"
            + "        {\n"
            + "            var values = new List<int>();\n"
            + "            for (int i = 0; i < n; i++) { values.Add(i * 3); }\n"
            + "            string text = string.Format(\"{0}:{1}\", n, values.Count);\n"
            + "            return values.Where(v => v % 2 == 0).Select(v => v + text.Length).Sum();\n"
            + "        }\n"
            + "    }\n"
            + "}\n";

        private static readonly string[] FrameworkPrewarmReferences =
        {
            "System.Private.CoreLib.dll",
            "System.Runtime.dll",
            "System.Collections.dll",
            "System.Linq.dll",
        };

        private Dictionary<string, CachedReference> _cache = new Dictionary<string, CachedReference>(StringComparer.Ordinal);
        private List<MetadataReference>? _references;
        private bool _referencesIncludeUnityEngine;

        public RoslynCompilerService()
        {
            RoslynVersion = ReadRoslynVersion();
        }

        public string RoslynVersion { get; }

        public string RefsHash { get; private set; } = string.Empty;

        public int ReferenceCount => _references == null ? 0 : _references.Count;

        /// <summary>Total metadata files read from disk over the host's lifetime.</summary>
        public int FilesRead { get; private set; }

        public RoslynHostRefsSet SetReferences(string refsHash, IList<string> paths)
        {
            Stopwatch stopwatch = Stopwatch.StartNew();
            var ack = new RoslynHostRefsSet { RefsHash = refsHash ?? string.Empty };
            var next = new Dictionary<string, CachedReference>(StringComparer.Ordinal);
            var references = new List<MetadataReference>(paths.Count);
            bool unityEngine = false;
            foreach (string path in paths)
            {
                if (string.IsNullOrEmpty(path) || next.ContainsKey(path))
                {
                    continue;
                }

                long ticks;
                long length;
                try
                {
                    var info = new FileInfo(path);
                    if (!info.Exists)
                    {
                        ack.Skipped.Add(path);
                        continue;
                    }

                    ticks = info.LastWriteTimeUtc.Ticks;
                    length = info.Length;
                }
                catch (Exception ex)
                {
                    ack.Skipped.Add(path + ": " + ex.Message);
                    continue;
                }

                if (_cache.TryGetValue(path, out CachedReference? cached) && cached.Ticks == ticks && cached.Length == length)
                {
                    ack.Reused++;
                }
                else
                {
                    try
                    {
                        cached = new CachedReference(ticks, length, MetadataReference.CreateFromFile(path));
                    }
                    catch (Exception ex)
                    {
                        ack.Skipped.Add(path + ": " + ex.Message);
                        continue;
                    }

                    FilesRead++;
                    ack.Loaded++;
                }

                next[path] = cached;
                references.Add(cached.Reference);
                if (Path.GetFileName(path).StartsWith("UnityEngine", StringComparison.OrdinalIgnoreCase))
                {
                    unityEngine = true;
                }
            }

            // Only the current set stays cached, so a long session does not accumulate stale metadata.
            _cache = next;
            _references = references;
            _referencesIncludeUnityEngine = unityEngine;
            RefsHash = ack.RefsHash;
            ack.Count = references.Count;
            ack.LoadMs = Math.Round(stopwatch.Elapsed.TotalMilliseconds, 3);
            return ack;
        }

        public RoslynHostCompileResult Compile(RoslynHostCompileRequest request)
        {
            Stopwatch stopwatch = Stopwatch.StartNew();
            var result = new RoslynHostCompileResult { Id = request.Id };
            try
            {
                if (string.IsNullOrEmpty(request.Id))
                {
                    result.Error = "The compile request has no id.";
                }
                else if (_references == null)
                {
                    result.Error = "No references are set; send setRefs first.";
                }
                else if (request.RefsHash.Length > 0 && !string.Equals(request.RefsHash, RefsHash, StringComparison.Ordinal))
                {
                    result.Error = "refsHash mismatch: the host has '" + RefsHash + "', the request has '" + request.RefsHash + "'.";
                }
                else if (!TryParseLanguageVersion(request.LanguageVersion, out LanguageVersion languageVersion))
                {
                    result.Error = "Unsupported language version '" + request.LanguageVersion + "'.";
                }
                else
                {
                    CompileCore(
                        request.Source,
                        string.IsNullOrEmpty(request.FilePath) ? DefaultFilePath : request.FilePath,
                        request.Defines,
                        languageVersion,
                        request.AllowUnsafe,
                        request.NoWarn,
                        string.IsNullOrEmpty(request.AssemblyName) ? "ucb_" + Guid.NewGuid().ToString("N") : request.AssemblyName,
                        _references,
                        result);
                }
            }
            catch (Exception ex)
            {
                result.Ok = false;
                result.Assembly = null;
                result.Error = "Compiler failure: " + ex.GetType().FullName + ": " + ex.Message;
            }

            result.CompileMs = Math.Round(stopwatch.Elapsed.TotalMilliseconds, 3);
            return result;
        }

        /// <summary>
        /// First prewarm dummy, before hello: plain C# against the host's own framework, which JITs
        /// the parser, binder, and emitter. Returns elapsed ms; failures are only logged by the caller.
        /// </summary>
        public double PrewarmFramework()
        {
            Stopwatch stopwatch = Stopwatch.StartNew();
            string frameworkDirectory = Path.GetDirectoryName(typeof(object).Assembly.Location) ?? string.Empty;
            var references = new List<MetadataReference>();
            foreach (string name in FrameworkPrewarmReferences)
            {
                string path = Path.Combine(frameworkDirectory, name);
                if (File.Exists(path))
                {
                    references.Add(MetadataReference.CreateFromFile(path));
                }
            }

            var result = new RoslynHostCompileResult();
            CompileCore(FrameworkPrewarmSource, "prewarm-framework.cs", new List<string>(), LanguageVersion.CSharp9, false, new List<string>(), "UcbRoslynHostPrewarm1", references, result);
            return stopwatch.Elapsed.TotalMilliseconds;
        }

        /// <summary>
        /// Second prewarm dummy, after the first setRefs: binds against the editor's references
        /// (UnityEngine types when they are present), which loads their symbol tables.
        /// </summary>
        public double PrewarmReferences()
        {
            if (_references == null)
            {
                return 0;
            }

            Stopwatch stopwatch = Stopwatch.StartNew();
            string unity = _referencesIncludeUnityEngine
                ? "            var go = new UnityEngine.GameObject(\"ucb-prewarm\");\n"
                  + "            builder.Append(go.name).Append(UnityEngine.Vector3.one);\n"
                  + "            UnityEngine.Debug.Log(builder.ToString());\n"
                : string.Empty;
            string source =
                "namespace UcbRoslynHostPrewarm\n"
                + "{\n"
                + "    public static class ReferenceProbe\n"
                + "    {\n"
                + "        public static string Run()\n"
                + "        {\n"
                + "            var builder = new System.Text.StringBuilder();\n"
                + "            builder.Append(System.Linq.Enumerable.Count(new[] { 1, 2, 3 }));\n"
                + unity
                + "            return builder.ToString();\n"
                + "        }\n"
                + "    }\n"
                + "}\n";
            var result = new RoslynHostCompileResult();
            CompileCore(source, "prewarm-references.cs", new List<string>(), LanguageVersion.CSharp9, false, new List<string>(), "UcbRoslynHostPrewarm2", _references, result);
            return stopwatch.Elapsed.TotalMilliseconds;
        }

        public static bool TryParseLanguageVersion(string? text, out LanguageVersion version)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                version = LanguageVersion.CSharp9;
                return true;
            }

            if (!LanguageVersionFacts.TryParse(text!.Trim(), out version))
            {
                return false;
            }

            version = version.MapSpecifiedToEffectiveVersion();
            return true;
        }

        /// <summary><c>436</c>, <c>0436</c>, and <c>cs0436</c> all become <c>CS0436</c>.</summary>
        public static string NormalizeDiagnosticId(string id)
        {
            string trimmed = id.Trim();
            if (trimmed.Length > 0 && int.TryParse(trimmed, NumberStyles.None, CultureInfo.InvariantCulture, out int number))
            {
                return "CS" + number.ToString("D4", CultureInfo.InvariantCulture);
            }

            if (trimmed.StartsWith("CS", StringComparison.OrdinalIgnoreCase))
            {
                return "CS" + trimmed.Substring(2);
            }

            return trimmed;
        }

        private static void CompileCore(
            string source,
            string filePath,
            IList<string> defines,
            LanguageVersion languageVersion,
            bool allowUnsafe,
            IList<string> noWarn,
            string assemblyName,
            List<MetadataReference> references,
            RoslynHostCompileResult result)
        {
            var symbols = new List<string>();
            foreach (string define in defines)
            {
                string trimmed = define == null ? string.Empty : define.Trim();
                if (trimmed.Length > 0 && !symbols.Contains(trimmed))
                {
                    symbols.Add(trimmed);
                }
            }

            var suppressed = new Dictionary<string, ReportDiagnostic>(StringComparer.OrdinalIgnoreCase);
            foreach (string id in AlwaysSuppressed)
            {
                suppressed[id] = ReportDiagnostic.Suppress;
            }

            foreach (string id in noWarn)
            {
                if (!string.IsNullOrWhiteSpace(id))
                {
                    suppressed[NormalizeDiagnosticId(id)] = ReportDiagnostic.Suppress;
                }
            }

            var parseOptions = new CSharpParseOptions(
                languageVersion: languageVersion,
                documentationMode: DocumentationMode.None,
                kind: SourceCodeKind.Regular,
                preprocessorSymbols: symbols);
            SyntaxTree tree = CSharpSyntaxTree.ParseText(SourceText.From(source ?? string.Empty, Encoding.UTF8), parseOptions, filePath);
            CSharpCompilationOptions options = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
                .WithOptimizationLevel(OptimizationLevel.Release)
                .WithConcurrentBuild(false)
                .WithAllowUnsafe(allowUnsafe)
                .WithSpecificDiagnosticOptions(suppressed);
            CSharpCompilation compilation = CSharpCompilation.Create(assemblyName, new[] { tree }, references, options);

            using (var stream = new MemoryStream())
            {
                EmitResult emit = compilation.Emit(stream);
                foreach (Diagnostic diagnostic in emit.Diagnostics)
                {
                    if (result.Diagnostics.Count >= MaxDiagnostics)
                    {
                        break;
                    }

                    if (diagnostic.IsSuppressed
                        || (diagnostic.Severity != DiagnosticSeverity.Error && diagnostic.Severity != DiagnosticSeverity.Warning))
                    {
                        continue;
                    }

                    result.Diagnostics.Add(ToWire(diagnostic));
                }

                result.Ok = emit.Success;
                result.Assembly = emit.Success ? stream.ToArray() : null;
            }
        }

        private static RoslynHostDiagnostic ToWire(Diagnostic diagnostic)
        {
            // Mapped span: honours #line, so wrapper-relative positions become user-code lines.
            FileLinePositionSpan span = diagnostic.Location.GetMappedLineSpan();
            return new RoslynHostDiagnostic
            {
                Id = diagnostic.Id,
                Severity = diagnostic.Severity == DiagnosticSeverity.Error ? "error" : "warning",
                Line = span.IsValid ? span.StartLinePosition.Line + 1 : 0,
                Column = span.IsValid ? span.StartLinePosition.Character + 1 : 0,
                File = span.IsValid ? span.Path ?? string.Empty : string.Empty,
                Message = diagnostic.GetMessage(CultureInfo.InvariantCulture),
            };
        }

        private static string ReadRoslynVersion()
        {
            Assembly assembly = typeof(CSharpCompilation).Assembly;
            var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
            string? version = informational == null ? null : informational.InformationalVersion;
            if (string.IsNullOrEmpty(version))
            {
                Version? assemblyVersion = assembly.GetName().Version;
                version = assemblyVersion == null ? "unknown" : assemblyVersion.ToString();
            }

            int plus = version!.IndexOf('+');
            return plus >= 0 ? version.Substring(0, plus) : version;
        }

        private sealed class CachedReference
        {
            public CachedReference(long ticks, long length, MetadataReference reference)
            {
                Ticks = ticks;
                Length = length;
                Reference = reference;
            }

            public long Ticks { get; }

            public long Length { get; }

            public MetadataReference Reference { get; }
        }
    }
}
