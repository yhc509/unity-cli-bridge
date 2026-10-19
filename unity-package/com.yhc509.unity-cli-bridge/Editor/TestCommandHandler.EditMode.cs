#nullable enable
using System;
using System.Diagnostics;
using System.Threading.Tasks;
using UnityCli.Protocol;
using UnityEditor;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine;

namespace UnityCliBridge.Bridge.Editor
{
    internal sealed partial class TestCommandHandler
    {
        private void StartEditModeRun(
            TestRunArgs args,
            TaskCompletionSource<ResponseEnvelope> completion,
            string projectHash,
            string requestId)
        {
            var stopwatch = Stopwatch.StartNew();
            ResolveFilterFullNamesThenStart(
                TestMode.EditMode,
                args,
                completion,
                projectHash,
                requestId,
                stopwatch,
                resolvedTestNames => StartEditModeRunResolved(
                    args,
                    completion,
                    projectHash,
                    requestId,
                    stopwatch,
                    resolvedTestNames));
        }

        private void StartEditModeRunResolved(
            TestRunArgs args,
            TaskCompletionSource<ResponseEnvelope> completion,
            string projectHash,
            string requestId,
            Stopwatch stopwatch,
            string[]? resolvedTestNames)
        {
            string runId = Guid.NewGuid().ToString("N");
            int timeoutSec = ResolveTestTimeoutSeconds(args);
            BeginRun_PersistSession(runId, "edit", timeoutSec, false);

            var callbacks = TestRunnerCallbacks.Create(runId, "edit", false);
            callbacks.StoreInstanceIdToSession();

            var api = ScriptableObject.CreateInstance<TestRunnerApi>();
            api.RegisterCallbacks(callbacks);

            void Cleanup()
            {
                try
                {
                    api.UnregisterCallbacks(callbacks);
                }
                catch
                {
                }

                UnityEngine.Object.DestroyImmediate(api);
                UnityEngine.Object.DestroyImmediate(callbacks);
            }

            string runGuid;
            try
            {
                runGuid = api.Execute(new ExecutionSettings(BuildFilter(args, TestMode.EditMode, resolvedTestNames)));
                StoreActiveRunGuid(runGuid);
            }
            catch
            {
                stopwatch.Stop();
                Cleanup();
                throw;
            }

            bool cancelRequested = false;
            DateTime cancelRequestedAt = DateTime.MinValue;

            void Poll()
            {
                if (completion.Task.IsCompleted)
                {
                    EditorApplication.update -= Poll;
                    Cleanup();
                    return;
                }

                if (TestRunCompletedOnDisk(runId, out string? resultJson))
                {
                    EditorApplication.update -= Poll;
                    stopwatch.Stop();
                    completion.TrySetResult(BuildTestRunResultEnvelope(
                        requestId,
                        projectHash,
                        resultJson!,
                        stopwatch.ElapsedMilliseconds,
                        args.failuresOnly));
                    Cleanup();
                    return;
                }

                // The tests finished, but the Test Framework is still restoring scenes.
                // Cancelling or timing out here would drop that result and release the lock early.
                if (HasPendingSettlement(runId))
                {
                    return;
                }

                if (cancelRequested)
                {
                    double cancelSeconds = (DateTime.UtcNow - cancelRequestedAt).TotalSeconds;
                    if (cancelSeconds <= ProtocolConstants.TestRunCancelGraceSeconds)
                    {
                        return;
                    }

                    callbacks.MarkTimedOut();
                    EditorApplication.update -= Poll;
                    stopwatch.Stop();
                    if (TestRunCompletedOnDisk(runId, out string? finalJson))
                    {
                        completion.TrySetResult(BuildTestRunResultEnvelope(
                            requestId,
                            projectHash,
                            finalJson!,
                            stopwatch.ElapsedMilliseconds,
                            args.failuresOnly));
                    }
                    else
                    {
                        completion.TrySetResult(ResponseEnvelope.Failure(
                            requestId,
                            projectHash,
                            ProtocolConstants.ErrorTestTimeout,
                            "EditMode test run did not finish within " + timeoutSec + " seconds.",
                            false,
                            stopwatch.ElapsedMilliseconds,
                            ProtocolConstants.TransportLive,
                            null));
                        EndRun();
                    }

                    Cleanup();
                    return;
                }

                if (stopwatch.Elapsed.TotalSeconds > timeoutSec)
                {
                    cancelRequested = true;
                    cancelRequestedAt = DateTime.UtcNow;
                    TestRunnerApi.CancelTestRun(runGuid);
                }
            }

            EditorApplication.update += Poll;
            Poll();
        }

        private static bool TestRunCompletedOnDisk(string runId, out string? json)
        {
            string runsDir = System.IO.Path.Combine(
                Application.dataPath,
                "..",
                ProtocolConstants.TestRunsDirectoryRelative);
            string path = System.IO.Path.Combine(runsDir, runId + ".json");
            if (System.IO.File.Exists(path))
            {
                json = System.IO.File.ReadAllText(path);
                return true;
            }

            json = null;
            return false;
        }

        internal static Filter BuildFilter(TestRunArgs args, TestMode mode, string[]? resolvedTestNames = null)
        {
            var filter = new Filter { testMode = mode };
            if (resolvedTestNames != null)
            {
                // Unity treats an empty testNames array the same as no name filter.
                filter.testNames = resolvedTestNames.Length > 0
                    ? resolvedTestNames
                    : new[] { NoMatchingTestName };
            }

            if (!string.IsNullOrEmpty(args.category))
            {
                filter.categoryNames = new[] { args.category };
            }

            if (!string.IsNullOrEmpty(args.assembly))
            {
                filter.assemblyNames = new[] { args.assembly };
            }

            return filter;
        }
    }
}
