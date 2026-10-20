using System.Text.Json;
using UnityCli.Cli.Models;
using UnityCli.Cli.Services;
using UnityCli.Protocol;

namespace UnityCli.Cli.Tests;

public sealed class CliArgumentParserTests
{
    [Fact]
    public void Parse_TestList_DefaultsToAllMode()
    {
        var parsed = CliArgumentParser.Parse(["test", "list"]);
        var args = ProtocolJson.Deserialize<TestListArgs>(parsed.ToEnvelope().argumentsJson);

        Assert.Equal(CommandKind.TestList, parsed.Kind);
        Assert.Equal("all", parsed.TestMode);
        Assert.NotNull(args);
        Assert.False(args!.noDetail);
    }

    [Fact]
    public void Parse_TestList_AcceptsExplicitMode()
    {
        var parsed = CliArgumentParser.Parse(["test", "list", "--mode", "edit"]);

        Assert.Equal(CommandKind.TestList, parsed.Kind);
        Assert.Equal("edit", parsed.TestMode);
    }

    [Fact]
    public void Parse_TestList_AcceptsNoDetail()
    {
        var parsed = CliArgumentParser.Parse(["test", "list", "--no-detail"]);
        var args = ProtocolJson.Deserialize<TestListArgs>(parsed.ToEnvelope().argumentsJson);

        Assert.Equal(CommandKind.TestList, parsed.Kind);
        Assert.True(parsed.TestNoDetail);
        Assert.NotNull(args);
        Assert.True(args!.noDetail);
    }

    [Fact]
    public void Parse_TestList_RejectsInvalidMode()
    {
        var ex = Assert.Throws<CliUsageException>(() => CliArgumentParser.Parse(["test", "list", "--mode", "bogus"]));

        Assert.Contains("--mode", ex.Message);
    }

    [Fact]
    public void Parse_TestRun_RequiresMode()
    {
        var ex = Assert.Throws<CliUsageException>(() => CliArgumentParser.Parse(["test", "run"]));

        Assert.Contains("--mode", ex.Message);
    }

    [Fact]
    public void Parse_TestRun_AcceptsAllOptions()
    {
        var parsed = CliArgumentParser.Parse([
            "test", "run",
            "--mode", "play",
            "--filter", "Foo.Bar",
            "--category", "Smoke",
            "--assembly", "UnityCliBridge.Bridge.Editor.Tests",
            "--no-domain-reload",
            "--failures-only",
            "--timeout", "600",
            "--wait",
        ]);
        var args = ProtocolJson.Deserialize<TestRunArgs>(parsed.ToEnvelope().argumentsJson);

        Assert.Equal(CommandKind.TestRun, parsed.Kind);
        Assert.Equal("play", parsed.TestMode);
        Assert.Equal("Foo.Bar", parsed.TestFilter);
        Assert.Equal("Smoke", parsed.TestCategory);
        Assert.Equal("UnityCliBridge.Bridge.Editor.Tests", parsed.TestAssembly);
        Assert.True(parsed.TestNoDomainReload);
        Assert.True(parsed.TestFailuresOnly);
        Assert.Equal(600, parsed.TestTimeoutSeconds);
        Assert.True(parsed.TestWait);
        Assert.NotNull(args);
        Assert.True(args!.failuresOnly);
    }

    [Fact]
    public void Parse_TestRun_RejectsNoDomainReloadForEditMode()
    {
        var ex = Assert.Throws<CliUsageException>(() =>
            CliArgumentParser.Parse(["test", "run", "--mode", "edit", "--no-domain-reload"]));

        Assert.Equal("--no-domain-reload is only valid with --mode play.", ex.Message);
    }

    [Fact]
    public void Parse_TestRun_RejectsModeAll()
    {
        var ex = Assert.Throws<CliUsageException>(() => CliArgumentParser.Parse(["test", "run", "--mode", "all"]));

        Assert.Contains("--mode", ex.Message);
    }

    [Fact]
    public void Parse_TestRun_RejectsTimeoutAboveMax()
    {
        var ex = Assert.Throws<CliUsageException>(
            () => CliArgumentParser.Parse(["test", "run", "--mode", "edit", "--timeout", "9999"]));

        Assert.Contains("--timeout", ex.Message);
    }

    [Fact]
    public void Parse_TestResults_AcceptsRunIdOptional()
    {
        var noId = CliArgumentParser.Parse(["test", "results"]);
        Assert.Equal(CommandKind.TestResults, noId.Kind);
        Assert.Null(noId.TestRunId);

        var withId = CliArgumentParser.Parse(["test", "results", "--run-id", "0123456789abcdef0123456789abcdef"]);
        Assert.Equal(CommandKind.TestResults, withId.Kind);
        Assert.Equal("0123456789abcdef0123456789abcdef", withId.TestRunId);
    }

    [Fact]
    public void Parse_TestResults_AcceptsFailuresOnly()
    {
        var parsed = CliArgumentParser.Parse(["test", "results", "--run-id", "0123456789abcdef0123456789abcdef", "--failures-only"]);
        var args = ProtocolJson.Deserialize<TestResultsArgs>(parsed.ToEnvelope().argumentsJson);

        Assert.Equal(CommandKind.TestResults, parsed.Kind);
        Assert.Equal("0123456789abcdef0123456789abcdef", parsed.TestRunId);
        Assert.True(parsed.TestFailuresOnly);
        Assert.NotNull(args);
        Assert.True(args!.failuresOnly);
    }

    [Fact]
    public void Parse_TestCancel_MapsToProtocolCommand()
    {
        var parsed = CliArgumentParser.Parse(["test", "cancel"]);

        Assert.Equal(CommandKind.TestCancel, parsed.Kind);

        CommandEnvelope envelope = parsed.ToEnvelope();
        Assert.Equal(ProtocolConstants.CommandTestCancel, envelope.command);
    }

    [Fact]
    public void Parse_TestCancel_DoesNotRequireForce()
    {
        var parsed = CliArgumentParser.Parse(["test", "cancel"]);

        Assert.False(CliArgumentParser.ForceRequiredByCatalog(parsed));
    }

    [Fact]
    public void Parse_ReadConsole_DefaultsToIncludingStackTrace()
    {
        var parsed = CliArgumentParser.Parse(["read-console"]);
        var args = ProtocolJson.Deserialize<ReadConsoleArgs>(parsed.ToEnvelope().argumentsJson);

        Assert.Equal(CommandKind.ReadConsole, parsed.Kind);
        Assert.NotNull(args);
        Assert.False(args!.noStackTrace);
    }

    [Fact]
    public void Parse_ReadConsole_AcceptsNoStackTrace()
    {
        var parsed = CliArgumentParser.Parse(["read-console", "--limit", "10", "--no-stacktrace"]);
        var args = ProtocolJson.Deserialize<ReadConsoleArgs>(parsed.ToEnvelope().argumentsJson);

        Assert.Equal(CommandKind.ReadConsole, parsed.Kind);
        Assert.True(parsed.ConsoleNoStackTrace);
        Assert.NotNull(args);
        Assert.Equal(10, args!.limit);
        Assert.True(args.noStackTrace);
    }

    [Fact]
    public void Parse_InstancesList_AcceptsBrief()
    {
        var parsed = CliArgumentParser.Parse(["instances", "list", "--brief"]);

        Assert.Equal(CommandKind.InstancesList, parsed.Kind);
        Assert.True(parsed.InstancesBrief);
    }

    [Fact]
    public void Parse_AcceptsJsonFlagAfterCommand()
    {
        var parsed = CliArgumentParser.Parse(["status", "--json"]);

        Assert.Equal(CommandKind.Status, parsed.Kind);
        Assert.True(parsed.JsonOutput);
        Assert.Equal(OutputMode.Json, parsed.OutputMode);
    }

    [Fact]
    public void Parse_AcceptsCompactOutputFlagAfterCommand()
    {
        var parsed = CliArgumentParser.Parse(["status", "--output", "compact"]);

        Assert.Equal(CommandKind.Status, parsed.Kind);
        Assert.False(parsed.JsonOutput);
        Assert.Equal(OutputMode.Compact, parsed.OutputMode);
    }

    [Fact]
    public void Parse_OutputOptionBeforeAnotherOption_ThrowsMissingValueMessage()
    {
        var ex = Assert.Throws<CliUsageException>(() => CliArgumentParser.Parse(["status", "--output", "--json"]));

        Assert.Equal("`--output` requires a value. Use `default`, `json`, or `compact`.", ex.Message);
    }

    [Fact]
    public void Parse_RawPayload_CanUseCompactOutputMode()
    {
        var parsed = CliArgumentParser.Parse([
            "raw",
            "--output", "compact",
            "--json", "{\"command\":\"status\",\"arguments\":{}}"
        ]);

        Assert.Equal(CommandKind.Raw, parsed.Kind);
        Assert.Equal(OutputMode.Compact, parsed.OutputMode);
        Assert.Equal("{\"command\":\"status\",\"arguments\":{}}", parsed.RawJson);
    }

    [Fact]
    public void DetectOutputMode_OutputBeforeCommand_ReturnsCompact()
    {
        var outputMode = CliArgumentParser.DetectOutputMode(["--output", "compact", "status"]);

        Assert.Equal(OutputMode.Compact, outputMode);
    }

    [Fact]
    public void DetectOutputMode_OutputAfterCommand_ReturnsCompact()
    {
        var outputMode = CliArgumentParser.DetectOutputMode(["status", "--output", "compact"]);

        Assert.Equal(OutputMode.Compact, outputMode);
    }

    [Fact]
    public void DetectOutputMode_InvalidOutputValue_IsIgnored()
    {
        var outputMode = CliArgumentParser.DetectOutputMode(["--output", "invalid", "status"]);

        Assert.Equal(OutputMode.Default, outputMode);
    }

    [Fact]
    public void DetectOutputMode_JsonFlag_ReturnsJson()
    {
        var outputMode = CliArgumentParser.DetectOutputMode(["--json", "status"]);

        Assert.Equal(OutputMode.Json, outputMode);
    }

    [Fact]
    public void DetectOutputMode_NoOutputFlags_ReturnsDefault()
    {
        var outputMode = CliArgumentParser.DetectOutputMode(["status"]);

        Assert.Equal(OutputMode.Default, outputMode);
    }

    [Fact]
    public void Parse_OutputMode_LastWriteWinsWhenOutputComesAfterJson()
    {
        var parsed = CliArgumentParser.Parse(["status", "--json", "--output", "compact"]);

        Assert.Equal(OutputMode.Compact, parsed.OutputMode);
    }

    [Fact]
    public void Parse_OutputMode_LastWriteWinsWhenJsonComesAfterOutput()
    {
        var parsed = CliArgumentParser.Parse(["status", "--output", "compact", "--json"]);

        Assert.Equal(OutputMode.Json, parsed.OutputMode);
    }

    [Fact]
    public void Parse_RawRequiresPayload()
    {
        var ex = Assert.Throws<CliUsageException>(() => CliArgumentParser.Parse(["raw"]));
        Assert.Contains("payload", ex.Message);
    }

    [Fact]
    public void Parse_CompileUsesLiveDefaultTimeout()
    {
        var parsed = CliArgumentParser.Parse(["compile"]);

        Assert.Equal(CommandKind.Compile, parsed.Kind);
        Assert.Equal(ProtocolConstants.DefaultLiveTimeoutMs, parsed.TimeoutMs);
        Assert.False(parsed.TimeoutMsSpecified);
    }

    [Theory]
    [InlineData("compile", CommandKind.Compile)]
    [InlineData("refresh", CommandKind.Refresh)]
    public void Parse_CompileAndRefresh_AcceptWait(string command, CommandKind expectedKind)
    {
        var parsed = CliArgumentParser.Parse([command, "--wait"]);

        Assert.Equal(expectedKind, parsed.Kind);
        Assert.True(parsed.Wait);
    }

    [Theory]
    [InlineData("compile", CommandKind.Compile)]
    [InlineData("refresh", CommandKind.Refresh)]
    [InlineData("play", CommandKind.Play)]
    [InlineData("stop", CommandKind.Stop)]
    public void Parse_EditorStateCommands_WaitByDefault(string command, CommandKind expectedKind)
    {
        var parsed = CliArgumentParser.Parse([command]);

        Assert.Equal(expectedKind, parsed.Kind);
        Assert.True(parsed.Wait);
    }

    [Theory]
    [InlineData("compile")]
    [InlineData("refresh")]
    [InlineData("play")]
    [InlineData("stop")]
    public void Parse_EditorStateCommands_NoWaitOptsOut(string command)
    {
        var parsed = CliArgumentParser.Parse([command, "--no-wait"]);

        Assert.False(parsed.Wait);
    }

    [Fact]
    public void Parse_Pause_DoesNotWait()
    {
        var parsed = CliArgumentParser.Parse(["pause"]);

        Assert.False(parsed.Wait);
    }

    [Fact]
    public void Parse_CompileTimeoutMs_MarksExplicitTimeout()
    {
        var parsed = CliArgumentParser.Parse(["compile", "--wait", "--timeout-ms", "5000"]);

        Assert.Equal(5_000, parsed.TimeoutMs);
        Assert.True(parsed.TimeoutMsSpecified);
    }

    [Fact]
    public void Parse_AssetDeleteRequiresForce()
    {
        var ex = Assert.Throws<CliUsageException>(() => CliArgumentParser.Parse(["asset", "delete", "--path", "Assets/Test.asset"]));

        Assert.Contains("--force", ex.Message);
    }

    [Fact]
    public void Parse_AssetFindUsesLiveDefaultTimeout()
    {
        var parsed = CliArgumentParser.Parse(["asset", "find", "--name", "Sample", "--folder", "Assets"]);

        Assert.Equal(CommandKind.AssetFind, parsed.Kind);
        Assert.Equal("Sample", parsed.AssetName);
        Assert.Equal("Assets", parsed.AssetFolder);
        Assert.Equal(ProtocolConstants.DefaultLiveTimeoutMs, parsed.TimeoutMs);
    }

    [Fact]
    public void Parse_AssetFind_AcceptsTypeWithoutName()
    {
        var parsed = CliArgumentParser.Parse(["asset", "find", "--type", "Material", "--folder", "Assets"]);

        Assert.Equal(CommandKind.AssetFind, parsed.Kind);
        Assert.Null(parsed.AssetName);
        Assert.Equal("Material", parsed.AssetType);
        Assert.Equal("Assets", parsed.AssetFolder);
    }

    [Fact]
    public void Parse_AssetFind_TypeOnlyToEnvelope_PreservesTypeQuery()
    {
        var parsed = CliArgumentParser.Parse(["asset", "find", "--type", "Scene"]);

        var args = ProtocolJson.Deserialize<AssetFindArgs>(parsed.ToEnvelope().argumentsJson);

        Assert.NotNull(args);
        Assert.Equal(string.Empty, args!.name);
        Assert.Equal("Scene", args.type);
        Assert.Equal(ProtocolConstants.DefaultAssetFindLimit, args.limit);
    }

    [Fact]
    public void Parse_AssetFind_RequiresNameOrType()
    {
        var ex = Assert.Throws<CliUsageException>(() => CliArgumentParser.Parse(["asset", "find"]));

        Assert.Contains("--name", ex.Message);
        Assert.Contains("--type", ex.Message);
    }

    [Fact]
    public void Parse_AssetInfo_AcceptsPackagesPath()
    {
        var parsed = CliArgumentParser.Parse([
            "asset",
            "info",
            "--path", "Packages/com.unity.render-pipelines.universal/Runtime/Materials/Lit.mat"
        ]);

        Assert.Equal(CommandKind.AssetInfo, parsed.Kind);
        Assert.Equal(
            "Packages/com.unity.render-pipelines.universal/Runtime/Materials/Lit.mat",
            parsed.AssetPath);
    }

    [Fact]
    public void Parse_AssetTypes_UsesLiveDefaultTimeout()
    {
        var parsed = CliArgumentParser.Parse(["asset", "types"]);

        Assert.Equal(CommandKind.AssetTypes, parsed.Kind);
        Assert.Equal(ProtocolConstants.DefaultLiveTimeoutMs, parsed.TimeoutMs);
    }

    [Fact]
    public void Parse_RawJsonOptionStillAcceptsPayload()
    {
        var parsed = CliArgumentParser.Parse(["raw", "--json", "{\"command\":\"status\",\"arguments\":{}}"]);

        Assert.Equal(CommandKind.Raw, parsed.Kind);
        Assert.Equal("{\"command\":\"status\",\"arguments\":{}}", parsed.RawJson);
    }

    [Fact]
    public void Parse_AssetCreateMaterial_UsesLiveTimeout()
    {
        var parsed = CliArgumentParser.Parse(["asset", "create", "--type", "material", "--path", "Assets/Test/Example"]);

        Assert.Equal(CommandKind.AssetCreate, parsed.Kind);
        Assert.Equal("material", parsed.AssetCreateType);
        Assert.Equal("Assets/Test/Example", parsed.AssetPath);
        Assert.Equal(ProtocolConstants.DefaultLiveTimeoutMs, parsed.TimeoutMs);
    }

    [Fact]
    public void Parse_AssetCreateScriptableObject_RequiresScriptOrTypeName()
    {
        var ex = Assert.Throws<CliUsageException>(() =>
            CliArgumentParser.Parse(["asset", "create", "--type", "scriptable-object", "--path", "Assets/Test/Example.asset"]));

        Assert.Contains("--script", ex.Message);
        Assert.Contains("--type-name", ex.Message);
    }

    [Fact]
    public void Parse_AssetCreate_AcceptsInitialMapAndForce()
    {
        var parsed = CliArgumentParser.Parse([
            "asset",
            "create",
            "--type", "input-actions",
            "--path", "Assets/Input/NewActions",
            "--initial-map", "Gameplay",
            "--force"
        ]);

        Assert.Equal(CommandKind.AssetCreate, parsed.Kind);
        Assert.Equal("input-actions", parsed.AssetCreateType);
        Assert.Equal("Gameplay", parsed.AssetInitialMap);
        Assert.True(parsed.Force);
    }

    [Fact]
    public void Parse_AssetCreateScriptableObject_AcceptsTypeNameAndDataJson()
    {
        var parsed = CliArgumentParser.Parse([
            "asset",
            "create",
            "--type", "scriptable-object",
            "--path", "Assets/Data/Config",
            "--type-name", "MyNamespace.ConfigAsset",
            "--data-json", "{\"title\":\"Hello\"}"
        ]);

        Assert.Equal("scriptable-object", parsed.AssetCreateType);
        Assert.Equal("MyNamespace.ConfigAsset", parsed.AssetTypeName);
        Assert.Equal("{\"title\":\"Hello\"}", parsed.AssetDataJson);
    }

    [Fact]
    public void Parse_AssetCreateAnimatorOverrideController_RequiresBaseController()
    {
        var ex = Assert.Throws<CliUsageException>(() =>
            CliArgumentParser.Parse(["asset", "create", "--type", "animator-override-controller", "--path", "Assets/Test/Override"]));

        Assert.Contains("--base-controller", ex.Message);
    }

    [Fact]
    public void Parse_AssetCreateRenderTexture_AcceptsSizeOptions()
    {
        var parsed = CliArgumentParser.Parse([
            "asset",
            "create",
            "--type", "render-texture",
            "--path", "Assets/Textures/Target",
            "--width", "2048",
            "--height", "1024",
            "--depth", "0"
        ]);

        Assert.Equal("render-texture", parsed.AssetCreateType);
        Assert.Equal(2048, parsed.AssetWidth);
        Assert.Equal(1024, parsed.AssetHeight);
        Assert.Equal(0, parsed.AssetDepth);
    }

    [Fact]
    public void Parse_AssetCreate_AcceptsExtensionTypeId()
    {
        var parsed = CliArgumentParser.Parse([
            "asset",
            "create",
            "--type", "custom-note",
            "--path", "Assets/Test/CustomNote"
        ]);

        Assert.Equal("custom-note", parsed.AssetCreateType);
    }

    [Fact]
    public void Parse_AssetCreate_AcceptsExtensionCustomOptions()
    {
        var parsed = CliArgumentParser.Parse([
            "asset",
            "create",
            "--type", "custom-note",
            "--path", "Assets/Test/CustomNote",
            "--title", "Hello",
            "--count", "7",
            "--enabled"
        ]);

        var args = ProtocolJson.Deserialize<AssetCreateArgs>(parsed.ToEnvelope().argumentsJson);
        Assert.NotNull(args);
        Assert.Equal("custom-note", args.type);
        Assert.Equal("{\"title\":\"Hello\",\"count\":7,\"enabled\":true}", args.optionsJson);
    }

    [Fact]
    public void Parse_AssetCreate_BuiltInRejectsUnknownCustomOption()
    {
        var ex = Assert.Throws<CliUsageException>(() =>
            CliArgumentParser.Parse([
                "asset",
                "create",
                "--type", "material",
                "--path", "Assets/Test/Material",
                "--widht", "512"
            ]));

        Assert.Contains("--widht", ex.Message);
    }

    [Fact]
    public void Parse_MaterialInfo_RequiresPath()
    {
        var ex = Assert.Throws<CliUsageException>(() =>
            CliArgumentParser.Parse(["material", "info"]));

        Assert.Contains("--path", ex.Message);
    }

    [Fact]
    public void Parse_MaterialInfo_AcceptsPath()
    {
        var parsed = CliArgumentParser.Parse([
            "material", "info",
            "--path", "Assets/Materials/Wood.mat"
        ]);

        Assert.Equal(CommandKind.MaterialInfo, parsed.Kind);
        Assert.Equal("Assets/Materials/Wood.mat", parsed.MaterialPath);
        Assert.Equal(ProtocolConstants.DefaultLiveTimeoutMs, parsed.TimeoutMs);
    }

    [Fact]
    public void Parse_MaterialInfo_AcceptsOmitDefaults()
    {
        var parsed = CliArgumentParser.Parse([
            "material", "info",
            "--path", "Assets/Materials/Wood.mat",
            "--omit-defaults"
        ]);

        Assert.Equal(CommandKind.MaterialInfo, parsed.Kind);
        Assert.True(parsed.OmitDefaults);
    }

    [Fact]
    public void Parse_MaterialInfo_ToEnvelope_IncludesOmitDefaults()
    {
        var parsed = CliArgumentParser.Parse([
            "material", "info",
            "--path", "Assets/Materials/Wood.mat",
            "--omit-defaults"
        ]);

        var args = ProtocolJson.Deserialize<MaterialInfoArgs>(parsed.ToEnvelope().argumentsJson);
        Assert.NotNull(args);
        Assert.Equal("Assets/Materials/Wood.mat", args.path);
        Assert.True(args.omitDefaults);
    }

    [Fact]
    public void Parse_MaterialSet_RequiresPropertyOrTexture()
    {
        var ex = Assert.Throws<CliUsageException>(() =>
            CliArgumentParser.Parse([
                "material", "set",
                "--path", "Assets/Materials/Wood.mat"
            ]));

        Assert.Contains("--property", ex.Message);
    }

    [Fact]
    public void Parse_MaterialSet_AcceptsPropertyAndValue()
    {
        var parsed = CliArgumentParser.Parse([
            "material", "set",
            "--path", "Assets/Materials/Wood.mat",
            "--property", "_Color",
            "--value", "1,0,0,1"
        ]);

        Assert.Equal(CommandKind.MaterialSet, parsed.Kind);
        Assert.Equal("Assets/Materials/Wood.mat", parsed.MaterialPath);
        Assert.Equal("_Color", parsed.MaterialProperty);
        Assert.Equal("1,0,0,1", parsed.MaterialValue);
    }

    [Fact]
    public void Parse_MaterialSet_AcceptsTextureAndAsset()
    {
        var parsed = CliArgumentParser.Parse([
            "material", "set",
            "--path", "Assets/Materials/Wood.mat",
            "--texture", "_MainTex",
            "--asset", "Assets/Textures/wood.png"
        ]);

        Assert.Equal(CommandKind.MaterialSet, parsed.Kind);
        Assert.Equal("_MainTex", parsed.MaterialTexture);
        Assert.Equal("Assets/Textures/wood.png", parsed.MaterialTextureAsset);
    }

    [Fact]
    public void Parse_MaterialSet_UsesLiveTimeout()
    {
        var parsed = CliArgumentParser.Parse([
            "material", "set",
            "--path", "Assets/Materials/Wood.mat",
            "--property", "_Metallic",
            "--value", "0.8"
        ]);

        Assert.Equal(ProtocolConstants.DefaultLiveTimeoutMs, parsed.TimeoutMs);
    }

    [Fact]
    public void Parse_Material_UnknownSubcommandThrows()
    {
        var ex = Assert.Throws<CliUsageException>(() =>
            CliArgumentParser.Parse(["material", "delete"]));

        Assert.Contains("delete", ex.Message);
    }

    [Fact]
    public void Parse_PrefabInspect_UsesLiveTimeout()
    {
        var parsed = CliArgumentParser.Parse([
            "prefab",
            "inspect",
            "--path", "Assets/Prefabs/Enemy"
        ]);

        Assert.Equal(CommandKind.PrefabInspect, parsed.Kind);
        Assert.Equal("Assets/Prefabs/Enemy", parsed.PrefabPath);
        Assert.Equal(ProtocolConstants.DefaultLiveTimeoutMs, parsed.TimeoutMs);
    }

    [Fact]
    public void Parse_PrefabInspect_WithoutNode_UsesEmptyNodeSentinel()
    {
        var parsed = CliArgumentParser.Parse([
            "prefab",
            "inspect",
            "--path", "Assets/Prefabs/Enemy.prefab"
        ]);

        var args = ProtocolJson.Deserialize<PrefabInspectArgs>(parsed.ToEnvelope().argumentsJson);
        Assert.NotNull(args);
        Assert.Equal("Assets/Prefabs/Enemy.prefab", args.path);
        Assert.Equal(string.Empty, args.node);
        Assert.False(args.withValues);
        Assert.Null(args.maxDepth);
        Assert.False(args.omitDefaults);
    }

    [Fact]
    public void Parse_PrefabInspect_AcceptsDepthAndOmitDefaults()
    {
        var parsed = CliArgumentParser.Parse([
            "prefab",
            "inspect",
            "--path", "Assets/Prefabs/Enemy.prefab",
            "--node", "/Visual[0]",
            "--with-values",
            "--max-depth", "3",
            "--omit-defaults"
        ]);

        Assert.Equal(CommandKind.PrefabInspect, parsed.Kind);
        Assert.Equal("Assets/Prefabs/Enemy.prefab", parsed.PrefabPath);
        Assert.Equal("/Visual[0]", parsed.SceneTarget);
        Assert.True(parsed.PrefabWithValues);
        Assert.Equal(3, parsed.MaxDepth);
        Assert.True(parsed.OmitDefaults);
    }

    [Fact]
    public void Parse_PrefabInspect_ToEnvelope_IncludesDepthAndOmitDefaults()
    {
        var parsed = CliArgumentParser.Parse([
            "prefab",
            "inspect",
            "--path", "Assets/Prefabs/Enemy.prefab",
            "--node", "/Visual[0]",
            "--with-values",
            "--max-depth", "2",
            "--omit-defaults"
        ]);

        var args = ProtocolJson.Deserialize<PrefabInspectArgs>(parsed.ToEnvelope().argumentsJson);
        Assert.NotNull(args);
        Assert.Equal("Assets/Prefabs/Enemy.prefab", args.path);
        Assert.Equal("/Visual[0]", args.node);
        Assert.True(args.withValues);
        Assert.Equal(2, args.maxDepth);
        Assert.True(args.omitDefaults);
    }

    [Fact]
    public void Parse_PrefabInspect_MaxDepthZeroThrows()
    {
        Assert.Throws<CliUsageException>(() =>
            CliArgumentParser.Parse(["prefab", "inspect", "--path", "Assets/P.prefab", "--max-depth", "0"]));
    }

    [Fact]
    public void Parse_PrefabCreate_RequiresExactlyOneSpecSource()
    {
        var ex = Assert.Throws<CliUsageException>(() =>
            CliArgumentParser.Parse([
                "prefab",
                "create",
                "--path", "Assets/Prefabs/Enemy"
            ]));

        Assert.Contains("--spec-file", ex.Message);
        Assert.Contains("--spec-json", ex.Message);
    }

    [Fact]
    public void Parse_PrefabCreate_AcceptsSpecJsonAndForce()
    {
        var parsed = CliArgumentParser.Parse([
            "prefab",
            "create",
            "--path", "Assets/Prefabs/Enemy",
            "--spec-json", "{\"version\":1}",
            "--force"
        ]);

        Assert.Equal(CommandKind.PrefabCreate, parsed.Kind);
        Assert.Equal("Assets/Prefabs/Enemy", parsed.PrefabPath);
        Assert.Equal("{\"version\":1}", parsed.PrefabSpecJson);
        Assert.True(parsed.Force);
    }

    [Fact]
    public void Parse_PrefabPatch_AcceptsSpecFile()
    {
        var parsed = CliArgumentParser.Parse([
            "prefab",
            "patch",
            "--path", "Assets/Prefabs/Enemy.prefab",
            "--spec-file", "Specs/enemy.json"
        ]);

        Assert.Equal(CommandKind.PrefabPatch, parsed.Kind);
        Assert.Equal("Assets/Prefabs/Enemy.prefab", parsed.PrefabPath);
        Assert.Equal("Specs/enemy.json", parsed.PrefabSpecFile);
    }

    [Fact]
    public void Parse_SceneOpen_UsesLiveTimeout()
    {
        var parsed = CliArgumentParser.Parse([
            "scene",
            "open",
            "--path", "Assets/Scenes/System"
        ]);

        Assert.Equal(CommandKind.SceneOpen, parsed.Kind);
        Assert.Equal("Assets/Scenes/System", parsed.ScenePath);
        Assert.Equal(ProtocolConstants.DefaultLiveTimeoutMs, parsed.TimeoutMs);
    }

    [Fact]
    public void Parse_SceneInspect_AcceptsWithValues()
    {
        var parsed = CliArgumentParser.Parse([
            "scene",
            "inspect",
            "--path", "Assets/Scenes/System.unity",
            "--with-values"
        ]);

        Assert.Equal(CommandKind.SceneInspect, parsed.Kind);
        Assert.Equal("Assets/Scenes/System.unity", parsed.ScenePath);
        Assert.True(parsed.SceneWithValues);
    }

    [Fact]
    public void Parse_SceneInspect_WithoutNode_UsesEmptyNodeSentinel()
    {
        var parsed = CliArgumentParser.Parse([
            "scene",
            "inspect",
            "--path", "Assets/Scenes/System.unity"
        ]);

        var args = ProtocolJson.Deserialize<SceneInspectArgs>(parsed.ToEnvelope().argumentsJson);
        Assert.NotNull(args);
        Assert.Equal("Assets/Scenes/System.unity", args.path);
        Assert.Equal(string.Empty, args.node);
        Assert.False(args.withValues);
        Assert.Null(args.maxDepth);
        Assert.False(args.omitDefaults);
    }

    [Fact]
    public void Parse_SceneInspect_AcceptsDepthAndOmitDefaults()
    {
        var parsed = CliArgumentParser.Parse([
            "scene",
            "inspect",
            "--path", "Assets/Scenes/System.unity",
            "--node", "/Player[0]/Arm[0]",
            "--max-depth", "3",
            "--omit-defaults"
        ]);

        Assert.Equal(CommandKind.SceneInspect, parsed.Kind);
        Assert.Equal("Assets/Scenes/System.unity", parsed.ScenePath);
        Assert.Equal("/Player[0]/Arm[0]", parsed.SceneTarget);
        Assert.Equal(3, parsed.MaxDepth);
        Assert.True(parsed.OmitDefaults);
    }

    [Fact]
    public void Parse_SceneInspect_MaxDepthZeroThrows()
    {
        var ex = Assert.Throws<CliUsageException>(() =>
            CliArgumentParser.Parse([
                "scene",
                "inspect",
                "--path", "Assets/Scenes/System.unity",
                "--max-depth", "0"
            ]));

        Assert.Contains("--max-depth", ex.Message);
    }

    [Fact]
    public void Parse_SceneInspect_ToEnvelope_IncludesDepthAndOmitDefaults()
    {
        var parsed = CliArgumentParser.Parse([
            "scene",
            "inspect",
            "--path", "Assets/Scenes/System.unity",
            "--node", "/Player[0]/Arm[0]",
            "--with-values",
            "--max-depth", "4",
            "--omit-defaults"
        ]);

        var args = ProtocolJson.Deserialize<SceneInspectArgs>(parsed.ToEnvelope().argumentsJson);
        Assert.NotNull(args);
        Assert.Equal("Assets/Scenes/System.unity", args.path);
        Assert.Equal("/Player[0]/Arm[0]", args.node);
        Assert.True(args.withValues);
        Assert.Equal(4, args.maxDepth);
        Assert.True(args.omitDefaults);
    }

    [Fact]
    public void Parse_ScenePatch_RequiresExactlyOneSpecSource()
    {
        var ex = Assert.Throws<CliUsageException>(() =>
            CliArgumentParser.Parse([
                "scene",
                "patch",
                "--path", "Assets/Scenes/System.unity"
            ]));

        Assert.Contains("--spec-file", ex.Message);
        Assert.Contains("--spec-json", ex.Message);
    }

    [Fact]
    public void Parse_ScenePatch_DeleteOperationRequiresForce()
    {
        var ex = Assert.Throws<CliUsageException>(() =>
            CliArgumentParser.Parse([
                "scene",
                "patch",
                "--path", "Assets/Scenes/System.unity",
                "--spec-json", "{\"version\":1,\"operations\":[{\"op\":\"delete-gameobject\",\"target\":\"/Enemy[0]\"}]}"
            ]));

        Assert.Contains("--force", ex.Message);
    }

    [Fact]
    public void Parse_ScenePatch_AcceptsSpecJsonAndForce()
    {
        var parsed = CliArgumentParser.Parse([
            "scene",
            "patch",
            "--path", "Assets/Scenes/System.unity",
            "--spec-json", "{\"version\":1,\"operations\":[{\"op\":\"add-gameobject\",\"parent\":\"/\",\"node\":{\"name\":\"SpawnPoint\"}}]}",
            "--force"
        ]);

        Assert.Equal(CommandKind.ScenePatch, parsed.Kind);
        Assert.Equal("Assets/Scenes/System.unity", parsed.ScenePath);
        Assert.Equal("{\"version\":1,\"operations\":[{\"op\":\"add-gameobject\",\"parent\":\"/\",\"node\":{\"name\":\"SpawnPoint\"}}]}", parsed.SceneSpecJson);
        Assert.True(parsed.Force);
    }

    [Fact]
    public void Parse_ScenePatch_ToEnvelope_UsesScenePatchArgs()
    {
        var parsed = CliArgumentParser.Parse([
            "scene",
            "patch",
            "--path", "Assets/Scenes/System.unity",
            "--spec-json", "{\"version\":1,\"operations\":[{\"op\":\"add-gameobject\",\"parent\":\"/\",\"node\":{\"name\":\"SpawnPoint\"}}]}",
            "--force"
        ]);

        var args = ProtocolJson.Deserialize<ScenePatchArgs>(parsed.ToEnvelope().argumentsJson);
        Assert.NotNull(args);
        Assert.Equal("Assets/Scenes/System.unity", args.path);
        Assert.True(args.force);
        Assert.Contains("add-gameobject", args.specJson);
    }

    [Fact]
    public void Parse_SceneAddObject_RequiresPathAndName()
    {
        var ex = Assert.Throws<CliUsageException>(() =>
            CliArgumentParser.Parse(["scene", "add-object", "--path", "Assets/Scenes/Test.unity"]));

        Assert.Contains("--name", ex.Message);
    }

    [Fact]
    public void Parse_SceneAddObject_AcceptsOptions()
    {
        var parsed = CliArgumentParser.Parse([
            "scene", "add-object",
            "--path", "Assets/Scenes/Test.unity",
            "--parent", "/Root[0]",
            "--position", "3,0,0",
            "--name", "SpawnPoint",
            "--primitive", "cube",
            "--components", "Rigidbody,BoxCollider"
        ]);

        Assert.Equal(CommandKind.SceneAddObject, parsed.Kind);
        Assert.Equal("Assets/Scenes/Test.unity", parsed.ScenePath);
        Assert.Equal("/Root[0]", parsed.SceneParent);
        Assert.Equal("3,0,0", parsed.ScenePosition);
        Assert.Equal("SpawnPoint", parsed.SceneObjectName);
        Assert.Equal("Cube", parsed.ScenePrimitive);
        Assert.Equal("Rigidbody,BoxCollider", parsed.SceneComponents);
        Assert.Equal(ProtocolConstants.DefaultLiveTimeoutMs, parsed.TimeoutMs);
    }

    [Fact]
    public void Parse_SceneAddObject_RejectsUnsupportedPrimitive()
    {
        var ex = Assert.Throws<CliUsageException>(() =>
            CliArgumentParser.Parse([
                "scene", "add-object",
                "--path", "Assets/Scenes/Test.unity",
                "--name", "SpawnPoint",
                "--primitive", "pyramid"
            ]));

        Assert.Contains("--primitive", ex.Message);
    }

    [Fact]
    public void Parse_SceneAddObject_ToEnvelope_UsesScenePatchAndIncludesShortcutMetadata()
    {
        var parsed = CliArgumentParser.Parse([
            "scene", "add-object",
            "--path", "Assets/Scenes/Test.unity",
            "--parent", "/Environment[0]",
            "--position", "3,0,0",
            "--name", "SpawnPoint",
            "--primitive", "Cube"
        ]);

        var envelope = parsed.ToEnvelope();
        Assert.Equal(ProtocolConstants.CommandScenePatch, envelope.command);
        Assert.Contains("add-gameobject", envelope.argumentsJson);
        Assert.Contains("SpawnPoint", envelope.argumentsJson);
        var args = ProtocolJson.Deserialize<ScenePatchArgs>(envelope.argumentsJson);
        Assert.NotNull(args);
        Assert.Equal("/Environment[0]", args.parent);
        Assert.Equal("Cube", args.primitive);
        Assert.NotNull(args.position);
        Assert.Equal(3f, args.position!.x);
        Assert.Equal(0f, args.position.y);
        Assert.Equal(0f, args.position.z);
        Assert.Contains("\"parent\":\"/Environment[0]\"", args.specJson);
        Assert.Contains("\"primitive\":\"Cube\"", args.specJson);
        Assert.Contains("\"localPosition\":{\"x\":3", args.specJson);
    }

    [Fact]
    public void Parse_SceneAddObject_ToEnvelope_DefaultsParentToRoot()
    {
        var parsed = CliArgumentParser.Parse([
            "scene", "add-object",
            "--path", "Assets/Scenes/Test.unity",
            "--name", "SpawnPoint"
        ]);

        var args = ProtocolJson.Deserialize<ScenePatchArgs>(parsed.ToEnvelope().argumentsJson);
        Assert.NotNull(args);
        Assert.Equal("/", args.parent);
        Assert.Contains("\"parent\":\"/\"", args.specJson);
    }

    [Fact]
    public void Parse_SceneSetTransform_RequiresNode()
    {
        var ex = Assert.Throws<CliUsageException>(() =>
            CliArgumentParser.Parse(["scene", "set-transform", "--position", "1,2,3"]));

        Assert.Contains("--node", ex.Message);
    }

    [Fact]
    public void Parse_SceneSetTransform_RequiresAtLeastOneTransformProp()
    {
        var ex = Assert.Throws<CliUsageException>(() =>
            CliArgumentParser.Parse(["scene", "set-transform", "--node", "/Root[0]"]));

        Assert.Contains("--position", ex.Message);
    }

    [Fact]
    public void Parse_SceneSetTransform_AcceptsNodeAndTransforms()
    {
        var parsed = CliArgumentParser.Parse([
            "scene", "set-transform",
            "--node", "/Root[0]/Player[0]",
            "--position", "1,2,3",
            "--rotation", "0,45,0",
            "--scale", "2,2,2"
        ]);

        Assert.Equal(CommandKind.SceneSetTransform, parsed.Kind);
        Assert.Equal("/Root[0]/Player[0]", parsed.SceneTarget);
        Assert.Equal(ProtocolConstants.DefaultLiveTimeoutMs, parsed.TimeoutMs);
    }

    [Fact]
    public void Parse_SceneSetTransform_ToEnvelope_UsesDedicatedArgs()
    {
        var parsed = CliArgumentParser.Parse([
            "scene", "set-transform",
            "--node", "/Root[0]/Player[0]",
            "--position", "1,2,3",
            "--rotation", "0,45,0",
            "--scale", "2,2,2"
        ]);

        var envelope = parsed.ToEnvelope();
        Assert.Equal(ProtocolConstants.CommandSceneSetTransform, envelope.command);

        var args = ProtocolJson.Deserialize<SceneSetTransformArgs>(envelope.argumentsJson);
        Assert.NotNull(args);
        Assert.Equal("/Root[0]/Player[0]", args.node);
        Assert.NotNull(args.position);
        Assert.Equal(1f, args.position!.x);
        Assert.Equal(2f, args.position.y);
        Assert.Equal(3f, args.position.z);
        Assert.NotNull(args.rotation);
        Assert.Equal(45f, args.rotation!.y);
        Assert.NotNull(args.scale);
        Assert.Equal(2f, args.scale!.x);
    }

    [Fact]
    public void Parse_SceneSetTransform_ToEnvelope_OmitsUnsetVectors()
    {
        var parsed = CliArgumentParser.Parse([
            "scene", "set-transform",
            "--node", "/Root[0]",
            "--rotation", "0,30,0",
        ]);

        string argumentsJson = parsed.ToEnvelope().argumentsJson;
        using var document = JsonDocument.Parse(argumentsJson);
        Assert.False(document.RootElement.TryGetProperty("position", out _));
        Assert.False(document.RootElement.TryGetProperty("scale", out _));
        Assert.True(document.RootElement.TryGetProperty("rotation", out JsonElement rotation));
        Assert.Equal(0f, rotation.GetProperty("x").GetSingle());
        Assert.Equal(30f, rotation.GetProperty("y").GetSingle());
        Assert.Equal(0f, rotation.GetProperty("z").GetSingle());
        Assert.Contains("\"node\":\"/Root[0]\"", argumentsJson);
    }

    [Fact]
    public void Parse_SceneAddComponent_RequiresType()
    {
        var ex = Assert.Throws<CliUsageException>(() =>
            CliArgumentParser.Parse(["scene", "add-component", "--path", "Assets/Scenes/Test.unity", "--node", "/Root[0]"]));

        Assert.Contains("--type", ex.Message);
    }

    [Fact]
    public void Parse_SceneAddComponent_RejectsLegacyTargetFlag()
    {
        var ex = Assert.Throws<CliUsageException>(() =>
            CliArgumentParser.Parse([
                "scene", "add-component",
                "--path", "Assets/Scenes/Test.unity",
                "--target", "/Root[0]",
                "--type", "BoxCollider"
            ]));

        Assert.Contains("Unsupported option: --target", ex.Message);
    }

    [Fact]
    public void Parse_SceneAddComponent_AcceptsTypeAndValues()
    {
        var parsed = CliArgumentParser.Parse([
            "scene", "add-component",
            "--path", "Assets/Scenes/Test.unity",
            "--node", "/Root[0]",
            "--type", "BoxCollider",
            "--values", "{\"center\":{\"x\":0,\"y\":0.5,\"z\":0}}"
        ]);

        Assert.Equal(CommandKind.SceneAddComponent, parsed.Kind);
        Assert.Equal(ProtocolConstants.DefaultLiveTimeoutMs, parsed.TimeoutMs);
        var envelope = parsed.ToEnvelope();
        Assert.Contains("add-component", envelope.argumentsJson);
        Assert.Contains("BoxCollider", envelope.argumentsJson);
        var args = ProtocolJson.Deserialize<ScenePatchArgs>(envelope.argumentsJson);
        Assert.NotNull(args);
        Assert.Contains("\"component\":", args.specJson);
    }

    [Fact]
    public void Parse_SceneRemoveComponent_RequiresForce()
    {
        var ex = Assert.Throws<CliUsageException>(() =>
            CliArgumentParser.Parse([
                "scene", "remove-component",
                "--path", "Assets/Scenes/Test.unity",
                "--node", "/Root[0]",
                "--type", "BoxCollider"
            ]));

        Assert.Contains("--force", ex.Message);
    }

    [Fact]
    public void Parse_SceneRemoveComponent_AcceptsForce()
    {
        var parsed = CliArgumentParser.Parse([
            "scene", "remove-component",
            "--path", "Assets/Scenes/Test.unity",
            "--node", "/Root[0]",
            "--type", "BoxCollider",
            "--force"
        ]);

        Assert.Equal(CommandKind.SceneRemoveComponent, parsed.Kind);
        Assert.True(parsed.Force);
        Assert.Equal(ProtocolConstants.DefaultLiveTimeoutMs, parsed.TimeoutMs);
        var envelope = parsed.ToEnvelope();
        Assert.Contains("remove-component", envelope.argumentsJson);
        var args = ProtocolJson.Deserialize<ScenePatchArgs>(envelope.argumentsJson);
        Assert.NotNull(args);
        Assert.Contains("componentType", args.specJson);
    }

    [Fact]
    public void Parse_SceneRemoveComponent_AcceptsIndex()
    {
        var parsed = CliArgumentParser.Parse([
            "scene", "remove-component",
            "--path", "Assets/Scenes/Test.unity",
            "--node", "/Player[0]",
            "--type", "BoxCollider",
            "--index", "1",
            "--force"
        ]);

        Assert.Equal(CommandKind.SceneRemoveComponent, parsed.Kind);
        Assert.Equal(1, parsed.SceneComponentIndex);
        var envelope = parsed.ToEnvelope();
        var args = ProtocolJson.Deserialize<ScenePatchArgs>(envelope.argumentsJson);
        Assert.NotNull(args);
        Assert.Contains("\"componentIndex\":1", args.specJson);
    }

    [Fact]
    public void Parse_SceneListComponents_RequiresNode()
    {
        var ex = Assert.Throws<CliUsageException>(() =>
            CliArgumentParser.Parse(["scene", "list-components"]));

        Assert.Contains("--node", ex.Message);
    }

    [Fact]
    public void Parse_SceneListComponents_AcceptsNode()
    {
        var parsed = CliArgumentParser.Parse([
            "scene", "list-components",
            "--node", "/Player[0]"
        ]);

        Assert.Equal(CommandKind.SceneListComponents, parsed.Kind);
        var envelope = parsed.ToEnvelope();
        Assert.Equal(ProtocolConstants.CommandSceneListComponents, envelope.command);
        Assert.Contains("/Player[0]", envelope.argumentsJson);
    }

    [Fact]
    public void Parse_PrefabListComponents_RequiresNode()
    {
        var ex = Assert.Throws<CliUsageException>(() =>
            CliArgumentParser.Parse(["prefab", "list-components", "--path", "Assets/Prefabs/P.prefab"]));

        Assert.Contains("--node", ex.Message);
    }

    [Fact]
    public void Parse_PrefabListComponents_AcceptsPathAndNode()
    {
        var parsed = CliArgumentParser.Parse([
            "prefab", "list-components",
            "--path", "Assets/Prefabs/Player.prefab",
            "--node", "/Root[0]"
        ]);

        Assert.Equal(CommandKind.PrefabListComponents, parsed.Kind);
        var envelope = parsed.ToEnvelope();
        Assert.Equal(ProtocolConstants.CommandPrefabListComponents, envelope.command);
        Assert.Contains("/Root[0]", envelope.argumentsJson);
        Assert.Contains("Player.prefab", envelope.argumentsJson);
    }

    [Fact]
    public void Parse_PrefabAddComponent_RequiresType()
    {
        var ex = Assert.Throws<CliUsageException>(() =>
            CliArgumentParser.Parse([
                "prefab", "add-component",
                "--path", "Assets/Prefabs/P.prefab",
                "--node", "/Root[0]"
            ]));

        Assert.Contains("--type", ex.Message);
    }

    [Fact]
    public void Parse_PrefabAddComponent_RejectsLegacyTargetFlag()
    {
        var ex = Assert.Throws<CliUsageException>(() =>
            CliArgumentParser.Parse([
                "prefab", "add-component",
                "--path", "Assets/Prefabs/P.prefab",
                "--target", "/Root[0]",
                "--type", "BoxCollider"
            ]));

        Assert.Contains("Unsupported option: --target", ex.Message);
    }

    [Fact]
    public void Parse_PrefabAddComponent_AcceptsTypeAndValues()
    {
        var parsed = CliArgumentParser.Parse([
            "prefab", "add-component",
            "--path", "Assets/Prefabs/Player.prefab",
            "--node", "/Root[0]",
            "--type", "Rigidbody",
            "--values", "{\"mass\":5}"
        ]);

        Assert.Equal(CommandKind.PrefabAddComponent, parsed.Kind);
        var envelope = parsed.ToEnvelope();
        Assert.Equal(ProtocolConstants.CommandPrefabPatch, envelope.command);
        Assert.Contains("add-component", envelope.argumentsJson);
        Assert.Contains("Rigidbody", envelope.argumentsJson);
        var args = ProtocolJson.Deserialize<PrefabPatchArgs>(envelope.argumentsJson);
        Assert.NotNull(args);
        Assert.Contains("\"component\":", args.specJson);
    }

    [Fact]
    public void Parse_PrefabRemoveComponent_RequiresForce()
    {
        var ex = Assert.Throws<CliUsageException>(() =>
            CliArgumentParser.Parse([
                "prefab", "remove-component",
                "--path", "Assets/Prefabs/P.prefab",
                "--node", "/Root[0]",
                "--type", "BoxCollider"
            ]));

        Assert.Contains("--force", ex.Message);
    }

    [Fact]
    public void Parse_PrefabRemoveComponent_AcceptsForce()
    {
        var parsed = CliArgumentParser.Parse([
            "prefab", "remove-component",
            "--path", "Assets/Prefabs/Player.prefab",
            "--node", "/Root[0]",
            "--type", "BoxCollider",
            "--force"
        ]);

        Assert.Equal(CommandKind.PrefabRemoveComponent, parsed.Kind);
        var envelope = parsed.ToEnvelope();
        Assert.Equal(ProtocolConstants.CommandPrefabPatch, envelope.command);
        Assert.Contains("remove-component", envelope.argumentsJson);
        var args = ProtocolJson.Deserialize<PrefabPatchArgs>(envelope.argumentsJson);
        Assert.NotNull(args);
        Assert.Contains("componentType", args.specJson);
    }

    [Fact]
    public void Parse_PrefabRemoveComponent_AcceptsIndex()
    {
        var parsed = CliArgumentParser.Parse([
            "prefab", "remove-component",
            "--path", "Assets/Prefabs/Player.prefab",
            "--node", "/Player[0]",
            "--type", "BoxCollider",
            "--index", "1",
            "--force"
        ]);

        Assert.Equal(CommandKind.PrefabRemoveComponent, parsed.Kind);
        Assert.Equal(1, parsed.SceneComponentIndex);
        var envelope = parsed.ToEnvelope();
        var args = ProtocolJson.Deserialize<PrefabPatchArgs>(envelope.argumentsJson);
        Assert.NotNull(args);
        Assert.Contains("\"componentIndex\":1", args.specJson);
    }

    [Fact]
    public void Parse_SceneAssignMaterial_RequiresNode()
    {
        var ex = Assert.Throws<CliUsageException>(() =>
            CliArgumentParser.Parse([
                "scene", "assign-material",
                "--material", "Assets/Materials/Test.mat"
            ]));

        Assert.Contains("--node", ex.Message);
    }

    [Fact]
    public void Parse_SceneAssignMaterial_AcceptsNodeAndMaterial()
    {
        var parsed = CliArgumentParser.Parse([
            "scene", "assign-material",
            "--node", "/Cube[0]",
            "--material", "Assets/Materials/MyMat.mat"
        ]);

        Assert.Equal(CommandKind.SceneAssignMaterial, parsed.Kind);
        Assert.Equal("/Cube[0]", parsed.SceneTarget);
        Assert.Equal("Assets/Materials/MyMat.mat", parsed.MaterialPath);
        Assert.Equal(ProtocolConstants.DefaultLiveTimeoutMs, parsed.TimeoutMs);
    }

    [Fact]
    public void Parse_SceneAssignMaterial_ToEnvelope_UsesDedicatedArgs()
    {
        var parsed = CliArgumentParser.Parse([
            "scene", "assign-material",
            "--node", "/Cube[0]",
            "--material", "Assets/Materials/MyMat.mat"
        ]);

        var envelope = parsed.ToEnvelope();
        Assert.Equal(ProtocolConstants.CommandSceneAssignMaterial, envelope.command);

        var args = ProtocolJson.Deserialize<SceneAssignMaterialArgs>(envelope.argumentsJson);
        Assert.NotNull(args);
        Assert.Equal("/Cube[0]", args.node);
        Assert.Equal("Assets/Materials/MyMat.mat", args.material);
    }

    [Fact]
    public void Parse_SceneSetTransform_InvalidPositionFormatThrows()
    {
        var parsed = CliArgumentParser.Parse([
            "scene", "set-transform",
            "--node", "/Root[0]",
            "--position", "1,2"
        ]);

        var ex = Assert.Throws<CliUsageException>(() => parsed.ToEnvelope());
        Assert.Contains("x,y,z", ex.Message);
    }

    [Fact]
    public void Parse_Screenshot_DefaultsToGameView()
    {
        var parsed = CliArgumentParser.Parse(["screenshot"]);

        Assert.Equal(CommandKind.Screenshot, parsed.Kind);
        Assert.Equal(ParsedCommand.DefaultScreenshotView, parsed.ScreenshotView);
        Assert.Null(parsed.ScreenshotCamera);
    }

    [Fact]
    public void ToEnvelope_Screenshot_DefaultsToGameViewWithoutExplicitView()
    {
        var parsed = new ParsedCommand(CommandKind.Screenshot);

        var args = ProtocolJson.Deserialize<ScreenshotArgs>(parsed.ToEnvelope().argumentsJson);

        Assert.NotNull(args);
        Assert.Equal(ParsedCommand.DefaultScreenshotView, args!.view);
        Assert.Null(args.camera);
    }

    [Fact]
    public void Parse_Screenshot_AcceptsGameView()
    {
        var parsed = CliArgumentParser.Parse(["screenshot", "--view", "game"]);

        Assert.Equal(CommandKind.Screenshot, parsed.Kind);
        Assert.Equal("game", parsed.ScreenshotView);
        Assert.Equal(ProtocolConstants.DefaultLiveTimeoutMs, parsed.TimeoutMs);
    }

    [Fact]
    public void Parse_Screenshot_AcceptsSceneViewWithSize()
    {
        var parsed = CliArgumentParser.Parse([
            "screenshot", "--view", "scene",
            "--width", "1920", "--height", "1080",
            "--path", "/tmp/test.png"
        ]);

        Assert.Equal(CommandKind.Screenshot, parsed.Kind);
        Assert.Equal("scene", parsed.ScreenshotView);
        Assert.Equal(1920, parsed.ScreenshotWidth);
        Assert.Equal(1080, parsed.ScreenshotHeight);
        Assert.Equal("/tmp/test.png", parsed.ScreenshotPath);
    }

    [Fact]
    public void Parse_Screenshot_AcceptsLightweightOptions()
    {
        var parsed = CliArgumentParser.Parse([
            "screenshot",
            "--format", "jpg",
            "--quality", "70",
            "--max-width", "1024"
        ]);

        Assert.Equal(CommandKind.Screenshot, parsed.Kind);
        Assert.Equal("jpg", parsed.ScreenshotFormat);
        Assert.Equal(70, parsed.ScreenshotQuality);
        Assert.Equal(1024, parsed.ScreenshotMaxWidth);
    }

    [Fact]
    public void ToEnvelope_Screenshot_IncludesLightweightOptions()
    {
        var parsed = CliArgumentParser.Parse([
            "screenshot",
            "--format", "jpeg",
            "--quality", "70",
            "--max-width", "1024"
        ]);

        using var document = JsonDocument.Parse(parsed.ToEnvelope().argumentsJson);
        var arguments = document.RootElement;

        Assert.Equal("jpeg", arguments.GetProperty("format").GetString());
        Assert.Equal(70, arguments.GetProperty("quality").GetInt32());
        Assert.Equal(1024, arguments.GetProperty("maxWidth").GetInt32());
    }

    [Fact]
    public void ToEnvelope_Screenshot_WithoutMaxWidth_LeavesTheDefaultToTheBridge()
    {
        var parsed = CliArgumentParser.Parse(["screenshot"]);

        using var document = JsonDocument.Parse(parsed.ToEnvelope().argumentsJson);

        Assert.Equal(0, document.RootElement.GetProperty("maxWidth").GetInt32());
    }

    [Fact]
    public void ToEnvelope_Screenshot_WithZeroMaxWidth_SendsTheUncappedSentinel()
    {
        // 0 on the wire already means "unspecified", so an explicit opt-out has to travel as
        // something the bridge cannot read as silence.
        var parsed = CliArgumentParser.Parse(["screenshot", "--max-width", "0"]);

        using var document = JsonDocument.Parse(parsed.ToEnvelope().argumentsJson);

        Assert.Equal(0, parsed.ScreenshotMaxWidth);
        Assert.Equal(
            ScreenshotDefaults.MaxWidthUncapped,
            document.RootElement.GetProperty("maxWidth").GetInt32());
    }

    [Fact]
    public void Parse_Screenshot_RejectsNegativeMaxWidth()
    {
        Assert.Throws<CliUsageException>(() => CliArgumentParser.Parse([
            "screenshot", "--max-width", "-1"
        ]));
    }

    [Fact]
    public void Parse_Screenshot_AcceptsCameraName()
    {
        var parsed = CliArgumentParser.Parse([
            "screenshot", "--camera", "Main Camera",
            "--path", "/tmp/render.png"
        ]);

        Assert.Equal(CommandKind.Screenshot, parsed.Kind);
        Assert.Null(parsed.ScreenshotView);
        Assert.Equal("Main Camera", parsed.ScreenshotCamera);
        Assert.Equal("/tmp/render.png", parsed.ScreenshotPath);
    }

    [Fact]
    public void Parse_Screenshot_RejectsInvalidView()
    {
        var ex = Assert.Throws<CliUsageException>(() =>
            CliArgumentParser.Parse(["screenshot", "--view", "invalid"]));

        Assert.Contains("game", ex.Message);
        Assert.Contains("scene", ex.Message);
    }

    [Fact]
    public void Parse_Screenshot_RejectsBothViewAndCamera()
    {
        var ex = Assert.Throws<CliUsageException>(() =>
            CliArgumentParser.Parse(["screenshot", "--view", "game", "--camera", "Main Camera"]));

        Assert.Contains("--view", ex.Message);
        Assert.Contains("--camera", ex.Message);
    }

    [Fact]
    public void Parse_Screenshot_RejectsInvalidFormat()
    {
        var ex = Assert.Throws<CliUsageException>(() =>
            CliArgumentParser.Parse(["screenshot", "--format", "webp"]));

        Assert.Contains("--format", ex.Message);
        Assert.Contains("png", ex.Message);
        Assert.Contains("jpg", ex.Message);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("101")]
    public void Parse_Screenshot_RejectsInvalidQuality(string quality)
    {
        var ex = Assert.Throws<CliUsageException>(() =>
            CliArgumentParser.Parse(["screenshot", "--quality", quality]));

        Assert.Contains("--quality", ex.Message);
    }

    [Fact]
    public void Parse_Screenshot_UsesLiveTimeout()
    {
        var parsed = CliArgumentParser.Parse(["screenshot"]);

        Assert.Equal(ProtocolConstants.DefaultLiveTimeoutMs, parsed.TimeoutMs);
    }

    [Fact]
    public void Parse_ExecuteMenu_AcceptsPath()
    {
        var parsed = CliArgumentParser.Parse(["execute-menu", "--path", "Assets/Refresh"]);

        Assert.Equal(CommandKind.ExecuteMenu, parsed.Kind);
        Assert.Equal("Assets/Refresh", parsed.MenuPath);
        Assert.False(parsed.MenuList);
        Assert.Equal(ProtocolConstants.DefaultLiveTimeoutMs, parsed.TimeoutMs);
    }

    [Fact]
    public void Parse_ExecuteMenu_AcceptsListPrefix()
    {
        var parsed = CliArgumentParser.Parse(["execute-menu", "--list", "GameObject"]);
        var args = ProtocolJson.Deserialize<ExecuteMenuArgs>(parsed.ToEnvelope().argumentsJson);

        Assert.Equal(CommandKind.ExecuteMenu, parsed.Kind);
        Assert.True(parsed.MenuList);
        Assert.Equal("GameObject", parsed.MenuListPrefix);
        Assert.NotNull(args);
        Assert.True(args.list);
        Assert.Equal("GameObject", args.prefix);
        Assert.Equal(string.Empty, args.path);
    }

    [Fact]
    public void Parse_ExecuteMenu_RejectsPathAndListTogether()
    {
        var ex = Assert.Throws<CliUsageException>(() =>
            CliArgumentParser.Parse(["execute-menu", "--path", "Assets/Refresh", "--list", "GameObject"]));

        Assert.Contains("--path", ex.Message);
        Assert.Contains("--list", ex.Message);
    }

    [Fact]
    public void Parse_ExecuteMenu_RequiresPathOrList()
    {
        var ex = Assert.Throws<CliUsageException>(() =>
            CliArgumentParser.Parse(["execute-menu"]));

        Assert.Contains("--path", ex.Message);
        Assert.Contains("--list", ex.Message);
    }

    [Fact]
    public void Parse_Execute_RequiresCodeOrFile()
    {
        var ex = Assert.Throws<CliUsageException>(() =>
            CliArgumentParser.Parse(["execute", "--force"]));

        Assert.Contains("--code", ex.Message);
        Assert.Contains("--file", ex.Message);
    }

    [Fact]
    public void Parse_Execute_RequiresForce()
    {
        var ex = Assert.Throws<CliUsageException>(() =>
            CliArgumentParser.Parse(["execute", "--code", "Debug.Log(42);"]));

        Assert.Contains("--force", ex.Message);
    }

    [Fact]
    public void Parse_Execute_AcceptsCodeAndForce()
    {
        var parsed = CliArgumentParser.Parse([
            "execute",
            "--code", "Debug.Log(42);",
            "--force"
        ]);

        Assert.Equal(CommandKind.ExecuteCode, parsed.Kind);
        Assert.Equal("Debug.Log(42);", parsed.ExecuteCodeSnippet);
        Assert.True(parsed.Force);
        Assert.Equal(ProtocolConstants.DefaultLiveTimeoutMs, parsed.TimeoutMs);
    }

    [Fact]
    public void Parse_Execute_AcceptsFileAndForce()
    {
        var parsed = CliArgumentParser.Parse([
            "execute",
            "--file", "./scripts/setup.cs",
            "--force"
        ]);

        Assert.Equal(CommandKind.ExecuteCode, parsed.Kind);
        Assert.Equal("./scripts/setup.cs", parsed.ExecuteCodeFile);
        Assert.True(parsed.Force);
    }

    [Fact]
    public void Parse_Execute_AcceptsArgs()
    {
        var parsed = CliArgumentParser.Parse([
            "execute",
            "--code", "Debug.Log(42);",
            "--args", "{\"k\":\"v\",\"n\":1}",
            "--force"
        ]);

        Assert.Equal(CommandKind.ExecuteCode, parsed.Kind);
        Assert.Equal("{\"k\":\"v\",\"n\":1}", parsed.ExecuteCodeArgsJson);
    }

    [Fact]
    public void Execute_WithTimeoutSeconds_StoresOnParsedCommand()
    {
        var parsed = CliArgumentParser.Parse([
            "execute",
            "--code", "void M(){}",
            "--force",
            "--timeout", "60"
        ]);

        Assert.Equal(60, parsed.ExecuteCodeTimeoutSeconds);
    }

    [Fact]
    public void Execute_WithoutTimeout_LeavesNull()
    {
        var parsed = CliArgumentParser.Parse([
            "execute",
            "--code", "void M(){}",
            "--force"
        ]);

        Assert.Null(parsed.ExecuteCodeTimeoutSeconds);
    }

    [Fact]
    public void Execute_TimeoutZeroOrNegative_Throws()
    {
        Assert.Throws<CliUsageException>(() => CliArgumentParser.Parse([
            "execute",
            "--code", "x",
            "--force",
            "--timeout", "0"
        ]));
        Assert.Throws<CliUsageException>(() => CliArgumentParser.Parse([
            "execute",
            "--code", "x",
            "--force",
            "--timeout", "-5"
        ]));
    }

    [Fact]
    public void Execute_TimeoutAboveMax_Throws()
    {
        Assert.Throws<CliUsageException>(() => CliArgumentParser.Parse([
            "execute",
            "--code", "x",
            "--force",
            "--timeout", "601"
        ]));
    }

    [Fact]
    public void Execute_TimeoutNonInteger_Throws()
    {
        Assert.Throws<CliUsageException>(() => CliArgumentParser.Parse([
            "execute",
            "--code", "x",
            "--force",
            "--timeout", "abc"
        ]));
    }

    [Fact]
    public void Execute_TimeoutSeconds_TranslatedToTimeoutMsInArgs()
    {
        var parsed = CliArgumentParser.Parse([
            "execute",
            "--code", "void M(){}",
            "--force",
            "--timeout", "45"
        ]);
        var envelope = parsed.ToEnvelope();

        using var doc = JsonDocument.Parse(envelope.argumentsJson);
        Assert.True(doc.RootElement.TryGetProperty("timeoutMs", out var timeoutMs));
        Assert.Equal(45000, timeoutMs.GetInt32());
    }

    [Fact]
    public void Execute_NoTimeout_TimeoutMsZeroInArgs()
    {
        var parsed = CliArgumentParser.Parse([
            "execute",
            "--code", "void M(){}",
            "--force"
        ]);
        var envelope = parsed.ToEnvelope();

        using var doc = JsonDocument.Parse(envelope.argumentsJson);
        bool hasField = doc.RootElement.TryGetProperty("timeoutMs", out var timeoutMs);
        Assert.True(!hasField || timeoutMs.GetInt32() == 0);
    }

    [Fact]
    public void Parse_Execute_ToEnvelope_IncludesArgsJson()
    {
        var parsed = CliArgumentParser.Parse([
            "execute",
            "--code", "Debug.Log(42);",
            "--args", "{\"k\":\"v\",\"n\":1}",
            "--force"
        ]);

        var envelope = parsed.ToEnvelope();
        var args = ProtocolJson.Deserialize<ExecuteCodeArgs>(envelope.argumentsJson);

        Assert.Equal(ProtocolConstants.CommandExecuteCode, envelope.command);
        Assert.Equal("Debug.Log(42);", args.code);
        Assert.Equal("{\"k\":\"v\",\"n\":1}", args.argumentsJson);
    }

    [Fact]
    public void Parse_Execute_AcceptsCodeAndArgs()
    {
        var parsed = CliArgumentParser.Parse([
            "execute",
            "--code", "Debug.Log(__pucArgsJson);",
            "--args", "{\"message\":\"hello\"}",
            "--force"
        ]);

        Assert.Equal("Debug.Log(__pucArgsJson);", parsed.ExecuteCodeSnippet);
        Assert.Equal("{\"message\":\"hello\"}", parsed.ExecuteCodeArgsJson);
        Assert.True(parsed.Force);
    }

    [Fact]
    public void Parse_Execute_AcceptsFileAndArgs()
    {
        using var temp = new TempDirectory();
        string codePath = Path.Combine(temp.Path, "setup.cs");
        File.WriteAllText(codePath, "Debug.Log(__pucArgsJson);");

        var parsed = CliArgumentParser.Parse([
            "execute",
            "--file", codePath,
            "--args", "{\"scene\":\"Main\"}",
            "--force"
        ]);

        var envelope = parsed.ToEnvelope();
        var args = ProtocolJson.Deserialize<ExecuteCodeArgs>(envelope.argumentsJson);

        Assert.Equal(codePath, parsed.ExecuteCodeFile);
        Assert.Equal("{\"scene\":\"Main\"}", parsed.ExecuteCodeArgsJson);
        Assert.Equal("Debug.Log(__pucArgsJson);", args.code);
        Assert.Equal("{\"scene\":\"Main\"}", args.argumentsJson);
    }

    [Fact]
    public void Parse_Execute_FileKeepsTopOfFileUsings()
    {
        using var temp = new TempDirectory();
        string codePath = Path.Combine(temp.Path, "setup.cs");
        const string source = "using UnityEngine;\r\n\r\nvar btn = GameObject.Find(\"ButtonName\");\r\n";
        File.WriteAllText(codePath, source);

        ParsedCommand parsed = CliArgumentParser.Parse([
            "execute",
            "--file", codePath,
            "--force"
        ]);

        var args = ProtocolJson.Deserialize<ExecuteCodeArgs>(parsed.ToEnvelope().argumentsJson);

        Assert.NotNull(args);
        Assert.StartsWith("using UnityEngine;", args!.code);
        string stripped = UsingDirectiveUtility.StripUsingDirectives(args.code, out string[] usings);
        Assert.Equal(["using UnityEngine;"], usings);
        Assert.Contains("GameObject.Find", stripped);
        Assert.DoesNotContain("using ", stripped);
    }

    [Fact]
    public void Parse_Execute_ArgsWithoutValue_Throws()
    {
        var ex = Assert.Throws<CliUsageException>(() =>
            CliArgumentParser.Parse([
                "execute",
                "--code", "Debug.Log(42);",
                "--args",
                "--force"
            ]));

        Assert.Equal("--args requires a JSON value.", ex.Message);
    }

    [Fact]
    public void Parse_Execute_ArgsMissingValueAtEnd_Throws()
    {
        var ex = Assert.Throws<CliUsageException>(() =>
            CliArgumentParser.Parse([
                "execute",
                "--code", "Debug.Log(42);",
                "--args"
            ]));

        Assert.Equal("--args requires a JSON value.", ex.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Parse_Execute_ArgsBlankValue_Throws(string argsJson)
    {
        var ex = Assert.Throws<CliUsageException>(() =>
            CliArgumentParser.Parse([
                "execute",
                "--code", "Debug.Log(42);",
                "--args", argsJson,
                "--force"
            ]));

        Assert.Equal("--args requires a JSON value.", ex.Message);
    }

    [Fact]
    public void Parse_Execute_RejectsInvalidArgsJson()
    {
        var ex = Assert.Throws<CliUsageException>(() =>
            CliArgumentParser.Parse([
                "execute",
                "--code", "Debug.Log(42);",
                "--args", "{",
                "--force"
            ]));

        Assert.Contains("--args", ex.Message);
        Assert.Contains("JSON", ex.Message);
    }

    [Fact]
    public void Parse_Execute_RejectsBothCodeAndFile()
    {
        var ex = Assert.Throws<CliUsageException>(() =>
            CliArgumentParser.Parse([
                "execute",
                "--code", "Debug.Log(42);",
                "--file", "./scripts/setup.cs",
                "--force"
            ]));

        Assert.Contains("--code", ex.Message);
        Assert.Contains("--file", ex.Message);
    }

    [Fact]
    public void Parse_Execute_UsesLiveTimeout()
    {
        var parsed = CliArgumentParser.Parse([
            "execute", "--code", "var x = 1;", "--force"
        ]);

        Assert.Equal(ProtocolConstants.DefaultLiveTimeoutMs, parsed.TimeoutMs);
    }

    [Fact]
    public void Parse_Custom_RequiresCommandName()
    {
        var ex = Assert.Throws<CliUsageException>(() =>
            CliArgumentParser.Parse(["custom"]));

        Assert.Contains("command name", ex.Message);
    }

    [Fact]
    public void Parse_Custom_AcceptsCommandName()
    {
        var parsed = CliArgumentParser.Parse(["custom", "terrain-setup"]);

        Assert.Equal(CommandKind.Custom, parsed.Kind);
        Assert.Equal("terrain-setup", parsed.CustomCommandName);
        Assert.Equal(ProtocolConstants.DefaultLiveTimeoutMs, parsed.TimeoutMs);
    }

    [Fact]
    public void Parse_Custom_AcceptsJson()
    {
        var parsed = CliArgumentParser.Parse([
            "custom", "terrain-setup",
            "--json", "{\"size\": 1000}"
        ]);

        Assert.Equal(CommandKind.Custom, parsed.Kind);
        Assert.Equal("terrain-setup", parsed.CustomCommandName);
        Assert.Equal("{\"size\": 1000}", parsed.CustomArgsJson);
    }

    [Fact]
    public void Parse_Custom_ToEnvelope_UsesCustomProtocol()
    {
        var parsed = CliArgumentParser.Parse([
            "custom", "terrain-setup",
            "--json", "{\"size\": 1000}"
        ]);

        var envelope = parsed.ToEnvelope();
        Assert.Equal(ProtocolConstants.CommandCustom, envelope.command);
        Assert.Contains("terrain-setup", envelope.argumentsJson);
    }

    [Fact]
    public void Parse_Custom_UsesLiveTimeout()
    {
        var parsed = CliArgumentParser.Parse(["custom", "my-tool"]);

        Assert.Equal(ProtocolConstants.DefaultLiveTimeoutMs, parsed.TimeoutMs);
    }

    [Fact]
    public void Parse_Custom_PreservesGlobalJsonOutputAndCommandJsonArgs()
    {
        var parsed = CliArgumentParser.Parse([
            "--json",
            "custom", "terrain-setup",
            "--json", "{\"size\": 1000}"
        ]);

        Assert.True(parsed.JsonOutput);
        Assert.Equal(CommandKind.Custom, parsed.Kind);
        Assert.Equal("{\"size\": 1000}", parsed.CustomArgsJson);
    }

    public static TheoryData<string[], CommandKind> PackageCommandsWithLongDefaultTimeout()
    {
        var commands = new TheoryData<string[], CommandKind>();
        commands.Add(["package", "list"], CommandKind.PackageList);
        commands.Add(["package", "add", "--name", "com.unity.textmeshpro"], CommandKind.PackageAdd);
        commands.Add(["package", "remove", "--name", "com.unity.textmeshpro", "--force"], CommandKind.PackageRemove);
        commands.Add(["package", "search", "--query", "physics"], CommandKind.PackageSearch);
        return commands;
    }

    [Theory]
    [MemberData(nameof(PackageCommandsWithLongDefaultTimeout))]
    public void Parse_PackageCommands_UsePackageLiveTimeout(string[] args, CommandKind expectedKind)
    {
        var parsed = CliArgumentParser.Parse(args);

        Assert.Equal(expectedKind, parsed.Kind);
        Assert.Equal(ProtocolConstants.DefaultPackageLiveTimeoutMs, parsed.TimeoutMs);
    }

    [Fact]
    public void Parse_PackageCommand_TimeoutMsOverrideWins()
    {
        var parsed = CliArgumentParser.Parse(["package", "list", "--timeout-ms", "42"]);

        Assert.Equal(CommandKind.PackageList, parsed.Kind);
        Assert.Equal(42, parsed.TimeoutMs);
    }

    [Fact]
    public void Parse_PackageList_DefaultsToUnfilteredUnlimitedArgs()
    {
        var parsed = CliArgumentParser.Parse(["package", "list"]);

        var args = ProtocolJson.Deserialize<PackageListArgs>(parsed.ToEnvelope().argumentsJson);

        Assert.NotNull(args);
        Assert.Equal(CommandKind.PackageList, parsed.Kind);
        Assert.Equal(string.Empty, args!.filter);
        Assert.Equal(ProtocolConstants.DefaultPackageListLimit, args.limit);
    }

    [Fact]
    public void Parse_PackageList_AcceptsFilter()
    {
        var parsed = CliArgumentParser.Parse(["package", "list", "--filter", "textmesh"]);

        var args = ProtocolJson.Deserialize<PackageListArgs>(parsed.ToEnvelope().argumentsJson);

        Assert.NotNull(args);
        Assert.Equal("textmesh", parsed.PackageFilter);
        Assert.Equal("textmesh", args!.filter);
        Assert.Equal(ProtocolConstants.DefaultPackageListLimit, args.limit);
    }

    [Fact]
    public void Parse_PackageList_AcceptsLimit()
    {
        var parsed = CliArgumentParser.Parse(["package", "list", "--limit", "3"]);

        var args = ProtocolJson.Deserialize<PackageListArgs>(parsed.ToEnvelope().argumentsJson);

        Assert.NotNull(args);
        Assert.Equal(3, parsed.PackageLimit);
        Assert.Equal(string.Empty, args!.filter);
        Assert.Equal(3, args.limit);
    }

    [Fact]
    public void Parse_PackageList_AcceptsFilterAndLimit()
    {
        var parsed = CliArgumentParser.Parse(["package", "list", "--filter", "unity", "--limit", "2"]);

        var args = ProtocolJson.Deserialize<PackageListArgs>(parsed.ToEnvelope().argumentsJson);

        Assert.NotNull(args);
        Assert.Equal("unity", args!.filter);
        Assert.Equal(2, args.limit);
    }

    [Fact]
    public void Parse_PackageList_LimitZeroSerializesUnlimitedSentinel()
    {
        var parsed = CliArgumentParser.Parse(["package", "list", "--limit", "0"]);

        var args = ProtocolJson.Deserialize<PackageListArgs>(parsed.ToEnvelope().argumentsJson);

        Assert.NotNull(args);
        Assert.Equal(0, parsed.PackageLimit);
        Assert.Equal(0, args!.limit);
    }

    [Fact]
    public void Parse_NonPackageCommand_KeepsDefaultLiveTimeout()
    {
        var parsed = CliArgumentParser.Parse(["asset", "find", "--name", "Player"]);

        Assert.Equal(CommandKind.AssetFind, parsed.Kind);
        Assert.Equal(ProtocolConstants.DefaultLiveTimeoutMs, parsed.TimeoutMs);
    }

    [Fact]
    public void Parse_PackageAdd_RequiresName()
    {
        var ex = Assert.Throws<CliUsageException>(() =>
            CliArgumentParser.Parse(["package", "add"]));

        Assert.Contains("--name", ex.Message);
    }

    [Fact]
    public void Parse_PackageAdd_AcceptsNameAndVersion()
    {
        var parsed = CliArgumentParser.Parse([
            "package", "add",
            "--name", "com.unity.textmeshpro",
            "--version", "3.0.6"
        ]);

        Assert.Equal(CommandKind.PackageAdd, parsed.Kind);
        Assert.Equal("com.unity.textmeshpro", parsed.PackageName);
        Assert.Equal("3.0.6", parsed.PackageVersion);
    }

    [Fact]
    public void Parse_PackageRemove_RequiresForce()
    {
        var ex = Assert.Throws<CliUsageException>(() =>
            CliArgumentParser.Parse(["package", "remove", "--name", "com.unity.textmeshpro"]));

        Assert.Contains("--force", ex.Message);
    }

    [Fact]
    public void Parse_PackageRemove_AcceptsNameAndForce()
    {
        var parsed = CliArgumentParser.Parse([
            "package", "remove",
            "--name", "com.unity.textmeshpro",
            "--force"
        ]);

        Assert.Equal(CommandKind.PackageRemove, parsed.Kind);
        Assert.Equal("com.unity.textmeshpro", parsed.PackageName);
        Assert.True(parsed.Force);
    }

    [Fact]
    public void Parse_PackageSearch_RequiresQuery()
    {
        var ex = Assert.Throws<CliUsageException>(() =>
            CliArgumentParser.Parse(["package", "search"]));

        Assert.Contains("--query", ex.Message);
    }

    [Fact]
    public void Parse_PackageSearch_AcceptsQuery()
    {
        var parsed = CliArgumentParser.Parse([
            "package", "search",
            "--query", "physics"
        ]);

        Assert.Equal(CommandKind.PackageSearch, parsed.Kind);
        Assert.Equal("physics", parsed.PackageQuery);
    }

    [Fact]
    public void Parse_Package_UnknownSubcommandThrows()
    {
        var ex = Assert.Throws<CliUsageException>(() =>
            CliArgumentParser.Parse(["package", "upgrade"]));

        Assert.Contains("upgrade", ex.Message);
    }

    [Fact]
    public void Parse_EditorLaunch_Defaults()
    {
        var parsed = CliArgumentParser.Parse(["editor", "launch"]);

        Assert.Equal(CommandKind.EditorLaunch, parsed.Kind);
        Assert.False(parsed.EditorGui);
        Assert.False(parsed.EditorNoGraphics);
        Assert.False(parsed.EditorNoWait);
        Assert.Null(parsed.EditorWaitTimeoutSeconds);
        Assert.Null(parsed.EditorPathOverride);
    }

    [Fact]
    public void Parse_EditorLaunch_AllOptions()
    {
        var parsed = CliArgumentParser.Parse([
            "editor", "launch", "--gui", "--nographics", "--no-wait",
            "--timeout", "120", "--editor-path", "/tmp/Unity",
        ]);

        Assert.Equal(CommandKind.EditorLaunch, parsed.Kind);
        Assert.True(parsed.EditorGui);
        Assert.True(parsed.EditorNoGraphics);
        Assert.True(parsed.EditorNoWait);
        Assert.Equal(120, parsed.EditorWaitTimeoutSeconds);
        Assert.Equal("/tmp/Unity", parsed.EditorPathOverride);
    }

    [Fact]
    public void Parse_EditorStop_ForceAndTimeout()
    {
        var parsed = CliArgumentParser.Parse(["editor", "stop", "--force", "--timeout", "10"]);

        Assert.Equal(CommandKind.EditorStop, parsed.Kind);
        Assert.True(parsed.Force);
        Assert.Equal(10, parsed.EditorWaitTimeoutSeconds);
    }

    [Fact]
    public void Parse_EditorUnknownSubcommand_Throws()
    {
        Assert.Throws<CliUsageException>(() => CliArgumentParser.Parse(["editor", "restart"]));
    }
}
