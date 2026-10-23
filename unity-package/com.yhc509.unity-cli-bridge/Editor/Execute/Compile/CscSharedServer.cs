#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using UnityCli.Protocol;

namespace UnityCliBridge.Bridge.Editor
{
    /// <summary>Failure codes in a csc-shared fallback reason (<c>code: detail</c>).</summary>
    internal static class CscSharedFailureCodes
    {
        public const string Unavailable = "csc-shared-unavailable";
        public const string Failed = "csc-shared-failed";
        public const string Timeout = "csc-shared-timeout";
        public const string LoadFailed = "csc-shared-load-failed";
    }

    /// <summary>The bundled files csc-shared runs: the muxer, csc, and the compiler server.</summary>
    internal sealed class CscSharedToolchain
    {
        public const string ServerFileName = "VBCSCompiler.dll";

        public CscSharedToolchain(string dotnetPath, string cscPath, string serverPath, string roslynVersion)
        {
            DotnetPath = dotnetPath;
            CscPath = cscPath;
            ServerPath = serverPath;
            RoslynVersion = roslynVersion;
        }

        public string DotnetPath { get; }

        public string CscPath { get; }

        public string ServerPath { get; }

        public string RoslynVersion { get; }

        /// <summary>The runtime and Roslyn of <paramref name="report"/> (never a system .NET); null with a reason when incomplete.</summary>
        public static CscSharedToolchain? FromReport(BundledToolchainReport report, out string error)
        {
            BundledToolComponent? runtime = report.Runtime;
            BundledToolComponent? roslyn = report.Roslyn;
            if (runtime == null || !runtime.Found || string.IsNullOrEmpty(runtime.Path))
            {
                error = "bundled .NET runtime not found: " + (runtime?.Error ?? report.Error ?? "unknown");
                return null;
            }

            if (roslyn == null || !roslyn.Found || string.IsNullOrEmpty(roslyn.Path) || string.IsNullOrEmpty(roslyn.Directory))
            {
                error = "bundled Roslyn not found: " + (roslyn?.Error ?? report.Error ?? "unknown");
                return null;
            }

            string server = Path.Combine(roslyn.Directory!, ServerFileName);
            if (!File.Exists(server))
            {
                error = "bundled compiler server not found: " + server;
                return null;
            }

            error = string.Empty;
            return new CscSharedToolchain(runtime.Path!, roslyn.Path!, server, roslyn.Version ?? string.Empty);
        }
    }

    /// <summary>
    /// <c>Library/com.yhc509.unity-cli-bridge/csc-shared/server.json</c>: this project's editor used
    /// the compiler server on <see cref="Pipe"/>. Written before the first csc run of a domain and
    /// deleted once the server is stopped, so a record that survives is what proves a leftover server
    /// with that pipe belongs to this project.
    /// </summary>
    internal sealed class CscSharedServerRecord
    {
        public string Pipe { get; set; } = string.Empty;

        public int EditorPid { get; set; }

        /// <summary>The server's pid once found; 0 when not known.</summary>
        public int ServerPid { get; set; }

        public string StartedUtc { get; set; } = string.Empty;

        public string ToJson()
        {
            var obj = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["pipe"] = Pipe,
                ["editorPid"] = EditorPid,
                ["serverPid"] = ServerPid,
                ["startedUtc"] = StartedUtc,
            };
            return RoslynHostJson.Serialize(obj) + "\n";
        }

        public static CscSharedServerRecord? Parse(string json)
        {
            try
            {
                if (!(RoslynHostJson.Parse(json) is Dictionary<string, object?> h))
                {
                    return null;
                }

                return new CscSharedServerRecord
                {
                    Pipe = RoslynHostJson.GetString(h, "pipe") ?? string.Empty,
                    EditorPid = (int)RoslynHostJson.GetInt64(h, "editorPid", 0, 0, int.MaxValue),
                    ServerPid = (int)RoslynHostJson.GetInt64(h, "serverPid", 0, 0, int.MaxValue),
                    StartedUtc = RoslynHostJson.GetString(h, "startedUtc") ?? string.Empty,
                };
            }
            catch (FormatException)
            {
                return null;
            }
        }

        public static CscSharedServerRecord? TryRead(string path)
        {
            try
            {
                return File.Exists(path) ? Parse(File.ReadAllText(path, Encoding.UTF8)) : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        public void Write(string path)
        {
            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            // Temp file + replace, same as status.json. A direct write of the live server.json
            // loses on Windows when a reader still has the previous file open, and that failure
            // used to leave serverPid at the 0 written before the server existed.
            string temp = path + "." + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp";
            File.WriteAllText(temp, ToJson(), new UTF8Encoding(false));
            try
            {
                if (File.Exists(path))
                {
                    File.Replace(temp, path, null);
                }
                else
                {
                    File.Move(temp, path);
                }
            }
            catch (Exception)
            {
                try
                {
                    File.Delete(temp);
                }
                catch (Exception)
                {
                    // Best effort.
                }

                throw;
            }
        }
    }

    /// <summary>
    /// Which compiler-server processes may be stopped (plan §2.7, same spirit as the host's orphan rule).
    /// Only with a record from this project's Library, whose pipe is this project's
    /// (<c>ucb-&lt;projectHash[:8]&gt;-…</c>), whose editor is this one or gone, and only processes whose
    /// command line is a compiler server on exactly that pipe. The editor's own script-compiler server
    /// uses a different pipe, so it never matches.
    /// </summary>
    internal static class CscSharedOrphanPolicy
    {
        public static List<int> SelectTargets(
            CscSharedServerRecord? record,
            string projectHash,
            int currentEditorPid,
            Func<int, bool> isAlive,
            IEnumerable<KeyValuePair<int, string>> processes,
            out string reason)
        {
            var targets = new List<int>();
            if (record == null)
            {
                reason = "no server record";
                return targets;
            }

            if (!CscSharedPipe.IsProjectPipe(record.Pipe, projectHash))
            {
                reason = "record pipe '" + record.Pipe + "' is not this project's";
                return targets;
            }

            if (record.EditorPid > 0 && record.EditorPid != currentEditorPid && isAlive(record.EditorPid))
            {
                reason = "editor pid " + record.EditorPid + " that used the server is still running";
                return targets;
            }

            foreach (KeyValuePair<int, string> process in processes)
            {
                if (process.Key > 0 && process.Key != currentEditorPid && CscSharedPipe.IsServerCommandLine(process.Value, record.Pipe))
                {
                    targets.Add(process.Key);
                }
            }

            reason = targets.Count == 0 ? "no server on " + record.Pipe : "server on " + record.Pipe + " is this project's";
            return targets;
        }
    }

    /// <summary>Process probes, swappable in tests.</summary>
    internal interface ICscSharedProcessTable
    {
        /// <summary>Processes whose command line mentions the compiler server.</summary>
        IReadOnlyList<KeyValuePair<int, string>> ListServers();

        /// <summary>
        /// True when <see cref="ListServers"/> is too slow for the main thread (Windows: CIM through
        /// PowerShell, seconds). A stop on the main thread then never lists the process table.
        /// </summary>
        bool ListingIsSlow { get; }

        bool IsAlive(int pid);

        /// <summary>
        /// What proves later that a pid is still the process <see cref="ListServers"/> found: its start
        /// time on Windows; null on macOS/Linux, where <see cref="IsServerOnPipe"/> re-reads the command line.
        /// </summary>
        long? ReadStartTicks(int pid);

        /// <summary>
        /// Whether <paramref name="pid"/> is still the compiler server on <paramref name="pipe"/>,
        /// without listing every process: Windows compares the start time captured when the server was
        /// found (reading one command line also needs PowerShell there); macOS/Linux read the command line.
        /// </summary>
        bool IsServerOnPipe(int pid, string pipe, long? startTicks);

        void Kill(int pid);

        /// <summary>Starts a short-lived child without waiting for it or reading its output.</summary>
        void StartDetached(ProcessStartInfo startInfo);
    }

    internal sealed class CscSharedProcessTable : ICscSharedProcessTable
    {
        public static readonly CscSharedProcessTable Instance = new CscSharedProcessTable();

        private static bool IsWindows
        {
            get { return RuntimeInformation.IsOSPlatform(OSPlatform.Windows); }
        }

        public bool ListingIsSlow
        {
            get { return IsWindows; }
        }

        public IReadOnlyList<KeyValuePair<int, string>> ListServers()
        {
            return RoslynHostProcessInspector.FindByCommandLine("VBCSCompiler");
        }

        public bool IsAlive(int pid)
        {
            return RoslynHostProcessInspector.IsAlive(pid);
        }

        public long? ReadStartTicks(int pid)
        {
            if (!IsWindows || pid <= 0)
            {
                return null;
            }

            try
            {
                using (Process process = Process.GetProcessById(pid))
                {
                    return process.StartTime.ToUniversalTime().Ticks;
                }
            }
            catch (Exception)
            {
                return null;
            }
        }

        public bool IsServerOnPipe(int pid, string pipe, long? startTicks)
        {
            if (pid <= 0)
            {
                return false;
            }

            if (!IsWindows)
            {
                return CscSharedPipe.IsServerCommandLine(RoslynHostProcessInspector.ReadCommandLine(pid), pipe);
            }

            if (!startTicks.HasValue)
            {
                return false;
            }

            try
            {
                using (Process process = Process.GetProcessById(pid))
                {
                    return !process.HasExited && process.StartTime.ToUniversalTime().Ticks == startTicks.Value;
                }
            }
            catch (Exception)
            {
                return false;
            }
        }

        public void Kill(int pid)
        {
            RoslynHostProcessInspector.TryKill(pid);
        }

        public void StartDetached(ProcessStartInfo startInfo)
        {
            startInfo.RedirectStandardInput = false;
            startInfo.RedirectStandardOutput = false;
            startInfo.RedirectStandardError = false;
            using (Process.Start(startInfo))
            {
                // Not waited for: the client exits on its own once the server has its request.
            }
        }
    }

    internal sealed class CscSharedServerOptions
    {
        /// <summary><c>EditorApplication.applicationContentsPath</c>.</summary>
        public string ContentsPath { get; set; } = string.Empty;

        public string ProjectHash { get; set; } = string.Empty;

        public string UnityVersion { get; set; } = string.Empty;

        public int EditorPid { get; set; }

        /// <summary><c>Library/com.yhc509.unity-cli-bridge/csc-shared</c>: record, status, and per-compile temp files.</summary>
        public string Directory { get; set; } = string.Empty;

        public string ConfiguredCompiler { get; set; } = string.Empty;

        public int CompileTimeoutMs { get; set; } = ExecuteCompilerSettings.DefaultCompileTimeoutMs;

        /// <summary>How long a stop waits for a killed server to leave the process table.</summary>
        public TimeSpan KillWait { get; set; } = TimeSpan.FromMilliseconds(250);

        /// <summary>How long the first compile of a domain waits for the leftover-server cleanup.</summary>
        public TimeSpan OrphanWait { get; set; } = TimeSpan.FromSeconds(3);

        /// <summary>How long a compile waits for a background retire of the server (normally tens of ms).</summary>
        public TimeSpan RetireWait { get; set; } = TimeSpan.FromSeconds(3);

        /// <summary>How long a retire waits for a server pid lookup still in flight before it lists the processes itself.</summary>
        public TimeSpan RetireDiscoveryWait { get; set; } = TimeSpan.FromSeconds(10);

        public bool WriteStatusFile { get; set; } = true;

        /// <summary>Tests: a fixed pipe instead of <see cref="CscSharedPipe.BuildName"/>.</summary>
        public string? PipeName { get; set; }

        /// <summary>Tests: a toolchain instead of the editor's bundled one.</summary>
        public Func<CscSharedToolchain?>? Toolchain { get; set; }
    }

    /// <summary>One csc run. Exactly one of: an assembly, the user's diagnostics, or an infrastructure failure.</summary>
    internal sealed class CscSharedRun
    {
        public byte[]? Assembly { get; set; }

        public List<RoslynHostDiagnostic> Diagnostics { get; set; } = new List<RoslynHostDiagnostic>();

        /// <summary><c>code: detail</c> (<see cref="CscSharedFailureCodes"/>) when csc could not compile at all.</summary>
        public string? InfrastructureFailure { get; set; }

        public double ElapsedMs { get; set; }
    }

    /// <summary>
    /// Runs <c>execute</c> compiles through <c>csc /shared</c> on the editor's bundled runtime and
    /// Roslyn, and owns the compiler server that csc starts on this project's pipe. csc is started
    /// only through <see cref="BundledDotnetProcess"/> (absolute muxer, scrubbed environment) with
    /// <c>DOTNET_HOST_PATH</c> set to the same muxer, because csc starts the server with whatever
    /// that variable (or else PATH) names. The run happens on a worker thread; the caller waits at
    /// most the compile timeout, then the client is killed and the compile is an infrastructure failure.
    /// When the Roslyn host takes over again, <see cref="RetireInBackground"/> stops the idle server
    /// off the main thread; the next csc run starts a new one.
    /// Unity-free; the editor hooks are in <c>CscSharedEditorLifecycle</c>.
    /// </summary>
    internal sealed class CscSharedServer
    {
        private readonly CscSharedServerOptions _options;
        private readonly ICscSharedProcessTable _processes;
        private readonly object _gate = new object();
        private readonly object _statusGate = new object();
        private readonly ManualResetEvent _orphansDone = new ManualResetEvent(true);
        private readonly ManualResetEvent _retireDone = new ManualResetEvent(true);
        private readonly object _recordGate = new object();

        private CscSharedToolchain? _toolchain;
        private string? _toolchainError;
        private bool _toolchainResolved;
        private bool _usedThisDomain;
        private bool _recordWritten;
        private int _serverPid;

        /// <summary>Start time of <see cref="_serverPid"/> where <see cref="ICscSharedProcessTable.ReadStartTicks"/> gives one (Windows).</summary>
        private long? _serverStartTicks;
        private int _discoveryAttempts;
        private bool _discovering;

        /// <summary>Bumped by every csc run; a retire that sees it change leaves the new server alone.</summary>
        private int _runGeneration;

        /// <summary>Bumped by <see cref="Shutdown"/>; a retire in flight stops acting once it changes.</summary>
        private int _stopGeneration;
        private bool _retiring;
        private int _retires;

        /// <summary>True once <c>server.json</c> reads back with <see cref="_serverPid"/>.</summary>
        private bool _recordSynced;

        /// <summary>
        /// How many times one domain looks for one server. The process is not always visible on the
        /// first scans. A dead recorded pid starts a fresh budget for the server that replaces it.
        /// </summary>
        private const int MaxDiscoveryAttempts = 8;

        private string _state = "idle";
        private string _note = string.Empty;
        private int _compiles;
        private double _firstCompileMs;
        private string _lastError = string.Empty;
        private DateTime? _lastErrorUtc;
        private double _lastShutdownMs;

        public CscSharedServer(CscSharedServerOptions options, ICscSharedProcessTable? processes = null)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _processes = processes ?? CscSharedProcessTable.Instance;
            PipeName = string.IsNullOrEmpty(options.PipeName)
                ? CscSharedPipe.BuildName(options.ProjectHash, options.UnityVersion)
                : options.PipeName!;
        }

        public string PipeName { get; }

        public string RecordPath
        {
            get { return Path.Combine(_options.Directory, CscSharedStatus.ServerRecordFileName); }
        }

        public string StatusPath
        {
            get { return Path.Combine(_options.Directory, CscSharedStatus.FileName); }
        }

        public string TempDirectory
        {
            get { return Path.Combine(_options.Directory, "tmp"); }
        }

        /// <summary>True once this domain ran csc (so a server may be up and the hooks have work to do).</summary>
        public bool UsedThisDomain
        {
            get
            {
                lock (_gate)
                {
                    return _usedThisDomain;
                }
            }
        }

        /// <summary>
        /// Domain-load entry (main thread). Writes the status file and, only when a server record was
        /// left behind (a failed stop, a killed editor), cleans that server up on a background thread.
        /// Without a record nothing else runs.
        /// </summary>
        public void OnDomainLoaded()
        {
            WriteStatus();
            if (!File.Exists(RecordPath))
            {
                return;
            }

            _orphansDone.Reset();
            var cleanup = new Thread(() =>
            {
                try
                {
                    CleanUpLeftoverServer();
                }
                finally
                {
                    _orphansDone.Set();
                }
            })
            {
                IsBackground = true,
                Name = "ucb-csc-shared-orphans",
            };
            cleanup.Start();
        }

        /// <summary>Kills a server this project left behind, per <see cref="CscSharedOrphanPolicy"/>. Returns the killed pids.</summary>
        public List<int> CleanUpLeftoverServer()
        {
            CscSharedServerRecord? record = CscSharedServerRecord.TryRead(RecordPath);
            if (record == null)
            {
                TryDelete(RecordPath);
                return new List<int>();
            }

            List<int> targets = CscSharedOrphanPolicy.SelectTargets(
                record,
                _options.ProjectHash,
                _options.EditorPid,
                _processes.IsAlive,
                _processes.ListServers(),
                out string reason);
            if (targets.Count == 0 && reason.StartsWith("editor pid ", StringComparison.Ordinal))
            {
                // Spared: another live editor still owns it.
                return targets;
            }

            foreach (int pid in targets)
            {
                _processes.Kill(pid);
            }

            TryDelete(RecordPath);
            return targets;
        }

        /// <summary>Main thread: compiles <paramref name="request"/> (the host-shaped inputs) through csc /shared.</summary>
        public CscSharedRun Compile(RoslynHostCompileRequest request, IReadOnlyList<string> references)
        {
            var watch = Stopwatch.StartNew();
            _orphansDone.WaitOne(_options.OrphanWait);
            _retireDone.WaitOne(_options.RetireWait);
            CscSharedToolchain? toolchain = ResolveToolchain(out string toolchainError);
            if (toolchain == null)
            {
                return Fail(CscSharedFailureCodes.Unavailable + ": " + toolchainError, watch);
            }

            string name = string.IsNullOrEmpty(request.AssemblyName) ? "ucb_execute_" + Guid.NewGuid().ToString("N") : request.AssemblyName;
            string directory = TempDirectory;
            string source = Path.Combine(directory, name + ".cs");
            string output = Path.Combine(directory, name + ".dll");
            string responseFile = Path.Combine(directory, name + ".rsp");
            try
            {
                Directory.CreateDirectory(directory);
                File.WriteAllText(source, request.Source ?? string.Empty, new UTF8Encoding(false));
                File.WriteAllText(
                    responseFile,
                    ResponseFileWriter.FormatContent(CscSharedCommandLine.BuildResponseFile(request, references, output)),
                    new UTF8Encoding(false));
                EnsureRecord();

                BundledDotnetRunResult? run = RunCsc(toolchain, responseFile, source, out string? runError);
                if (run == null)
                {
                    return Fail(runError ?? CscSharedFailureCodes.Failed + ": csc did not run", watch);
                }

                string text = run.StandardOutput + run.StandardError;
                List<RoslynHostDiagnostic> diagnostics = CscSharedCommandLine.ParseDiagnostics(text);
                if (run.ExitCode == 0 && File.Exists(output))
                {
                    byte[] bytes = File.ReadAllBytes(output);
                    return Succeed(new CscSharedRun { Assembly = bytes, Diagnostics = diagnostics }, watch);
                }

                if (run.ExitCode != 0 && CscSharedCommandLine.IsUserCompileError(diagnostics))
                {
                    return Succeed(new CscSharedRun { Diagnostics = diagnostics }, watch);
                }

                string summary = CscSharedCommandLine.Summarize(text);
                return Fail(
                    CscSharedFailureCodes.Failed + ": exit " + run.ExitCode + (summary.Length > 0 ? ": " + summary : ", no output"),
                    watch);
            }
            catch (Exception ex)
            {
                return Fail(CscSharedFailureCodes.Failed + ": " + ex.GetType().Name + ": " + ex.Message, watch);
            }
            finally
            {
                TryDelete(source);
                TryDelete(output);
                TryDelete(responseFile);
            }
        }

        /// <summary>
        /// Stops this project's server (main thread; <c>beforeAssemblyReload</c>, <c>quitting</c>) and
        /// keeps that cheap (<see cref="StopServerProcesses"/>): the server this domain found is killed
        /// directly once it is proven to still be ours, with no client process and no process listing.
        /// Does nothing at all (no process) when this domain never ran csc and no record is left.
        /// <c>lastShutdownMs</c> is the main-thread time of this call.
        /// </summary>
        public void Shutdown(string reason)
        {
            bool used;
            lock (_gate)
            {
                used = _usedThisDomain;
                _usedThisDomain = false;
                _recordWritten = false;
                _stopGeneration++;
            }

            if (!used && !File.Exists(RecordPath))
            {
                return;
            }

            var watch = Stopwatch.StartNew();
            bool stopped = StopServerProcesses(allowSlowListing: false, proceed: () => true);
            if (stopped)
            {
                TryDelete(RecordPath);
            }

            lock (_gate)
            {
                _serverPid = 0;
                _serverStartTicks = null;
                _recordSynced = false;
                _discoveryAttempts = 0;
                _state = "stopped";
                _note = (reason ?? string.Empty) + (stopped ? string.Empty : " (shutdown client started; the next domain load removes what is left)");
                _lastShutdownMs = watch.Elapsed.TotalMilliseconds;
            }

            WriteStatus();
        }

        /// <summary>
        /// Main thread, after the Roslyn host served a compile: stops a server this domain started
        /// (the host was building, starting, or down) on a background thread, so it does not sit idle
        /// next to the host until the editor quits. The compile does not wait. A csc run that starts
        /// before the retire finishes waits for it (bounded), and a run the retire could not wait out
        /// keeps its server. The next csc run after a retire starts a new server. False when there is
        /// nothing to stop or a retire is already running.
        /// </summary>
        public bool RetireInBackground(string reason)
        {
            int runGeneration;
            int stopGeneration;
            lock (_gate)
            {
                if (!_usedThisDomain || _retiring)
                {
                    return false;
                }

                _retiring = true;
                _retireDone.Reset();
                runGeneration = _runGeneration;
                stopGeneration = _stopGeneration;
            }

            try
            {
                new Thread(() => RetireWorker(reason, runGeneration, stopGeneration)) { IsBackground = true, Name = "ucb-csc-shared-retire" }.Start();
            }
            catch (Exception)
            {
                lock (_gate)
                {
                    _retiring = false;
                }

                _retireDone.Set();
                return false;
            }

            return true;
        }

        /// <summary>How many times this domain retired the server because the host took over.</summary>
        public int Retires
        {
            get
            {
                lock (_gate)
                {
                    return _retires;
                }
            }
        }

        /// <summary>True while a retire runs (tests).</summary>
        public bool IsRetiring
        {
            get
            {
                lock (_gate)
                {
                    return _retiring;
                }
            }
        }

        private void RetireWorker(string reason, int runGeneration, int stopGeneration)
        {
            try
            {
                // A pid lookup in flight (seconds on Windows) is about to say which process is ours.
                var waited = Stopwatch.StartNew();
                while (waited.Elapsed < _options.RetireDiscoveryWait)
                {
                    lock (_gate)
                    {
                        if (!_discovering)
                        {
                            break;
                        }
                    }

                    Thread.Sleep(20);
                }

                Func<bool> proceed = () =>
                {
                    lock (_gate)
                    {
                        return _runGeneration == runGeneration && _stopGeneration == stopGeneration;
                    }
                };
                if (!proceed())
                {
                    return;
                }

                bool stopped = StopServerProcesses(allowSlowListing: true, proceed: proceed);
                bool reset;
                lock (_recordGate)
                {
                    lock (_gate)
                    {
                        reset = stopped && _runGeneration == runGeneration && _stopGeneration == stopGeneration;
                        if (reset)
                        {
                            _usedThisDomain = false;
                            _recordWritten = false;
                            _serverPid = 0;
                            _serverStartTicks = null;
                            _recordSynced = false;
                            _discoveryAttempts = 0;
                            _state = "stopped";
                            _note = "retired: " + (reason ?? string.Empty);
                            _retires++;
                        }
                    }

                    if (reset)
                    {
                        TryDelete(RecordPath);
                    }
                }

                if (reset)
                {
                    WriteStatus();
                }
            }
            catch (Exception)
            {
                // Best effort; Shutdown and the next domain load still clean up.
            }
            finally
            {
                lock (_gate)
                {
                    _retiring = false;
                }

                _retireDone.Set();
            }
        }

        /// <summary>
        /// Stops whatever serves our pipe. Returns true when nothing of ours is left on it (the
        /// record can go). In order:
        /// <list type="number">
        /// <item>The server this domain found: if it already exited, nothing is left (csc runs reset
        /// a pid that died, and only one server can hold a pipe); if it is still a compiler server on
        /// our pipe (<see cref="ICscSharedProcessTable.IsServerOnPipe"/>), it is killed and waited for.</item>
        /// <item>Otherwise the process table is listed and every server on our pipe is killed per
        /// <see cref="CscSharedOrphanPolicy"/>, unless listing is too slow for the caller
        /// (<paramref name="allowSlowListing"/> false on Windows).</item>
        /// <item>Then the bundled <c>-shutdown</c> client is started without waiting and false is
        /// returned, so the record stays and the next domain load kills a server still on the pipe.</item>
        /// </list>
        /// <paramref name="proceed"/> is checked before every kill.
        /// </summary>
        private bool StopServerProcesses(bool allowSlowListing, Func<bool> proceed)
        {
            int pid;
            long? startTicks;
            lock (_gate)
            {
                pid = _serverPid;
                startTicks = _serverStartTicks;
            }

            if (pid > 0)
            {
                if (!_processes.IsAlive(pid))
                {
                    return true;
                }

                if (_processes.IsServerOnPipe(pid, PipeName, startTicks))
                {
                    if (!proceed())
                    {
                        return false;
                    }

                    _processes.Kill(pid);
                    if (WaitUntilGone(pid))
                    {
                        return true;
                    }
                }
            }

            if (allowSlowListing || !_processes.ListingIsSlow)
            {
                CscSharedServerRecord record = CscSharedServerRecord.TryRead(RecordPath)
                    ?? new CscSharedServerRecord { Pipe = PipeName, EditorPid = _options.EditorPid };
                List<int> targets = CscSharedOrphanPolicy.SelectTargets(
                    record,
                    _options.ProjectHash,
                    _options.EditorPid,
                    _processes.IsAlive,
                    _processes.ListServers(),
                    out string _);
                foreach (int target in targets)
                {
                    if (!proceed())
                    {
                        return false;
                    }

                    _processes.Kill(target);
                }

                return true;
            }

            StartShutdownClient();
            return false;
        }

        private bool WaitUntilGone(int pid)
        {
            var watch = Stopwatch.StartNew();
            while (true)
            {
                if (!_processes.IsAlive(pid))
                {
                    return true;
                }

                if (watch.Elapsed >= _options.KillWait)
                {
                    return false;
                }

                Thread.Sleep(10);
            }
        }

        /// <summary>The bundled <c>-shutdown</c> client for our pipe, not waited for.</summary>
        private void StartShutdownClient()
        {
            CscSharedToolchain? toolchain = ResolveToolchain(out string _);
            if (toolchain == null)
            {
                return;
            }

            try
            {
                _processes.StartDetached(BundledDotnetProcess.CreateStartInfo(
                    toolchain.DotnetPath,
                    CscSharedCommandLine.BuildShutdownArguments(toolchain.ServerPath, PipeName),
                    _options.Directory));
            }
            catch (Exception)
            {
                // The record stays; the next domain load still removes the server.
            }
        }

        public CscSharedStatus Snapshot()
        {
            lock (_gate)
            {
                return new CscSharedStatus
                {
                    UpdatedUtc = RoslynHostStatus.FormatUtc(DateTime.UtcNow),
                    EditorPid = _options.EditorPid,
                    ConfiguredCompiler = _options.ConfiguredCompiler,
                    PipeName = PipeName,
                    State = _state,
                    Note = _note,
                    ServerPid = _serverPid,
                    DotnetPath = _toolchain?.DotnetPath ?? string.Empty,
                    CscPath = _toolchain?.CscPath ?? string.Empty,
                    RoslynVersion = _toolchain?.RoslynVersion ?? string.Empty,
                    CompileTimeoutMs = _options.CompileTimeoutMs,
                    Compiles = _compiles,
                    FirstCompileMs = _firstCompileMs,
                    LastShutdownMs = _lastShutdownMs,
                    LastError = _lastError.Length > 0 ? _lastError : (_toolchainError ?? string.Empty),
                    LastErrorUtc = _lastErrorUtc.HasValue ? RoslynHostStatus.FormatUtc(_lastErrorUtc.Value) : string.Empty,
                };
            }
        }

        /// <summary>The start info for one csc run: bundled muxer, scrubbed environment, <c>DOTNET_HOST_PATH</c> = that muxer.</summary>
        public ProcessStartInfo CreateCscStartInfo(CscSharedToolchain toolchain, string responseFile, string source)
        {
            ProcessStartInfo startInfo = BundledDotnetProcess.CreateStartInfo(
                toolchain.DotnetPath,
                CscSharedCommandLine.BuildArguments(toolchain.CscPath, PipeName, responseFile, source),
                _options.Directory);
            startInfo.Environment["DOTNET_HOST_PATH"] = toolchain.DotnetPath;
            startInfo.StandardOutputEncoding = Encoding.UTF8;
            startInfo.StandardErrorEncoding = Encoding.UTF8;
            return startInfo;
        }

        private BundledDotnetRunResult? RunCsc(CscSharedToolchain toolchain, string responseFile, string source, out string? error)
        {
            error = null;
            ProcessStartInfo startInfo = CreateCscStartInfo(toolchain, responseFile, source);
            int timeoutMs = Math.Max(1000, _options.CompileTimeoutMs);
            BundledDotnetRunResult? result = null;
            Exception? failure = null;
            var worker = new Thread(() =>
            {
                try
                {
                    result = BundledDotnetProcess.Run(startInfo, TimeSpan.FromMilliseconds(timeoutMs));
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
            })
            {
                IsBackground = true,
                Name = "ucb-csc-shared",
            };
            worker.Start();
            if (!worker.Join(timeoutMs + 5000))
            {
                error = CscSharedFailureCodes.Timeout + ": csc did not finish in " + timeoutMs + " ms";
                return null;
            }

            if (failure != null)
            {
                error = CscSharedFailureCodes.Failed + ": cannot start csc: " + failure.GetType().Name + ": " + failure.Message;
                return null;
            }

            if (result == null || result.TimedOut)
            {
                error = CscSharedFailureCodes.Timeout + ": csc did not finish in " + timeoutMs + " ms (killed)";
                return null;
            }

            return result;
        }

        private CscSharedToolchain? ResolveToolchain(out string error)
        {
            lock (_gate)
            {
                if (!_toolchainResolved)
                {
                    _toolchainResolved = true;
                    try
                    {
                        if (_options.Toolchain != null)
                        {
                            _toolchain = _options.Toolchain();
                            _toolchainError = _toolchain == null ? "no toolchain" : null;
                        }
                        else
                        {
                            _toolchain = CscSharedToolchain.FromReport(BundledToolchainLocator.Locate(_options.ContentsPath), out string reason);
                            _toolchainError = _toolchain == null ? reason : null;
                        }
                    }
                    catch (Exception ex)
                    {
                        _toolchain = null;
                        _toolchainError = ex.GetType().Name + ": " + ex.Message;
                    }
                }

                error = _toolchainError ?? string.Empty;
                return _toolchain;
            }
        }

        private void EnsureRecord()
        {
            lock (_recordGate)
            {
                lock (_gate)
                {
                    _usedThisDomain = true;
                    _runGeneration++;
                    if (_recordWritten)
                    {
                        return;
                    }

                    _recordWritten = true;
                }

                try
                {
                    new CscSharedServerRecord
                    {
                        Pipe = PipeName,
                        EditorPid = _options.EditorPid,
                        StartedUtc = RoslynHostStatus.FormatUtc(DateTime.UtcNow),
                    }.Write(RecordPath);
                }
                catch (Exception)
                {
                    // Without a record a killed editor's server is left to its own idle timeout.
                }
            }
        }

        private CscSharedRun Succeed(CscSharedRun run, Stopwatch watch)
        {
            run.ElapsedMs = watch.Elapsed.TotalMilliseconds;
            bool writeStatus;
            bool discover;
            int knownPid;
            lock (_gate)
            {
                writeStatus = _compiles == 0 || _state != "ready";
                if (_compiles == 0)
                {
                    _firstCompileMs = run.ElapsedMs;
                }

                _compiles++;
                _state = "ready";
                _note = string.Empty;
                knownPid = _serverPid;
            }

            // A replaced server keeps a dead pid unless we look again. IsAlive is a direct probe
            // (not the command-line listing); the listing stays off this thread.
            if (knownPid > 0 && !_processes.IsAlive(knownPid))
            {
                lock (_gate)
                {
                    if (_serverPid == knownPid)
                    {
                        _serverPid = 0;
                        _serverStartTicks = null;
                        _recordSynced = false;
                        knownPid = 0;
                        if (!_discovering)
                        {
                            _discoveryAttempts = 0;
                        }
                    }
                    else
                    {
                        knownPid = _serverPid;
                    }
                }
            }

            lock (_gate)
            {
                knownPid = _serverPid;
                // Keep looking until the real pid is on disk, and again after that process exits.
                // Three immediate misses used to freeze serverPid at 0 for the rest of the domain.
                discover = knownPid == 0 && !_discovering && _discoveryAttempts < MaxDiscoveryAttempts;
                if (discover)
                {
                    _discovering = true;
                    _discoveryAttempts++;
                }
            }

            if (knownPid > 0)
            {
                TryPersistServerPid(knownPid);
            }

            if (discover)
            {
                // The compile never waits. This thread rewrites server.json and status.json when
                // the server shows up, including the first status that went out with serverPid 0.
                new Thread(DiscoverServer) { IsBackground = true, Name = "ucb-csc-shared-pid" }.Start();
            }

            if (writeStatus)
            {
                WriteStatus();
            }
            else
            {
                // Keeps the compile count current for doctor without a file write on the request path.
                ThreadPool.QueueUserWorkItem(_ => WriteStatus());
            }

            return run;
        }

        private CscSharedRun Fail(string reason, Stopwatch watch)
        {
            lock (_gate)
            {
                _state = "failed";
                _lastError = reason;
                _lastErrorUtc = DateTime.UtcNow;
            }

            ForgetDeadServer();

            WriteStatus();
            return new CscSharedRun { InfrastructureFailure = reason, ElapsedMs = watch.Elapsed.TotalMilliseconds };
        }

        /// <summary>
        /// After a failed csc run: a known server that is gone is forgotten, so a stop does not trust
        /// it while that run may have started a replacement (successful runs do the same in <see cref="Succeed"/>).
        /// </summary>
        private void ForgetDeadServer()
        {
            int known;
            lock (_gate)
            {
                known = _serverPid;
            }

            if (known <= 0 || _processes.IsAlive(known))
            {
                return;
            }

            lock (_gate)
            {
                if (_serverPid == known)
                {
                    _serverPid = 0;
                    _serverStartTicks = null;
                    _recordSynced = false;
                    if (!_discovering)
                    {
                        _discoveryAttempts = 0;
                    }
                }
            }
        }

        /// <summary>Background: finds the server csc started on our pipe, for the record, doctor, and a cheap stop.</summary>
        private void DiscoverServer()
        {
            int pid = 0;
            long? startTicks = null;
            try
            {
                foreach (KeyValuePair<int, string> process in _processes.ListServers())
                {
                    if (CscSharedPipe.IsServerCommandLine(process.Value, PipeName))
                    {
                        pid = process.Key;
                        break;
                    }
                }

                if (pid > 0)
                {
                    startTicks = _processes.ReadStartTicks(pid);
                }
            }
            catch (Exception)
            {
                pid = 0;
            }

            bool update;
            try
            {
                lock (_gate)
                {
                    update = pid > 0 && _usedThisDomain;
                    if (update)
                    {
                        _serverPid = pid;
                        _serverStartTicks = startTicks;
                    }
                }

                if (!update)
                {
                    return;
                }

                TryPersistServerPid(pid);
                WriteStatus();
            }
            finally
            {
                lock (_gate)
                {
                    _discovering = false;
                }
            }
        }

        /// <summary>
        /// Writes <paramref name="pid"/> into <c>server.json</c> and only then treats the record as
        /// current. A failed write (the file is still 0) is retried on the next compile.
        /// </summary>
        private void TryPersistServerPid(int pid)
        {
            if (pid <= 0)
            {
                return;
            }

            lock (_gate)
            {
                if (_recordSynced && _serverPid == pid)
                {
                    return;
                }
            }

            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    CscSharedServerRecord record = CscSharedServerRecord.TryRead(RecordPath)
                        ?? new CscSharedServerRecord
                        {
                            Pipe = PipeName,
                            EditorPid = _options.EditorPid,
                            StartedUtc = RoslynHostStatus.FormatUtc(DateTime.UtcNow),
                        };
                    if (!string.Equals(record.Pipe, PipeName, StringComparison.Ordinal))
                    {
                        record.Pipe = PipeName;
                        record.EditorPid = _options.EditorPid;
                    }

                    record.ServerPid = pid;
                    record.Write(RecordPath);
                    CscSharedServerRecord? readBack = CscSharedServerRecord.TryRead(RecordPath);
                    if (readBack != null && readBack.ServerPid == pid)
                    {
                        lock (_gate)
                        {
                            if (_serverPid == pid)
                            {
                                _recordSynced = true;
                            }
                        }

                        return;
                    }
                }
                catch (Exception)
                {
                    // The next attempt, or the next compile, tries again.
                }

                if (attempt < 2)
                {
                    Thread.Sleep(50);
                }
            }
        }

        private void WriteStatus()
        {
            if (!_options.WriteStatusFile || string.IsNullOrEmpty(_options.Directory))
            {
                return;
            }

            try
            {
                // Snapshot inside the lock. A compile and the background lookup both write, and a
                // snapshot taken before the lookup finished must not overwrite the pid it recorded.
                lock (_statusGate)
                {
                    Snapshot().Write(StatusPath);
                }
            }
            catch (Exception)
            {
                // Diagnostics only.
            }
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception)
            {
                // Best effort.
            }
        }
    }
}
