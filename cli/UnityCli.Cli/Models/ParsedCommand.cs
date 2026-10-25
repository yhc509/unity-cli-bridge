using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using UnityCli.Protocol;

namespace UnityCli.Cli.Models;

public enum CommandKind
{
    Help,
    Version,
    Status,
    Compile,
    Refresh,
    ReadConsole,
    Play,
    Pause,
    Stop,
    ExecuteMenu,
    AssetFind,
    AssetTypes,
    AssetInfo,
    AssetReimport,
    AssetMkdir,
    AssetMove,
    AssetRename,
    AssetDelete,
    AssetCreate,
    SceneOpen,
    SceneInspect,
    ScenePatch,
    SceneAddObject,
    SceneSetTransform,
    SceneAddComponent,
    SceneRemoveComponent,
    SceneListComponents,
    SceneAssignMaterial,
    PrefabInspect,
    PrefabCreate,
    PrefabPatch,
    PrefabAddComponent,
    PrefabRemoveComponent,
    PrefabListComponents,
    TestList,
    TestRun,
    TestResults,
    TestCancel,
    InstancesList,
    InstancesUse,
    Doctor,
    Raw,
    Screenshot,
    PackageList,
    PackageAdd,
    PackageRemove,
    PackageSearch,
    ExecuteCode,
    Custom,
    MaterialInfo,
    MaterialSet,
    QaClick,
    QaTap,
    QaSwipe,
    QaKey,
    QaUiDump,
    QaWorldDump,
    QaWait,
    QaWaitUntil,
    QaRunSequence,
    RecordStart,
    RecordStop,
    RecordStatus,
    ProfileStats,
    ProfileCaptureStart,
    ProfileCaptureStop,
    ProfileStatus,
    ProfileAnalyze,
    ProfileCompare,
    ProfileMemory,
    ProfileMemoryCompare,
    ProfileMemoryTop,
    ProfileMemorySnapshot,
    EditorLaunch,
    EditorStop,
}

public sealed class ParsedCommand
{
    public const string DefaultScreenshotView = "game";

    public ParsedCommand(CommandKind kind)
    {
        Kind = kind;
    }

    public CommandKind Kind { get; }
    public bool JsonOutput => OutputMode == OutputMode.Json;
    public OutputMode OutputMode { get; set; }
    public string? ProjectOverride { get; set; }
    public int TimeoutMs { get; set; } = ProtocolConstants.DefaultLiveTimeoutMs;
    public bool TimeoutMsSpecified { get; set; }
    public int ConsoleLimit { get; set; } = ProtocolConstants.DefaultConsoleLimit;
    public string? ConsoleType { get; set; }
    public bool ConsoleNoStackTrace { get; set; }
    public string? MenuPath { get; set; }
    public bool MenuList { get; set; }
    public string? MenuListPrefix { get; set; }
    public string? InstanceTarget { get; set; }
    public bool InstancesBrief { get; set; }
    public string? RawJson { get; set; }
    public string? ScreenshotView { get; set; }
    public string? ScreenshotCamera { get; set; }
    public string? ScreenshotPath { get; set; }
    public int? ScreenshotWidth { get; set; }
    public int? ScreenshotHeight { get; set; }
    public string? ScreenshotFormat { get; set; }
    public int? ScreenshotQuality { get; set; }
    public int? ScreenshotMaxWidth { get; set; }
    public string? PackageName { get; set; }
    public string? PackageVersion { get; set; }
    public string? PackageQuery { get; set; }
    public string? PackageFilter { get; set; }
    public int PackageLimit { get; set; } = ProtocolConstants.DefaultPackageListLimit;
    public string? TestMode { get; set; }
    public string? TestFilter { get; set; }
    public string? TestCategory { get; set; }
    public string? TestAssembly { get; set; }
    public bool TestNoDetail { get; set; }
    public bool TestNoDomainReload { get; set; }
    public bool TestFailuresOnly { get; set; }
    public int? TestTimeoutSeconds { get; set; }
    public bool TestWait { get; set; }
    public bool Wait { get; set; }
    /// <summary>Command path for `<command...> --help` (e.g. "qa key"); null for global help.</summary>
    public string? HelpTopic { get; set; }
    public string? TestRunId { get; set; }
    public string? RecordPath { get; set; }
    public int? RecordDuration { get; set; }
    public bool RecordWait { get; set; }
    public int? RecordFps { get; set; }
    public int? RecordMaxWidth { get; set; }
    public string? RecordRunId { get; set; }
    public int? ProfileFrames { get; set; }
    public string? ProfilePreset { get; set; }
    public int? ProfileDuration { get; set; }
    public double? ProfileBudgetMs { get; set; }
    public bool ProfileWait { get; set; }
    public string? ProfileCaptureId { get; set; }
    public string? ProfileAnalyzeMarker { get; set; }
    public int? ProfileAnalyzeFrame { get; set; }
    public bool ProfileAnalyzeGc { get; set; }
    public bool ProfileAnalyzeSpikes { get; set; }
    public int? ProfileLimit { get; set; }
    public string? ProfileCompareBaseId { get; set; }
    public string? ProfileCompareHeadId { get; set; }
    public double? ProfileThresholdPercent { get; set; }
    public string? ProfileMemoryTopType { get; set; }
    public string? ProfileMemoryTopScope { get; set; }
    public bool QaSequenceProfile { get; set; }
    public string? ExecuteCodeSnippet { get; set; }
    public string? ExecuteCodeFile { get; set; }
    public string? ExecuteCodeArgsJson { get; set; }
    public int? ExecuteCodeTimeoutSeconds { get; set; }
    public string? CustomCommandName { get; set; }
    public string? CustomArgsJson { get; set; }
    public string? MaterialPath { get; set; }
    public string? MaterialProperty { get; set; }
    public string? MaterialValue { get; set; }
    public string? MaterialTexture { get; set; }
    public string? MaterialTextureAsset { get; set; }
    public string? AssetName { get; set; }
    public string? AssetType { get; set; }
    public string? AssetFolder { get; set; }
    public int AssetLimit { get; set; } = ProtocolConstants.DefaultAssetFindLimit;
    public string? AssetPath { get; set; }
    public string? AssetGuid { get; set; }
    public string? AssetFrom { get; set; }
    public string? AssetTo { get; set; }
    public string? AssetNewName { get; set; }
    public string? AssetCreateType { get; set; }
    public string? AssetShader { get; set; }
    public string? AssetScript { get; set; }
    public string? AssetTypeName { get; set; }
    public string? AssetDataJson { get; set; }
    public bool AssetLegacy { get; set; }
    public string? AssetInitialMap { get; set; }
    public string? AssetRootName { get; set; }
    public string? AssetBaseController { get; set; }
    public int? AssetWidth { get; set; }
    public int? AssetHeight { get; set; }
    public int? AssetDepth { get; set; }
    public Dictionary<string, object?> AssetCustomOptions { get; } = new(StringComparer.OrdinalIgnoreCase);
    public string? ScenePath { get; set; }
    public string? SceneSpecFile { get; set; }
    public string? SceneSpecJson { get; set; }
    public bool SceneWithValues { get; set; }
    public int? MaxDepth { get; set; }
    public bool OmitDefaults { get; set; }
    public string? SceneTarget { get; set; }
    public string? SceneParent { get; set; }
    public string? SceneObjectName { get; set; }
    public string? ScenePrimitive { get; set; }
    public string? SceneComponents { get; set; }
    public string? SceneComponentType { get; set; }
    public int? SceneComponentIndex { get; set; }
    public string? SceneComponentValues { get; set; }
    public string? ScenePosition { get; set; }
    public string? SceneRotation { get; set; }
    public string? SceneScale { get; set; }
    public string? PrefabPath { get; set; }
    public string? PrefabSpecFile { get; set; }
    public string? PrefabSpecJson { get; set; }
    public bool PrefabWithValues { get; set; }
    public bool Force { get; set; }
    public string? QaId { get; set; }
    public string? QaTarget { get; set; }
    public string QaButton { get; set; } = "left";
    public int? QaTapX { get; set; }
    public int? QaTapY { get; set; }
    public int? QaScreenshotWidth { get; set; }
    public int? QaScreenshotHeight { get; set; }
    public int QaDumpLimit { get; set; }
    public bool QaDumpInteractableOnly { get; set; }
    public string? QaDumpText { get; set; }
    public bool QaDumpOmitRect { get; set; }
    public bool QaWorldDumpIncludeOffscreen { get; set; }
    public string? QaSwipeFrom { get; set; }
    public string? QaSwipeTo { get; set; }
    public int QaSwipeDuration { get; set; } = ProtocolConstants.DefaultQaSwipeDurationMs;
    public string? QaKeyName { get; set; }
    public int QaWaitMs { get; set; }
    public string? QaWaitScene { get; set; }
    public string? QaWaitLogContains { get; set; }
    public string? QaWaitObjectExists { get; set; }
    public string? QaWaitObjectInteractable { get; set; }
    public string? QaWaitObjectGone { get; set; }
    public int QaWaitTimeout { get; set; } = ProtocolConstants.DefaultQaWaitUntilTimeoutMs;
    public QaRunSequenceArgs? QaSequenceArgs { get; set; }
    public int QaSequenceTimeoutMs { get; set; }
    public bool QaSequenceRecord { get; set; }
    public string? QaSequenceRecordPath { get; set; }
    public bool EditorGui { get; set; }
    public bool EditorNoGraphics { get; set; }
    public string? EditorPathOverride { get; set; }
    public bool EditorNoWait { get; set; }
    public int? EditorWaitTimeoutSeconds { get; set; }

    public CommandEnvelope ToEnvelope()
    {
        if (Kind == CommandKind.Raw)
        {
            try
            {
                var raw = JsonDocument.Parse(RawJson ?? throw new CliUsageException("A raw command requires a `--json` payload."));
                var root = raw.RootElement;

                // TryGetProperty/GetString throw InvalidOperationException on the wrong ValueKind,
                // which escapes the JsonException catch below and surfaces as CLI_ERROR.
                if (root.ValueKind != JsonValueKind.Object)
                {
                    throw new CliUsageException("The raw payload must be a JSON object.");
                }

                if (!root.TryGetProperty("command", out var commandElement))
                {
                    throw new CliUsageException("The raw payload requires a `command` field.");
                }

                if (commandElement.ValueKind != JsonValueKind.String
                    || string.IsNullOrWhiteSpace(commandElement.GetString()))
                {
                    throw new CliUsageException("The raw payload `command` must be a non-empty string.");
                }

                var rawRequest = new CommandEnvelope
                {
                    requestId = Guid.NewGuid().ToString("N"),
                    command = commandElement.GetString() ?? string.Empty,
                    argumentsJson = BuildRawArgumentsJson(root),
                };

                return rawRequest;
            }
            catch (JsonException exception)
            {
                throw new CliUsageException("The raw payload must be valid JSON. " + exception.Message);
            }
        }

        var request = new CommandEnvelope
        {
            requestId = Guid.NewGuid().ToString("N"),
            command = Kind switch
            {
                CommandKind.Status => "status",
                CommandKind.Compile => "compile",
                CommandKind.Refresh => "refresh",
                CommandKind.ReadConsole => "read-console",
                CommandKind.Play => "play",
                CommandKind.Pause => "pause",
                CommandKind.Stop => "stop",
                CommandKind.ExecuteMenu => "execute-menu",
                CommandKind.Screenshot => ProtocolConstants.CommandScreenshot,
                CommandKind.PackageList => ProtocolConstants.CommandPackageList,
                CommandKind.PackageAdd => ProtocolConstants.CommandPackageAdd,
                CommandKind.PackageRemove => ProtocolConstants.CommandPackageRemove,
                CommandKind.PackageSearch => ProtocolConstants.CommandPackageSearch,
                CommandKind.ExecuteCode => ProtocolConstants.CommandExecuteCode,
                CommandKind.Custom => ProtocolConstants.CommandCustom,
                CommandKind.MaterialInfo => ProtocolConstants.CommandMaterialInfo,
                CommandKind.MaterialSet => ProtocolConstants.CommandMaterialSet,
                CommandKind.QaClick => ProtocolConstants.CommandQaClick,
                CommandKind.QaTap => ProtocolConstants.CommandQaTap,
                CommandKind.QaSwipe => ProtocolConstants.CommandQaSwipe,
                CommandKind.QaKey => ProtocolConstants.CommandQaKey,
                CommandKind.QaUiDump => ProtocolConstants.CommandQaUiDump,
                CommandKind.QaWorldDump => ProtocolConstants.CommandQaWorldDump,
                CommandKind.QaWaitUntil => ProtocolConstants.CommandQaWaitUntil,
                CommandKind.QaRunSequence => ProtocolConstants.CommandQaRunSequence,
                CommandKind.AssetFind => ProtocolConstants.CommandAssetFind,
                CommandKind.AssetTypes => ProtocolConstants.CommandAssetTypes,
                CommandKind.AssetInfo => ProtocolConstants.CommandAssetInfo,
                CommandKind.AssetReimport => ProtocolConstants.CommandAssetReimport,
                CommandKind.AssetMkdir => ProtocolConstants.CommandAssetMkdir,
                CommandKind.AssetMove => ProtocolConstants.CommandAssetMove,
                CommandKind.AssetRename => ProtocolConstants.CommandAssetRename,
                CommandKind.AssetDelete => ProtocolConstants.CommandAssetDelete,
                CommandKind.AssetCreate => ProtocolConstants.CommandAssetCreate,
                CommandKind.SceneOpen => ProtocolConstants.CommandSceneOpen,
                CommandKind.SceneInspect => ProtocolConstants.CommandSceneInspect,
                CommandKind.ScenePatch => ProtocolConstants.CommandScenePatch,
                CommandKind.SceneAddObject => ProtocolConstants.CommandScenePatch,
                CommandKind.SceneSetTransform => ProtocolConstants.CommandSceneSetTransform,
                CommandKind.SceneAddComponent => ProtocolConstants.CommandScenePatch,
                CommandKind.SceneRemoveComponent => ProtocolConstants.CommandScenePatch,
                CommandKind.SceneListComponents => ProtocolConstants.CommandSceneListComponents,
                CommandKind.SceneAssignMaterial => ProtocolConstants.CommandSceneAssignMaterial,
                CommandKind.PrefabInspect => ProtocolConstants.CommandPrefabInspect,
                CommandKind.PrefabCreate => ProtocolConstants.CommandPrefabCreate,
                CommandKind.PrefabPatch => ProtocolConstants.CommandPrefabPatch,
                CommandKind.PrefabAddComponent => ProtocolConstants.CommandPrefabPatch,
                CommandKind.PrefabRemoveComponent => ProtocolConstants.CommandPrefabPatch,
                CommandKind.PrefabListComponents => ProtocolConstants.CommandPrefabListComponents,
                CommandKind.TestList => ProtocolConstants.CommandTestList,
                CommandKind.TestRun => ProtocolConstants.CommandTestRun,
                CommandKind.TestResults => ProtocolConstants.CommandTestResults,
                CommandKind.TestCancel => ProtocolConstants.CommandTestCancel,
                CommandKind.RecordStart => ProtocolConstants.CommandRecordStart,
                CommandKind.RecordStop => ProtocolConstants.CommandRecordStop,
                CommandKind.RecordStatus => ProtocolConstants.CommandRecordStatus,
                CommandKind.ProfileStats => ProtocolConstants.CommandProfileStats,
                CommandKind.ProfileCaptureStart => ProtocolConstants.CommandProfileCaptureStart,
                CommandKind.ProfileCaptureStop => ProtocolConstants.CommandProfileCaptureStop,
                CommandKind.ProfileStatus => ProtocolConstants.CommandProfileStatus,
                CommandKind.ProfileMemory => ProtocolConstants.CommandProfileMemory,
                CommandKind.ProfileMemoryTop => ProtocolConstants.CommandProfileMemoryTop,
                CommandKind.ProfileMemorySnapshot => ProtocolConstants.CommandProfileMemorySnapshot,
                CommandKind.EditorStop => ProtocolConstants.CommandEditorQuit,
                _ => throw new CliUsageException($"Unsupported live command: {Kind}"),
            },
            argumentsJson = BuildArgumentsJson(),
        };

        return request;
    }

    private string BuildArgumentsJson()
    {
        object payload = Kind switch
        {
            CommandKind.ReadConsole => new
            {
                limit = ConsoleLimit,
                type = ConsoleType,
                noStackTrace = ConsoleNoStackTrace,
            },
            CommandKind.ExecuteMenu => new ExecuteMenuArgs
            {
                path = MenuPath ?? string.Empty,
                list = MenuList,
                prefix = MenuListPrefix,
            },
            CommandKind.Screenshot => new ScreenshotArgs
            {
                view = ResolveScreenshotView(),
                camera = ScreenshotCamera,
                outputPath = ScreenshotPath,
                width = ScreenshotWidth ?? 0,
                height = ScreenshotHeight ?? 0,
                format = ScreenshotFormat,
                quality = ScreenshotQuality ?? 0,
                maxWidth = ResolveScreenshotMaxWidth(),
            },
            CommandKind.PackageList => new PackageListArgs
            {
                filter = PackageFilter ?? string.Empty,
                limit = PackageLimit,
            },
            CommandKind.PackageAdd => new PackageAddArgs
            {
                name = PackageName ?? string.Empty,
                version = PackageVersion,
            },
            CommandKind.PackageRemove => new PackageRemoveArgs
            {
                name = PackageName ?? string.Empty,
                force = Force,
            },
            CommandKind.PackageSearch => new PackageSearchArgs
            {
                query = PackageQuery ?? string.Empty,
            },
            CommandKind.TestList => new TestListArgs
            {
                mode = TestMode ?? "all",
                noDetail = TestNoDetail,
            },
            CommandKind.TestRun => new TestRunArgs
            {
                mode = TestMode ?? string.Empty,
                filter = TestFilter ?? string.Empty,
                category = TestCategory ?? string.Empty,
                assembly = TestAssembly ?? string.Empty,
                noDomainReload = TestNoDomainReload,
                timeoutSeconds = TestTimeoutSeconds ?? 0,
                failuresOnly = TestFailuresOnly,
            },
            CommandKind.TestResults => new TestResultsArgs
            {
                runId = TestRunId ?? string.Empty,
                failuresOnly = TestFailuresOnly,
            },
            CommandKind.RecordStart => new RecordStartArgs
            {
                path = RecordPath,
                fps = RecordFps ?? 0,
                maxWidth = RecordMaxWidth ?? 0,
                durationSeconds = RecordDuration ?? 0,
            },
            CommandKind.RecordStatus => new RecordStatusArgs
            {
                recordingId = RecordRunId,
            },
            CommandKind.RecordStop => new { },
            CommandKind.ProfileStats => new ProfileStatsArgs
            {
                frames = ProfileFrames ?? 0,
                preset = ProfilePreset ?? string.Empty,
            },
            CommandKind.ProfileCaptureStart => new ProfileCaptureStartArgs
            {
                frames = ProfileFrames ?? 0,
                durationSeconds = ProfileDuration ?? 0,
                budgetMs = (float)(ProfileBudgetMs ?? 0),
            },
            CommandKind.ProfileCaptureStop => new { },
            CommandKind.ProfileStatus => new ProfileStatusArgs
            {
                captureId = ProfileCaptureId,
            },
            CommandKind.ProfileMemory => new ProfileMemoryArgs
            {
                frames = ProfileFrames ?? 0,
            },
            CommandKind.ProfileMemoryTop => new ProfileMemoryTopArgs
            {
                type = ProfileMemoryTopType ?? string.Empty,
                limit = ProfileLimit ?? 0,
                scope = ProfileMemoryTopScope ?? string.Empty,
            },
            CommandKind.ProfileMemorySnapshot => new ProfileMemorySnapshotArgs(),
            CommandKind.EditorStop => new EditorQuitArgs
            {
                force = Force,
            },
            CommandKind.ExecuteCode => new ExecuteCodeArgs
            {
                code = ResolveExecuteCode(),
                argumentsJson = ExecuteCodeArgsJson,
                force = Force,
                timeoutMs = ExecuteCodeTimeoutSeconds.HasValue ? ExecuteCodeTimeoutSeconds.Value * 1000 : 0,
            },
            CommandKind.Custom => new CustomCommandArgs
            {
                commandName = CustomCommandName ?? string.Empty,
                argumentsJson = CustomArgsJson ?? "{}",
            },
            CommandKind.MaterialInfo => new MaterialInfoArgs
            {
                path = MaterialPath ?? string.Empty,
                omitDefaults = OmitDefaults,
            },
            CommandKind.MaterialSet => new MaterialSetArgs
            {
                path = MaterialPath ?? string.Empty,
                property = MaterialProperty,
                value = MaterialValue,
                texture = MaterialTexture,
                textureAsset = MaterialTextureAsset,
            },
            CommandKind.QaClick => new QaClickArgs
            {
                qaId = QaId,
                target = QaTarget,
                button = QaButton,
            },
            CommandKind.QaTap => new QaTapArgs
            {
                x = QaTapX ?? 0,
                y = QaTapY ?? 0,
                screenshotWidth = QaScreenshotWidth ?? 0,
                screenshotHeight = QaScreenshotHeight ?? 0,
                target = QaTarget,
                button = QaButton,
            },
            CommandKind.QaUiDump => new QaUiDumpArgs
            {
                screenshotWidth = QaScreenshotWidth ?? 0,
                screenshotHeight = QaScreenshotHeight ?? 0,
                limit = QaDumpLimit,
                interactableOnly = QaDumpInteractableOnly,
                text = QaDumpText ?? string.Empty,
                omitRect = QaDumpOmitRect,
            },
            CommandKind.QaWorldDump => new QaWorldDumpArgs
            {
                screenshotWidth = QaScreenshotWidth ?? 0,
                screenshotHeight = QaScreenshotHeight ?? 0,
                includeOffscreen = QaWorldDumpIncludeOffscreen,
                limit = QaDumpLimit,
                text = QaDumpText ?? string.Empty,
            },
            CommandKind.QaSwipe => new QaSwipeArgs
            {
                target = QaTarget ?? string.Empty,
                fromX = ParseCoordinate(QaSwipeFrom, 0, "--from", usesTargetRelativeOffsets: !string.IsNullOrWhiteSpace(QaTarget)),
                fromY = ParseCoordinate(QaSwipeFrom, 1, "--from", usesTargetRelativeOffsets: !string.IsNullOrWhiteSpace(QaTarget)),
                toX = ParseCoordinate(QaSwipeTo, 0, "--to", usesTargetRelativeOffsets: !string.IsNullOrWhiteSpace(QaTarget)),
                toY = ParseCoordinate(QaSwipeTo, 1, "--to", usesTargetRelativeOffsets: !string.IsNullOrWhiteSpace(QaTarget)),
                durationMs = QaSwipeDuration,
                screenshotWidth = QaScreenshotWidth ?? 0,
                screenshotHeight = QaScreenshotHeight ?? 0,
                button = QaButton,
            },
            CommandKind.QaKey => new QaKeyArgs
            {
                key = QaKeyName ?? string.Empty,
            },
            CommandKind.QaWaitUntil => new QaWaitUntilArgs
            {
                scene = QaWaitScene,
                logContains = QaWaitLogContains,
                objectExists = QaWaitObjectExists,
                objectInteractable = QaWaitObjectInteractable,
                objectGone = QaWaitObjectGone,
                timeoutMs = QaWaitTimeout,
            },
            CommandKind.QaRunSequence => BuildQaRunSequenceArgs(),
            CommandKind.AssetFind => new AssetFindArgs
            {
                name = AssetName ?? string.Empty,
                type = AssetType,
                folder = AssetFolder,
                limit = AssetLimit,
            },
            CommandKind.AssetTypes => new { },
            CommandKind.AssetInfo => new AssetInfoArgs
            {
                path = AssetPath,
                guid = AssetGuid,
            },
            CommandKind.AssetReimport => new AssetPathArgs
            {
                path = AssetPath ?? string.Empty,
            },
            CommandKind.AssetMkdir => new AssetPathArgs
            {
                path = AssetPath ?? string.Empty,
            },
            CommandKind.AssetMove => new AssetMoveArgs
            {
                from = AssetFrom ?? string.Empty,
                to = AssetTo ?? string.Empty,
                force = Force,
            },
            CommandKind.AssetRename => new AssetRenameArgs
            {
                path = AssetPath ?? string.Empty,
                name = AssetNewName ?? string.Empty,
                force = Force,
            },
            CommandKind.AssetDelete => new AssetPathArgs
            {
                path = AssetPath ?? string.Empty,
                force = Force,
            },
            CommandKind.AssetCreate => new AssetCreateArgs
            {
                type = AssetCreateType ?? string.Empty,
                path = AssetPath ?? string.Empty,
                force = Force,
                script = AssetScript,
                typeName = AssetTypeName,
                dataJson = AssetDataJson,
                optionsJson = BuildAssetCreateOptionsJson(),
            },
            CommandKind.SceneOpen => new SceneOpenArgs
            {
                path = ScenePath ?? string.Empty,
                force = Force,
            },
            CommandKind.SceneInspect => new SceneInspectArgs
            {
                path = ScenePath ?? string.Empty,
                node = SceneTarget ?? string.Empty,
                withValues = SceneWithValues,
                maxDepth = MaxDepth,
                omitDefaults = OmitDefaults,
            },
            CommandKind.ScenePatch => new ScenePatchArgs
            {
                path = ScenePath ?? string.Empty,
                force = Force,
                specJson = ResolveSceneSpecJson(),
            },
            CommandKind.SceneAddObject => BuildSceneAddObjectArgs(),
            CommandKind.SceneSetTransform => BuildSceneSetTransformArgs(),
            CommandKind.SceneAddComponent => new ScenePatchArgs
            {
                path = ScenePath ?? string.Empty,
                force = Force,
                specJson = BuildAddComponentSpec(),
            },
            CommandKind.SceneRemoveComponent => new ScenePatchArgs
            {
                path = ScenePath ?? string.Empty,
                force = Force,
                specJson = BuildRemoveComponentSpec(),
            },
            CommandKind.SceneListComponents => new
            {
                node = SceneTarget ?? string.Empty,
            },
            CommandKind.SceneAssignMaterial => new SceneAssignMaterialArgs
            {
                node = SceneTarget ?? string.Empty,
                material = MaterialPath ?? string.Empty,
            },
            CommandKind.PrefabInspect => new PrefabInspectArgs
            {
                path = PrefabPath ?? string.Empty,
                node = SceneTarget ?? string.Empty,
                withValues = PrefabWithValues,
                maxDepth = MaxDepth,
                omitDefaults = OmitDefaults,
            },
            CommandKind.PrefabCreate => new PrefabCreateArgs
            {
                path = PrefabPath ?? string.Empty,
                force = Force,
                specJson = ResolvePrefabSpecJson(),
            },
            CommandKind.PrefabPatch => new PrefabPatchArgs
            {
                path = PrefabPath ?? string.Empty,
                force = Force,
                specJson = ResolvePrefabSpecJson(),
            },
            CommandKind.PrefabAddComponent => new PrefabPatchArgs
            {
                path = PrefabPath ?? string.Empty,
                force = Force,
                specJson = BuildAddComponentSpec(),
            },
            CommandKind.PrefabRemoveComponent => new PrefabPatchArgs
            {
                path = PrefabPath ?? string.Empty,
                force = Force,
                specJson = BuildRemoveComponentSpec(),
            },
            CommandKind.PrefabListComponents => new
            {
                path = PrefabPath ?? string.Empty,
                node = SceneTarget ?? string.Empty,
            },
            _ => new { },
        };

        return JsonSerializer.Serialize(payload, ProtocolJson.Default);
    }

    private QaRunSequenceArgs BuildQaRunSequenceArgs()
    {
        QaRunSequenceArgs args = QaSequenceArgs ?? new QaRunSequenceArgs();
        if (QaSequenceTimeoutMs > 0)
        {
            args.timeoutMs = QaSequenceTimeoutMs;
        }

        args.profile = QaSequenceProfile;
        args.record = QaSequenceRecord;
        args.recordPath = QaSequenceRecordPath;
        return args;
    }

    private string BuildRawArgumentsJson(JsonElement root)
    {
        if (!root.TryGetProperty("arguments", out JsonElement argumentsElement))
        {
            return Force ? "{\"force\":true}" : "{}";
        }

        if (argumentsElement.ValueKind is not (JsonValueKind.Object or JsonValueKind.Null or JsonValueKind.Undefined))
        {
            throw new CliUsageException("The raw payload `arguments` must be a JSON object.");
        }

        if (!Force)
        {
            return argumentsElement.GetRawText();
        }

        JsonObject arguments;
        if (argumentsElement.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            arguments = new JsonObject();
        }
        else
        {
            arguments = JsonNode.Parse(argumentsElement.GetRawText()) as JsonObject
                ?? throw new CliUsageException("raw `--force` requires `arguments` to be a JSON object.");
        }

        // Raw force is explicit: the flag may fill a missing value, but not contradict payload intent.
        if (arguments.TryGetPropertyValue("force", out JsonNode? forceNode))
        {
            if (forceNode is JsonValue forceValue
                && forceValue.TryGetValue<bool>(out bool payloadForce)
                && payloadForce)
            {
                arguments["force"] = true;
                return arguments.ToJsonString(ProtocolJson.Default);
            }

            throw new CliUsageException("force flag conflicts with raw payload");
        }

        arguments["force"] = true;
        return arguments.ToJsonString(ProtocolJson.Default);
    }

    private string? ResolveScreenshotView()
    {
        if (Kind != CommandKind.Screenshot || !string.IsNullOrWhiteSpace(ScreenshotCamera))
        {
            return null;
        }

        return string.IsNullOrWhiteSpace(ScreenshotView)
            ? DefaultScreenshotView
            : ScreenshotView;
    }

    /// <summary>
    /// Maps <c>--max-width</c> onto the wire, where 0 means "unspecified, apply the default cap".
    /// A caller who passed <c>--max-width 0</c> is asking for the opposite — no cap at all — so it
    /// travels as the uncapped sentinel rather than as a value the bridge would read as silence.
    /// </summary>
    private int ResolveScreenshotMaxWidth()
    {
        if (ScreenshotMaxWidth is null)
        {
            return 0;
        }

        return ScreenshotMaxWidth.Value > 0
            ? ScreenshotMaxWidth.Value
            : ScreenshotDefaults.MaxWidthUncapped;
    }

    private string? BuildAssetCreateOptionsJson()
    {
        var options = new Dictionary<string, object?>(AssetCustomOptions, StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(AssetShader))
        {
            options["shader"] = AssetShader;
        }

        if (AssetLegacy)
        {
            options["legacy"] = true;
        }

        if (!string.IsNullOrWhiteSpace(AssetInitialMap))
        {
            options["initialMap"] = AssetInitialMap;
        }

        if (!string.IsNullOrWhiteSpace(AssetRootName))
        {
            options["rootName"] = AssetRootName;
        }

        if (!string.IsNullOrWhiteSpace(AssetBaseController))
        {
            options["baseController"] = AssetBaseController;
        }

        if (AssetWidth.HasValue)
        {
            options["width"] = AssetWidth.Value;
        }

        if (AssetHeight.HasValue)
        {
            options["height"] = AssetHeight.Value;
        }

        if (AssetDepth.HasValue)
        {
            options["depth"] = AssetDepth.Value;
        }

        return options.Count == 0 ? null : JsonSerializer.Serialize(options, ProtocolJson.Default);
    }

    private string ResolveExecuteCode()
    {
        if (!string.IsNullOrWhiteSpace(ExecuteCodeFile))
        {
            string filePath = Path.GetFullPath(ExecuteCodeFile);
            if (!File.Exists(filePath))
            {
                throw new CliUsageException("Could not find the code file: " + filePath);
            }

            return File.ReadAllText(filePath);
        }

        return ExecuteCodeSnippet ?? string.Empty;
    }

    private static int ParseCoordinate(string? csv, int index, string option, bool usesTargetRelativeOffsets)
    {
        if (string.IsNullOrWhiteSpace(csv))
        {
            throw new CliUsageException($"{option} must be {GetQaSwipeCoordinateDescription(usesTargetRelativeOffsets)}.");
        }

        var parts = csv.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length != 2 || index >= parts.Length || !int.TryParse(parts[index], out var value))
        {
            throw new CliUsageException($"{option} must be {GetQaSwipeCoordinateDescription(usesTargetRelativeOffsets)}.");
        }

        return value;
    }

    private static string GetQaSwipeCoordinateDescription(bool usesTargetRelativeOffsets)
    {
        return usesTargetRelativeOffsets
            ? "`x,y` pixel offset from the target center"
            : "`x,y` absolute screen pixel coordinates";
    }

    public string ResolvePrefabSpecJson()
    {
        return ResolveSpecJson(PrefabSpecJson, PrefabSpecFile, "prefab");
    }

    public string ResolveSceneSpecJson()
    {
        return ResolveSpecJson(SceneSpecJson, SceneSpecFile, "scene");
    }

    private ScenePatchArgs BuildSceneAddObjectArgs()
    {
        SceneVector3Value? position = string.IsNullOrWhiteSpace(ScenePosition)
            ? null
            : ParseVector3(ScenePosition!, "--position");
        string? primitive = string.IsNullOrWhiteSpace(ScenePrimitive)
            ? null
            : RequireSupportedScenePrimitive(ScenePrimitive);

        return new ScenePatchArgs
        {
            path = ScenePath ?? string.Empty,
            force = Force,
            parent = SceneParent ?? "/",
            primitive = primitive,
            position = position,
            specJson = BuildAddObjectSpec(position, primitive),
        };
    }

    private string BuildAddObjectSpec(SceneVector3Value? position, string? primitive)
    {
        var node = new Dictionary<string, object?>
        {
            ["name"] = SceneObjectName ?? "GameObject",
        };

        if (!string.IsNullOrWhiteSpace(primitive))
        {
            node["primitive"] = primitive;
        }

        if (position != null)
        {
            node["transform"] = new Dictionary<string, object?>
            {
                ["localPosition"] = position,
            };
        }

        if (!string.IsNullOrWhiteSpace(SceneComponents))
        {
            string[] components = SceneComponents.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var componentList = new List<object>();
            foreach (string component in components)
            {
                componentList.Add(new Dictionary<string, object?>
                {
                    ["type"] = component,
                });
            }

            node["components"] = componentList;
        }

        var op = new Dictionary<string, object?>
        {
            ["op"] = "add-gameobject",
            ["parent"] = SceneParent ?? "/",
            ["node"] = node,
        };

        var spec = new Dictionary<string, object?>
        {
            ["version"] = 1,
            ["operations"] = new[] { op },
        };

        return JsonSerializer.Serialize(spec, ProtocolJson.Default);
    }

    private SceneSetTransformArgs BuildSceneSetTransformArgs()
    {
        return new SceneSetTransformArgs
        {
            node = SceneTarget ?? string.Empty,
            position = string.IsNullOrWhiteSpace(ScenePosition)
                ? null
                : ParseVector3(ScenePosition!, "--position"),
            rotation = string.IsNullOrWhiteSpace(SceneRotation)
                ? null
                : ParseVector3(SceneRotation!, "--rotation"),
            scale = string.IsNullOrWhiteSpace(SceneScale)
                ? null
                : ParseVector3(SceneScale!, "--scale"),
        };
    }

    private string BuildAddComponentSpec()
    {
        var op = new Dictionary<string, object?>
        {
            ["op"] = "add-component",
            ["target"] = SceneTarget ?? string.Empty,
            ["component"] = BuildComponentSpec(),
        };

        var spec = new Dictionary<string, object?>
        {
            ["version"] = 1,
            ["operations"] = new[] { op },
        };

        return JsonSerializer.Serialize(spec, ProtocolJson.Default);
    }

    private string BuildRemoveComponentSpec()
    {
        var op = new Dictionary<string, object?>
        {
            ["op"] = "remove-component",
            ["target"] = SceneTarget ?? string.Empty,
            ["componentType"] = SceneComponentType ?? string.Empty,
        };
        if (SceneComponentIndex.HasValue)
        {
            op["componentIndex"] = SceneComponentIndex.Value;
        }

        var spec = new Dictionary<string, object?>
        {
            ["version"] = 1,
            ["operations"] = new[] { op },
        };

        return JsonSerializer.Serialize(spec, ProtocolJson.Default);
    }

    private Dictionary<string, object?> BuildComponentSpec()
    {
        var component = new Dictionary<string, object?>
        {
            ["type"] = SceneComponentType ?? string.Empty,
        };

        if (!string.IsNullOrWhiteSpace(SceneComponentValues))
        {
            component["values"] = ParseSceneComponentValues();
        }

        return component;
    }

    private object? ParseSceneComponentValues()
    {
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, object?>>(SceneComponentValues!, ProtocolJson.Default);
        }
        catch (JsonException exception)
        {
            throw new CliUsageException("`--values` must be a JSON object. " + exception.Message);
        }
    }

    private static string RequireSupportedScenePrimitive(string primitive)
    {
        string normalized = ProtocolConstants.NormalizeScenePrimitive(primitive);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new CliUsageException("`--primitive` must be one of `" + string.Join("`, `", ProtocolConstants.SupportedScenePrimitiveNames) + "`.");
        }

        return normalized;
    }

    private static SceneVector3Value ParseVector3(string csv, string option)
    {
        string[] parts = csv.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length != 3
            || !float.TryParse(parts[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float x)
            || !float.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float y)
            || !float.TryParse(parts[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float z))
        {
            throw new CliUsageException($"{option} must be `x,y,z`.");
        }

        return new SceneVector3Value
        {
            x = x,
            y = y,
            z = z,
        };
    }

    private static string ResolveSpecJson(string? inlineJson, string? specFile, string label)
    {
        string? specJson = inlineJson;
        if (!string.IsNullOrWhiteSpace(specFile))
        {
            string filePath = Path.GetFullPath(specFile);
            if (!File.Exists(filePath))
            {
                throw new CliUsageException("Could not find the spec file: " + filePath);
            }

            specJson = File.ReadAllText(filePath);
        }

        if (string.IsNullOrWhiteSpace(specJson))
        {
            throw new CliUsageException(label + " spec is empty.");
        }

        return specJson;
    }
}

public sealed class CliUsageException : Exception
{
    public CliUsageException(string message)
        : base(message)
    {
    }
}
