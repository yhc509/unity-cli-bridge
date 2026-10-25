#nullable enable
using System;
using System.IO;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text;

namespace UnityCli.Protocol
{
    public static class ProtocolConstants
    {
        public const string AppName = "unity-cli";
        public const string ProtocolVersion = "8";
        public const int DefaultLiveTimeoutMs = 30_000;
        public const int DefaultTimeoutMs = DefaultLiveTimeoutMs;
        public const int DefaultExecuteTimeoutMs = 30_000;
        public const int MaxExecuteTimeoutMs = 600_000;
        public const int DefaultConsoleLimit = 50;
        public const int DefaultAssetFindLimit = 50;
        public const int DefaultPackageListLimit = 0;
        public const int DefaultPackageRequestTimeoutSeconds = 300;
        public const int DefaultPackageLiveTimeoutMs = 360_000;
        public const int DefaultCompileRefreshWaitTimeoutSeconds = 120;
        public const int DefaultTestRunTimeoutSeconds = 300;
        public const int MaxTestRunTimeoutSeconds = 1800;
        public const int TestRunCancelGraceSeconds = 30;
        public const int RegistryHeartbeatSeconds = 2;
        public const string BusyErrorCode = "BUSY";
        public const string ErrorAssetForceRequired = "ASSET_FORCE_REQUIRED";
        public const string ErrorExecuteForceRequired = "EXECUTE_FORCE_REQUIRED";
        public const string ErrorExecuteTimeout = "EXECUTE_TIMEOUT";
        public const string ErrorPrefabForceRequired = "PREFAB_FORCE_REQUIRED";
        public const string ErrorPrefabStageDirty = "PREFAB_STAGE_DIRTY";
        public const string ErrorPackageForceRequired = "PACKAGE_FORCE_REQUIRED";
        public const string ErrorPackageBusy = "PACKAGE_BUSY";
        public const string PackageBusyMessage = "Another package command is in progress. Try again when it finishes.";
        public const string ErrorPackageTimeout = "PACKAGE_TIMEOUT";
        public const string ErrorTestBusy = "TEST_RUN_IN_PROGRESS";
        public const string ErrorTestTimeout = "TEST_RUN_TIMEOUT";
        public const string ErrorTestCancelled = "TEST_RUN_CANCELLED";
        public const string ErrorTestRunFailed = "TEST_RUN_FAILED";
        public const string ErrorTestInterrupted = "TEST_RUN_INTERRUPTED";
        public const string ErrorTestListTimeout = "TEST_LIST_TIMEOUT";
        public const string ErrorTestRunNotFound = "TEST_RUN_NOT_FOUND";
        public const string ErrorTestInvalidMode = "TEST_INVALID_MODE";
        public const string ErrorTestPlayModeEntryFailed = "TEST_PLAYMODE_ENTRY_FAILED";
        public const string ErrorRecordRequiresPlaymode = "RECORD_REQUIRES_PLAYMODE";
        public const string ErrorRecordInProgress = "RECORD_IN_PROGRESS";
        public const string ErrorRecordNotActive = "RECORD_NOT_ACTIVE";
        public const string ErrorRecordFailed = "RECORD_FAILED";
        public const string ErrorRecordInterrupted = "RECORD_INTERRUPTED";
        public const string ErrorRecordNotFound = "RECORD_NOT_FOUND";
        public const string ErrorRecordTimeout = "RECORD_TIMEOUT";
        public const string ErrorProfileRequiresPlaymode = "PROFILE_REQUIRES_PLAYMODE";
        public const string ErrorProfileInProgress = "PROFILE_IN_PROGRESS";
        public const string ErrorProfileNotRunning = "PROFILE_NOT_RUNNING";
        public const string ErrorProfileNotFound = "PROFILE_NOT_FOUND";
        public const string ErrorProfileMarkerAmbiguous = "PROFILE_MARKER_AMBIGUOUS";
        public const string ErrorProfileFailed = "PROFILE_FAILED";
        public const string ErrorProfileInterrupted = "PROFILE_INTERRUPTED";
        public const string ErrorProfileTimeout = "PROFILE_TIMEOUT";
        public const string ErrorProfileEmpty = "PROFILE_EMPTY";
        public const string ErrorProfileWaitTimeout = "PROFILE_WAIT_TIMEOUT";

        /// <summary>Success notice on `profile capture stop` when the capture already finished on its own.</summary>
        public const string NoticeProfileAlreadyCompleted = "PROFILE_ALREADY_COMPLETED";
        public const string ErrorEditorDirty = "EDITOR_DIRTY";
        public const string ErrorHeadlessNoGraphics = "HEADLESS_NO_GRAPHICS";
        public const string ErrorEditorLaunchFailed = "EDITOR_LAUNCH_FAILED";
        public const string ErrorEditorWaitTimeout = "EDITOR_WAIT_TIMEOUT";
        public const string ErrorEditorStopTimeout = "EDITOR_STOP_TIMEOUT";
        public const string ErrorEditorAlreadyRunning = "EDITOR_ALREADY_RUNNING_CONFLICT";
        public const string ErrorCompileWaitTimeout = "COMPILE_WAIT_TIMEOUT";
        public const string ErrorRefreshWaitTimeout = "REFRESH_WAIT_TIMEOUT";
        public const string ErrorPlayWaitTimeout = "PLAY_WAIT_TIMEOUT";
        public const string ErrorStopWaitTimeout = "STOP_WAIT_TIMEOUT";
        public const string ErrorSceneForceRequired = "SCENE_FORCE_REQUIRED";
        public const string ErrorBackupFailed = "BACKUP_FAILED";
        public const string ErrorBackupRestoreFailed = "BACKUP_RESTORE_FAILED";
        public const string ErrorInternalInvalidPayload = "INTERNAL_INVALID_PAYLOAD";
        public const string ErrorRequestTooLarge = "REQUEST_TOO_LARGE";
        public const string ErrorRequestReadTimeout = "REQUEST_READ_TIMEOUT";
        public const string ErrorProtocolMismatch = "PROTOCOL_MISMATCH";
        public const string ErrorUnauthorized = "UNAUTHORIZED";
        public const string ErrorSceneDirty = "SCENE_DIRTY";
        public const string StatusSuccess = "success";
        public const string StatusError = "error";
        public const string TransportLive = "live";
        public const string CommandPing = "ping";
        public const string CommandStatus = "status";
        public const string CommandRefresh = "refresh";
        public const string CommandCompile = "compile";
        public const string CommandPlay = "play";
        public const string CommandPause = "pause";
        public const string CommandStop = "stop";
        public const string CommandExecuteMenu = "execute-menu";
        public const string CommandScreenshot = "screenshot";
        public const string CommandRecordStart = "record-start";
        public const string CommandRecordStop = "record-stop";
        public const string CommandRecordStatus = "record-status";
        public const string CommandExecuteCode = "execute-code";
        public const string CommandCustom = "custom";
        public const string CommandTestList = "test-list";
        public const string CommandTestRun = "test-run";
        public const string CommandTestResults = "test-results";
        public const string CommandTestCancel = "test-cancel";
        public const string CommandPackageList = "package-list";
        public const string CommandPackageAdd = "package-add";
        public const string CommandPackageRemove = "package-remove";
        public const string CommandPackageSearch = "package-search";
        public const string CommandMaterialInfo = "material-info";
        public const string CommandMaterialSet = "material-set";
        public const string CommandReadConsole = "read-console";
        public const string CommandAssetFind = "asset-find";
        public const string CommandAssetTypes = "asset-types";
        public const string CommandAssetInfo = "asset-info";
        public const string CommandAssetReimport = "asset-reimport";
        public const string CommandAssetMkdir = "asset-mkdir";
        public const string CommandAssetMove = "asset-move";
        public const string CommandAssetRename = "asset-rename";
        public const string CommandAssetDelete = "asset-delete";
        public const string CommandAssetCreate = "asset-create";
        public const string CommandSceneOpen = "scene-open";
        public const string CommandSceneInspect = "scene-inspect";
        public const string CommandScenePatch = "scene-patch";
        public const string CommandSceneSetTransform = "scene-set-transform";
        public const string CommandSceneAssignMaterial = "scene-assign-material";
        public const string CommandSceneListComponents = "scene-list-components";
        public const string CommandPrefabInspect = "prefab-inspect";
        public const string CommandPrefabCreate = "prefab-create";
        public const string CommandPrefabPatch = "prefab-patch";
        public const string CommandPrefabListComponents = "prefab-list-components";
        public const string CommandQaClick = "qa-click";
        public const string CommandQaTap = "qa-tap";
        public const string CommandQaSwipe = "qa-swipe";
        public const string CommandQaKey = "qa-key";
        public const string CommandQaUiDump = "qa-ui-dump";
        public const string CommandQaWaitUntil = "qa-wait-until";
        public const string CommandQaWorldDump = "qa-world-dump";
        public const string CommandQaRunSequence = "qa-run-sequence";
        public const string CommandProfileStats = "profile-stats";
        public const string CommandProfileCaptureStart = "profile-capture-start";
        public const string CommandProfileCaptureStop = "profile-capture-stop";
        public const string CommandProfileStatus = "profile-status";
        public const string CommandProfileMemory = "profile-memory";
        public const string CommandProfileMemoryTop = "profile-memory-top";
        public const string CommandProfileMemorySnapshot = "profile-memory-snapshot";
        public const string CommandEditorQuit = "editor-quit";
        public const int DefaultQaWaitUntilTimeoutMs = 10_000;
        public const int DefaultQaSwipeDurationMs = 300;
        public const int DefaultQaRunSequenceTimeoutMs = 60_000;
        public const int MaxQaRunSequenceTimeoutMs = 600_000;
        public const int DefaultQaRunSequenceStepTimeoutMs = 5_000;
        public const float DefaultQaNearEpsilon = 0.01f;
        public const int TestPlayModeEntryTimeoutSeconds = 15;
        public const string TestSessionKeyActiveRunId = "UCB.Test.activeRunId";
        public const string TestSessionKeyActiveMode = "UCB.Test.activeMode";
        public const string TestSessionKeyActiveStartedAt = "UCB.Test.activeStartedAt";
        public const string TestSessionKeyActiveTimeoutSeconds = "UCB.Test.activeTimeoutSeconds";
        public const string TestSessionKeyActiveRunGuid = "UCB.Test.activeRunGuid";
        public const string TestSessionKeyActiveNoDomainReload = "UCB.Test.activeNoDomainReload";
        public const string TestSessionKeyActiveFailuresOnly = "UCB.Test.activeFailuresOnly";
        public const string TestSessionKeyProgressCompleted = "UCB.Test.progress.completed";
        public const string TestSessionKeyProgressTotal = "UCB.Test.progress.total";
        public const string TestSessionKeyInlineResultRunId = "UCB.Test.inlineResult.runId";
        public const string TestSessionKeyInlineResultJson = "UCB.Test.inlineResult.json";
        public const string TestSessionKeyScopeActive = "UCB.Test.scope.active";
        public const string TestSessionKeyScopeWasEnabled = "UCB.Test.scope.wasEnabled";
        public const string TestSessionKeyScopePreviousFlags = "UCB.Test.scope.previousFlags";
        public const string TestSessionKeyCallbacksInstanceId = "UCB.Test.callbacks.instanceId";
        public const string TestSessionKeySettlementArmedAt = "UCB.Test.settlementArmedAt";
        public const string TestPendingResultSuffix = ".pending.json";
        public const string TestRunsDirectoryRelative = "Library/com.yhc509.unity-cli-bridge/test-runs";
        public const string TestLastRunFileRelative = "Library/com.yhc509.unity-cli-bridge/last-run.json";
        public const string TestRunInterruptedMessage = "EditMode run interrupted by domain reload — no result available";
        public const string RecordingsDirectoryRelative = "Library/com.yhc509.unity-cli-bridge/recordings";
        public const int DefaultRecordFps = 30;
        public const int MaxRecordDurationSeconds = 600;
        public const string RecordSessionKeyActiveId = "UCB.Record.activeId";
        public const string RecordSessionKeyTargetPath = "UCB.Record.targetPath";
        public const string RecordSessionKeyStartedAt = "UCB.Record.startedAt";
        public const string RecordSessionKeyDurationSeconds = "UCB.Record.durationSeconds";
        public const string ProfilesDirectoryRelative = "Library/com.yhc509.unity-cli-bridge/profiles";
        public const string MemoryReportsDirectoryRelative = "Library/com.yhc509.unity-cli-bridge/memory";
        public const string SnapshotsDirectoryRelative = "Library/com.yhc509.unity-cli-bridge/snapshots";
        public const int ProfileMemorySnapshotTimeoutSeconds = 300;
        public const int DefaultProfileMemoryFrames = 30;
        public const double DefaultProfileMemoryThresholdPercent = 5.0;
        public const int DefaultProfileMemoryCompareLimit = 10;
        public const int DefaultProfileMemoryTopLimit = 20;
        public const int MaxProfileMemoryTopLimit = 100;
        public const int MaxProfileMemoryTopScanPerType = 2000;
        public const int MaxProfileMemoryTopVisitedPerType = 20000;
        public const int DefaultProfileStatsFrames = 60;
        public const int MaxProfileStatsFrames = 1000;
        public const int ProfileStatsTimeoutSeconds = 120;
        public const int MaxProfileCaptureSeconds = 600;
        public const int ProfileRingBufferFallbackFrames = 2000;
        public const float DefaultProfileBudgetMs = 16.67f;
        public const int ProfileWalkFramesPerTick = 50;
        public const int ProfileFrameTopMarkerCount = 10;
        public const int DefaultProfileListLimit = 5;
        public const int ProfileStopWaitTimeoutSeconds = 120;
        public const int MaxProfileCaptureWaitSeconds = 3600;
        public const int ProfileCaptureWaitPollMs = 500;
        public const int MaxProfileStatsMarkers = 10;
        public const int ProfileMarkerStatsWarmupFrames = 2;

        /// <summary>Schema 2 adds gcByScript and gcUnattributedBytes. Schema 1 sidecars are still read.</summary>
        public const int ProfileSidecarSchemaVersion = 2;
        public const string ProfileSessionKeyActiveId = "UCB.Profile.activeId";
        public const string ProfileSessionKeyLastId = "UCB.Profile.lastId";
        public const string ProfileEmptyHint =
            "No profiler frames were recorded. This happens when stop follows start right after entering Play Mode, or when the Editor is not ticking (minimized or in the background). Retry with `profile capture start --frames <n> --wait`.";
        public const string GpuWaitMarkerName = "Gfx.WaitForPresentOnGfxThread";
        public static readonly string[] SupportedScenePrimitiveNames =
        {
            "Cube",
            "Sphere",
            "Capsule",
            "Cylinder",
            "Plane",
            "Quad",
        };

        public static string NormalizeScenePrimitive(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            switch (value.Trim().ToLowerInvariant())
            {
                case "cube":
                    return "Cube";
                case "sphere":
                    return "Sphere";
                case "capsule":
                    return "Capsule";
                case "cylinder":
                    return "Cylinder";
                case "plane":
                    return "Plane";
                case "quad":
                    return "Quad";
                default:
                    return string.Empty;
            }
        }

        public static bool IsPackageRequestTimedOut(TimeSpan elapsed, int timeoutSeconds = DefaultPackageRequestTimeoutSeconds)
        {
            if (timeoutSeconds <= 0)
            {
                return true;
            }

            return elapsed.TotalSeconds >= timeoutSeconds;
        }

        public static string BuildPackageRequestTimeoutMessage(int timeoutSeconds)
        {
            return $"The package command did not finish within {timeoutSeconds} seconds. Check Package Manager in the Editor.";
        }

        public static string ComputeProjectHash(string projectRoot)
        {
            var normalized = GetCanonicalPath(projectRoot)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Replace('\\', '/')
                .ToLowerInvariant();

            using (var sha = SHA256.Create())
            {
                byte[] bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(normalized));
                var builder = new StringBuilder(bytes.Length * 2);
                for (int index = 0; index < bytes.Length; index++)
                {
                    builder.Append(bytes[index].ToString("x2"));
                }

                return builder.ToString().Substring(0, 12);
            }
        }

        public static string GetCanonicalPath(string path)
        {
            var fullPath = Path.GetFullPath(path);
            var realPath = TryResolveRealPath(fullPath);
            return (string.IsNullOrWhiteSpace(realPath) ? fullPath : realPath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        public static string BuildPipeName(string projectHash)
        {
            if (Path.DirectorySeparatorChar == '\\')
            {
                return $"unity-cli-{projectHash}";
            }

            return Path.Combine(Path.GetTempPath(), $"unity-cli-{projectHash}.sock");
        }

        private static string? TryResolveRealPath(string fullPath)
        {
            if (string.IsNullOrWhiteSpace(fullPath))
            {
                return null;
            }

            if (Path.DirectorySeparatorChar == '\\')
            {
                return null;
            }

            if (!Directory.Exists(fullPath) && !File.Exists(fullPath))
            {
                return null;
            }

            IntPtr resolvedPointer = RealPath(fullPath, IntPtr.Zero);
            if (resolvedPointer == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                return Marshal.PtrToStringAnsi(resolvedPointer);
            }
            finally
            {
                Free(resolvedPointer);
            }
        }

        [DllImport("libc", EntryPoint = "realpath", SetLastError = true)]
        private static extern IntPtr RealPath(string path, IntPtr buffer);

        [DllImport("libc", EntryPoint = "free")]
        private static extern void Free(IntPtr pointer);
    }
}
