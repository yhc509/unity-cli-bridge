#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using UnityCli.Protocol;
using UnityEditor;
using UnityEngine;

namespace UnityCliBridge.Bridge.Editor
{
    internal sealed class SessionStateRoslynHostStore : IRoslynHostSessionStore
    {
        public string GetString(string key, string fallback)
        {
            return SessionState.GetString(key, fallback);
        }

        public void SetString(string key, string value)
        {
            SessionState.SetString(key, value);
        }
    }

    /// <summary>
    /// Editor hooks for the Roslyn host (plan §2.5). The host is the default (<c>UCB_EXECUTE_COMPILER</c>
    /// unset, <c>auto</c>, or <c>roslyn-host</c>); with <c>codedom</c> or <c>csc-shared</c> this does
    /// nothing at all, so <c>codedom</c> keeps the CodeDOM path with no extra hook, thread, file, or
    /// process. With the host:
    /// <list type="bullet">
    /// <item><c>beforeAssemblyReload</c>: quit, wait ≤ 300 ms, kill, delete the pidfile — a reload
    /// loses the managed <c>Process</c> but not necessarily its pipe, so the host would never see EOF.</item>
    /// <item>After a reload (<c>afterAssemblyReload</c>, or the first editor tick if that never
    /// fires): orphan cleanup, then the prewarm policy's start, both off the main thread. The
    /// event fires while the editor is still finishing the reload, so the host overlaps that work
    /// instead of starting after it (6000.3: the first miss right after a reload went from ~476 ms
    /// to ~332 ms). Not <c>delayCall</c>, which rides the inspector-update cycle and starves in an
    /// unfocused GUI editor.</item>
    /// <item><c>quitting</c>: quit, then kill.</item>
    /// </list>
    /// Entering Play Mode without a domain reload fires none of these, so the host stays.
    /// </summary>
    [InitializeOnLoad]
    internal static class RoslynHostEditorLifecycle
    {
        private static readonly ExecuteCompilerSettings _settings;
        private static readonly RoslynHostSupervisor? _supervisor;
        private static readonly RoslynHostLauncherOptions? _launcherOptions;
        private static readonly string? _setupError;
        private static bool _domainLoaded;

        static RoslynHostEditorLifecycle()
        {
            _settings = ExecuteCompilerSettings.FromEnvironment();
            if (!_settings.UsesRoslynHost)
            {
                return;
            }

            if (BridgeHost.IsSecondaryUnityProcess())
            {
                _setupError = "not started in a secondary editor process";
                return;
            }

            try
            {
                string projectRoot = ProtocolConstants.GetCanonicalPath(Path.Combine(Application.dataPath, ".."));
                string projectHash = ProtocolConstants.ComputeProjectHash(projectRoot);
                string hostDirectory = RoslynHostStatus.GetHostDirectory(projectRoot);
                int editorPid = Process.GetCurrentProcess().Id;
                _launcherOptions = new RoslynHostLauncherOptions
                {
                    ContentsPath = EditorApplication.applicationContentsPath,
                    PackageRoot = ResolvePackageRootFromSource(),
                    CacheRoot = hostDirectory,
                    UnityVersion = Application.unityVersion,
                    ProjectHash = projectHash,
                    EditorPid = editorPid,
                    EditorStartUnixMs = ReadEditorStartUnixMs(),
                };
                _supervisor = new RoslynHostSupervisor(
                    _settings,
                    new SessionStateRoslynHostStore(),
                    new RoslynHostLauncher(_launcherOptions),
                    () => CompileRequestBuilder.CollectLoadedAssemblyReferences(),
                    new RoslynHostSupervisorOptions
                    {
                        ProjectHash = projectHash,
                        EditorPid = editorPid,
                        HostDirectory = hostDirectory,
                        PrewarmCompile = BuildPrewarmRequest,
                        NextBackendId = CscSharedEditorLifecycle.Backend != null ? CompileBackendIds.CscShared : CompileBackendIds.CodeDom,
                    });
                AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
                EditorApplication.quitting += OnQuitting;
                AssemblyReloadEvents.afterAssemblyReload += OnDomainLoaded;
                EditorApplication.update += OnDomainLoaded;
            }
            catch (Exception ex)
            {
                _supervisor = null;
                _setupError = ex.GetType().Name + ": " + ex.Message;
                UnityEngine.Debug.LogWarning("[unity-cli-bridge] Roslyn host unavailable, execute uses CodeDOM: " + _setupError);
            }
        }

        public static ExecuteCompilerSettings Settings
        {
            get { return _settings; }
        }

        /// <summary>Null unless the settings use the host and it is set up in this (main) editor process.</summary>
        public static RoslynHostSupervisor? Supervisor
        {
            get { return _supervisor; }
        }

        /// <summary>
        /// The backend chain for this domain: <c>roslyn-host → csc-shared → codedom</c> by default
        /// (<c>auto</c>, <c>roslyn-host</c>), <c>csc-shared → codedom</c> with <c>csc-shared</c>, and
        /// <c>codedom</c> alone with <c>codedom</c>. An invalid setting is reported as the fallback reason.
        /// </summary>
        public static CompileBackendSelector CreateSelector()
        {
            var codeDom = new CodeDomBackend();
            var preferred = new List<ICompileBackend>();
            if (_supervisor != null)
            {
                preferred.Add(new MainThreadPreparedBackend(new RoslynHostBackend(_supervisor), EnsureMainThreadInputs));
            }

            CscSharedBackend? cscShared = _settings.UsesCscShared ? CscSharedEditorLifecycle.Backend : null;
            if (cscShared != null)
            {
                preferred.Add(cscShared);
            }

            string? reason;
            if (_settings.UsesRoslynHost && _supervisor == null)
            {
                reason = RoslynHostFailureCodes.StartFailed + ": " + (_setupError ?? "host setup failed");
            }
            else if (_settings.Mode == ExecuteCompilerMode.CscShared && cscShared == null)
            {
                reason = CscSharedFailureCodes.Unavailable + ": " + CscSharedEditorLifecycle.SetupError;
            }
            else
            {
                reason = _settings.InvalidSettingReason;
            }

            return new CompileBackendSelector(codeDom, preferred, reason);
        }

        private static void OnDomainLoaded()
        {
            AssemblyReloadEvents.afterAssemblyReload -= OnDomainLoaded;
            EditorApplication.update -= OnDomainLoaded;
            if (_supervisor == null || _domainLoaded)
            {
                return;
            }

            _domainLoaded = true;

            EnsureMainThreadInputs();
            _supervisor.OnDomainLoaded();
        }

        /// <summary>
        /// A wrapped snippet touching what real snippets touch (UnityEngine, UnityEditor, Linq, a
        /// lambda, the result field). Background thread: wrapping is plain string work, and defines
        /// only steer the preprocessor, so the main-thread define list is not needed.
        /// </summary>
        private static RoslynHostCompileRequest BuildPrewarmRequest(System.Collections.Generic.IReadOnlyList<string> references)
        {
            const string Snippet =
                "var ucbPrewarm = new System.Collections.Generic.List<int> { 1, 2, 3 };\n"
                + "__pucResult = ucbPrewarm.Where(v => v > 1).Sum() + Mathf.Max(1, 2) + (EditorApplication.isPlaying ? 1 : 0);\n"
                + "Debug.Log(Application.unityVersion + \" \" + __pucArgsJson.Length);";
            var request = new CompileRequest(ExecuteCodeHandler.BuildWrappedCode(Snippet), "9.0", Array.Empty<string>(), references);
            return RoslynHostBackend.BuildHostRequest(request, RoslynHostBackend.BuildDefines(request));
        }

        private static void OnBeforeAssemblyReload()
        {
            _supervisor?.Shutdown("domain-reload");
        }

        private static void OnQuitting()
        {
            _supervisor?.Shutdown("editor-quit");
        }

        /// <summary>Main thread: the package folder, when the compile-time source path did not resolve it.</summary>
        private static void EnsureMainThreadInputs()
        {
            if (_launcherOptions == null || IsPackageRoot(_launcherOptions.PackageRoot))
            {
                return;
            }

            try
            {
                var package = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(RoslynHostEditorLifecycle).Assembly);
                if (package != null && IsPackageRoot(package.resolvedPath))
                {
                    _launcherOptions.PackageRoot = package.resolvedPath;
                }
            }
            catch (Exception)
            {
                // The build then fails with a clear "cannot read host source" reason.
            }
        }

        private static string ResolvePackageRootFromSource([CallerFilePath] string sourcePath = "")
        {
            try
            {
                // <package>/Editor/Execute/Compile/RoslynHostEditorLifecycle.cs
                string? directory = Path.GetDirectoryName(sourcePath);
                for (int i = 0; i < 3 && directory != null; i++)
                {
                    directory = Path.GetDirectoryName(directory);
                }

                return directory != null && IsPackageRoot(directory) ? directory : string.Empty;
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }

        private static bool IsPackageRoot(string? directory)
        {
            return !string.IsNullOrEmpty(directory)
                && Path.IsPathRooted(directory)
                && File.Exists(Path.Combine(Path.Combine(directory, "RoslynHost~"), "UcbRoslynHost.cs"));
        }

        private static long? ReadEditorStartUnixMs()
        {
            // Only Windows reuses pids fast enough to matter; elsewhere the host baselines the parent's
            // start time itself, which avoids trusting Mono's StartTime conversion.
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return null;
            }

            try
            {
                DateTime start = Process.GetCurrentProcess().StartTime.ToUniversalTime();
                return (long)(start - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>Runs a main-thread preparation step before delegating; keeps <see cref="RoslynHostBackend"/> Unity-free.</summary>
        private sealed class MainThreadPreparedBackend : ICompileBackend
        {
            private readonly ICompileBackend _inner;
            private readonly Action _prepare;

            public MainThreadPreparedBackend(ICompileBackend inner, Action prepare)
            {
                _inner = inner;
                _prepare = prepare;
            }

            public string Id
            {
                get { return _inner.Id; }
            }

            public CompileResult TryCompile(CompileRequest request)
            {
                _prepare();
                return _inner.TryCompile(request);
            }
        }
    }
}
