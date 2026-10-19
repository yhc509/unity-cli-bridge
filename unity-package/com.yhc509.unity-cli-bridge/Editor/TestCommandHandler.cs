#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using UnityCli.Protocol;
using UnityEditor;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine;

namespace UnityCliBridge.Bridge.Editor
{
    internal sealed partial class TestCommandHandler
    {
        private static readonly object _activeLock = new object();
        private static bool _hasActiveRun;

        /// <summary>
        /// Bumped whenever a cancel releases the run lock before the run id was persisted.
        /// A filter resolution that is still in flight compares the generation it captured when
        /// it took the lock against this counter, so it can tell "the lock is still mine" from
        /// "a cancel already released it" and abort instead of starting the run outside the lock.
        /// </summary>
        private static int _cancelGeneration;
        private static EditorApplication.CallbackFunction? _editModeRestoreWatchdog;
        private const int TestListTimeoutSeconds = 30;
        private const int TestFilterResolutionTimeoutSeconds = 15;
        private const string NoMatchingTestName = "\u0000unity-cli-bridge-no-matching-test\u0000";

        public bool CanHandle(string command)
        {
            return ProtocolHelpers.IsTestCommand(command);
        }

        public bool IsDeferred(string command, string? argumentsJson = null)
        {
            return ProtocolHelpers.IsDeferredTestCommand(command);
        }

        public string Handle(string command, string argumentsJson)
        {
            if (string.Equals(command, ProtocolConstants.CommandTestList, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Deferred test command must be started through StartDeferred: " + command);
            }

            if (string.Equals(command, ProtocolConstants.CommandTestResults, StringComparison.Ordinal))
            {
                return HandleResults(argumentsJson);
            }

            if (string.Equals(command, ProtocolConstants.CommandTestCancel, StringComparison.Ordinal))
            {
                return HandleCancel();
            }

            throw new InvalidOperationException("Deferred test command must be started through StartDeferred: " + command);
        }

        public void StartDeferred(
            string command,
            string argumentsJson,
            TaskCompletionSource<ResponseEnvelope> completion,
            string projectHash)
        {
            if (completion.Task.IsCompleted)
            {
                return;
            }

            string requestId = GetRequestId(completion);
            if (string.Equals(command, ProtocolConstants.CommandTestList, StringComparison.Ordinal))
            {
                StartDeferredList(argumentsJson, completion, projectHash, requestId);
                return;
            }

            if (!string.Equals(command, ProtocolConstants.CommandTestRun, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Unsupported deferred test command: " + command);
            }

            if (!TryBeginRun_Internal(out string activeRunId, out string activeMode))
            {
                completion.TrySetResult(ResponseEnvelope.Failure(
                    requestId,
                    projectHash,
                    ProtocolConstants.ErrorTestBusy,
                    "A test run is already in progress (runId=" + activeRunId + ", mode=" + activeMode + ").",
                    true,
                    0,
                    ProtocolConstants.TransportLive,
                    null));
                return;
            }

            try
            {
                TestRunArgs args = ProtocolJson.Deserialize<TestRunArgs>(argumentsJson) ?? new TestRunArgs();
                if (string.Equals(args.mode, "edit", StringComparison.Ordinal))
                {
                    StartEditModeRun(args, completion, projectHash, requestId);
                    return;
                }

                if (string.Equals(args.mode, "play", StringComparison.Ordinal))
                {
                    StartPlayModeRun(args, completion, projectHash, requestId);
                    return;
                }

                EndRun();
                completion.TrySetResult(ResponseEnvelope.Failure(
                    requestId,
                    projectHash,
                    ProtocolConstants.ErrorTestInvalidMode,
                    "test run --mode must be edit or play: '" + args.mode + "'",
                    false,
                    0,
                    ProtocolConstants.TransportLive,
                    null));
            }
            catch
            {
                EndRun();
                throw;
            }
        }

        private static void StartDeferredList(
            string argumentsJson,
            TaskCompletionSource<ResponseEnvelope> completion,
            string projectHash,
            string requestId)
        {
            TestListArgs args = ProtocolJson.Deserialize<TestListArgs>(argumentsJson) ?? new TestListArgs();
            bool includeEditMode = string.Equals(args.mode, "edit", StringComparison.Ordinal)
                || string.Equals(args.mode, "all", StringComparison.Ordinal);
            bool includePlayMode = string.Equals(args.mode, "play", StringComparison.Ordinal)
                || string.Equals(args.mode, "all", StringComparison.Ordinal);

            if (!includeEditMode && !includePlayMode)
            {
                completion.TrySetResult(ResponseEnvelope.Failure(
                    requestId,
                    projectHash,
                    ProtocolConstants.ErrorTestInvalidMode,
                    "test list --mode must be edit, play, or all: '" + args.mode + "'",
                    false,
                    0,
                    ProtocolConstants.TransportLive,
                    null));
                return;
            }

            var editEntries = new List<TestListEntry>();
            var playEntries = new List<TestListEntry>();
            var api = ScriptableObject.CreateInstance<TestRunnerApi>();
            var stopwatch = Stopwatch.StartNew();
            int expectedCallbacks = (includeEditMode ? 1 : 0) + (includePlayMode ? 1 : 0);
            int receivedCallbacks = 0;
            bool completed = false;
            bool pollRegistered = false;

            void Cleanup()
            {
                if (pollRegistered)
                {
                    EditorTickPump.Remove(Poll);
                    pollRegistered = false;
                }

                UnityEngine.Object.DestroyImmediate(api);
            }

            void Complete(ResponseEnvelope response)
            {
                if (completed)
                {
                    return;
                }

                completed = true;
                Cleanup();
                completion.TrySetResult(response);
            }

            void CompleteSuccess()
            {
                if (completed)
                {
                    return;
                }

                stopwatch.Stop();
                var tests = new List<TestListEntry>();
                if (includeEditMode)
                {
                    tests.AddRange(editEntries);
                }

                if (includePlayMode)
                {
                    tests.AddRange(playEntries);
                }

                Complete(ResponseEnvelope.Success(
                    requestId,
                    projectHash,
                    SerializeTestListPayload(new TestListPayload
                    {
                        mode = args.mode,
                        tests = tests.ToArray(),
                    }, args),
                    stopwatch.ElapsedMilliseconds,
                    ProtocolConstants.TransportLive));
            }

            void CompleteTimeout()
            {
                if (completed)
                {
                    return;
                }

                stopwatch.Stop();
                Complete(ResponseEnvelope.Failure(
                    requestId,
                    projectHash,
                    ProtocolConstants.ErrorTestListTimeout,
                    "test list did not finish within " + TestListTimeoutSeconds + " seconds.",
                    false,
                    stopwatch.ElapsedMilliseconds,
                    ProtocolConstants.TransportLive,
                    null));
            }

            void Poll()
            {
                if (completion.Task.IsCompleted)
                {
                    if (!completed)
                    {
                        completed = true;
                        Cleanup();
                    }

                    return;
                }

                if (receivedCallbacks >= expectedCallbacks)
                {
                    CompleteSuccess();
                    return;
                }

                if (stopwatch.Elapsed.TotalSeconds > TestListTimeoutSeconds)
                {
                    CompleteTimeout();
                }
            }

            void RequestList(TestMode mode, string modeLabel, List<TestListEntry> targetEntries)
            {
                api.RetrieveTestList(mode, adaptor =>
                {
                    if (completed)
                    {
                        return;
                    }

                    AppendListForMode(adaptor, modeLabel, targetEntries);
                    receivedCallbacks++;
                });
            }

            try
            {
                EditorTickPump.Add(Poll);
                pollRegistered = true;

                if (includeEditMode)
                {
                    RequestList(TestMode.EditMode, "edit", editEntries);
                }

                if (includePlayMode)
                {
                    RequestList(TestMode.PlayMode, "play", playEntries);
                }

                Poll();
            }
            catch
            {
                if (!completed)
                {
                    completed = true;
                    Cleanup();
                }

                throw;
            }
        }

        private static void AppendListForMode(
            ITestAdaptor? root,
            string modeLabel,
            List<TestListEntry> entries)
        {
            if (root == null)
            {
                return;
            }

            CollectLeafTests(root, modeLabel, entries);
        }

        private static void CollectLeafTests(
            ITestAdaptor adaptor,
            string modeLabel,
            List<TestListEntry> entries)
        {
            if (adaptor.IsSuite)
            {
                foreach (ITestAdaptor child in adaptor.Children)
                {
                    CollectLeafTests(child, modeLabel, entries);
                }

                return;
            }

            entries.Add(new TestListEntry
            {
                fullName = adaptor.FullName ?? string.Empty,
                assembly = GetAssemblyName(adaptor),
                mode = modeLabel,
                categories = adaptor.Categories ?? Array.Empty<string>(),
            });
        }

        private void ResolveFullNamesAsync(
            TestMode mode,
            string substringFilter,
            Action<string[]> onResolved,
            Action<string> onError)
        {
            if (string.IsNullOrEmpty(substringFilter))
            {
                onResolved(Array.Empty<string>());
                return;
            }

            var api = ScriptableObject.CreateInstance<TestRunnerApi>();
            ITestAdaptor? root = null;
            bool completed = false;
            bool pollRegistered = false;
            DateTime deadline = DateTime.UtcNow.AddSeconds(TestFilterResolutionTimeoutSeconds);

            void Cleanup()
            {
                if (pollRegistered)
                {
                    EditorTickPump.Remove(Poll);
                    pollRegistered = false;
                }

                UnityEngine.Object.DestroyImmediate(api);
            }

            void CompleteResolved(string[] resolvedFullNames)
            {
                if (completed)
                {
                    return;
                }

                completed = true;
                Cleanup();
                onResolved(resolvedFullNames);
            }

            void CompleteError(string message)
            {
                if (completed)
                {
                    return;
                }

                completed = true;
                Cleanup();
                onError(message);
            }

            void Poll()
            {
                if (root != null)
                {
                    var matched = new List<string>();
                    CollectMatchingFullNames(root, substringFilter, matched);
                    CompleteResolved(matched.ToArray());
                    return;
                }

                if (DateTime.UtcNow > deadline)
                {
                    CompleteError(
                        "Filter substring resolution timeout ("
                        + TestFilterResolutionTimeoutSeconds
                        + "s).");
                }
            }

            try
            {
                api.RetrieveTestList(mode, adaptor => root = adaptor);
                EditorTickPump.Add(Poll);
                pollRegistered = true;
                Poll();
            }
            catch (Exception exception)
            {
                CompleteError("Filter substring resolution failed: " + exception.Message);
            }
        }

        internal static void CollectMatchingFullNames(
            ITestAdaptor adaptor,
            string needle,
            List<string> matched)
        {
            if (adaptor.IsSuite)
            {
                foreach (ITestAdaptor child in adaptor.Children)
                {
                    CollectMatchingFullNames(child, needle, matched);
                }

                return;
            }

            string fullName = adaptor.FullName ?? string.Empty;
            if (ProtocolHelpers.TestFullNameMatchesFilter(fullName, needle))
            {
                matched.Add(fullName);
            }
        }

        private void ResolveFilterFullNamesThenStart(
            TestMode mode,
            TestRunArgs args,
            TaskCompletionSource<ResponseEnvelope> completion,
            string projectHash,
            string requestId,
            Stopwatch stopwatch,
            Action<string[]?> start)
        {
            // Captured in the same main-thread dispatch that took the run lock, before any
            // EditorApplication.update poll can run. Filter resolution can take up to
            // TestFilterResolutionTimeoutSeconds, during which the run id is not persisted yet
            // and a `test cancel` can release the lock — the generation tells us that happened.
            int startGeneration = GetCancelGeneration();

            if (string.IsNullOrEmpty(args.filter))
            {
                TryStartResolvedRun(null);
                return;
            }

            ResolveFullNamesAsync(
                mode,
                args.filter,
                resolvedFullNames => TryStartResolvedRun(resolvedFullNames),
                errorMessage =>
                {
                    if (TryCompleteCancelledStart(
                        startGeneration,
                        completion,
                        projectHash,
                        requestId,
                        stopwatch))
                    {
                        return;
                    }

                    stopwatch.Stop();
                    EndRun();
                    completion.TrySetResult(ResponseEnvelope.Failure(
                        requestId,
                        projectHash,
                        ProtocolConstants.ErrorTestListTimeout,
                        errorMessage,
                        false,
                        stopwatch.ElapsedMilliseconds,
                        ProtocolConstants.TransportLive,
                        null));
                });

            void TryStartResolvedRun(string[]? resolvedFullNames)
            {
                // Must precede every other branch: once a cancel has released the lock it may
                // already belong to a different run, so neither EndRun() nor start() may run.
                if (TryCompleteCancelledStart(
                    startGeneration,
                    completion,
                    projectHash,
                    requestId,
                    stopwatch))
                {
                    return;
                }

                if (completion.Task.IsCompleted)
                {
                    stopwatch.Stop();
                    EndRun();
                    return;
                }

                try
                {
                    start(resolvedFullNames);
                }
                catch (Exception exception)
                {
                    stopwatch.Stop();
                    EndRun();
                    completion.TrySetResult(ResponseEnvelope.Failure(
                        requestId,
                        projectHash,
                        "TEST_RUN_START_FAILED",
                        "Could not start the test run: " + exception.Message,
                        false,
                        stopwatch.ElapsedMilliseconds,
                        ProtocolConstants.TransportLive,
                        null));
                }
            }
        }

        private static int GetCancelGeneration()
        {
            lock (_activeLock)
            {
                return _cancelGeneration;
            }
        }

        /// <summary>
        /// Answers a pending <c>test run</c> whose start was cancelled while its filter was still
        /// resolving. Deliberately does NOT call <see cref="EndRun"/>: the cancel already released
        /// the lock, which a later <c>test run</c> may since have taken.
        /// </summary>
        private static bool TryCompleteCancelledStart(
            int startGeneration,
            TaskCompletionSource<ResponseEnvelope> completion,
            string projectHash,
            string requestId,
            Stopwatch stopwatch)
        {
            if (GetCancelGeneration() == startGeneration)
            {
                return false;
            }

            stopwatch.Stop();
            completion.TrySetResult(ResponseEnvelope.Failure(
                requestId,
                projectHash,
                ProtocolConstants.ErrorTestCancelled,
                "The test run was cancelled before it started.",
                false,
                stopwatch.ElapsedMilliseconds,
                ProtocolConstants.TransportLive,
                null));
            return true;
        }

        private static string HandleResults(string argumentsJson)
        {
            TestResultsArgs args = ProtocolJson.Deserialize<TestResultsArgs>(argumentsJson) ?? new TestResultsArgs();
            string runId = args.runId;
            if (!string.IsNullOrEmpty(runId) && !ProtocolHelpers.IsValid32HexId(runId))
            {
                throw new CommandFailureException(
                    "INVALID_ARGS",
                    "runId must be 32 hex characters.");
            }

            if (string.IsNullOrEmpty(runId))
            {
                runId = TryReadLastRunId();
                if (string.IsNullOrEmpty(runId))
                {
                    runId = SessionState.GetString(
                        ProtocolConstants.TestSessionKeyInlineResultRunId,
                        string.Empty);
                }

                if (string.IsNullOrEmpty(runId))
                {
                    throw new CommandFailureException(
                        ProtocolConstants.ErrorTestRunNotFound,
                        "No test run to query (last-run.json does not exist).");
                }

                // last-run.json is on disk, so a corrupted or hand-edited file must not
                // reach the path either. It reads as "no run" rather than bad input.
                if (!ProtocolHelpers.IsValid32HexId(runId))
                {
                    throw new CommandFailureException(
                        ProtocolConstants.ErrorTestRunNotFound,
                        "No test run to query (the recorded runId is not valid).");
                }
            }

            string projectRoot = Path.Combine(Application.dataPath, "..");
            string runsDir = Path.Combine(projectRoot, ProtocolConstants.TestRunsDirectoryRelative);
            string filePath = Path.Combine(runsDir, runId + ".json");

            if (File.Exists(filePath))
            {
                return ApplyTestResultProjection(File.ReadAllText(filePath), args.failuresOnly);
            }

            string inlineRunId = SessionState.GetString(
                ProtocolConstants.TestSessionKeyInlineResultRunId,
                string.Empty);
            if (string.Equals(inlineRunId, runId, StringComparison.Ordinal))
            {
                string inlineJson = SessionState.GetString(
                    ProtocolConstants.TestSessionKeyInlineResultJson,
                    string.Empty);
                if (!string.IsNullOrWhiteSpace(inlineJson))
                {
                    return ApplyTestResultProjection(inlineJson, args.failuresOnly);
                }
            }

            string activeRunId = SessionState.GetString(ProtocolConstants.TestSessionKeyActiveRunId, string.Empty);
            if (string.Equals(activeRunId, runId, StringComparison.Ordinal))
            {
                return ProtocolJson.Serialize(new TestRunResultPayload
                {
                    runId = runId,
                    mode = SessionState.GetString(ProtocolConstants.TestSessionKeyActiveMode, string.Empty),
                    status = "Running",
                    startedAt = SessionState.GetString(ProtocolConstants.TestSessionKeyActiveStartedAt, string.Empty),
                    summary = new TestRunSummary
                    {
                        completed = SessionState.GetInt(ProtocolConstants.TestSessionKeyProgressCompleted, 0),
                        total = SessionState.GetInt(ProtocolConstants.TestSessionKeyProgressTotal, 0),
                    },
                });
            }

            throw new CommandFailureException(
                ProtocolConstants.ErrorTestRunNotFound,
                "runId '" + runId + "' does not match a test run.");
        }

        private static string HandleCancel()
        {
            string activeRunId;
            string activeMode;
            lock (_activeLock)
            {
                if (!_hasActiveRun)
                {
                    return ProtocolJson.Serialize(new TestCancelPayload
                    {
                        cancelled = false,
                        runId = string.Empty,
                        mode = string.Empty,
                        message = "There is no in-progress test run to cancel.",
                    });
                }

                activeRunId = SessionState.GetString(ProtocolConstants.TestSessionKeyActiveRunId, string.Empty);
                activeMode = SessionState.GetString(ProtocolConstants.TestSessionKeyActiveMode, string.Empty);

                if (!ProtocolHelpers.IsValid32HexId(activeRunId))
                {
                    // The lock is held but no run id has been persisted yet — the start is still
                    // resolving its --filter. Release the lock without synthesizing a completion:
                    // fabricating callbacks with an empty run id would write a stray
                    // "<runsDir>/.json" sidecar and repoint last-run.json at an empty run id,
                    // breaking `test results` with no --run-id.
                    _cancelGeneration++;
                    EndRun();

                    return ProtocolJson.Serialize(new TestCancelPayload
                    {
                        cancelled = true,
                        runId = string.Empty,
                        mode = activeMode,
                        message = "Cancelled the test run that was still starting.",
                    });
                }
            }

            bool isPlayMode = string.Equals(activeMode, "play", StringComparison.Ordinal);

            // Attempt a graceful TestRunnerApi cancel first; MarkCancelled() below always
            // releases the lock (via TestCommandHandler.EndRun() in its finally block) even
            // if the graceful cancel fails or the run guid is stale.
            TryCancelActiveUnityTestRun();

            lock (_activeLock)
            {
                string currentRunId = SessionState.GetString(
                    ProtocolConstants.TestSessionKeyActiveRunId,
                    string.Empty);
                if (!_hasActiveRun || !string.Equals(currentRunId, activeRunId, StringComparison.Ordinal))
                {
                    // CancelTestRun forces a play-mode exit and can pump the editor loop, so
                    // Unity's RunFinished may have landed in that gap and already written the real
                    // result. Synthesizing a "Cancelled" payload now would overwrite a genuinely
                    // completed run with a zeroed summary and repoint last-run.json at it.
                    return ProtocolJson.Serialize(new TestCancelPayload
                    {
                        cancelled = false,
                        runId = activeRunId,
                        mode = activeMode,
                        message = "The test run had already finished just before the cancel request (runId=" + activeRunId + ").",
                    });
                }
            }

            TestRunnerCallbacks callbacks = EnsureCallbacksForCompletion(
                activeRunId,
                activeMode,
                out bool fabricatedCallbacks);
            callbacks.MarkCancelled();

            if (isPlayMode)
            {
                // The sidecar exists now, so the pending `test run` client gets the cancelled
                // result instead of hanging until its IPC timeout when the watchdog is torn down.
                RespondToPendingPlayModeStart();
                DestroyIfNotRegisteredPlayModeCallback(callbacks);
                CleanupPlayModeRegistration();
            }
            else if (fabricatedCallbacks)
            {
                // EditMode runs are dispatchable mid-run and their live callbacks object is still
                // registered against StartEditModeRunResolved's api and captured by its Poll
                // closure — only an object this call just fabricated may be destroyed here.
                UnityEngine.Object.DestroyImmediate(callbacks);
            }

            return ProtocolJson.Serialize(new TestCancelPayload
            {
                cancelled = true,
                runId = activeRunId,
                mode = activeMode,
                message = "The test run was cancelled (runId=" + activeRunId + ").",
            });
        }

        private static string TryReadLastRunId()
        {
            try
            {
                string lastRunPath = Path.Combine(
                    Application.dataPath,
                    "..",
                    ProtocolConstants.TestLastRunFileRelative);
                if (!File.Exists(lastRunPath))
                {
                    return string.Empty;
                }

                string content = File.ReadAllText(lastRunPath);
                TestRunnerCallbacks_LastRunPointer? pointer =
                    ProtocolJson.Deserialize<TestRunnerCallbacks_LastRunPointer>(content);
                return pointer?.lastRunId ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string GetAssemblyName(ITestAdaptor test)
        {
            string fullName = test.TypeInfo?.Assembly?.FullName ?? string.Empty;
            int commaIndex = fullName.IndexOf(',');
            return commaIndex >= 0
                ? fullName.Substring(0, commaIndex).Trim()
                : fullName;
        }

        private static string GetRequestId(TaskCompletionSource<ResponseEnvelope> completion)
        {
            string? requestId = completion.Task.AsyncState as string;
            if (string.IsNullOrWhiteSpace(requestId))
            {
                throw new InvalidOperationException("Deferred test request ID is missing.");
            }

            return requestId;
        }

        private static bool TryBeginRun_Internal(out string activeRunId, out string activeMode)
        {
            lock (_activeLock)
            {
                if (_hasActiveRun)
                {
                    activeRunId = SessionState.GetString(ProtocolConstants.TestSessionKeyActiveRunId, string.Empty);
                    activeMode = SessionState.GetString(ProtocolConstants.TestSessionKeyActiveMode, string.Empty);
                    return false;
                }

                _hasActiveRun = true;
                activeRunId = string.Empty;
                activeMode = string.Empty;
                return true;
            }
        }

        internal static int ResolveTestTimeoutSeconds(TestRunArgs args)
        {
            return args.timeoutSeconds > 0
                ? Math.Min(args.timeoutSeconds, ProtocolConstants.MaxTestRunTimeoutSeconds)
                : ProtocolConstants.DefaultTestRunTimeoutSeconds;
        }

        internal static void BeginRun_PersistSession(
            string runId,
            string mode,
            int timeoutSeconds,
            bool noDomainReload,
            bool failuresOnly = false)
        {
            ClearInlineResultFromSession();
            SessionState.SetString(ProtocolConstants.TestSessionKeyActiveRunId, runId);
            SessionState.SetString(ProtocolConstants.TestSessionKeyActiveMode, mode);
            SessionState.SetString(ProtocolConstants.TestSessionKeyActiveStartedAt, DateTime.UtcNow.ToString("O"));
            SessionState.SetInt(ProtocolConstants.TestSessionKeyActiveTimeoutSeconds, timeoutSeconds);
            SessionState.SetBool(ProtocolConstants.TestSessionKeyActiveNoDomainReload, noDomainReload);
            SessionState.SetBool(ProtocolConstants.TestSessionKeyActiveFailuresOnly, failuresOnly);
        }

        internal static void StoreActiveRunGuid(string runGuid)
        {
            SessionState.SetString(ProtocolConstants.TestSessionKeyActiveRunGuid, runGuid);
        }

        internal static void EndRun()
        {
            lock (_activeLock)
            {
                _hasActiveRun = false;
                SessionState.EraseString(ProtocolConstants.TestSessionKeyActiveRunId);
                SessionState.EraseString(ProtocolConstants.TestSessionKeyActiveMode);
                SessionState.EraseString(ProtocolConstants.TestSessionKeyActiveStartedAt);
                SessionState.EraseInt(ProtocolConstants.TestSessionKeyActiveTimeoutSeconds);
                SessionState.EraseString(ProtocolConstants.TestSessionKeyActiveRunGuid);
                SessionState.EraseBool(ProtocolConstants.TestSessionKeyActiveNoDomainReload);
                SessionState.EraseBool(ProtocolConstants.TestSessionKeyActiveFailuresOnly);
                SessionState.EraseInt(ProtocolConstants.TestSessionKeyProgressCompleted);
                SessionState.EraseInt(ProtocolConstants.TestSessionKeyProgressTotal);
                UnityObjectIdentity.EraseSessionObject(ProtocolConstants.TestSessionKeyCallbacksInstanceId);
                StopRestoredEditModeWatchdog();
            }
        }

        internal static void RestoreLockFromSession()
        {
            CleanupStaleTestRunTempFiles();

            string activeRunId = SessionState.GetString(ProtocolConstants.TestSessionKeyActiveRunId, string.Empty);
            if (string.IsNullOrEmpty(activeRunId))
            {
                return;
            }

            string activeMode = SessionState.GetString(ProtocolConstants.TestSessionKeyActiveMode, string.Empty);
            bool hasTimeout = TryGetActiveTimeoutSeconds(out int timeoutSeconds);
            bool hasStartedAt = TryGetActiveStartedAtUtc(out DateTime startedAtUtc);
            bool hasDeadline = hasTimeout && hasStartedAt;

            if (string.Equals(activeMode, "edit", StringComparison.Ordinal)
                && (!hasDeadline || IsActiveRunPastDeadline(startedAtUtc, timeoutSeconds)))
            {
                MarkRestoredEditModeRunInterrupted(activeRunId);
                return;
            }

            if (string.Equals(activeMode, "edit", StringComparison.Ordinal))
            {
                lock (_activeLock)
                {
                    _hasActiveRun = true;
                }

                RestoreEditModeInterruptedRunFromSession(activeRunId, startedAtUtc, timeoutSeconds);
                return;
            }

            if (string.Equals(activeMode, "play", StringComparison.Ordinal)
                && hasDeadline
                && IsActiveRunPastDeadline(startedAtUtc, timeoutSeconds))
            {
                MarkRestoredPlayModeRunTimedOut(activeRunId);
                return;
            }

            if (!hasTimeout)
            {
                timeoutSeconds = ProtocolConstants.DefaultTestRunTimeoutSeconds;
            }

            lock (_activeLock)
            {
                _hasActiveRun = true;
            }

            if (string.Equals(activeMode, "play", StringComparison.Ordinal))
            {
                RestorePlayModeRunFromSession(activeRunId, timeoutSeconds);
            }
        }

        internal static int GetActiveTimeoutSeconds()
        {
            if (!TryGetActiveTimeoutSeconds(out int timeoutSeconds))
            {
                return ProtocolConstants.DefaultTestRunTimeoutSeconds;
            }

            return timeoutSeconds > 0
                ? Math.Min(timeoutSeconds, ProtocolConstants.MaxTestRunTimeoutSeconds)
                : ProtocolConstants.DefaultTestRunTimeoutSeconds;
        }

        internal static bool TryGetActiveTimeoutSeconds(out int timeoutSeconds)
        {
            int storedTimeoutSeconds = SessionState.GetInt(
                ProtocolConstants.TestSessionKeyActiveTimeoutSeconds,
                0);
            if (storedTimeoutSeconds <= 0)
            {
                timeoutSeconds = 0;
                return false;
            }

            timeoutSeconds = Math.Min(storedTimeoutSeconds, ProtocolConstants.MaxTestRunTimeoutSeconds);
            return true;
        }

        internal static bool TryGetActiveStartedAtUtc(out DateTime startedAtUtc)
        {
            string startedAtValue = SessionState.GetString(
                ProtocolConstants.TestSessionKeyActiveStartedAt,
                string.Empty);
            if (DateTime.TryParse(
                startedAtValue,
                null,
                System.Globalization.DateTimeStyles.RoundtripKind,
                out DateTime parsed))
            {
                startedAtUtc = parsed.ToUniversalTime();
                return true;
            }

            startedAtUtc = DateTime.UtcNow;
            return false;
        }

        internal static bool GetActiveFailuresOnly()
        {
            return SessionState.GetBool(
                ProtocolConstants.TestSessionKeyActiveFailuresOnly,
                false);
        }

        private static bool IsActiveRunPastDeadline(DateTime startedAtUtc, int timeoutSeconds)
        {
            DateTime deadline = startedAtUtc.AddSeconds(timeoutSeconds + ProtocolConstants.TestRunCancelGraceSeconds);
            return DateTime.UtcNow >= deadline;
        }

        private static void MarkRestoredEditModeRunInterrupted(string runId)
        {
            TestRunnerCallbacks callbacks = EnsureCallbacksForCompletion(runId, "edit");
            callbacks.MarkFailed(ProtocolConstants.TestRunInterruptedMessage);
            DestroyIfNotRegisteredPlayModeCallback(callbacks);
        }

        private static void RestoreEditModeInterruptedRunFromSession(
            string runId,
            DateTime startedAtUtc,
            int timeoutSeconds)
        {
            StopRestoredEditModeWatchdog();

            DateTime deadline = startedAtUtc.AddSeconds(timeoutSeconds + ProtocolConstants.TestRunCancelGraceSeconds);
            void Poll()
            {
                string activeRunId = SessionState.GetString(
                    ProtocolConstants.TestSessionKeyActiveRunId,
                    string.Empty);
                if (!string.Equals(activeRunId, runId, StringComparison.Ordinal))
                {
                    StopRestoredEditModeWatchdog();
                    return;
                }

                if (DateTime.UtcNow >= deadline)
                {
                    MarkRestoredEditModeRunInterrupted(runId);
                }
            }

            _editModeRestoreWatchdog = Poll;
            EditorTickPump.Add(_editModeRestoreWatchdog);
            Poll();
        }

        private static void StopRestoredEditModeWatchdog()
        {
            if (_editModeRestoreWatchdog == null)
            {
                return;
            }

            EditorTickPump.Remove(_editModeRestoreWatchdog);
            _editModeRestoreWatchdog = null;
        }

        internal static ResponseEnvelope BuildTestRunResultEnvelope(
            string requestId,
            string? projectHash,
            string resultJson,
            long durationMs,
            bool failuresOnly = false)
        {
            resultJson = ApplyTestResultProjection(resultJson, failuresOnly);
            if (TryDeserializeTestRunResult(resultJson, out TestRunResultPayload result)
                && ProtocolHelpers.IsTestRunResultStatusError(result.status))
            {
                return ResponseEnvelope.Failure(
                    requestId,
                    projectHash,
                    ProtocolHelpers.GetTestRunResultErrorCode(result.status, result.warnings),
                    ProtocolHelpers.BuildTestRunResultErrorMessage(result),
                    false,
                    durationMs,
                    ProtocolConstants.TransportLive,
                    resultJson);
            }

            return ResponseEnvelope.Success(
                requestId,
                projectHash,
                resultJson,
                durationMs,
                ProtocolConstants.TransportLive);
        }

        private static bool TryDeserializeTestRunResult(string resultJson, out TestRunResultPayload result)
        {
            try
            {
                TestRunResultPayload? parsed = ProtocolJson.Deserialize<TestRunResultPayload>(resultJson);
                if (parsed != null)
                {
                    result = parsed;
                    return true;
                }
            }
            catch
            {
            }

            result = new TestRunResultPayload();
            return false;
        }

        private static string SerializeTestListPayload(TestListPayload payload, TestListArgs args)
        {
            if (TestResultProjectionUtility.ShouldIncludeTestListDetail(args))
            {
                return ProtocolJson.Serialize(payload);
            }

            return JsonConvert.SerializeObject(new
            {
                payload.mode,
                tests = payload.tests.Select(test => new
                {
                    test.fullName,
                    test.mode,
                }).ToArray(),
            }, BridgeJsonSettings.CamelCaseIgnoreNull);
        }

        private static string ApplyTestResultProjection(string resultJson, bool failuresOnly)
        {
            if (!failuresOnly)
            {
                return resultJson;
            }

            if (!TryDeserializeTestRunResult(resultJson, out TestRunResultPayload result))
            {
                return resultJson;
            }

            return ProtocolJson.Serialize(TestResultProjectionUtility.ApplyFailuresOnly(result, failuresOnly));
        }

        private static void CleanupStaleTestRunTempFiles()
        {
            try
            {
                string runsDir = Path.Combine(
                    Application.dataPath,
                    "..",
                    ProtocolConstants.TestRunsDirectoryRelative);
                AtomicFileUtility.CleanupTempFiles(runsDir);
            }
            catch (Exception exception)
            {
                UnityEngine.Debug.LogWarning("[unity-cli-bridge] Failed to clean stale test run temp files: " + exception.Message);
            }
        }

        private static void ClearInlineResultFromSession()
        {
            SessionState.EraseString(ProtocolConstants.TestSessionKeyInlineResultRunId);
            SessionState.EraseString(ProtocolConstants.TestSessionKeyInlineResultJson);
        }

        [Serializable]
        private sealed class TestRunnerCallbacks_LastRunPointer
        {
            public string lastRunId = string.Empty;
        }
    }
}
