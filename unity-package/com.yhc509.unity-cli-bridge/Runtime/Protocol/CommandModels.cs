#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace UnityCli.Protocol
{
    [Serializable]
    public sealed class PingPayload
    {
        public string message = string.Empty;
        public string timestampUtc = string.Empty;
    }

    [Serializable]
    public sealed class StatusPayload
    {
        public string projectRoot = string.Empty;
        public string projectHash = string.Empty;
        public string projectName = string.Empty;
        public string unityVersion = string.Empty;
        public bool isPlaying;
        public bool isPaused;
        public bool isCompiling;
        public bool isUpdating;
        public string activeScenePath = string.Empty;
        public string pipeName = string.Empty;
        // True while a Play Mode enter/exit is requested but not finished (isPlaying != isPlayingOrWillChangePlaymode).
        public bool isChangingPlayMode;
        // Changes on every domain reload; lets the CLI tell whether the reload that follows `play` has happened.
        public string domainId = string.Empty;
    }

    [Serializable]
    public sealed class MessagePayload
    {
        public string message = string.Empty;
    }

    [Serializable]
    public sealed class EditorQuitArgs
    {
        public bool force;
    }

    [Serializable]
    public sealed class EditorQuitPayload
    {
        public bool stopping;
        public int editorProcessId;
    }

    [Serializable]
    public sealed class PlayStatePayload
    {
        public bool isPlaying;
        // Domain id at the time of the request, and whether entering Play Mode will reload the domain.
        // The CLI waits for the domain id to change before reporting Play Mode as ready.
        public string domainId = string.Empty;
        public bool reloadsDomain;
    }

    [Serializable]
    public sealed class PauseStatePayload
    {
        public bool isPaused;
    }

    [Serializable]
    public sealed class StopStatePayload
    {
        public bool isPlaying;
        public bool isPaused;
    }

    [Serializable]
    public sealed class ExecuteMenuArgs
    {
        public string path = string.Empty;
        public bool list;
        public string? prefix;
    }

    [Serializable]
    public sealed class ExecuteMenuPayload
    {
        public string path = string.Empty;
        public bool executed;
        public string? prefix;
        public string[] menus = Array.Empty<string>();
    }

    [Serializable]
    public sealed class ScreenshotArgs
    {
        public string? view;
        public string? camera;
        public string? outputPath;
        public int width;
        public int height;
        public string? format;
        public int quality;
        public int maxWidth;
    }

    [Serializable]
    public sealed class ScreenshotPayload
    {
        public string savedPath = string.Empty;
        public int width;
        public int height;
        public int screenWidth;
        public int screenHeight;
        public string coordinateOrigin = "bottom-left";
        public string imageOrigin = "top-left";
        public long fileSizeBytes;
        public string format = "png";
    }

    [Serializable]
    public sealed class RecordStartArgs
    {
        public string? path;
        public int fps;
        public int maxWidth;
        public int durationSeconds;
    }

    [Serializable]
    public sealed class RecordStatusArgs
    {
        public string? recordingId;
    }

    [Serializable]
    public sealed class RecordStartedPayload
    {
        public string recordingId = string.Empty;
        public string status = string.Empty;
        public string targetPath = string.Empty;
        public string startedAt = string.Empty;
        public int durationSeconds;
    }

    [Serializable]
    public sealed class RecordResultPayload
    {
        public string recordingId = string.Empty;
        public string status = string.Empty;
        public string path = string.Empty;
        public long durationMs;
        public long fileSizeBytes;
        public int fps;
        public int width;
        public int height;
    }

    [Serializable]
    public sealed class ExecuteCodeArgs
    {
        public string code = string.Empty;
        public string? argumentsJson;
        public bool force;
        public int timeoutMs;
    }

    [Serializable]
    public sealed class ExecuteCodePayload
    {
        public string output = string.Empty;
        public bool success;
        public string? error;
        public string? result;
        public bool hasResult;
        // True when the wrapped snippet was already compiled in this domain (SHA-256 cache hit).
        public bool cacheHit;
        // Wall milliseconds spent compiling (0 on a cache hit).
        public long compileMs;
    }

    [Serializable]
    public sealed class CustomCommandArgs
    {
        public string commandName = string.Empty;
        public string argumentsJson = "{}";
    }

    [Serializable]
    public sealed class CustomCommandPayload
    {
        public string commandName = string.Empty;
        public string resultJson = "{}";
    }

    [Serializable]
    public sealed class ReadConsoleArgs
    {
        public int limit = ProtocolConstants.DefaultConsoleLimit;
        public string? type;
        public bool noStackTrace;
    }

    [Serializable]
    public sealed class ReadConsolePayload
    {
        public ConsoleLogEntry[] entries = Array.Empty<ConsoleLogEntry>();
    }

    [Serializable]
    public sealed class PackageAddArgs
    {
        public string name = string.Empty;
        public string? version;
    }

    [Serializable]
    public sealed class PackageRemoveArgs
    {
        public string name = string.Empty;
        public bool force;
    }

    [Serializable]
    public sealed class PackageListArgs
    {
        public string filter = string.Empty;
        public int limit = ProtocolConstants.DefaultPackageListLimit;
    }

    [Serializable]
    public sealed class PackageSearchArgs
    {
        public string query = string.Empty;
    }

    [Serializable]
    public sealed class PackageRecord
    {
        public string name = string.Empty;
        public string version = string.Empty;
        public string displayName = string.Empty;
        public string source = string.Empty;
    }

    public static class PackageListFilterUtility
    {
        public static PackageRecord[] ApplyPackageListFilter(IReadOnlyList<PackageRecord> records, PackageListArgs args)
        {
            if (records == null)
            {
                throw new ArgumentNullException(nameof(records));
            }

            if (args == null)
            {
                throw new ArgumentNullException(nameof(args));
            }

            IEnumerable<PackageRecord> filteredRecords = records.OrderBy(record => record.name, StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(args.filter))
            {
                string filter = args.filter.Trim();
                filteredRecords = filteredRecords.Where(record =>
                    record.name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0
                    || record.displayName.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0);
            }

            if (args.limit > 0)
            {
                filteredRecords = filteredRecords.Take(args.limit);
            }

            return filteredRecords.ToArray();
        }
    }

    [Serializable]
    public sealed class PackageListPayload
    {
        public PackageRecord[] packages = Array.Empty<PackageRecord>();
    }

    [Serializable]
    public sealed class PackageMutationPayload
    {
        public string name = string.Empty;
        public string version = string.Empty;
        public bool added;
        public bool removed;
    }

    [Serializable]
    public sealed class PackageSearchPayload
    {
        public PackageRecord[] results = Array.Empty<PackageRecord>();
    }

    [Serializable]
    public sealed class MaterialInfoArgs
    {
        public string path = string.Empty;
        public bool omitDefaults;
    }

    [Serializable]
    public sealed class MaterialSetArgs
    {
        public string path = string.Empty;
        public string? property;
        public string? value;
        public string? texture;
        public string? textureAsset;
    }

    [Serializable]
    public sealed class MaterialPropertyRecord
    {
        public string name = string.Empty;
        public string type = string.Empty;
        public string value = string.Empty;
    }

    [Serializable]
    public sealed class MaterialInfoPayload
    {
        public string path = string.Empty;
        public string shader = string.Empty;
        public MaterialPropertyRecord[] properties = Array.Empty<MaterialPropertyRecord>();
    }

    [Serializable]
    public sealed class MaterialSetPayload
    {
        public string path = string.Empty;
        public string property = string.Empty;
        public string previousValue = string.Empty;
        public string newValue = string.Empty;
    }

    [Serializable]
    public sealed class AssetFindArgs
    {
        public string name = string.Empty;
        public string? type;
        public string? folder;
        public int limit = ProtocolConstants.DefaultAssetFindLimit;
    }

    [Serializable]
    public sealed class AssetInfoArgs
    {
        public string? path;
        public string? guid;
    }

    [Serializable]
    public sealed class AssetTypesPayload
    {
        public AssetCreateTypeDescriptor[] types = Array.Empty<AssetCreateTypeDescriptor>();
    }

    [Serializable]
    public sealed class AssetPathArgs
    {
        public string path = string.Empty;
        public bool force;
    }

    [Serializable]
    public sealed class AssetMoveArgs
    {
        public string from = string.Empty;
        public string to = string.Empty;
        public bool force;
    }

    [Serializable]
    public sealed class AssetRenameArgs
    {
        public string path = string.Empty;
        public string name = string.Empty;
        public bool force;
    }

    [Serializable]
    public sealed class AssetCreateArgs
    {
        public string type = string.Empty;
        public string path = string.Empty;
        public bool force;
        public string? script;
        public string? typeName;
        public string? dataJson;
        public string? optionsJson;
    }

    [Serializable]
    public sealed class SceneVector3Value
    {
        public float x;
        public float y;
        public float z;
    }

    [Serializable]
    public sealed class SceneOpenArgs
    {
        public string path = string.Empty;
        public bool force;
    }

    [Serializable]
    public sealed class SceneInspectArgs
    {
        public string path = string.Empty;
        public string node = string.Empty;
        public bool withValues;
        public int? maxDepth;
        public bool omitDefaults;
    }

    [Serializable]
    public sealed class ScenePatchArgs
    {
        public string path = string.Empty;
        public bool force;
        public string? parent;
        public string? primitive;
        public SceneVector3Value? position;
        public string specJson = string.Empty;
    }

    [Serializable]
    public sealed class SceneSetTransformArgs
    {
        public string node = string.Empty;
        public SceneVector3Value? position;
        public SceneVector3Value? rotation;
        public SceneVector3Value? scale;
    }

    [Serializable]
    public sealed class SceneAssignMaterialArgs
    {
        public string node = string.Empty;
        public string material = string.Empty;
    }

    [Serializable]
    public sealed class PrefabInspectArgs
    {
        public string path = string.Empty;
        public string node = string.Empty;
        public bool withValues;
        public int? maxDepth;
        public bool omitDefaults;
    }

    [Serializable]
    public sealed class PrefabCreateArgs
    {
        public string path = string.Empty;
        public bool force;
        public string specJson = string.Empty;
    }

    [Serializable]
    public sealed class PrefabPatchArgs
    {
        public string path = string.Empty;
        public bool force;
        public string specJson = string.Empty;
    }

    [Serializable]
    public sealed class AssetCreateTypeDescriptor
    {
        public string typeId = string.Empty;
        public string displayName = string.Empty;
        public string defaultExtension = string.Empty;
        public string origin = string.Empty;
        public bool supportsDataPatch;
        public string[] requiredOptions = Array.Empty<string>();
        public string[] optionalOptions = Array.Empty<string>();
        public string[] aliases = Array.Empty<string>();
        public string[] notes = Array.Empty<string>();
    }

    [Serializable]
    public sealed class AssetRecord
    {
        public string path = string.Empty;
        public string guid = string.Empty;
        public string assetName = string.Empty;
        public string mainType = string.Empty;
        public bool isFolder;
        public bool exists;
    }

    [Serializable]
    public sealed class AssetFindPayload
    {
        public AssetRecord[] results = Array.Empty<AssetRecord>();
    }

    [Serializable]
    public sealed class AssetMutationPayload
    {
        public AssetRecord asset = new AssetRecord();
        public bool created;
        public bool deleted;
        public bool reimported;
        public bool overwritten;
        public string previousPath = string.Empty;
    }

    [Serializable]
    public sealed class AssetCreatePayload
    {
        public AssetRecord asset = new AssetRecord();
        public string createdType = string.Empty;
        public bool overwritten;
    }

    [Serializable]
    public sealed class SceneOpenPayload
    {
        public AssetRecord asset = new AssetRecord();
        public string activeScenePath = string.Empty;
        public bool opened;
    }

    [Serializable]
    public sealed class SceneMutationPayload
    {
        public AssetRecord asset = new AssetRecord();
        public string activeScenePath = string.Empty;
        public bool patched;
        public string? createdPath;
        public string[]? warnings;
    }

    [Serializable]
    public sealed class SceneAssignMaterialPayload
    {
        public AssetRecord asset = new AssetRecord();
        public string activeScenePath = string.Empty;
        public string node = string.Empty;
        public string material = string.Empty;
        public string? previousMaterial;
    }

    [Serializable]
    public sealed class SceneSetTransformPayload
    {
        public AssetRecord asset = new AssetRecord();
        public string activeScenePath = string.Empty;
        public string node = string.Empty;
        public SceneVector3Value? position;
        public SceneVector3Value? rotation;
        public SceneVector3Value? scale;
    }

    [Serializable]
    public sealed class PrefabMutationPayload
    {
        public AssetRecord asset = new AssetRecord();
        public bool created;
        public bool patched;
        public bool overwritten;
        public string[]? warnings;
    }

    [Serializable]
    public sealed class ConsoleLogEntry
    {
        public string timestampUtc = string.Empty;
        public string type = string.Empty;
        public string message = string.Empty;
        public string stackTrace = string.Empty;
    }

    [Serializable]
    public sealed class ConsoleLogEntryWithoutStackTrace
    {
        public string timestampUtc = string.Empty;
        public string type = string.Empty;
        public string message = string.Empty;
    }

    [Serializable]
    public sealed class QaClickArgs
    {
        public string? qaId;
        public string? target;
        public string button = string.Empty;
    }

    [Serializable]
    public sealed class QaClickPayload
    {
        public bool targetFound;
        public string resolvedPath = string.Empty;
        public string? qaId;
    }

    [Serializable]
    public sealed class QaTapArgs
    {
        public int x;
        public int y;
        public int screenshotWidth;
        public int screenshotHeight;
        public string? target;
        public string button = string.Empty;
    }

    [Serializable]
    public sealed class QaTapPayload
    {
        public bool completed;
    }

    [Serializable]
    public sealed class QaUiDumpArgs
    {
        public int screenshotWidth;
        public int screenshotHeight;
        public int limit;
        public bool interactableOnly;
        public string text = string.Empty;
        public bool omitRect;
    }

    [Serializable]
    public sealed class QaUiElement
    {
        public string path = string.Empty;
        public string type = string.Empty;
        public string text = string.Empty;
        public bool interactable;
        public int x;
        public int y;
        public int width;
        public int height;
        public int centerX;
        public int centerY;
    }

    [Serializable]
    public sealed class QaUiDumpPayload
    {
        public QaUiElement[] elements = Array.Empty<QaUiElement>();
    }

    [Serializable]
    public sealed class QaWorldDumpArgs
    {
        public int screenshotWidth;
        public int screenshotHeight;
        public bool includeOffscreen;
        public int limit;
        public string text = string.Empty;
    }

    [Serializable]
    public sealed class QaWorldElement
    {
        public string path = string.Empty;
        public string label = string.Empty;
        public int centerX;
        public int centerY;
        public bool onScreen;
        public bool hasAction;
    }

    [Serializable]
    public sealed class QaWorldDumpPayload
    {
        public QaWorldElement[] elements = Array.Empty<QaWorldElement>();
    }

    public static class QaWorldDumpEditMode
    {
        public const string Hint =
            "qa world-dump image coordinates require Play Mode. Run `play`, then `qa world-dump` again. In Edit Mode, use `scene inspect --path <scene> --with-values --omit-defaults` to read the hierarchy, component enabled state, and prefab sources. Elements here are IQaTappable/QaTappable objects in loaded scenes and have no screenshot coordinates.";
    }

    [Serializable]
    public sealed class QaSwipeArgs
    {
        public string target = string.Empty;
        public int fromX;
        public int fromY;
        public int toX;
        public int toY;
        public int durationMs = ProtocolConstants.DefaultQaSwipeDurationMs;
        public int screenshotWidth;
        public int screenshotHeight;
        public string button = string.Empty;
    }

    [Serializable]
    public sealed class QaSwipePayload
    {
        public bool completed;
    }

    [Serializable]
    public sealed class QaKeyArgs
    {
        public string key = string.Empty;
    }

    [Serializable]
    public sealed class QaKeyPayload
    {
        public bool completed;
    }

    [Serializable]
    public sealed class QaWaitUntilArgs
    {
        public string? scene;
        public string? logContains;
        public string? objectExists;
        public string? objectInteractable;
        public string? objectGone;
        public int timeoutMs = ProtocolConstants.DefaultQaWaitUntilTimeoutMs;
    }

    [Serializable]
    public sealed class QaWaitUntilPayload
    {
        public bool conditionMet;
        public int elapsedMs;
        public string? reason;
    }

    [Serializable]
    public sealed class QaSequenceCondition
    {
        public string target = string.Empty;
        public string kind = string.Empty;
        public string key = string.Empty;
        public string op = string.Empty;
        public string value = string.Empty;
        public float epsilon;
    }

    [Serializable]
    public sealed class QaSequenceAction
    {
        public string kind = string.Empty;
        public string key = string.Empty;
        public bool hasTapCoords;
        public int x;
        public int y;
        public string target = string.Empty;
        public int fromX;
        public int fromY;
        public int toX;
        public int toY;
        public int durationMs;
        public int waitMs;
    }

    [Serializable]
    public sealed class QaSequenceStep
    {
        public string name = string.Empty;
        public QaSequenceCondition[] wait = Array.Empty<QaSequenceCondition>();
        public QaSequenceAction[] actions = Array.Empty<QaSequenceAction>();
        public int timeoutMs;
    }

    [Serializable]
    public sealed class QaRunSequenceArgs
    {
        public QaSequenceStep[] steps = Array.Empty<QaSequenceStep>();
        public int timeoutMs;
        public bool record;
        public bool profile;
        public string? recordPath;
    }

    [Serializable]
    public sealed class QaSequenceUnmet
    {
        public string target = string.Empty;
        public string kind = string.Empty;
        public string key = string.Empty;
        public string op = string.Empty;
        public string expected = string.Empty;
        public string actual = string.Empty;
    }

    [Serializable]
    public sealed class QaSequenceSnapshotEntry
    {
        public string target = string.Empty;
        public string key = string.Empty;
        public string value = string.Empty;
    }

    [Serializable]
    public sealed class QaSequenceFailure
    {
        public int index;
        public string name = string.Empty;
        public QaSequenceUnmet[] unmet = Array.Empty<QaSequenceUnmet>();
        public QaSequenceSnapshotEntry[] stateSnapshot = Array.Empty<QaSequenceSnapshotEntry>();
    }

    [Serializable]
    public sealed class QaRunSequencePayload
    {
        public string status = string.Empty;
        public int completedSteps;
        public int totalSteps;
        public bool hasFailure;
        public QaSequenceFailure failedStep = new QaSequenceFailure();
        public long elapsedMs;
        public string recordingPath = string.Empty;
        public string profileCaptureId = string.Empty;
    }

    [Serializable]
    public sealed class TestListArgs
    {
        public string mode = "all";
        public bool noDetail;
    }

    [Serializable]
    public sealed class TestListPayload
    {
        public string mode = string.Empty;
        public TestListEntry[] tests = Array.Empty<TestListEntry>();
    }

    [Serializable]
    public sealed class TestListEntry
    {
        public string fullName = string.Empty;
        public string assembly = string.Empty;
        public string mode = string.Empty;
        public string[] categories = Array.Empty<string>();
    }

    [Serializable]
    public sealed class TestRunArgs
    {
        public string mode = string.Empty;
        public string filter = string.Empty;
        public string category = string.Empty;
        public string assembly = string.Empty;
        public bool noDomainReload;
        public int timeoutSeconds;
        public bool failuresOnly;
    }

    [Serializable]
    public sealed class TestRunStartedPayload
    {
        public string runId = string.Empty;
        public string mode = string.Empty;
        public string status = "STARTED";
        public string startedAt = string.Empty;
    }

    [Serializable]
    public sealed class TestResultsArgs
    {
        public string runId = string.Empty;
        public bool failuresOnly;
    }

    [Serializable]
    public sealed class TestCancelPayload
    {
        public bool cancelled;
        public string runId = string.Empty;
        public string mode = string.Empty;
        public string message = string.Empty;
    }

    [Serializable]
    public sealed class TestRunResultPayload
    {
        public string runId = string.Empty;
        public string mode = string.Empty;
        public string status = string.Empty;
        public string startedAt = string.Empty;
        public long durationMs;
        public TestRunSummary summary = new TestRunSummary();
        public TestResultEntry[] tests = Array.Empty<TestResultEntry>();
        public string[] warnings = Array.Empty<string>();
    }

    [Serializable]
    public sealed class TestRunSummary
    {
        public int total;
        public int passed;
        public int failed;
        public int skipped;
        public int inconclusive;
        public int completed;
    }

    [Serializable]
    public sealed class TestResultEntry
    {
        public string fullName = string.Empty;
        public string assembly = string.Empty;
        public string[] categories = Array.Empty<string>();
        public string outcome = string.Empty;
        public long durationMs;
        public string message = string.Empty;
        public string stackTrace = string.Empty;
    }

    public static class ConsoleLogProjectionUtility
    {
        public static bool MatchesType(string? entryType, string? filter)
        {
            if (string.IsNullOrWhiteSpace(filter))
            {
                return true;
            }

            if (string.IsNullOrWhiteSpace(entryType))
            {
                return false;
            }

            if (string.Equals(filter.Trim(), "error", StringComparison.OrdinalIgnoreCase))
            {
                return string.Equals(entryType, "Error", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(entryType, "Exception", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(entryType, "Assert", StringComparison.OrdinalIgnoreCase);
            }

            return string.Equals(entryType, filter.Trim(), StringComparison.OrdinalIgnoreCase);
        }

        public static bool ShouldOmitStackTrace(ReadConsoleArgs args)
        {
            if (args == null)
            {
                throw new ArgumentNullException(nameof(args));
            }

            return args.noStackTrace;
        }

        public static ConsoleLogEntryWithoutStackTrace[] ApplyNoStackTrace(IReadOnlyList<ConsoleLogEntry> entries)
        {
            if (entries == null)
            {
                throw new ArgumentNullException(nameof(entries));
            }

            return entries
                .Select(entry => new ConsoleLogEntryWithoutStackTrace
                {
                    timestampUtc = entry.timestampUtc,
                    type = entry.type,
                    message = entry.message,
                })
                .ToArray();
        }
    }

    public static class QaDumpProjectionUtility
    {
        public static QaUiElement[] ApplyUiDumpFilters(IReadOnlyList<QaUiElement> elements, QaUiDumpArgs args)
        {
            if (elements == null)
            {
                throw new ArgumentNullException(nameof(elements));
            }

            if (args == null)
            {
                throw new ArgumentNullException(nameof(args));
            }

            IEnumerable<QaUiElement> query = elements;
            if (args.interactableOnly)
            {
                query = query.Where(element => element.interactable);
            }

            string textFilter = args.text == null ? string.Empty : args.text.Trim();
            if (!string.IsNullOrEmpty(textFilter))
            {
                query = query.Where(element =>
                    element.text != null
                    && element.text.IndexOf(textFilter, StringComparison.OrdinalIgnoreCase) >= 0);
            }

            if (args.limit > 0)
            {
                query = query.Take(args.limit);
            }

            return query.ToArray();
        }

        public static QaWorldElement[] ApplyWorldDumpFilters(IReadOnlyList<QaWorldElement> elements, QaWorldDumpArgs args)
        {
            if (elements == null)
            {
                throw new ArgumentNullException(nameof(elements));
            }

            if (args == null)
            {
                throw new ArgumentNullException(nameof(args));
            }

            IEnumerable<QaWorldElement> query = elements;
            string textFilter = args.text == null ? string.Empty : args.text.Trim();
            if (!string.IsNullOrEmpty(textFilter))
            {
                query = query.Where(element =>
                    element.label != null
                    && element.label.IndexOf(textFilter, StringComparison.OrdinalIgnoreCase) >= 0);
            }

            if (args.limit > 0)
            {
                query = query.Take(args.limit);
            }

            return query.ToArray();
        }

        public static bool ShouldIncludeWorldOnScreenField(IReadOnlyList<QaWorldElement> elements, QaWorldDumpArgs args)
        {
            if (elements == null)
            {
                throw new ArgumentNullException(nameof(elements));
            }

            if (args == null)
            {
                throw new ArgumentNullException(nameof(args));
            }

            return args.includeOffscreen;
        }

        public static bool ShouldIncludeWorldHasActionField(IReadOnlyList<QaWorldElement> elements)
        {
            if (elements == null)
            {
                throw new ArgumentNullException(nameof(elements));
            }

            return elements.Any(element => !element.hasAction);
        }
    }

    public static class TestResultProjectionUtility
    {
        public static TestRunResultPayload ApplyFailuresOnly(TestRunResultPayload payload, bool failuresOnly)
        {
            if (payload == null)
            {
                throw new ArgumentNullException(nameof(payload));
            }

            if (!failuresOnly)
            {
                return payload;
            }

            return new TestRunResultPayload
            {
                runId = payload.runId,
                mode = payload.mode,
                status = payload.status,
                startedAt = payload.startedAt,
                durationMs = payload.durationMs,
                summary = payload.summary,
                tests = (payload.tests ?? Array.Empty<TestResultEntry>())
                    .Where(entry => !IsPassed(entry))
                    .ToArray(),
                warnings = payload.warnings,
            };
        }

        public static bool ShouldIncludeTestListDetail(TestListArgs args)
        {
            if (args == null)
            {
                throw new ArgumentNullException(nameof(args));
            }

            return !args.noDetail;
        }

        private static bool IsPassed(TestResultEntry entry)
        {
            return string.Equals(entry.outcome, "Passed", StringComparison.OrdinalIgnoreCase);
        }
    }
}
