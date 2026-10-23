#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using UnityCli.Protocol;

namespace UnityCliBridge.Bridge.Editor
{
    /// <summary>Editor-session storage (SessionState in the editor). Only touched from the main thread.</summary>
    internal interface IRoslynHostSessionStore
    {
        string GetString(string key, string fallback);

        void SetString(string key, string value);
    }

    internal sealed class MemoryRoslynHostSessionStore : IRoslynHostSessionStore
    {
        private readonly Dictionary<string, string> _values = new Dictionary<string, string>(StringComparer.Ordinal);

        public string GetString(string key, string fallback)
        {
            lock (_values)
            {
                return _values.TryGetValue(key, out string? value) ? value : fallback;
            }
        }

        public void SetString(string key, string value)
        {
            lock (_values)
            {
                _values[key] = value;
            }
        }
    }

    /// <summary>A launched host plus how the launch went.</summary>
    internal sealed class RoslynHostLaunch
    {
        public RoslynHostLaunch(IRoslynHostInstance instance, string buildKey, bool buildCacheHit, double buildMs)
        {
            Instance = instance;
            BuildKey = buildKey;
            BuildCacheHit = buildCacheHit;
            BuildMs = buildMs;
        }

        public IRoslynHostInstance Instance { get; }

        public string BuildKey { get; }

        public bool BuildCacheHit { get; }

        public double BuildMs { get; }
    }

    /// <summary>
    /// Whether the next start has to compile the host image. Read on the main thread before
    /// <see cref="RoslynHostSupervisor.AcquireReady"/> decides to wait; must not start a process.
    /// </summary>
    internal enum RoslynHostImageDisposition
    {
        /// <summary>The image is already in the cache. A start is spawn, hello, setRefs, and prewarm.</summary>
        Ready,

        /// <summary>The toolchain can compile the image, and the cache does not have it yet. That compile outlasts the readiness wait.</summary>
        NeedsBuild,

        /// <summary>The image cannot be produced (toolchain or sources). A start fails without compiling.</summary>
        Unavailable,
    }

    /// <summary>Locates, builds, and starts a host. Throws <see cref="RoslynHostException"/> on failure.</summary>
    internal interface IRoslynHostLauncher
    {
        /// <summary>Cheap cache/toolchain check. May be called on the main thread, including while <see cref="Launch"/> runs.</summary>
        RoslynHostImageDisposition ImageDisposition { get; }

        RoslynHostLaunch Launch(bool prewarm, Action<IRoslynHostInstance> onSpawned);
    }

    /// <summary>
    /// Host crashes (exit, frame error, timeout, failed start) inside a sliding window. Three inside
    /// 60 s disable the host for the editor session. Times live in session storage so the window
    /// spans domain reloads.
    /// </summary>
    internal sealed class RoslynHostCrashTracker
    {
        public const int CrashLimit = 3;
        public static readonly TimeSpan Window = TimeSpan.FromSeconds(60);

        private readonly List<DateTime> _crashes = new List<DateTime>();

        public int RecentCount(DateTime nowUtc)
        {
            Prune(nowUtc);
            return _crashes.Count;
        }

        /// <summary>Records one crash; true when this one reaches <see cref="CrashLimit"/> inside <see cref="Window"/>.</summary>
        public bool Record(DateTime nowUtc)
        {
            Prune(nowUtc);
            _crashes.Add(nowUtc);
            return _crashes.Count >= CrashLimit;
        }

        public string Serialize()
        {
            var parts = new List<string>(_crashes.Count);
            foreach (DateTime crash in _crashes)
            {
                parts.Add(crash.Ticks.ToString(CultureInfo.InvariantCulture));
            }

            return string.Join(",", parts);
        }

        public void Load(string serialized, DateTime nowUtc)
        {
            _crashes.Clear();
            foreach (string part in (serialized ?? string.Empty).Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (long.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out long ticks))
                {
                    _crashes.Add(new DateTime(ticks, DateTimeKind.Utc));
                }
            }

            Prune(nowUtc);
        }

        private void Prune(DateTime nowUtc)
        {
            _crashes.RemoveAll(crash => nowUtc - crash > Window || crash > nowUtc + Window);
        }
    }

    /// <summary>Content hash of a reference set: sorted paths plus each file's mtime and size.</summary>
    internal static class RoslynHostReferenceSet
    {
        public static string ComputeHash(IReadOnlyList<string> paths)
        {
            var sorted = new List<string>(paths);
            sorted.Sort(StringComparer.Ordinal);
            var text = new StringBuilder(sorted.Count * 96);
            foreach (string path in sorted)
            {
                text.Append(path).Append('|');
                try
                {
                    var info = new FileInfo(path);
                    if (info.Exists)
                    {
                        text.Append(info.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture)).Append('|')
                            .Append(info.Length.ToString(CultureInfo.InvariantCulture));
                    }
                }
                catch (Exception)
                {
                    // Unreadable: hashed by path only.
                }

                text.Append('\n');
            }

            using (SHA256 sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(text.ToString()));
                var hex = new StringBuilder(16);
                for (int i = 0; i < 8; i++)
                {
                    hex.Append(hash[i].ToString("x2", CultureInfo.InvariantCulture));
                }

                return hex.ToString();
            }
        }

        public static bool SameList(IReadOnlyList<string>? left, IReadOnlyList<string>? right)
        {
            if (left == null || right == null || left.Count != right.Count)
            {
                return false;
            }

            for (int i = 0; i < left.Count; i++)
            {
                if (!string.Equals(left[i], right[i], StringComparison.Ordinal))
                {
                    return false;
                }
            }

            return true;
        }
    }

    internal sealed class RoslynHostSupervisorOptions
    {
        public string ProjectHash { get; set; } = string.Empty;

        public int EditorPid { get; set; }

        /// <summary><c>Library/com.yhc509.unity-cli-bridge/roslyn-host</c>; holds host.pid and status.json.</summary>
        public string HostDirectory { get; set; } = string.Empty;

        public TimeSpan QuitWait { get; set; } = TimeSpan.FromMilliseconds(300);

        public TimeSpan SetRefsTimeout { get; set; } = TimeSpan.FromSeconds(30);

        public TimeSpan IdleCheckInterval { get; set; } = TimeSpan.FromSeconds(30);

        public bool WriteStatusFile { get; set; } = true;

        /// <summary>
        /// Prewarm only: a representative compile request (a wrapped <c>execute</c> snippet) sent once
        /// after the first <c>setRefs</c>, so the first real compile after a reload finds the binder,
        /// the editor references' symbols, and both sides' compile path already warm. Null skips it.
        /// </summary>
        public Func<IReadOnlyList<string>, RoslynHostCompileRequest?>? PrewarmCompile { get; set; }

        /// <summary>The backend after the host in the chain, reported as the active one while the host is disabled.</summary>
        public string NextBackendId { get; set; } = CompileBackendIds.CodeDom;

        /// <summary>
        /// Replaces the <c>starting</c>-state readiness wait. The argument is the bound in milliseconds.
        /// The return value is ignored; <see cref="RoslynHostSupervisor.AcquireReady"/> re-reads the host.
        /// Null waits on the start worker. Tests install this so a <c>building</c> decision can assert
        /// the wait was not called, and a <c>starting</c> decision can record the bound without sleeping.
        /// </summary>
        public Func<int, bool>? WaitForStart { get; set; }
    }

    /// <summary>
    /// Owns the Roslyn host for one domain (plan §2.5–§2.6): background start (orphan cleanup →
    /// build → spawn → hello → first setRefs, off the main thread), a bounded wait only while the
    /// host is <c>starting</c> (image already built, process spawning or connecting), an immediate
    /// <c>host-building</c> result while it is <c>building</c> (the selector continues at csc-shared,
    /// then CodeDOM), per-compile timeout with kill, crash accounting
    /// (3 in 60 s disables the host for the editor session), quit-then-kill on reload/quit, optional
    /// idle exit, and the status file.
    /// Unity-free; the editor glue (<c>RoslynHostEditorLifecycle</c>) supplies the session store,
    /// launcher, and reference provider.
    /// </summary>
    internal sealed class RoslynHostSupervisor : IDisposable
    {
        public const string SessionKeyDisabledReason = "UnityCliBridge.RoslynHost.DisabledReason";
        public const string SessionKeyCrashes = "UnityCliBridge.RoslynHost.Crashes";
        public const string SessionKeyExecuteUsed = "UnityCliBridge.RoslynHost.ExecuteUsed";

        private static readonly TimeSpan JoinOnShutdown = TimeSpan.FromMilliseconds(250);

        private readonly ExecuteCompilerSettings _settings;
        private readonly IRoslynHostSessionStore _session;
        private readonly IRoslynHostLauncher _launcher;
        private readonly Func<IReadOnlyList<string>> _referenceProvider;
        private readonly RoslynHostSupervisorOptions _options;
        private readonly Func<DateTime> _clock;
        private readonly object _gate = new object();
        private readonly object _hostUse = new object();
        private readonly object _orphanGate = new object();
        private readonly object _statusGate = new object();
        private readonly RoslynHostCrashTracker _crashes = new RoslynHostCrashTracker();

        /// <summary>0 open, 1 shutdown in progress (non-final writes stop), 2 final status written.</summary>
        private volatile int _statusPhase;
        private readonly ManualResetEvent _startIdle = new ManualResetEvent(true);

        private IRoslynHostInstance? _host;
        private IRoslynHostInstance? _spawning;
        private Thread? _starter;
        private bool _shutdown;
        private bool _orphansChecked;
        private string _state = "idle";
        private string? _permanentFailure;
        private string? _disabledReason;
        private string _lastError = string.Empty;
        private DateTime? _lastErrorUtc;
        private int _pendingCrashes;
        private bool _pendingDisable;
        private string _hostRefsHash = string.Empty;
        private IReadOnlyList<string>? _lastRefList;
        private string _lastRefHash = string.Empty;
        private double _prewarmMs;
        private string _buildKey = string.Empty;
        private bool _buildCacheHit;
        private double _buildMs;
        private int _starts;
        private DateTime _lastUseUtc;
        private string _lastCompiler = string.Empty;
        private string _lastFallbackReason = string.Empty;
        private IReadOnlyList<string> _ignoredProjectOptions = Array.Empty<string>();
        private IReadOnlyList<string> _lastStderr = Array.Empty<string>();
        private Timer? _idleTimer;
        private int _compileCounter;

        public RoslynHostSupervisor(
            ExecuteCompilerSettings settings,
            IRoslynHostSessionStore session,
            IRoslynHostLauncher launcher,
            Func<IReadOnlyList<string>> referenceProvider,
            RoslynHostSupervisorOptions options,
            Func<DateTime>? clock = null)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _session = session ?? throw new ArgumentNullException(nameof(session));
            _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
            _referenceProvider = referenceProvider ?? throw new ArgumentNullException(nameof(referenceProvider));
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _clock = clock ?? (() => DateTime.UtcNow);

            // Main thread (constructed from the domain-load hook).
            string disabled = _session.GetString(SessionKeyDisabledReason, string.Empty);
            _disabledReason = disabled.Length == 0 ? null : disabled;
            _crashes.Load(_session.GetString(SessionKeyCrashes, string.Empty), _clock());
            if (_disabledReason != null)
            {
                _state = "disabled";
            }

            if (_settings.IdleExitMinutes > 0)
            {
                _idleTimer = new Timer(_ => CheckIdle(), null, _options.IdleCheckInterval, _options.IdleCheckInterval);
            }
        }

        public ExecuteCompilerSettings Settings
        {
            get { return _settings; }
        }

        public string State
        {
            get
            {
                lock (_gate)
                {
                    return _state;
                }
            }
        }

        public string? DisabledReason
        {
            get
            {
                lock (_gate)
                {
                    return _disabledReason;
                }
            }
        }

        /// <summary>The ready host's pid, or 0.</summary>
        public int HostPid
        {
            get
            {
                lock (_gate)
                {
                    return _host != null ? _host.Pid : 0;
                }
            }
        }

        public bool IsStarting
        {
            get
            {
                lock (_gate)
                {
                    return _starter != null;
                }
            }
        }

        public string PidFilePath
        {
            get { return Path.Combine(_options.HostDirectory, RoslynHostStatus.PidFileName); }
        }

        public string StatusFilePath
        {
            get { return Path.Combine(_options.HostDirectory, RoslynHostStatus.FileName); }
        }

        /// <summary>True when <c>execute</c> already compiled through the host earlier in this editor session (main thread).</summary>
        public bool ExecuteUsedThisSession
        {
            get { return string.Equals(_session.GetString(SessionKeyExecuteUsed, string.Empty), "1", StringComparison.Ordinal); }
        }

        /// <summary>Whether a domain load should start the host right away (plan §2.6).</summary>
        public static bool ShouldPrewarmOnLoad(RoslynHostPrewarmPolicy policy, bool executeUsedThisSession)
        {
            switch (policy)
            {
                case RoslynHostPrewarmPolicy.Eager:
                    return true;
                case RoslynHostPrewarmPolicy.Auto:
                    return executeUsedThisSession;
                default:
                    return false;
            }
        }

        /// <summary>
        /// Domain-load entry (main thread, after the assembly reload). Only the session-storage read stays
        /// on the main thread, which is still finishing the reload: the status write, the host-image probe
        /// of <see cref="BeginStart"/>, and the orphan cleanup run on a background thread. Orphan cleanup
        /// always runs first; the host starts right after it when the prewarm policy says so. An
        /// <c>execute</c> that arrives before that thread reaches <see cref="BeginStart"/> starts the host
        /// itself (<see cref="BeginStart"/> starts at most one).
        /// </summary>
        public void OnDomainLoaded()
        {
            bool prewarmNow = ShouldPrewarmOnLoad(_settings.Prewarm, ExecuteUsedThisSession);
            var load = new Thread(() =>
            {
                try
                {
                    WriteStatus();
                    if (prewarmNow)
                    {
                        BeginStart("domain-load");
                    }
                    else
                    {
                        EnsureOrphansCleaned();
                    }
                }
                catch (Exception)
                {
                    // The first execute starts the host and cleans orphans instead.
                }
            })
            {
                IsBackground = true,
                Name = "ucb-roslyn-host-load",
            };
            load.Start();
        }

        /// <summary>Starts the host on a background thread unless it is ready, starting, building, disabled, or failed for good.</summary>
        public void BeginStart(string trigger)
        {
            // File probe stays outside the lock. NeedsBuild is the one-time csc of the host image;
            // Ready and Unavailable are spawn-and-connect or a fast start failure.
            RoslynHostImageDisposition disposition = ReadImageDisposition();
            lock (_gate)
            {
                if (_shutdown || _disabledReason != null || _permanentFailure != null || _starter != null)
                {
                    return;
                }

                if (_host != null && _host.IsAlive)
                {
                    return;
                }

                _state = disposition == RoslynHostImageDisposition.NeedsBuild ? "building" : "starting";
                _startIdle.Reset();
                bool prewarm = _settings.Prewarm != RoslynHostPrewarmPolicy.Off;
                _starter = new Thread(() => StartWorker(prewarm, trigger)) { IsBackground = true, Name = "ucb-roslyn-host-start" };
                _starter.Start();
            }

            WriteStatus();
        }

        /// <summary>
        /// Main thread: a ready host, or null with a <c>code: detail</c> reason.
        /// <list type="bullet">
        /// <item><c>ready</c> — the live host, no wait.</item>
        /// <item><c>building</c> — the host image is not in the cache (including a first call that has just started that build). Returns <c>host-building</c> immediately and leaves the build running; the selector continues at csc-shared, then CodeDOM. The one-time csc outlasts <paramref name="waitMs"/>, so waiting only made the opt-in slower than the next compiler.</item>
        /// <item><c>starting</c> — the image is already built and the process is spawning or connecting (hello, setRefs, prewarm), or a start failure that does not compile. Wait up to <paramref name="waitMs"/>. This is the post-reload first miss: <c>afterAssemblyReload</c> starts the host while the editor finishes the reload, the cache hit reaches spawn well before the next <c>execute</c>, and the remainder fits in the bound and beats mcs. <c>onSpawned</c> moves <c>building</c> to <c>starting</c> once the process is alive, so a call that arrives after the compile still waits. If the host is still not ready when the wait ends, the reason is <c>host-not-ready</c> and the selector continues at csc-shared, then CodeDOM.</item>
        /// <item>A host that died while idle — <c>host-crashed</c> immediately, restart in the background. The image is already built, so that restart is <c>starting</c>.</item>
        /// </list>
        /// </summary>
        public IRoslynHostInstance? AcquireReady(int waitMs, out string reason)
        {
            bool deadWhileIdle = false;
            reason = string.Empty;
            _session.SetString(SessionKeyExecuteUsed, "1");
            SyncSession();
            lock (_gate)
            {
                if (_disabledReason != null)
                {
                    reason = RoslynHostFailureCodes.SessionDisabled + ": " + _disabledReason;
                    return null;
                }

                if (_permanentFailure != null)
                {
                    reason = _permanentFailure;
                    return null;
                }

                if (_host != null)
                {
                    if (_host.IsAlive)
                    {
                        reason = string.Empty;
                        _lastUseUtc = _clock();
                        return _host;
                    }

                    // Died while idle: like a crash mid-compile, this request falls back and the
                    // host restarts in the background for the next one.
                    IRoslynHostInstance dead = _host;
                    _host = null;
                    _hostRefsHash = string.Empty;
                    _lastStderr = dead.StderrTail();
                    RecordCrashLocked(RoslynHostFailureCodes.Crashed + ": the host exited while idle");
                    dead.Dispose();
                    RoslynHostPidRecord.TryDelete(PidFilePath);
                    reason = _lastError;
                    deadWhileIdle = true;
                }
            }

            SyncSession();
            if (DisabledReason != null)
            {
                reason = RoslynHostFailureCodes.SessionDisabled + ": " + DisabledReason;
                return null;
            }

            if (deadWhileIdle)
            {
                BeginStart("restart");
                return null;
            }

            BeginStart("execute");
            lock (_gate)
            {
                if (_host != null && _host.IsAlive)
                {
                    reason = string.Empty;
                    _lastUseUtc = _clock();
                    return _host;
                }

                if (_disabledReason != null)
                {
                    reason = RoslynHostFailureCodes.SessionDisabled + ": " + _disabledReason;
                    return null;
                }

                if (_permanentFailure != null)
                {
                    reason = _permanentFailure;
                    return null;
                }

                // Process already spawned, or the image was already built when the start began
                // (state "starting", including the window before onSpawned). Connect.
                if (_spawning == null && _state != "starting")
                {
                    reason = _state == "building"
                        ? RoslynHostFailureCodes.Building + ": the host is still being built"
                        : (_lastError.Length > 0 ? _lastError : RoslynHostFailureCodes.NotReady + ": the host did not start");
                    return null;
                }
            }

            Stopwatch waited = Stopwatch.StartNew();
            WaitForConnect(Math.Max(0, waitMs));
            SyncSession();
            lock (_gate)
            {
                if (_host != null && _host.IsAlive)
                {
                    reason = string.Empty;
                    _lastUseUtc = _clock();
                    return _host;
                }

                if (_disabledReason != null)
                {
                    reason = RoslynHostFailureCodes.SessionDisabled + ": " + _disabledReason;
                }
                else if (_permanentFailure != null)
                {
                    reason = _permanentFailure;
                }
                else if (_starter != null)
                {
                    reason = RoslynHostFailureCodes.NotReady + ": the host is still starting after "
                        + waited.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture) + " ms";
                }
                else
                {
                    reason = _lastError.Length > 0 ? _lastError : RoslynHostFailureCodes.NotReady + ": the host did not start";
                }

                return null;
            }
        }

        private static RoslynHostImageDisposition ReadImageDisposition(IRoslynHostLauncher launcher)
        {
            try
            {
                return launcher.ImageDisposition;
            }
            catch (Exception)
            {
                // Same as a missing toolchain: the start fails without a long compile, and the wait
                // ends when that failure is recorded instead of pretending a build is in progress.
                return RoslynHostImageDisposition.Unavailable;
            }
        }

        private RoslynHostImageDisposition ReadImageDisposition()
        {
            return ReadImageDisposition(_launcher);
        }

        /// <summary>The <c>starting</c> wait. <see cref="RoslynHostSupervisorOptions.WaitForStart"/> replaces it in tests.</summary>
        private void WaitForConnect(int waitMs)
        {
            Func<int, bool>? wait = _options.WaitForStart;
            if (wait != null)
            {
                wait(waitMs);
                return;
            }

            _startIdle.WaitOne(waitMs);
        }

        /// <summary>
        /// Main thread: compiles on <paramref name="host"/>, re-sending <c>setRefs</c> first only when
        /// the reference set changed. An infrastructure failure kills the host, counts a crash, and
        /// rethrows so the caller falls back.
        /// </summary>
        public RoslynHostCompileResult Compile(IRoslynHostInstance host, RoslynHostCompileRequest request, IReadOnlyList<string> references)
        {
            string refsHash;
            if (RoslynHostReferenceSet.SameList(_lastRefList, references))
            {
                refsHash = _lastRefHash;
            }
            else
            {
                refsHash = RoslynHostReferenceSet.ComputeHash(references);
                _lastRefList = new List<string>(references);
                _lastRefHash = refsHash;
            }

            request.Id = "c" + Interlocked.Increment(ref _compileCounter).ToString(CultureInfo.InvariantCulture);
            request.RefsHash = refsHash;
            TimeSpan timeout = TimeSpan.FromMilliseconds(_settings.CompileTimeoutMs);
            try
            {
                lock (_hostUse)
                {
                    if (!string.Equals(_hostRefsHash, refsHash, StringComparison.Ordinal))
                    {
                        var setRefs = new RoslynHostSetRefs { RefsHash = refsHash, Paths = new List<string>(references) };
                        host.Request(setRefs.ToMessage(), RoslynHostMessageTypes.RefsSet, timeout);
                        _hostRefsHash = refsHash;
                    }

                    RoslynHostMessage reply = host.Request(request.ToMessage(), RoslynHostMessageTypes.Result, timeout);
                    RoslynHostCompileResult result = RoslynHostCompileResult.FromMessage(reply);
                    if (!string.Equals(result.Id, request.Id, StringComparison.Ordinal))
                    {
                        throw new RoslynHostException(
                            RoslynHostFailureCodes.FrameError,
                            "reply id '" + result.Id + "' does not match request '" + request.Id + "'");
                    }

                    lock (_gate)
                    {
                        _lastUseUtc = _clock();
                    }

                    return result;
                }
            }
            catch (RoslynHostException ex)
            {
                OnHostFailure(host, ex);
                throw;
            }
        }

        /// <summary>Records which backend served the last compile; rewrites the status file only when that changes.</summary>
        public void ReportCompile(string compiler, string? fallbackReason, IReadOnlyList<string> ignoredProjectOptions)
        {
            bool changed;
            lock (_gate)
            {
                string reason = fallbackReason ?? string.Empty;
                changed = !string.Equals(_lastCompiler, compiler, StringComparison.Ordinal)
                    || !string.Equals(_lastFallbackReason, reason, StringComparison.Ordinal)
                    || !RoslynHostReferenceSet.SameList(_ignoredProjectOptions, ignoredProjectOptions);
                _lastCompiler = compiler;
                _lastFallbackReason = reason;
                _ignoredProjectOptions = ignoredProjectOptions ?? Array.Empty<string>();
            }

            if (changed)
            {
                WriteStatus();
            }
        }

        /// <summary>Quit → wait → kill, delete the pidfile, and cancel a start in flight. Safe to call repeatedly.</summary>
        public void Shutdown(string reason)
        {
            Shutdown(reason, _options.QuitWait);
        }

        /// <summary>
        /// <see cref="Shutdown(string)"/> with its own quit wait. <see cref="TimeSpan.Zero"/> kills the host
        /// without waiting for it to exit, which keeps the wait out of a domain reload.
        /// </summary>
        public void Shutdown(string reason, TimeSpan quitWait)
        {
            IRoslynHostInstance? host;
            IRoslynHostInstance? spawning;
            Thread? starter;
            lock (_gate)
            {
                _shutdown = true;
                if (_statusPhase == 0)
                {
                    _statusPhase = 1;
                }

                host = _host;
                _host = null;
                spawning = _spawning;
                _spawning = null;
                starter = _starter;
                _state = "stopped";
                if (host != null)
                {
                    _lastStderr = host.StderrTail();
                }
            }

            if (spawning != null)
            {
                spawning.Kill();
            }

            if (host != null)
            {
                lock (_hostUse)
                {
                    host.Stop(quitWait);
                }
            }

            if (starter != null && starter != Thread.CurrentThread)
            {
                starter.Join(JoinOnShutdown);
                lock (_gate)
                {
                    // A start that finished after the kill above.
                    if (_host != null)
                    {
                        _host.Kill();
                        _host = null;
                    }
                }
            }

            RoslynHostPidRecord.TryDelete(PidFilePath);
            Interlocked.Exchange(ref _idleTimer, null)?.Dispose();
            // Waits out a write already inside the status lock, then writes the final
            // snapshot and closes the file. Later WriteStatus calls do not open a temp file.
            WriteStatus(reason, closing: true);
        }

        public void Dispose()
        {
            Shutdown("disposed");
        }

        /// <summary>Kills a host this project left behind (plan §2.5). Runs once per domain, before any start.</summary>
        public void EnsureOrphansCleaned()
        {
            lock (_orphanGate)
            {
                if (_orphansChecked)
                {
                    return;
                }

                _orphansChecked = true;
                RoslynHostPidRecord? record = RoslynHostPidRecord.TryRead(PidFilePath);
                if (record == null)
                {
                    return;
                }

                OrphanVerdict verdict = RoslynHostOrphanPolicy.Evaluate(
                    record,
                    _options.ProjectHash,
                    _options.EditorPid,
                    RoslynHostProcessInspector.IsAlive,
                    RoslynHostProcessInspector.ReadCommandLine,
                    out string _);
                if (verdict == OrphanVerdict.Kill)
                {
                    RoslynHostProcessInspector.TryKill(record.Pid);
                }

                if (verdict != OrphanVerdict.Spare)
                {
                    RoslynHostPidRecord.TryDelete(PidFilePath);
                }
            }
        }

        public RoslynHostStatus Snapshot(string? stoppedReason = null)
        {
            lock (_gate)
            {
                IRoslynHostInstance? host = _host;
                return new RoslynHostStatus
                {
                    UpdatedUtc = RoslynHostStatus.FormatUtc(_clock()),
                    EditorPid = _options.EditorPid,
                    ConfiguredCompiler = _settings.RawCompiler,
                    ActiveBackend = _disabledReason == null && _permanentFailure == null ? CompileBackendIds.RoslynHost : _options.NextBackendId,
                    PrewarmPolicy = _settings.PrewarmName,
                    HostWaitMs = _settings.HostWaitMs,
                    CompileTimeoutMs = _settings.CompileTimeoutMs,
                    IdleExitMinutes = _settings.IdleExitMinutes,
                    State = _state,
                    Note = stoppedReason ?? string.Empty,
                    Pid = host != null ? host.Pid : 0,
                    HostStartedUtc = host != null ? RoslynHostStatus.FormatUtc(host.StartedUtc) : string.Empty,
                    StartMs = host != null ? host.StartMs : 0,
                    PrewarmMs = host != null ? _prewarmMs : 0,
                    RuntimeDirectory = host != null ? host.Hello.RuntimeDirectory : string.Empty,
                    RuntimeVersion = host != null ? host.Hello.RuntimeVersion : string.Empty,
                    RoslynVersion = host != null ? host.Hello.RoslynVersion : string.Empty,
                    BuildKey = _buildKey,
                    BuildCacheHit = _buildCacheHit,
                    BuildMs = _buildMs,
                    LastError = _lastError,
                    LastErrorUtc = _lastErrorUtc.HasValue ? RoslynHostStatus.FormatUtc(_lastErrorUtc.Value) : string.Empty,
                    SessionDisabledReason = _disabledReason ?? string.Empty,
                    RecentCrashes = _crashes.RecentCount(_clock()),
                    Starts = _starts,
                    LastCompiler = _lastCompiler,
                    LastFallbackReason = _lastFallbackReason,
                    IgnoredProjectOptions = new List<string>(_ignoredProjectOptions),
                    StderrTail = new List<string>(host != null ? host.StderrTail() : _lastStderr),
                };
            }
        }

        private void StartWorker(bool prewarm, string trigger)
        {
            IRoslynHostInstance? instance = null;
            try
            {
                EnsureOrphansCleaned();
                lock (_gate)
                {
                    if (_shutdown)
                    {
                        return;
                    }
                }

                RoslynHostLaunch launch = _launcher.Launch(prewarm, spawned =>
                {
                    lock (_gate)
                    {
                        _spawning = spawned;
                        // The image compiled (or was already there) and the process is alive.
                        // A call that arrives now waits out hello / setRefs / prewarm.
                        if (!_shutdown && _state == "building")
                        {
                            _state = "starting";
                        }
                    }

                    WritePidFile(spawned);
                });
                instance = launch.Instance;

                IReadOnlyList<string> references = _referenceProvider();
                string refsHash = RoslynHostReferenceSet.ComputeHash(references);
                RoslynHostMessage ackMessage = instance.Request(
                    new RoslynHostSetRefs { RefsHash = refsHash, Paths = new List<string>(references) }.ToMessage(),
                    RoslynHostMessageTypes.RefsSet,
                    _options.SetRefsTimeout);
                RoslynHostRefsSet ack = RoslynHostRefsSet.FromMessage(ackMessage);
                double prewarmCompileMs = prewarm ? PrewarmCompile(instance, references, refsHash) : 0;

                bool adopted = false;
                lock (_gate)
                {
                    _spawning = null;
                    _buildKey = launch.BuildKey;
                    _buildCacheHit = launch.BuildCacheHit;
                    _buildMs = launch.BuildMs;
                    if (!_shutdown)
                    {
                        _host = instance;
                        _hostRefsHash = refsHash;
                        _lastRefList = new List<string>(references);
                        _lastRefHash = refsHash;
                        _prewarmMs = instance.Hello.PrewarmMs + ack.PrewarmMs + prewarmCompileMs;
                        _starts++;
                        _state = "ready";
                        _lastUseUtc = _clock();
                        adopted = true;
                    }
                }

                if (!adopted)
                {
                    instance.Kill();
                    RoslynHostPidRecord.TryDelete(PidFilePath);
                }
            }
            catch (RoslynHostException ex)
            {
                OnStartFailure(instance, ex);
            }
            catch (Exception ex)
            {
                OnStartFailure(instance, new RoslynHostException(RoslynHostFailureCodes.StartFailed, ex.GetType().Name + ": " + ex.Message));
            }
            finally
            {
                // A start torn down by Shutdown no-ops here (_statusPhase is already 1 or 2).
                // A start that finishes on its own still writes before IsStarting clears, so a
                // waiter cannot delete the host directory while status.json*.tmp is open.
                WriteStatus();
                lock (_gate)
                {
                    _starter = null;
                    _spawning = null;
                }

                _startIdle.Set();
            }
        }

        /// <summary>The representative prewarm compile; its result is discarded (never loaded). A failure only costs the warm-up.</summary>
        private double PrewarmCompile(IRoslynHostInstance instance, IReadOnlyList<string> references, string refsHash)
        {
            Func<IReadOnlyList<string>, RoslynHostCompileRequest?>? factory = _options.PrewarmCompile;
            if (factory == null)
            {
                return 0;
            }

            Stopwatch watch = Stopwatch.StartNew();
            try
            {
                RoslynHostCompileRequest? request = factory(references);
                if (request == null)
                {
                    return 0;
                }

                request.Id = "prewarm";
                request.RefsHash = refsHash;
                RoslynHostCompileResult.FromMessage(instance.Request(request.ToMessage(), RoslynHostMessageTypes.Result, _options.SetRefsTimeout));
            }
            catch (RoslynHostException)
            {
                throw;
            }
            catch (Exception)
            {
                // A broken prewarm request must not block the host.
            }

            return watch.Elapsed.TotalMilliseconds;
        }

        private void OnStartFailure(IRoslynHostInstance? instance, RoslynHostException ex)
        {
            if (instance != null)
            {
                lock (_gate)
                {
                    _lastStderr = instance.StderrTail();
                }

                instance.Kill();
            }

            RoslynHostPidRecord.TryDelete(PidFilePath);
            lock (_gate)
            {
                if (_shutdown)
                {
                    _state = "stopped";
                    return;
                }

                bool permanent = ex.Code == RoslynHostFailureCodes.ToolchainMissing
                    || ex.Code == RoslynHostFailureCodes.BuildFailed
                    || ex.Code == RoslynHostFailureCodes.ForeignRuntime;
                if (permanent)
                {
                    // Retrying cannot help within this domain; a reload re-checks.
                    _permanentFailure = ex.Reason;
                    SetErrorLocked(ex.Reason);
                    _state = "failed";
                }
                else
                {
                    RecordCrashLocked(ex.Reason);
                }
            }
        }

        private void OnHostFailure(IRoslynHostInstance host, RoslynHostException ex)
        {
            lock (_gate)
            {
                if (ReferenceEquals(_host, host))
                {
                    _host = null;
                }

                _lastStderr = host.StderrTail();
                _hostRefsHash = string.Empty;
                RecordCrashLocked(ex.Reason);
            }

            host.Kill();
            RoslynHostPidRecord.TryDelete(PidFilePath);
            SyncSession();
            WriteStatus();

            // One restart per failure, in the background, so the next request finds a host; three
            // failures inside the window have disabled it instead (BeginStart then does nothing).
            BeginStart("restart");
        }

        private void RecordCrashLocked(string reason)
        {
            SetErrorLocked(reason);
            _pendingCrashes++;
            if (_crashes.Record(_clock()) && _disabledReason == null)
            {
                _disabledReason = RoslynHostCrashTracker.CrashLimit + " host failures within "
                    + (int)RoslynHostCrashTracker.Window.TotalSeconds + " s; last: " + reason;
                _pendingDisable = true;
                _state = "disabled";
            }
            else if (_disabledReason == null)
            {
                _state = "failed";
            }
        }

        private void SetErrorLocked(string reason)
        {
            _lastError = reason;
            _lastErrorUtc = _clock();
        }

        /// <summary>Main thread only: mirrors crash times and the disable reason into session storage.</summary>
        private void SyncSession()
        {
            string crashes;
            string? disabled;
            lock (_gate)
            {
                if (_pendingCrashes == 0 && !_pendingDisable)
                {
                    return;
                }

                _pendingCrashes = 0;
                _pendingDisable = false;
                crashes = _crashes.Serialize();
                disabled = _disabledReason;
            }

            _session.SetString(SessionKeyCrashes, crashes);
            if (disabled != null)
            {
                _session.SetString(SessionKeyDisabledReason, disabled);
            }
        }

        private void CheckIdle()
        {
            IRoslynHostInstance? host;
            lock (_gate)
            {
                host = _host;
                if (host == null || _shutdown || _settings.IdleExitMinutes <= 0
                    || _clock() - _lastUseUtc < TimeSpan.FromMinutes(_settings.IdleExitMinutes))
                {
                    return;
                }
            }

            if (!Monitor.TryEnter(_hostUse))
            {
                return;
            }

            try
            {
                lock (_gate)
                {
                    if (!ReferenceEquals(_host, host))
                    {
                        return;
                    }

                    _host = null;
                    _hostRefsHash = string.Empty;
                    _state = "stopped";
                    _lastStderr = host.StderrTail();
                }

                host.Stop(_options.QuitWait);
            }
            finally
            {
                Monitor.Exit(_hostUse);
            }

            RoslynHostPidRecord.TryDelete(PidFilePath);
            WriteStatus("idle-exit");
        }

        private void WritePidFile(IRoslynHostInstance spawned)
        {
            try
            {
                new RoslynHostPidRecord
                {
                    Pid = spawned.Pid,
                    EditorPid = _options.EditorPid,
                    StartTimeUnixMs = (long)(spawned.StartedUtc - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds,
                    Marker = RoslynHostPidRecord.FormatMarker(_options.ProjectHash),
                }.Write(PidFilePath);
            }
            catch (Exception)
            {
                // Without a pidfile, the host still exits on EOF / parent loss.
            }
        }

        /// <summary>
        /// Writes <c>status.json</c> under <see cref="_statusGate"/>, so the temp file and the
        /// rename both finish before the lock is released. Once <see cref="Shutdown"/> has set
        /// <see cref="_statusPhase"/>, a non-final call returns without opening a file. The
        /// closing call waits for any write already in the lock, writes the final snapshot,
        /// and only then lets <see cref="Shutdown"/> return.
        /// </summary>
        private void WriteStatus(string? stoppedReason = null, bool closing = false)
        {
            if (!_options.WriteStatusFile || string.IsNullOrEmpty(_options.HostDirectory))
            {
                if (closing)
                {
                    _statusPhase = 2;
                }

                return;
            }

            if (!closing && _statusPhase != 0)
            {
                return;
            }

            if (closing && _statusPhase == 2)
            {
                return;
            }

            try
            {
                RoslynHostStatus status = Snapshot(stoppedReason);
                lock (_statusGate)
                {
                    int phase = _statusPhase;
                    if (phase == 2 || (!closing && phase != 0))
                    {
                        return;
                    }

                    if (closing)
                    {
                        _statusPhase = 2;
                    }

                    status.Write(StatusFilePath);
                }
            }
            catch (Exception)
            {
                // Diagnostics only. A closing failure still leaves the phase closed when that
                // assignment happened before Write; a failure before it can be retried by a
                // second Shutdown.
            }
        }
    }
}
