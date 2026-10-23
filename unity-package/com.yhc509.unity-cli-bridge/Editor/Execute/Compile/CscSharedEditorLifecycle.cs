#nullable enable
using System;
using System.Diagnostics;
using System.IO;
using UnityCli.Protocol;
using UnityEditor;
using UnityEngine;

namespace UnityCliBridge.Bridge.Editor
{
    /// <summary>
    /// Editor hooks for the <c>csc /shared</c> fallback. Active by default (<c>UCB_EXECUTE_COMPILER</c>
    /// unset, <c>auto</c>, or <c>roslyn-host</c>, as the host's fallback) and with <c>csc-shared</c>;
    /// with <c>codedom</c> it does nothing at all (no extra hook, thread, file, or process). When active:
    /// <list type="bullet">
    /// <item>Domain load: the status file, and a background cleanup of a server this project left
    /// behind (only when its record survived a failed stop or a killed editor).</item>
    /// <item><c>beforeAssemblyReload</c> and <c>quitting</c>: stop our server (bundled
    /// <c>-shutdown</c> client for our pipe, bounded wait, then a kill of a server proven ours).
    /// Nothing runs when this domain never used csc-shared.</item>
    /// </list>
    /// </summary>
    [InitializeOnLoad]
    internal static class CscSharedEditorLifecycle
    {
        private static readonly CscSharedServer? _server;
        private static readonly CscSharedBackend? _backend;
        private static readonly string? _setupError;

        static CscSharedEditorLifecycle()
        {
            ExecuteCompilerSettings settings = ExecuteCompilerSettings.FromEnvironment();
            if (!settings.UsesCscShared)
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
                _server = new CscSharedServer(new CscSharedServerOptions
                {
                    ContentsPath = EditorApplication.applicationContentsPath,
                    ProjectHash = ProtocolConstants.ComputeProjectHash(projectRoot),
                    UnityVersion = Application.unityVersion,
                    EditorPid = Process.GetCurrentProcess().Id,
                    Directory = CscSharedStatus.GetDirectory(projectRoot),
                    ConfiguredCompiler = settings.RawCompiler,
                    CompileTimeoutMs = settings.CompileTimeoutMs,
                });
                _backend = new CscSharedBackend(_server);
                AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
                EditorApplication.quitting += OnQuitting;
                _server.OnDomainLoaded();
            }
            catch (Exception ex)
            {
                _server = null;
                _backend = null;
                _setupError = ex.GetType().Name + ": " + ex.Message;
                UnityEngine.Debug.LogWarning("[unity-cli-bridge] csc-shared unavailable: " + _setupError);
            }
        }

        /// <summary>Null unless the settings put csc-shared in the chain and setup succeeded in this (main) editor process.</summary>
        public static CscSharedBackend? Backend
        {
            get { return _backend; }
        }

        public static string SetupError
        {
            get { return _setupError ?? "csc-shared setup failed"; }
        }

        private static void OnBeforeAssemblyReload()
        {
            _server?.Shutdown("domain-reload");
        }

        private static void OnQuitting()
        {
            _server?.Shutdown("editor-quit");
        }
    }
}
