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

    /// <summary>Locates, builds, and starts a host. Throws <see cref="RoslynHostException"/> on failure.</summary>
    internal interface IRoslynHostLauncher
    {
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
    }

    /// <summary>
    /// Owns the Roslyn host for one domain (plan §2.5–§2.6): background start (orphan cleanup →
    /// build → spawn → hello → first setRefs, off the main thread), a bounded wait for a starting
    /// host, per-compile timeout with kill, crash accounting (3 in 60 s disables the host for the
    /// editor session), quit-then-kill on reload/quit, optional idle exit, and the status file.
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
        /// Domain-load entry (main thread, from delayCall): orphan cleanup always runs first on a
        /// background thread; the host starts right after it when the prewarm policy says so.
        /// </summary>
        public void OnDomainLoaded()
        {
            bool prewarmNow = ShouldPrewarmOnLoad(_settings.Prewarm, ExecuteUsedThisSession);
            WriteStatus();
            if (prewarmNow)
            {
                BeginStart("domain-load");
                return;
            }

            var cleanup = new Thread(() => EnsureOrphansCleaned()) { IsBackground = true, Name = "ucb-roslyn-host-orphans" };
            cleanup.Start();
        }

        /// <summary>Starts the host on a background thread unless it is ready, starting, disabled, or failed for good.</summary>
        public void BeginStart(string trigger)
        {
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

                _state = "starting";
                _startIdle.Reset();
                bool prewarm = _settings.Prewarm != RoslynHostPrewarmPolicy.Off;
                _starter = new Thread(() => StartWorker(prewarm, trigger)) { IsBackground = true, Name = "ucb-roslyn-host-start" };
                _starter.Start();
            }

            WriteStatus();
        }

        /// <summary>
        /// Main thread: a ready host, waiting up to <paramref name="waitMs"/> for one that is starting
        /// (and starting one if none is). Null with a <c>code: detail</c> reason when there is none.
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
            Stopwatch waited = Stopwatch.StartNew();
            _startIdle.WaitOne(Math.Max(0, waitMs));
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
            IRoslynHostInstance? host;
            IRoslynHostInstance? spawning;
            Thread? starter;
            lock (_gate)
            {
                _shutdown = true;
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
                    host.Stop(_options.QuitWait);
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
            WriteStatus(reason);
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
                    ActiveBackend = _disabledReason == null && _permanentFailure == null ? CompileBackendIds.RoslynHost : CompileBackendIds.CodeDom,
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
                        _prewarmMs = instance.Hello.PrewarmMs + ack.PrewarmMs;
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
                lock (_gate)
                {
                    _starter = null;
                    _spawning = null;
                }

                WriteStatus();
                _startIdle.Set();
            }
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

        private void WriteStatus(string? stoppedReason = null)
        {
            if (!_options.WriteStatusFile || string.IsNullOrEmpty(_options.HostDirectory))
            {
                return;
            }

            try
            {
                RoslynHostStatus status = Snapshot(stoppedReason);
                lock (_statusGate)
                {
                    status.Write(StatusFilePath);
                }
            }
            catch (Exception)
            {
                // Diagnostics only.
            }
        }
    }
}
