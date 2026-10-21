#nullable enable
using System.Linq;
using System.Text.Json;
using UnityCli.Cli.Models;
using UnityCli.Protocol;

namespace UnityCli.Cli.Services;

public static partial class CliArgumentParser
{
    private static string RequireConsoleType(string value)
    {
        return value.Trim().ToLowerInvariant() switch
        {
            "log" => "log",
            "warning" => "warning",
            "error" => "error",
            "exception" => "exception",
            "assert" => "assert",
            _ => throw new CliUsageException("`--type` must be `log`, `warning`, `error`, `exception`, or `assert`."),
        };
    }

    private static string RequireScreenshotView(string value)
    {
        return value.ToLowerInvariant() switch
        {
            "game" => "game",
            "scene" => "scene",
            _ => throw new CliUsageException("`--view` must be `game` or `scene`."),
        };
    }

    private static string RequireScreenshotFormat(string value)
    {
        return value.ToLowerInvariant() switch
        {
            "png" => "png",
            "jpg" => "jpg",
            "jpeg" => "jpeg",
            _ => throw new CliUsageException("`--format` must be `png`, `jpg`, or `jpeg`."),
        };
    }

    private static int RequireScreenshotQuality(string value)
    {
        int quality = RequireInt(value, "--quality");
        if (quality > 100)
        {
            throw new CliUsageException("--quality must be an integer from 1 to 100.");
        }

        return quality;
    }

    private static int RequireInt(string value, string option, int? minimumValue = 1)
    {
        if (!int.TryParse(value, out var result))
        {
            throw new CliUsageException($"{option} must be {DescribeIntegerRequirement(minimumValue)}.");
        }

        if (minimumValue.HasValue && result < minimumValue.Value)
        {
            throw new CliUsageException($"{option} must be {DescribeIntegerRequirement(minimumValue)}.");
        }

        return result;
    }

    private static string DescribeIntegerRequirement(int? minimumValue)
    {
        return minimumValue switch
        {
            null => "an integer",
            0 => "an integer >= 0",
            1 => "an integer >= 1",
            _ => "an integer >= " + minimumValue.Value,
        };
    }

    private static string RequireValue(Queue<string> tokens, string option)
    {
        if (tokens.Count == 0)
        {
            throw new CliUsageException($"{option} requires a value.");
        }

        return tokens.Dequeue();
    }

    private static string RequireJsonValue(Queue<string> tokens, string option)
    {
        if (tokens.Count == 0 || tokens.Peek().StartsWith("--", StringComparison.Ordinal))
        {
            throw new CliUsageException($"{option} requires a JSON value.");
        }

        string value = tokens.Dequeue();
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new CliUsageException($"{option} requires a JSON value.");
        }

        try
        {
            using var document = JsonDocument.Parse(value);
            return value;
        }
        catch (JsonException exception)
        {
            throw new CliUsageException($"{option} must be valid JSON. " + exception.Message);
        }
    }

    private static string RequireAssetPath(string value, string option, bool allowPackages = false)
    {
        try
        {
            return AssetPathUtility.Normalize(value, allowPackages);
        }
        catch (InvalidOperationException)
        {
            throw new CliUsageException(
                allowPackages
                    ? $"{option} must be an `Assets/...` or `Packages/...` path."
                    : $"{option} must be an `Assets/...` path.");
        }
    }

    private static string RequireAssetCreateType(string value)
    {
        string normalized = BuiltInAssetCreateCatalog.NormalizeTypeId(value);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new CliUsageException("`asset create --type` requires a value.");
        }

        return normalized;
    }

    private static string RequireScenePrimitive(string value)
    {
        string normalized = ProtocolConstants.NormalizeScenePrimitive(value);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new CliUsageException("`--primitive` must be one of `" + string.Join("`, `", ProtocolConstants.SupportedScenePrimitiveNames) + "`.");
        }

        return normalized;
    }

    private static string RequireTestMode(string value, bool allowAll)
    {
        string normalized = value.ToLowerInvariant();
        if (normalized is "edit" or "play" || (allowAll && normalized == "all"))
        {
            return normalized;
        }

        string allowed = allowAll ? "edit, play, all" : "edit, play";
        throw new CliUsageException($"--mode must be one of {allowed}.");
    }

    private static string RequireQaButton(string value)
    {
        string normalized = value.ToLowerInvariant();
        if (normalized is "left" or "right")
        {
            return normalized;
        }

        throw new CliUsageException("`--button` must be `left` or `right`.");
    }

    private static string RequireSubcommand(Queue<string> tokens, string command)
    {
        if (tokens.Count == 0)
        {
            throw new CliUsageException($"`{command}` requires a subcommand.");
        }

        return tokens.Dequeue().ToLowerInvariant();
    }

    private static void ValidateAssetOptions(ParsedCommand parsed)
    {
        switch (parsed.Kind)
        {
            case CommandKind.AssetFind
                when string.IsNullOrWhiteSpace(parsed.AssetName)
                && string.IsNullOrWhiteSpace(parsed.AssetType):
                throw new CliUsageException("`asset find` requires `--name` or `--type` (at least one).");
            case CommandKind.AssetInfo:
            {
                var hasPath = !string.IsNullOrWhiteSpace(parsed.AssetPath);
                var hasGuid = !string.IsNullOrWhiteSpace(parsed.AssetGuid);
                if (hasPath == hasGuid)
                {
                    throw new CliUsageException("`asset info` requires exactly one of `--path` or `--guid`.");
                }

                break;
            }
            case CommandKind.AssetReimport when string.IsNullOrWhiteSpace(parsed.AssetPath):
                throw new CliUsageException("`asset reimport` requires `--path`.");
            case CommandKind.AssetMkdir when string.IsNullOrWhiteSpace(parsed.AssetPath):
                throw new CliUsageException("`asset mkdir` requires `--path`.");
            case CommandKind.AssetMove when string.IsNullOrWhiteSpace(parsed.AssetFrom) || string.IsNullOrWhiteSpace(parsed.AssetTo):
                throw new CliUsageException("`asset move` requires both `--from` and `--to`.");
            case CommandKind.AssetRename when string.IsNullOrWhiteSpace(parsed.AssetPath) || string.IsNullOrWhiteSpace(parsed.AssetNewName):
                throw new CliUsageException("`asset rename` requires both `--path` and `--name`.");
            case CommandKind.AssetDelete when string.IsNullOrWhiteSpace(parsed.AssetPath):
                throw new CliUsageException("`asset delete` requires `--path`.");
            case CommandKind.AssetDelete when ForceRequiredByCatalog(parsed):
                throw new CliUsageException("`asset delete` requires `--force`.");
            case CommandKind.AssetCreate when string.IsNullOrWhiteSpace(parsed.AssetCreateType) || string.IsNullOrWhiteSpace(parsed.AssetPath):
                throw new CliUsageException("`asset create` requires `--type` and `--path`.");
            case CommandKind.AssetCreate when parsed.AssetCreateType == "scriptable-object"
                && string.IsNullOrWhiteSpace(parsed.AssetScript)
                && string.IsNullOrWhiteSpace(parsed.AssetTypeName):
                throw new CliUsageException("`asset create --type scriptable-object` requires `--script` or `--type-name`.");
            case CommandKind.AssetCreate when parsed.AssetCreateType == "animator-override-controller"
                && string.IsNullOrWhiteSpace(parsed.AssetBaseController):
                throw new CliUsageException("`asset create --type animator-override-controller` requires `--base-controller`.");
            case CommandKind.AssetCreate when IsBuiltInAssetCreateType(parsed.AssetCreateType)
                && parsed.AssetCustomOptions.Count > 0:
                throw new CliUsageException(
                    "`asset create --type " + parsed.AssetCreateType + "` does not support: "
                    + string.Join(", ", parsed.AssetCustomOptions.Keys.Select(key => "--" + ToKebabCase(key))));
            case CommandKind.PrefabInspect when string.IsNullOrWhiteSpace(parsed.PrefabPath):
                throw new CliUsageException("`prefab inspect` requires `--path`.");
            case CommandKind.PrefabCreate when string.IsNullOrWhiteSpace(parsed.PrefabPath):
                throw new CliUsageException("`prefab create` requires `--path`.");
            case CommandKind.PrefabPatch when string.IsNullOrWhiteSpace(parsed.PrefabPath):
                throw new CliUsageException("`prefab patch` requires `--path`.");
            case CommandKind.PrefabAddComponent when string.IsNullOrWhiteSpace(parsed.PrefabPath):
                throw new CliUsageException("`prefab add-component` requires `--path`.");
            case CommandKind.PrefabAddComponent when string.IsNullOrWhiteSpace(parsed.SceneTarget):
                throw new CliUsageException("`prefab add-component` requires `--node`.");
            case CommandKind.PrefabAddComponent when string.IsNullOrWhiteSpace(parsed.SceneComponentType):
                throw new CliUsageException("`prefab add-component` requires `--type`.");
            case CommandKind.PrefabRemoveComponent when string.IsNullOrWhiteSpace(parsed.PrefabPath):
                throw new CliUsageException("`prefab remove-component` requires `--path`.");
            case CommandKind.PrefabRemoveComponent when string.IsNullOrWhiteSpace(parsed.SceneTarget):
                throw new CliUsageException("`prefab remove-component` requires `--node`.");
            case CommandKind.PrefabRemoveComponent when string.IsNullOrWhiteSpace(parsed.SceneComponentType):
                throw new CliUsageException("`prefab remove-component` requires `--type`.");
            case CommandKind.PrefabRemoveComponent when ForceRequiredByCatalog(parsed):
                throw new CliUsageException("`prefab remove-component` requires `--force`.");
            case CommandKind.PrefabListComponents when string.IsNullOrWhiteSpace(parsed.PrefabPath):
                throw new CliUsageException("`prefab list-components` requires `--path`.");
            case CommandKind.PrefabListComponents when string.IsNullOrWhiteSpace(parsed.SceneTarget):
                throw new CliUsageException("`prefab list-components` requires `--node`.");
            case CommandKind.PrefabCreate when HasInvalidPrefabSpecSource(parsed):
                throw new CliUsageException("`prefab create` requires exactly one of `--spec-file` or `--spec-json`.");
            case CommandKind.PrefabPatch when HasInvalidPrefabSpecSource(parsed):
                throw new CliUsageException("`prefab patch` requires exactly one of `--spec-file` or `--spec-json`.");
            case CommandKind.PrefabPatch when ForceRequiredByCatalog(parsed):
                throw new CliUsageException("`prefab patch` requires `--force` for a destructive operation.");
        }
    }

    private static void ValidateSceneOptions(ParsedCommand parsed)
    {
        switch (parsed.Kind)
        {
            case CommandKind.SceneOpen when string.IsNullOrWhiteSpace(parsed.ScenePath):
                throw new CliUsageException("`scene open` requires `--path`.");
            case CommandKind.SceneInspect when string.IsNullOrWhiteSpace(parsed.ScenePath):
                throw new CliUsageException("`scene inspect` requires `--path`.");
            case CommandKind.ScenePatch when string.IsNullOrWhiteSpace(parsed.ScenePath):
                throw new CliUsageException("`scene patch` requires `--path`.");
            case CommandKind.ScenePatch when HasInvalidSceneSpecSource(parsed):
                throw new CliUsageException("`scene patch` requires exactly one of `--spec-file` or `--spec-json`.");
            case CommandKind.ScenePatch when ForceRequiredByCatalog(parsed):
                throw new CliUsageException("`scene patch` requires `--force` to use `delete-gameobject` or `remove-component`.");
            case CommandKind.SceneAddObject when string.IsNullOrWhiteSpace(parsed.ScenePath):
                throw new CliUsageException("`scene add-object` requires `--path`.");
            case CommandKind.SceneAddObject when string.IsNullOrWhiteSpace(parsed.SceneObjectName):
                throw new CliUsageException("`scene add-object` requires `--name`.");
            case CommandKind.SceneSetTransform when string.IsNullOrWhiteSpace(parsed.SceneTarget):
                throw new CliUsageException("`scene set-transform` requires `--node`.");
            case CommandKind.SceneSetTransform
                when string.IsNullOrWhiteSpace(parsed.ScenePosition)
                && string.IsNullOrWhiteSpace(parsed.SceneRotation)
                && string.IsNullOrWhiteSpace(parsed.SceneScale):
                throw new CliUsageException("`scene set-transform` requires at least one of `--position`, `--rotation`, or `--scale`.");
            case CommandKind.SceneAddComponent when string.IsNullOrWhiteSpace(parsed.ScenePath):
                throw new CliUsageException("`scene add-component` requires `--path`.");
            case CommandKind.SceneAddComponent when string.IsNullOrWhiteSpace(parsed.SceneTarget):
                throw new CliUsageException("`scene add-component` requires `--node`.");
            case CommandKind.SceneAddComponent when string.IsNullOrWhiteSpace(parsed.SceneComponentType):
                throw new CliUsageException("`scene add-component` requires `--type`.");
            case CommandKind.SceneRemoveComponent when string.IsNullOrWhiteSpace(parsed.ScenePath):
                throw new CliUsageException("`scene remove-component` requires `--path`.");
            case CommandKind.SceneRemoveComponent when string.IsNullOrWhiteSpace(parsed.SceneTarget):
                throw new CliUsageException("`scene remove-component` requires `--node`.");
            case CommandKind.SceneRemoveComponent when string.IsNullOrWhiteSpace(parsed.SceneComponentType):
                throw new CliUsageException("`scene remove-component` requires `--type`.");
            case CommandKind.SceneRemoveComponent when ForceRequiredByCatalog(parsed):
                throw new CliUsageException("`scene remove-component` requires `--force`.");
            case CommandKind.SceneListComponents when string.IsNullOrWhiteSpace(parsed.SceneTarget):
                throw new CliUsageException("`scene list-components` requires `--node`.");
            case CommandKind.SceneAssignMaterial when string.IsNullOrWhiteSpace(parsed.SceneTarget):
                throw new CliUsageException("`scene assign-material` requires `--node`.");
            case CommandKind.SceneAssignMaterial when string.IsNullOrWhiteSpace(parsed.MaterialPath):
                throw new CliUsageException("`scene assign-material` requires `--material`.");
        }
    }

    private static void ValidateScreenshotOptions(ParsedCommand parsed)
    {
        if (parsed.Kind != CommandKind.Screenshot)
        {
            return;
        }

        bool hasView = !string.IsNullOrWhiteSpace(parsed.ScreenshotView);
        bool hasCamera = !string.IsNullOrWhiteSpace(parsed.ScreenshotCamera);

        if (hasView && hasCamera)
        {
            throw new CliUsageException("`screenshot` requires exactly one of `--view` or `--camera`.");
        }

        if (!hasView && !hasCamera)
        {
            parsed.ScreenshotView = ParsedCommand.DefaultScreenshotView;
        }
    }

    private static void ValidateExecuteMenuOptions(ParsedCommand parsed)
    {
        if (parsed.Kind != CommandKind.ExecuteMenu)
        {
            return;
        }

        bool hasPath = !string.IsNullOrWhiteSpace(parsed.MenuPath);
        bool hasList = parsed.MenuList;
        if (hasPath == hasList)
        {
            throw new CliUsageException("`execute-menu` requires exactly one of `--path` or `--list <prefix>`.");
        }

        if (parsed.MenuList && string.IsNullOrWhiteSpace(parsed.MenuListPrefix))
        {
            throw new CliUsageException("`execute-menu --list` requires a prefix.");
        }
    }

    private static void ValidatePackageOptions(ParsedCommand parsed)
    {
        switch (parsed.Kind)
        {
            case CommandKind.PackageAdd when string.IsNullOrWhiteSpace(parsed.PackageName):
                throw new CliUsageException("`package add` requires `--name`.");
            case CommandKind.PackageRemove when string.IsNullOrWhiteSpace(parsed.PackageName):
                throw new CliUsageException("`package remove` requires `--name`.");
            case CommandKind.PackageRemove when ForceRequiredByCatalog(parsed):
                throw new CliUsageException("`package remove` requires `--force`.");
            case CommandKind.PackageSearch when string.IsNullOrWhiteSpace(parsed.PackageQuery):
                throw new CliUsageException("`package search` requires `--query`.");
        }
    }

    private static void ValidateTestOptions(ParsedCommand parsed)
    {
        switch (parsed.Kind)
        {
            case CommandKind.TestRun when string.IsNullOrWhiteSpace(parsed.TestMode):
                throw new CliUsageException("`test run` requires `--mode <edit|play>`.");
            case CommandKind.TestRun
                when string.Equals(parsed.TestMode, "edit", StringComparison.Ordinal)
                && parsed.TestNoDomainReload:
                throw new CliUsageException("--no-domain-reload is only valid with --mode play.");
            case CommandKind.TestRun when parsed.TestTimeoutSeconds > ProtocolConstants.MaxTestRunTimeoutSeconds:
                throw new CliUsageException($"--timeout cannot exceed {ProtocolConstants.MaxTestRunTimeoutSeconds} seconds.");
            case CommandKind.TestResults
                when !string.IsNullOrEmpty(parsed.TestRunId)
                && !ProtocolHelpers.IsValid32HexId(parsed.TestRunId):
                throw new CliUsageException("--run-id must be 32 hex characters (the runId returned by `test run`).");
        }
    }

    private static void ValidateRecordOptions(ParsedCommand parsed)
    {
        if (parsed.Kind == CommandKind.RecordStart && parsed.RecordWait && parsed.RecordDuration is null)
        {
            throw new CliUsageException("`record start --wait` requires `--duration <seconds>` (stop a manual recording with `record stop`).");
        }

        if (!string.IsNullOrEmpty(parsed.RecordRunId) && !ProtocolHelpers.IsValid32HexId(parsed.RecordRunId))
        {
            throw new CliUsageException("--recording-id must be 32 hex characters (the recordingId returned by `record start`).");
        }
    }

    private static void ValidateProfileOptions(ParsedCommand parsed)
    {
        if (parsed.Kind == CommandKind.ProfileCaptureStart && parsed.ProfileFrames.HasValue && parsed.ProfileDuration.HasValue)
        {
            throw new CliUsageException("profile capture start cannot take both --frames and --duration.");
        }

        if (parsed.Kind == CommandKind.ProfileAnalyze)
        {
            int queryCount = (string.IsNullOrEmpty(parsed.ProfileAnalyzeMarker) ? 0 : 1)
                + (parsed.ProfileAnalyzeFrame.HasValue ? 1 : 0)
                + (parsed.ProfileAnalyzeGc ? 1 : 0)
                + (parsed.ProfileAnalyzeSpikes ? 1 : 0);
            if (queryCount != 1)
            {
                throw new CliUsageException("profile analyze requires exactly one of --marker, --frame, --gc, or --spikes.");
            }
        }

        // double.TryParse accepts NaN/Infinity and overflows 1e400 to +Infinity; a non-finite value would
        // otherwise reach the JSON serializer (which refuses to write it) instead of failing as a usage error.
        if ((parsed.Kind is CommandKind.ProfileCompare or CommandKind.ProfileMemoryCompare)
            && parsed.ProfileThresholdPercent.HasValue
            && (!double.IsFinite(parsed.ProfileThresholdPercent.Value) || parsed.ProfileThresholdPercent.Value < 0))
        {
            throw new CliUsageException("--threshold must be a finite number >= 0.");
        }
    }

    private static void ValidateMaterialOptions(ParsedCommand parsed)
    {
        switch (parsed.Kind)
        {
            case CommandKind.MaterialInfo when string.IsNullOrWhiteSpace(parsed.MaterialPath):
                throw new CliUsageException("`material info` requires `--path`.");
            case CommandKind.MaterialSet when string.IsNullOrWhiteSpace(parsed.MaterialPath):
                throw new CliUsageException("`material set` requires `--path`.");
            case CommandKind.MaterialSet:
            {
                bool hasPropertySet = !string.IsNullOrWhiteSpace(parsed.MaterialProperty)
                    && !string.IsNullOrWhiteSpace(parsed.MaterialValue);
                bool hasTextureSet = !string.IsNullOrWhiteSpace(parsed.MaterialTexture)
                    && !string.IsNullOrWhiteSpace(parsed.MaterialTextureAsset);
                if (!hasPropertySet && !hasTextureSet)
                {
                    throw new CliUsageException("`material set` requires `--property`+`--value` or `--texture`+`--asset`.");
                }

                break;
            }
            case CommandKind.QaRunSequence:
                if (parsed.QaSequenceArgs == null || parsed.QaSequenceArgs.steps.Length == 0)
                {
                    throw new CliUsageException("`qa run-sequence` requires `--spec-json <json|@file>` with a non-empty steps array.");
                }

                if (parsed.QaSequenceTimeoutMs < 0
                    || parsed.QaSequenceTimeoutMs > ProtocolConstants.MaxQaRunSequenceTimeoutMs)
                {
                    throw new CliUsageException($"`qa run-sequence` `--timeout` must be from 0 to {ProtocolConstants.MaxQaRunSequenceTimeoutMs} ms.");
                }

                break;
        }
    }

    private static void ValidateQaOptions(ParsedCommand parsed)
    {
        if (parsed.Kind is CommandKind.QaClick or CommandKind.QaTap or CommandKind.QaSwipe)
        {
            parsed.QaButton = RequireQaButton(parsed.QaButton);
        }

        switch (parsed.Kind)
        {
            case CommandKind.QaClick:
            {
                bool hasQaId = !string.IsNullOrWhiteSpace(parsed.QaId);
                bool hasTarget = !string.IsNullOrWhiteSpace(parsed.QaTarget);
                if (hasQaId == hasTarget)
                {
                    throw new CliUsageException("`qa click` requires exactly one of `--qa-id` or `--target`.");
                }

                break;
            }
            case CommandKind.QaTap:
            {
                bool hasTarget = !string.IsNullOrWhiteSpace(parsed.QaTarget);
                bool hasAnyCoord = parsed.QaTapX.HasValue || parsed.QaTapY.HasValue;
                if (hasTarget)
                {
                    if (hasAnyCoord)
                    {
                        throw new CliUsageException("`qa tap` cannot combine `--target` with `--x`/`--y`.");
                    }
                }
                else if (!parsed.QaTapX.HasValue || !parsed.QaTapY.HasValue)
                {
                    throw new CliUsageException("`qa tap` requires both `--x` and `--y` (or `--target`).");
                }

                ValidateQaScreenshotDimensions(parsed, "`qa tap`");
                break;
            }
            case CommandKind.QaUiDump:
                ValidateQaScreenshotDimensions(parsed, "`qa ui-dump`");
                break;
            case CommandKind.QaWorldDump:
                ValidateQaScreenshotDimensions(parsed, "`qa world-dump`");
                break;
            case CommandKind.QaSwipe when string.IsNullOrWhiteSpace(parsed.QaSwipeFrom) || string.IsNullOrWhiteSpace(parsed.QaSwipeTo):
                throw new CliUsageException("`qa swipe` requires both `--from` and `--to`.");
            case CommandKind.QaSwipe:
                bool usesTargetRelativeOffsets = !string.IsNullOrWhiteSpace(parsed.QaTarget);
                RequireQaSwipeCoordinatePair(parsed.QaSwipeFrom!, "--from", usesTargetRelativeOffsets);
                RequireQaSwipeCoordinatePair(parsed.QaSwipeTo!, "--to", usesTargetRelativeOffsets);
                ValidateQaScreenshotDimensions(parsed, "`qa swipe`");
                if (usesTargetRelativeOffsets && (parsed.QaScreenshotWidth.HasValue || parsed.QaScreenshotHeight.HasValue))
                {
                    throw new CliUsageException("`qa swipe` cannot combine `--target` with `--screenshot-width`/`--screenshot-height`.");
                }

                break;
            case CommandKind.QaKey when string.IsNullOrWhiteSpace(parsed.QaKeyName):
                throw new CliUsageException("`qa key` requires `--key`.");
            case CommandKind.QaWait when parsed.QaWaitMs <= 0:
                throw new CliUsageException("`qa wait` requires `--ms`.");
            case CommandKind.QaWaitUntil:
            {
                bool hasScene = !string.IsNullOrWhiteSpace(parsed.QaWaitScene);
                bool hasLogContains = !string.IsNullOrWhiteSpace(parsed.QaWaitLogContains);
                bool hasObjectExists = !string.IsNullOrWhiteSpace(parsed.QaWaitObjectExists) || !string.IsNullOrWhiteSpace(parsed.QaId);
                bool hasObjectInteractable = !string.IsNullOrWhiteSpace(parsed.QaWaitObjectInteractable);
                bool hasObjectGone = !string.IsNullOrWhiteSpace(parsed.QaWaitObjectGone);
                if (!hasScene && !hasLogContains && !hasObjectExists && !hasObjectInteractable && !hasObjectGone)
                {
                    throw new CliUsageException("`qa wait-until` requires at least one of `--scene`, `--log-contains`, `--object-exists`, `--object-interactable`, `--object-gone`, or `--qa-id`.");
                }

                if (!string.IsNullOrWhiteSpace(parsed.QaId) && !string.IsNullOrWhiteSpace(parsed.QaWaitObjectExists))
                {
                    throw new CliUsageException("`qa wait-until` cannot use `--qa-id` and `--object-exists` together.");
                }

                if (hasObjectGone
                    && ((hasObjectExists && string.Equals(parsed.QaWaitObjectGone, parsed.QaWaitObjectExists, StringComparison.Ordinal))
                        || (hasObjectInteractable && string.Equals(parsed.QaWaitObjectGone, parsed.QaWaitObjectInteractable, StringComparison.Ordinal))))
                {
                    throw new CliUsageException("Cannot set `--object-gone` together with `--object-exists`/`--object-interactable` on the same target (the conditions are ANDed and would time out forever).");
                }

                if (!string.IsNullOrWhiteSpace(parsed.QaId))
                {
                    parsed.QaWaitObjectExists = parsed.QaId;
                }

                break;
            }
        }
    }

    private static void ValidateQaScreenshotDimensions(ParsedCommand parsed, string commandName)
    {
        bool hasScreenshotWidth = parsed.QaScreenshotWidth.HasValue;
        bool hasScreenshotHeight = parsed.QaScreenshotHeight.HasValue;
        if (hasScreenshotWidth != hasScreenshotHeight)
        {
            throw new CliUsageException($"{commandName} requires `--screenshot-width` and `--screenshot-height` together.");
        }
    }

    private static void RequireQaSwipeCoordinatePair(string value, string option, bool usesTargetRelativeOffsets)
    {
        string[] parts = value.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length != 2 || !int.TryParse(parts[0], out _) || !int.TryParse(parts[1], out _))
        {
            throw new CliUsageException($"{option} must be {GetQaSwipeCoordinateDescription(usesTargetRelativeOffsets)}.");
        }
    }

    private static string GetQaSwipeCoordinateDescription(bool usesTargetRelativeOffsets)
    {
        return usesTargetRelativeOffsets
            ? "`x,y` pixel offset from the target center"
            : "`x,y` absolute screen pixel coordinates";
    }

    private static void ValidateExecuteOptions(ParsedCommand parsed)
    {
        if (parsed.Kind != CommandKind.ExecuteCode)
        {
            return;
        }

        bool hasCode = !string.IsNullOrWhiteSpace(parsed.ExecuteCodeSnippet);
        bool hasFile = !string.IsNullOrWhiteSpace(parsed.ExecuteCodeFile);

        if (hasCode == hasFile)
        {
            throw new CliUsageException("`execute` requires exactly one of `--code` or `--file`.");
        }

        if (ForceRequiredByCatalog(parsed))
        {
            throw new CliUsageException("`execute` requires `--force`.");
        }

        if (parsed.ExecuteCodeTimeoutSeconds.HasValue)
        {
            int seconds = parsed.ExecuteCodeTimeoutSeconds.Value;
            int maxSeconds = ProtocolConstants.MaxExecuteTimeoutMs / 1000;
            if (seconds <= 0 || seconds > maxSeconds)
            {
                throw new CliUsageException(
                    $"--timeout must be an integer number of seconds from 1 to {maxSeconds}.");
            }
        }
    }

    internal static bool ForceRequiredByCatalog(ParsedCommand parsed)
    {
        if (parsed.Force)
        {
            return false;
        }

        CliCommandDescriptor descriptor = GetCatalogDescriptor(parsed.Kind);
        return descriptor.ForceRule switch
        {
            ForceRule.None => false,
            ForceRule.Always => true,
            ForceRule.OnOverwrite => false,
            ForceRule.OnDestructiveOp => PatchContainsDestructiveOperation(parsed),
            _ => throw new InvalidOperationException("Unsupported force rule: " + descriptor.ForceRule),
        };
    }

    internal static CliCommandDescriptor GetCatalogDescriptor(CommandKind kind)
    {
        string commandPath = kind switch
        {
            CommandKind.Status => "status",
            CommandKind.Compile => "compile",
            CommandKind.Refresh => "refresh",
            CommandKind.ReadConsole => "read-console",
            CommandKind.Play => "play",
            CommandKind.Pause => "pause",
            CommandKind.Stop => "stop",
            CommandKind.ExecuteMenu => "execute-menu",
            CommandKind.Screenshot => "screenshot",
            CommandKind.ExecuteCode => "execute",
            CommandKind.Custom => "custom",
            CommandKind.AssetFind => "asset find",
            CommandKind.AssetTypes => "asset types",
            CommandKind.AssetInfo => "asset info",
            CommandKind.AssetReimport => "asset reimport",
            CommandKind.AssetMkdir => "asset mkdir",
            CommandKind.AssetMove => "asset move",
            CommandKind.AssetRename => "asset rename",
            CommandKind.AssetDelete => "asset delete",
            CommandKind.AssetCreate => "asset create",
            CommandKind.SceneOpen => "scene open",
            CommandKind.SceneInspect => "scene inspect",
            CommandKind.ScenePatch => "scene patch",
            CommandKind.SceneAddObject => "scene add-object",
            CommandKind.SceneSetTransform => "scene set-transform",
            CommandKind.SceneAddComponent => "scene add-component",
            CommandKind.SceneRemoveComponent => "scene remove-component",
            CommandKind.SceneListComponents => "scene list-components",
            CommandKind.SceneAssignMaterial => "scene assign-material",
            CommandKind.PrefabInspect => "prefab inspect",
            CommandKind.PrefabCreate => "prefab create",
            CommandKind.PrefabPatch => "prefab patch",
            CommandKind.PrefabAddComponent => "prefab add-component",
            CommandKind.PrefabRemoveComponent => "prefab remove-component",
            CommandKind.PrefabListComponents => "prefab list-components",
            CommandKind.TestList => "test list",
            CommandKind.TestRun => "test run",
            CommandKind.TestResults => "test results",
            CommandKind.TestCancel => "test cancel",
            CommandKind.RecordStart => "record start",
            CommandKind.RecordStop => "record stop",
            CommandKind.RecordStatus => "record status",
            CommandKind.ProfileStats => "profile stats",
            CommandKind.ProfileCaptureStart => "profile capture start",
            CommandKind.ProfileCaptureStop => "profile capture stop",
            CommandKind.ProfileStatus => "profile status",
            CommandKind.ProfileAnalyze => "profile analyze",
            CommandKind.ProfileCompare => "profile compare",
            CommandKind.ProfileMemory => "profile memory",
            CommandKind.ProfileMemoryCompare => "profile memory compare",
            CommandKind.ProfileMemoryTop => "profile memory top",
            CommandKind.ProfileMemorySnapshot => "profile memory snapshot",
            CommandKind.EditorLaunch => "editor launch",
            CommandKind.EditorStop => "editor stop",
            CommandKind.PackageList => "package list",
            CommandKind.PackageAdd => "package add",
            CommandKind.PackageRemove => "package remove",
            CommandKind.PackageSearch => "package search",
            CommandKind.MaterialInfo => "material info",
            CommandKind.MaterialSet => "material set",
            CommandKind.QaClick => "qa click",
            CommandKind.QaTap => "qa tap",
            CommandKind.QaSwipe => "qa swipe",
            CommandKind.QaKey => "qa key",
            CommandKind.QaUiDump => "qa ui-dump",
            CommandKind.QaWorldDump => "qa world-dump",
            CommandKind.QaWait => "qa wait",
            CommandKind.QaWaitUntil => "qa wait-until",
            CommandKind.QaRunSequence => "qa run-sequence",
            CommandKind.InstancesList => "instances list",
            CommandKind.InstancesUse => "instances use",
            CommandKind.Doctor => "doctor",
            CommandKind.Raw => "raw",
            _ => throw new InvalidOperationException("Command kind has no catalog descriptor: " + kind),
        };

        return CliCommandCatalog.FindByCommand(commandPath)
            ?? throw new InvalidOperationException("Could not find a catalog descriptor: " + commandPath);
    }

    private static bool PatchContainsDestructiveOperation(ParsedCommand parsed)
    {
        return parsed.Kind switch
        {
            CommandKind.ScenePatch => ScenePatchContainsDestructiveOperation(parsed),
            CommandKind.PrefabPatch => PrefabPatchContainsDestructiveOperation(parsed),
            _ => false,
        };
    }

    private static bool HasInvalidPrefabSpecSource(ParsedCommand parsed)
    {
        bool hasFile = !string.IsNullOrWhiteSpace(parsed.PrefabSpecFile);
        bool hasInline = !string.IsNullOrWhiteSpace(parsed.PrefabSpecJson);
        return hasFile == hasInline;
    }

    private static bool HasInvalidSceneSpecSource(ParsedCommand parsed)
    {
        bool hasFile = !string.IsNullOrWhiteSpace(parsed.SceneSpecFile);
        bool hasInline = !string.IsNullOrWhiteSpace(parsed.SceneSpecJson);
        return hasFile == hasInline;
    }

    private static bool ScenePatchContainsDestructiveOperation(ParsedCommand parsed)
    {
        string specJson;
        try
        {
            specJson = parsed.ResolveSceneSpecJson();
        }
        catch (CliUsageException)
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(specJson);
            // TryGetProperty throws on a non-object root, and that escapes the JsonException
            // catch below. A spec we cannot read holds no destructive op; the editor rejects it.
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            if (!document.RootElement.TryGetProperty("operations", out JsonElement operations)
                || operations.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            foreach (JsonElement operation in operations.EnumerateArray())
            {
                if (operation.ValueKind != JsonValueKind.Object
                    || !operation.TryGetProperty("op", out JsonElement opElement)
                    || opElement.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                string? op = opElement.GetString();
                if (string.Equals(op, "delete-gameobject", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(op, "remove-component", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }
        catch (JsonException)
        {
            return false;
        }

        return false;
    }

    private static bool PrefabPatchContainsDestructiveOperation(ParsedCommand parsed)
    {
        string specJson;
        try
        {
            specJson = parsed.ResolvePrefabSpecJson();
        }
        catch (CliUsageException)
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(specJson);
            // TryGetProperty throws on a non-object root, and that escapes the JsonException
            // catch below. A spec we cannot read holds no destructive op; the editor rejects it.
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            if (!document.RootElement.TryGetProperty("operations", out JsonElement operations)
                || operations.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            foreach (JsonElement operation in operations.EnumerateArray())
            {
                if (operation.ValueKind != JsonValueKind.Object
                    || !operation.TryGetProperty("op", out JsonElement opElement)
                    || opElement.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                string? op = opElement.GetString();
                if (string.Equals(op, "remove-node", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(op, "remove-component", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }
        catch (JsonException)
        {
            return false;
        }

        return false;
    }
}
