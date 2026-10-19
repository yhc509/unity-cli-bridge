#nullable enable
using System;
using System.Threading.Tasks;
using UnityCli.Protocol;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine;

namespace UnityCliBridge.Bridge.Editor
{
    internal sealed partial class TestCommandHandler
    {
        private static TestRunnerApi? _playModeApi;
        private static TestRunnerCallbacks? _playModeCallbacks;
        private static EditorApplication.CallbackFunction? _playModeWatchdog;

        /// <summary>
        /// Set while a `test run --mode play` client is still waiting for its STARTED response.
        /// Tearing the watchdog down (e.g. from `test cancel` during a stuck PlayMode entry)
        /// without invoking this would leave that client hanging until its IPC timeout.
        /// </summary>
        private static Action? _playModePendingStartResponder;

        private void StartPlayModeRun(
            TestRunArgs args,
            TaskCompletionSource<ResponseEnvelope> completion,
            string projectHash,
            string requestId)
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            ResolveFilterFullNamesThenStart(
                TestMode.PlayMode,
                args,
                completion,
                projectHash,
                requestId,
                stopwatch,
                resolvedTestNames => StartPlayModeRunResolved(
                    args,
                    completion,
                    projectHash,
                    requestId,
                    resolvedTestNames));
        }

        private void StartPlayModeRunResolved(
            TestRunArgs args,
            TaskCompletionSource<ResponseEnvelope> completion,
            string projectHash,
            string requestId,
            string[]? resolvedTestNames)
        {
            string runId = Guid.NewGuid().ToString("N");
            int timeoutSec = ResolveTestTimeoutSeconds(args);
            BeginRun_PersistSession(runId, "play", timeoutSec, args.noDomainReload, args.failuresOnly);

            TestRunnerCallbacks? callbacks = null;
            try
            {
                if (args.noDomainReload)
                {
                    DomainReloadDisableScope.Activate();
                }

                TryGetActiveStartedAtUtc(out DateTime startedAtUtc);
                callbacks = TestRunnerCallbacks.Create(runId, "play", args.noDomainReload, startedAtUtc);
                callbacks.StoreInstanceIdToSession();
                RegisterPlayModeCallbacks(callbacks);

                string runGuid = _playModeApi!.Execute(new ExecutionSettings(BuildFilter(args, TestMode.PlayMode, resolvedTestNames)));
                StoreActiveRunGuid(runGuid);
            }
            catch (Exception exception)
            {
                if (callbacks != null)
                {
                    callbacks.MarkFailed("Failed to enter PlayMode: " + exception.Message);
                }
                else
                {
                    if (args.noDomainReload)
                    {
                        DomainReloadDisableScope.Deactivate();
                    }

                    EndRun();
                }

                CleanupPlayModeRegistration();

                completion.TrySetResult(ResponseEnvelope.Failure(
                    requestId,
                    projectHash,
                    "TEST_RUN_START_FAILED",
                    "Could not start the PlayMode test run: " + exception.Message,
                    false,
                    0,
                    ProtocolConstants.TransportLive,
                    null));
                return;
            }

            StartPlayModeWatchdog(runId, timeoutSec, completion, projectHash, requestId, args.failuresOnly);
        }

        internal static void RestorePlayModeRunFromSession(string runId, int timeoutSeconds)
        {
            EnsurePlayModeCallbacksFromSession(runId);
            StartPlayModeWatchdog(runId, timeoutSeconds, null, null, null, GetActiveFailuresOnly());
        }

        internal static void MarkRestoredPlayModeRunTimedOut(string runId)
        {
            TryCancelActiveUnityTestRun();
            TestRunnerCallbacks callbacks = EnsureCallbacksForCompletion(runId, "play");
            callbacks.MarkTimedOut();
            DestroyIfNotRegisteredPlayModeCallback(callbacks);
            CleanupPlayModeRegistration();
        }

        private static void RegisterPlayModeCallbacks(TestRunnerCallbacks callbacks)
        {
            CleanupPlayModeRegistration();

            _playModeApi = ScriptableObject.CreateInstance<TestRunnerApi>();
            _playModeApi.hideFlags = HideFlags.HideAndDontSave;
            _playModeCallbacks = callbacks;
            _playModeApi.RegisterCallbacks(callbacks);
        }

        private static TestRunnerCallbacks EnsurePlayModeCallbacksFromSession(string runId)
        {
            TestRunnerCallbacks? callbacks = TestRunnerCallbacks.TryFindFromSession();
            if (callbacks == null || !string.Equals(callbacks.RunId, runId, StringComparison.Ordinal))
            {
                callbacks = CreateCallbacksFromActiveSession(runId, "play");
                callbacks.StoreInstanceIdToSession();
            }

            RegisterPlayModeCallbacks(callbacks);
            return callbacks;
        }

        private static TestRunnerCallbacks EnsureCallbacksForCompletion(string runId, string mode)
        {
            return EnsureCallbacksForCompletion(runId, mode, out _);
        }

        /// <param name="fabricated">
        /// True when no live callbacks object for <paramref name="runId"/> was found and a
        /// replacement had to be created. Callers that may be looking at a still-registered
        /// object (EditMode runs own theirs) must only destroy a fabricated one.
        /// </param>
        private static TestRunnerCallbacks EnsureCallbacksForCompletion(
            string runId,
            string mode,
            out bool fabricated)
        {
            TestRunnerCallbacks? callbacks = TestRunnerCallbacks.TryFindFromSession();
            if (callbacks != null && string.Equals(callbacks.RunId, runId, StringComparison.Ordinal))
            {
                fabricated = false;
                return callbacks;
            }

            callbacks = CreateCallbacksFromActiveSession(runId, mode);
            callbacks.StoreInstanceIdToSession();
            fabricated = true;
            return callbacks;
        }

        private static TestRunnerCallbacks CreateCallbacksFromActiveSession(string runId, string mode)
        {
            TryGetActiveStartedAtUtc(out DateTime startedAtUtc);
            bool noDomainReload = SessionState.GetBool(
                ProtocolConstants.TestSessionKeyActiveNoDomainReload,
                false);
            return TestRunnerCallbacks.Create(runId, mode, noDomainReload, startedAtUtc);
        }

        private static void StartPlayModeWatchdog(
            string runId,
            int timeoutSec,
            TaskCompletionSource<ResponseEnvelope>? completion,
            string? projectHash,
            string? requestId,
            bool failuresOnly)
        {
            StopPlayModeWatchdog();

            TryGetActiveStartedAtUtc(out DateTime startedAtUtc);
            DateTime entryDeadline = DateTime.UtcNow.AddSeconds(ProtocolConstants.TestPlayModeEntryTimeoutSeconds);
            DateTime cancelAt = startedAtUtc.AddSeconds(timeoutSec);
            DateTime deadline = cancelAt.AddSeconds(ProtocolConstants.TestRunCancelGraceSeconds);
            bool startResponseSent = completion == null;
            bool cancelRequested = false;

            void Poll()
            {
                if (!startResponseSent && completion!.Task.IsCompleted)
                {
                    startResponseSent = true;
                }

                string activeRunId = SessionState.GetString(
                    ProtocolConstants.TestSessionKeyActiveRunId,
                    string.Empty);
                if (!string.Equals(activeRunId, runId, StringComparison.Ordinal))
                {
                    if (!startResponseSent && TestRunCompletedOnDisk(runId, out string? finalJson))
                    {
                        completion!.TrySetResult(BuildTestRunResultEnvelope(
                            requestId!,
                            projectHash,
                            finalJson!,
                            0,
                            failuresOnly));
                    }
                    else if (!startResponseSent)
                    {
                        completion!.TrySetResult(ResponseEnvelope.Failure(
                            requestId!,
                            projectHash,
                            "TEST_RUN_START_FAILED",
                            "The PlayMode test run finished before the STARTED response.",
                            false,
                            0,
                            ProtocolConstants.TransportLive,
                            null));
                    }

                    CleanupPlayModeRegistration();
                    return;
                }

                if (!startResponseSent && TestRunCompletedOnDisk(runId, out string? completedJson))
                {
                    startResponseSent = true;
                    completion!.TrySetResult(BuildTestRunResultEnvelope(
                        requestId!,
                        projectHash,
                        completedJson!,
                        0,
                        failuresOnly));
                }

                if (!startResponseSent
                    && (EditorApplication.isPlaying || EditorApplication.isPlayingOrWillChangePlaymode))
                {
                    startResponseSent = true;
                    completion!.TrySetResult(BuildPlayModeStartedResponse(requestId!, projectHash, runId));
                }

                if (!startResponseSent && DateTime.UtcNow >= entryDeadline)
                {
                    string reason = BuildPlayModeEntryFailureMessage();
                    TryCancelActiveUnityTestRun();
                    TestRunnerCallbacks callbacks = EnsureCallbacksForCompletion(runId, "play");
                    callbacks.MarkFailed(reason);
                    DestroyIfNotRegisteredPlayModeCallback(callbacks);
                    completion!.TrySetResult(ResponseEnvelope.Failure(
                        requestId!,
                        projectHash,
                        ProtocolConstants.ErrorTestPlayModeEntryFailed,
                        reason,
                        false,
                        0,
                        ProtocolConstants.TransportLive,
                        null));
                    CleanupPlayModeRegistration();
                    return;
                }

                DateTime now = DateTime.UtcNow;
                if (!cancelRequested && now >= cancelAt)
                {
                    cancelRequested = true;
                    TryCancelActiveUnityTestRun();
                }

                if (now >= deadline)
                {
                    TestRunnerCallbacks callbacks = EnsureCallbacksForCompletion(runId, "play");
                    callbacks.MarkTimedOut();
                    DestroyIfNotRegisteredPlayModeCallback(callbacks);

                    if (!startResponseSent)
                    {
                        completion!.TrySetResult(ResponseEnvelope.Failure(
                            requestId!,
                            projectHash,
                            ProtocolConstants.ErrorTestTimeout,
                            "PlayMode test run did not finish within " + timeoutSec + " seconds.",
                            false,
                            0,
                            ProtocolConstants.TransportLive,
                            null));
                    }

                    CleanupPlayModeRegistration();
                }
            }

            void RespondToPendingStart()
            {
                if (startResponseSent)
                {
                    return;
                }

                startResponseSent = true;

                // A cancel writes the result sidecar before tearing the watchdog down, so prefer
                // the real cancelled payload and only synthesize an envelope if it is missing.
                if (TestRunCompletedOnDisk(runId, out string? cancelledJson))
                {
                    completion!.TrySetResult(BuildTestRunResultEnvelope(
                        requestId!,
                        projectHash,
                        cancelledJson!,
                        0,
                        failuresOnly));
                    return;
                }

                completion!.TrySetResult(ResponseEnvelope.Failure(
                    requestId!,
                    projectHash,
                    ProtocolConstants.ErrorTestCancelled,
                    "The PlayMode test run was cancelled before the STARTED response.",
                    false,
                    0,
                    ProtocolConstants.TransportLive,
                    null));
            }

            _playModeWatchdog = Poll;
            _playModePendingStartResponder = completion == null ? null : (Action)RespondToPendingStart;
            EditorTickPump.Add(_playModeWatchdog);
            Poll();
        }

        /// <summary>
        /// Answers a `test run --mode play` client that is still waiting for its STARTED response,
        /// before the watchdog that owns that response is unsubscribed.
        /// </summary>
        private static void RespondToPendingPlayModeStart()
        {
            Action? responder = _playModePendingStartResponder;
            _playModePendingStartResponder = null;
            if (responder != null)
            {
                responder();
            }
        }

        private static ResponseEnvelope BuildPlayModeStartedResponse(
            string requestId,
            string? projectHash,
            string runId)
        {
            var startedPayload = new TestRunStartedPayload
            {
                runId = runId,
                mode = "play",
                status = "STARTED",
                startedAt = SessionState.GetString(
                    ProtocolConstants.TestSessionKeyActiveStartedAt,
                    DateTime.UtcNow.ToString("O")),
            };
            return ResponseEnvelope.Success(
                requestId,
                projectHash,
                ProtocolJson.Serialize(startedPayload),
                0,
                ProtocolConstants.TransportLive);
        }

        private static string BuildPlayModeEntryFailureMessage()
        {
            var scene = EditorSceneManager.GetActiveScene();
            return "PlayMode test run did not start entering PlayMode within "
                + ProtocolConstants.TestPlayModeEntryTimeoutSeconds
                + " seconds."
                + " isCompiling=" + EditorApplication.isCompiling
                + ", isUpdating=" + EditorApplication.isUpdating
                + ", isPlaying=" + EditorApplication.isPlaying
                + ", isPlayingOrWillChangePlaymode=" + EditorApplication.isPlayingOrWillChangePlaymode
                + ", activeScenePath=" + scene.path
                + ", activeSceneDirty=" + scene.isDirty
                + ".";
        }

        private static void TryCancelActiveUnityTestRun()
        {
            string runGuid = SessionState.GetString(
                ProtocolConstants.TestSessionKeyActiveRunGuid,
                string.Empty);
            if (string.IsNullOrEmpty(runGuid))
            {
                return;
            }

            try
            {
                TestRunnerApi.CancelTestRun(runGuid);
            }
            catch (Exception exception)
            {
                Debug.LogWarning("[unity-cli-bridge] Failed to cancel test run " + runGuid + ": " + exception.Message);
            }
        }

        private static void StopPlayModeWatchdog()
        {
            _playModePendingStartResponder = null;

            if (_playModeWatchdog == null)
            {
                return;
            }

            EditorTickPump.Remove(_playModeWatchdog);
            _playModeWatchdog = null;
        }

        private static void DestroyIfNotRegisteredPlayModeCallback(TestRunnerCallbacks callbacks)
        {
            if (!ReferenceEquals(callbacks, _playModeCallbacks))
            {
                UnityEngine.Object.DestroyImmediate(callbacks);
            }
        }

        private static void CleanupPlayModeRegistration()
        {
            StopPlayModeWatchdog();

            if (_playModeApi != null && _playModeCallbacks != null)
            {
                try
                {
                    _playModeApi.UnregisterCallbacks(_playModeCallbacks);
                }
                catch
                {
                }
            }

            if (_playModeApi != null)
            {
                UnityEngine.Object.DestroyImmediate(_playModeApi);
            }

            if (_playModeCallbacks != null)
            {
                UnityEngine.Object.DestroyImmediate(_playModeCallbacks);
            }

            _playModeApi = null;
            _playModeCallbacks = null;
        }
    }

    internal static class DomainReloadDisableScope
    {
        public static void Activate()
        {
            bool wasEnabled = EditorSettings.enterPlayModeOptionsEnabled;
            EnterPlayModeOptions previousFlags = EditorSettings.enterPlayModeOptions;

            SessionState.SetBool(ProtocolConstants.TestSessionKeyScopeWasEnabled, wasEnabled);
            SessionState.SetInt(ProtocolConstants.TestSessionKeyScopePreviousFlags, (int)previousFlags);
            SessionState.SetBool(ProtocolConstants.TestSessionKeyScopeActive, true);

            EditorSettings.enterPlayModeOptionsEnabled = true;
            EditorSettings.enterPlayModeOptions =
                previousFlags
                | EnterPlayModeOptions.DisableDomainReload
                | EnterPlayModeOptions.DisableSceneReload;
        }

        public static void Deactivate()
        {
            if (!SessionState.GetBool(ProtocolConstants.TestSessionKeyScopeActive, false))
            {
                return;
            }

            EditorSettings.enterPlayModeOptionsEnabled =
                SessionState.GetBool(ProtocolConstants.TestSessionKeyScopeWasEnabled, false);
            EditorSettings.enterPlayModeOptions =
                (EnterPlayModeOptions)SessionState.GetInt(ProtocolConstants.TestSessionKeyScopePreviousFlags, 0);
            SessionState.SetBool(ProtocolConstants.TestSessionKeyScopeActive, false);
        }

        public static void RestoreIfOrphaned()
        {
            if (!SessionState.GetBool(ProtocolConstants.TestSessionKeyScopeActive, false))
            {
                return;
            }

            string activeRunId = SessionState.GetString(ProtocolConstants.TestSessionKeyActiveRunId, string.Empty);
            if (string.IsNullOrEmpty(activeRunId))
            {
                Deactivate();
            }
        }
    }
}
