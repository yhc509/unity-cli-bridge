#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using UnityCli.Protocol;
using UnityEditor;
using UnityEngine;

namespace UnityCliBridge.Bridge.Editor
{
    internal sealed class ExecuteCodeHandler
    {
        private const string UserCodePlaceholder = "/*__PUC_USER_CODE__*/";

        // The args JSON is read from a static field instead of being spliced into the source, so the
        // compiled wrapper depends only on the user code and can be reused across calls (see _compiledCache).
        // `Object` is aliased to UnityEngine.Object: with both `using System;` and `using UnityEngine;`
        // in scope, `Object.FindFirstObjectByType<T>()` would otherwise fail as an ambiguous reference.
        private const string WrapperTemplate = @"
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using UnityEngine;
using UnityEditor;
using Object = UnityEngine.Object;
using PucFrameLoop = UnityCliBridge.Bridge.Editor.PucFrameLoop;

public static class PucExecuteWrapper
{
    internal static CancellationToken __pucToken;
    internal static readonly object __pucNoResult = new object();
    internal static object __pucResult = __pucNoResult;
    internal static string __puc_internal_argsJson = ""{}"";

    public static string Execute(CancellationToken token)
    {
        __pucToken = token;
        __pucResult = __pucNoResult;
        var __puc_internal_sb = new System.Text.StringBuilder();
        var __puc_internal_origOut = System.Console.Out;
        var __puc_internal_writer = new System.IO.StringWriter(__puc_internal_sb);
        System.Console.SetOut(__puc_internal_writer);
        try
        {
            string __pucArgsJson = __puc_internal_argsJson;
            /*__PUC_USER_CODE__*/
        }
        finally
        {
            System.Console.SetOut(__puc_internal_origOut);
        }

        return __puc_internal_sb.ToString();
    }
}";
        public bool CanHandle(string command)
        {
            return string.Equals(command, ProtocolConstants.CommandExecuteCode, StringComparison.Ordinal);
        }

        public string Handle(string command, string argumentsJson)
        {
            ExecuteCodeArgs args = ProtocolJson.Deserialize<ExecuteCodeArgs>(argumentsJson) ?? new ExecuteCodeArgs();
            if (!args.force)
            {
                throw new CommandFailureException(ProtocolConstants.ErrorExecuteForceRequired, "Running code requires --force.");
            }

            if (string.IsNullOrWhiteSpace(args.code))
            {
                throw new CommandFailureException("INVALID_ARGS", "The code to run is empty.");
            }

            int effectiveTimeoutMs = ResolveEffectiveTimeoutMs(args.timeoutMs);

            using var cts = new System.Threading.CancellationTokenSource();
            cts.CancelAfter(effectiveTimeoutMs);

            try
            {
                string wrappedCode = BuildWrappedCode(args.code);
                Type wrapperType = GetOrCompileWrapperType(wrappedCode, out bool cacheHit, out long compileMs);
                FieldInfo? argsField = wrapperType.GetField("__puc_internal_argsJson", BindingFlags.NonPublic | BindingFlags.Static);
                if (argsField == null)
                {
                    throw new CommandFailureException("EXECUTE_FAILED", "Could not find the wrapper argument field.");
                }

                argsField.SetValue(null, string.IsNullOrWhiteSpace(args.argumentsJson) ? "{}" : args.argumentsJson);

                MethodInfo? executeMethod = wrapperType.GetMethod("Execute", BindingFlags.Public | BindingFlags.Static);
                if (executeMethod == null)
                {
                    throw new CommandFailureException("EXECUTE_FAILED", "Could not find the Execute method.");
                }

                var logEntries = new List<string>();
                void OnLogMessageReceived(string condition, string stackTrace, LogType type)
                {
                    if (!string.IsNullOrWhiteSpace(condition) && !IsCompilerNoise(condition))
                    {
                        logEntries.Add(condition);
                    }
                }

                Application.logMessageReceived += OnLogMessageReceived;
                try
                {
                    string consoleOutput = (string)(executeMethod.Invoke(null, new object[] { cts.Token }) ?? string.Empty);
                    string output = MergeOutput(consoleOutput, logEntries);
                    object? noResult = wrapperType
                        .GetField("__pucNoResult", BindingFlags.NonPublic | BindingFlags.Static)
                        ?.GetValue(null);
                    object? rawResult = wrapperType
                        .GetField("__pucResult", BindingFlags.NonPublic | BindingFlags.Static)
                        ?.GetValue(null);
                    bool hasResult = !ReferenceEquals(rawResult, noResult);

                    return ProtocolJson.Serialize(new ExecuteCodePayload
                    {
                        output = output,
                        success = true,
                        hasResult = hasResult,
                        result = hasResult ? ExecuteValueSerializer.Serialize(rawResult, cts.Token) : null,
                        cacheHit = cacheHit,
                        compileMs = compileMs,
                    });
                }
                finally
                {
                    Application.logMessageReceived -= OnLogMessageReceived;
                }
            }
            catch (CommandFailureException)
            {
                throw;
            }
            catch (TargetInvocationException ex) when (ex.InnerException is OperationCanceledException)
            {
                throw new CommandFailureException(
                    ProtocolConstants.ErrorExecuteTimeout,
                    $"Code execution did not finish within {effectiveTimeoutMs} ms and was cancelled.");
            }
            catch (OperationCanceledException)
            {
                throw new CommandFailureException(
                    ProtocolConstants.ErrorExecuteTimeout,
                    $"Code execution did not finish within {effectiveTimeoutMs} ms and was cancelled.");
            }
            catch (TargetInvocationException ex) when (ex.InnerException != null)
            {
                throw new CommandFailureException(
                    "EXECUTE_FAILED",
                    ex.InnerException.Message,
                    ex.InnerException.ToString());
            }
            catch (Exception ex)
            {
                throw new CommandFailureException(
                    "EXECUTE_FAILED",
                    ex.Message,
                    ex.ToString());
            }
        }

        private static int ResolveEffectiveTimeoutMs(int requested)
        {
            if (requested <= 0)
            {
                return ProtocolConstants.DefaultExecuteTimeoutMs;
            }
            if (requested > ProtocolConstants.MaxExecuteTimeoutMs)
            {
                return ProtocolConstants.MaxExecuteTimeoutMs;
            }
            return requested;
        }

        internal static string BuildWrappedCode(string userCode)
        {
            string templateBody = UsingDirectiveUtility.StripUsingDirectives(WrapperTemplate, out string[] templateUsings);
            string userCodeBody = UsingDirectiveUtility.StripUsingDirectives(userCode, out string[] userUsings);
            // A user alias with the same name (e.g. `using Object = System.Object;`) wins over the template's.
            templateUsings = UsingDirectiveUtility.RemoveShadowedAliases(templateUsings, userUsings);
            string[] mergedUsings = MergeUsingDirectives(templateUsings, userUsings);
            string usingBlock = string.Join(Environment.NewLine, mergedUsings);
            string wrappedUserCode = string.Join(
                Environment.NewLine,
                "#line 1 \"user-code\"",
                userCodeBody,
                "#line default");
            string built = templateBody
                .Replace("            " + UserCodePlaceholder, wrappedUserCode);

            if (built.Contains(UserCodePlaceholder))
            {
                throw new CommandFailureException(
                    "EXECUTE_FAILED",
                    "A wrapper template placeholder was not substituted. Check whether the template indentation changed.");
            }

            return usingBlock + Environment.NewLine + Environment.NewLine + built;
        }

        private static readonly Regex CompilerNoisePattern = new Regex(
            @"^Script '[^']*' will not be compiled because it exists outside the Assets folder",
            RegexOptions.CultureInvariant);

        internal static bool IsCompilerNoise(string condition)
        {
            return CompilerNoisePattern.IsMatch(condition);
        }

        // Compiled wrapper types keyed by a hash of the full wrapped source. CodeDOM compilation costs
        // ~0.5 s per call; a cache hit skips it entirely. Statics reset on domain reload, which is also
        // exactly when cached assemblies could go stale (project scripts recompiled).
        private const int MaxCachedWrappers = 256;
        private static readonly Dictionary<string, Type> _compiledCache = new Dictionary<string, Type>(StringComparer.Ordinal);
        private static readonly CompileBackendSelector BackendSelector = new CompileBackendSelector(new CodeDomBackend());

        internal static int CachedWrapperCount
        {
            get { return _compiledCache.Count; }
        }

        private static Type GetOrCompileWrapperType(string wrappedCode, out bool cacheHit, out long compileMs)
        {
            string key = HashCode(wrappedCode);
            if (_compiledCache.TryGetValue(key, out Type? cached))
            {
                cacheHit = true;
                compileMs = 0;
                return cached;
            }

            var sw = System.Diagnostics.Stopwatch.StartNew();
            Assembly assembly = CompileCode(wrappedCode);
            compileMs = sw.ElapsedMilliseconds;
            cacheHit = false;
            Type? wrapperType = assembly.GetType("PucExecuteWrapper");
            if (wrapperType == null)
            {
                throw new CommandFailureException("EXECUTE_FAILED", "Could not find the wrapper type.");
            }

            if (_compiledCache.Count >= MaxCachedWrappers)
            {
                _compiledCache.Clear();
            }

            _compiledCache[key] = wrapperType;
            return wrapperType;
        }

        private static string HashCode(string text)
        {
            using var sha = SHA256.Create();
            byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(text));
            var sb = new StringBuilder(hash.Length * 2);
            foreach (byte b in hash)
            {
                sb.Append(b.ToString("x2"));
            }

            return sb.ToString();
        }

        private static string[] MergeUsingDirectives(IEnumerable<string> templateUsings, IEnumerable<string> userUsings)
        {
            var mergedUsings = new List<string>();
            var seenUsings = new HashSet<string>(StringComparer.Ordinal);

            AppendUsingDirectives(mergedUsings, seenUsings, templateUsings);
            AppendUsingDirectives(mergedUsings, seenUsings, userUsings);

            return mergedUsings.ToArray();
        }

        private static void AppendUsingDirectives(
            ICollection<string> mergedUsings,
            ISet<string> seenUsings,
            IEnumerable<string> usings)
        {
            foreach (string usingDirective in usings)
            {
                if (seenUsings.Add(usingDirective))
                {
                    mergedUsings.Add(usingDirective);
                }
            }
        }

        private static Assembly CompileCode(string code)
        {
            CompileResult result = BackendSelector.Select().TryCompile(CompileRequestBuilder.Build(code));
            if (!result.Succeeded || result.Assembly == null)
            {
                throw new CommandFailureException("COMPILE_FAILED", result.FormatFailureMessage());
            }

            return result.Assembly;
        }

        private static string MergeOutput(string consoleOutput, IEnumerable<string> logEntries)
        {
            IEnumerable<string> cleaned = new[] { consoleOutput }
                .Concat(logEntries)
                .SelectMany(segment => (segment ?? string.Empty).Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                .Select(line => line.Trim())
                .Where(line => line.Length > 0 && !IsCompilerNoise(line));
            return string.Join(Environment.NewLine, cleaned);
        }
    }

    /// <summary>
    /// Runs agent code once per editor frame inside the Editor, for Play Mode work that needs to
    /// react every frame (an autopilot, an input driver, a watcher that waits for a state).
    /// One <c>execute</c> call registers the loop and returns immediately; later calls read
    /// <see cref="Status"/> or call <see cref="Stop"/>. This replaces shell loops that call
    /// <c>execute</c> repeatedly (each call is a CLI process plus an IPC round trip).
    ///
    /// The callback runs on <c>EditorApplication.update</c> through <see cref="EditorTickPump"/>, so
    /// the editor keeps ticking at full rate while a loop is active, focused or not. A loop ends when
    /// the callback returns false, throws, exceeds its timeout, or Play Mode exits (for loops started
    /// in Play Mode). A domain reload (script compile, entering Play Mode with domain reload) drops
    /// every loop, so start loops after <c>play</c> has returned.
    /// </summary>
    public static class PucFrameLoop
    {
        private sealed class LoopState
        {
            public string Name = string.Empty;
            public Func<bool> OnFrame = () => false;
            public EditorApplication.CallbackFunction? Callback;
            public System.Diagnostics.Stopwatch Elapsed = new System.Diagnostics.Stopwatch();
            public double TimeoutSeconds;
            public bool StartedInPlayMode;
            public int Frames;
            public string State = "running";
            public string? Error;
        }

        private static readonly Dictionary<string, LoopState> _loops = new Dictionary<string, LoopState>(StringComparer.Ordinal);
        private static bool _playModeHooked;

        /// <summary>
        /// Starts (or replaces) a named loop. <paramref name="onFrame"/> runs once per editor frame;
        /// return true to keep going, false to finish. The loop also stops after
        /// <paramref name="timeoutSeconds"/> of wall time (default 60, capped at 600).
        /// </summary>
        public static void Start(string name, Func<bool> onFrame, double timeoutSeconds = 60)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ArgumentException("A loop name is required.", nameof(name));
            }

            if (onFrame == null)
            {
                throw new ArgumentNullException(nameof(onFrame));
            }

            Stop(name);
            HookPlayMode();

            var state = new LoopState
            {
                Name = name,
                OnFrame = onFrame,
                TimeoutSeconds = timeoutSeconds <= 0 ? 60 : Math.Min(timeoutSeconds, 600),
                StartedInPlayMode = EditorApplication.isPlaying,
            };
            state.Callback = () => Tick(state);
            _loops[name] = state;
            state.Elapsed.Start();
            EditorTickPump.Add(state.Callback);
        }

        /// <summary>Stops a running loop. Returns false when no loop with that name is running.</summary>
        public static bool Stop(string name)
        {
            if (!_loops.TryGetValue(name, out LoopState? state) || state.Callback == null || state.State != "running")
            {
                return false;
            }

            Finish(state, "stopped", null);
            return true;
        }

        /// <summary>True while the named loop is running.</summary>
        public static bool IsRunning(string name)
        {
            return _loops.TryGetValue(name, out LoopState? state) && state.State == "running";
        }

        /// <summary>
        /// One-line state for the named loop: <c>running</c>, <c>finished</c> (callback returned false),
        /// <c>timeout</c>, <c>stopped</c>, <c>play-mode-exited</c>, or <c>error: ...</c>, with frame count
        /// and elapsed seconds. <c>unknown</c> when no loop with that name exists in this domain.
        /// </summary>
        public static string Status(string name)
        {
            if (!_loops.TryGetValue(name, out LoopState? state))
            {
                return "unknown";
            }

            string head = state.Error == null ? state.State : state.State + ": " + state.Error;
            return head + " frames=" + state.Frames + " elapsed=" + state.Elapsed.Elapsed.TotalSeconds.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "s";
        }

        /// <summary>Names of the loops that are currently running.</summary>
        public static string[] Running
        {
            get { return _loops.Values.Where(l => l.State == "running").Select(l => l.Name).ToArray(); }
        }

        private static void Tick(LoopState state)
        {
            if (state.State != "running")
            {
                return;
            }

            if (state.StartedInPlayMode && !EditorApplication.isPlaying)
            {
                Finish(state, "play-mode-exited", null);
                return;
            }

            if (EditorApplication.isPaused)
            {
                return;
            }

            if (state.Elapsed.Elapsed.TotalSeconds > state.TimeoutSeconds)
            {
                Finish(state, "timeout", null);
                return;
            }

            bool keepGoing;
            try
            {
                state.Frames++;
                keepGoing = state.OnFrame();
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                Finish(state, "error", ex.GetType().Name + ": " + ex.Message);
                return;
            }

            if (!keepGoing)
            {
                Finish(state, "finished", null);
            }
        }

        private static void Finish(LoopState state, string finalState, string? error)
        {
            state.State = finalState;
            state.Error = error;
            state.Elapsed.Stop();
            EditorTickPump.Remove(state.Callback);
        }

        private static void HookPlayMode()
        {
            if (_playModeHooked)
            {
                return;
            }

            _playModeHooked = true;
            EditorApplication.playModeStateChanged += change =>
            {
                if (change != PlayModeStateChange.ExitingPlayMode)
                {
                    return;
                }

                foreach (LoopState state in _loops.Values.ToArray())
                {
                    if (state.State == "running" && state.StartedInPlayMode)
                    {
                        Finish(state, "play-mode-exited", null);
                    }
                }
            };
        }
    }
}
