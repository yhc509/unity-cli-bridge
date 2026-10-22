#nullable enable
using System;
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
    /// Editor hooks for the Roslyn host (plan §2.5). Does nothing at all unless
    /// <c>UCB_EXECUTE_COMPILER=roslyn-host</c>, so the default editor keeps today's CodeDOM path
    /// with no extra hook, thread, file, or process. With the opt-in:
    /// <list type="bullet">
    /// <item><c>beforeAssemblyReload</c>: quit, wait ≤ 300 ms, kill, delete the pidfile — a reload
    /// loses the managed <c>Process</c> but not necessarily its pipe, so the host would never see EOF.</item>
    /// <item>After a reload (<c>delayCall</c>): orphan cleanup, then the prewarm policy's start, both
    /// off the main thread.</item>
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
                    });
                AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
                EditorApplication.quitting += OnQuitting;
                EditorApplication.delayCall += OnDomainLoaded;
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

        /// <summary>Null unless the host is opted into and set up in this (main) editor process.</summary>
        public static RoslynHostSupervisor? Supervisor
        {
            get { return _supervisor; }
        }

        /// <summary>
        /// The backend chain for this domain: <c>roslyn-host → codedom</c> with the opt-in, otherwise
        /// <c>codedom</c> alone (a csc-shared step slots in before CodeDOM in a later version).
        /// </summary>
        public static CompileBackendSelector CreateSelector()
        {
            var codeDom = new CodeDomBackend();
            if (_supervisor != null)
            {
                return new CompileBackendSelector(
                    codeDom,
                    new ICompileBackend[] { new MainThreadPreparedBackend(new RoslynHostBackend(_supervisor), EnsureMainThreadInputs) },
                    null);
            }

            string? reason = _settings.UsesRoslynHost
                ? RoslynHostFailureCodes.StartFailed + ": " + (_setupError ?? "host setup failed")
                : _settings.CodeDomReason;
            return new CompileBackendSelector(codeDom, Array.Empty<ICompileBackend>(), reason);
        }

        private static void OnDomainLoaded()
        {
            if (_supervisor == null)
            {
                return;
            }

            EnsureMainThreadInputs();
            _supervisor.OnDomainLoaded();
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
