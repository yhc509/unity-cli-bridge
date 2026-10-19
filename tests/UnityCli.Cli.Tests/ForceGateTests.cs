using System.Reflection;
using System.Text.Json;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityCli.Cli.Models;
using UnityCli.Cli.Services;
using UnityCli.Protocol;

namespace UnityCli.Cli.Tests;

public sealed class ForceGateTests
{
    private const string DestructiveScenePatchJson = "{\"version\":1,\"operations\":[{\"op\":\"delete-gameobject\",\"target\":\"/Enemy[0]\"}]}";
    private const string NonDestructiveScenePatchJson = "{\"version\":1,\"operations\":[{\"op\":\"add-gameobject\",\"parent\":\"/\",\"node\":{\"name\":\"Cube\"}}]}";
    private const string DestructivePrefabPatchJson = "{\"version\":1,\"operations\":[{\"op\":\"remove-node\",\"target\":\"/Enemy[0]\"}]}";
    private const string NonDestructivePrefabPatchJson = "{\"version\":1,\"operations\":[{\"op\":\"add-child\",\"parent\":\"/Root[0]\",\"node\":{\"name\":\"Child\"}}]}";

    [Fact]
    public void ForceRequiredByCatalog_HonorsCatalogForceRules()
    {
        // "editor stop" has ForceRule.OnDestructiveOp but its destructiveness (unsaved
        // changes) is only known bridge-side: the bridge answers EDITOR_DIRTY and the CLI
        // deliberately never pre-requires --force (PatchContainsDestructiveOperation returns
        // false for EditorStop). This generic loop expects OnDestructiveOp commands to gate
        // CLI-side, so "editor stop" stays excluded; its bridge-gated contract is asserted
        // by ForceRequiredByCatalog_EditorStop_IsBridgeGated below.
        string[] bridgeGatedCommands = ["editor stop"];

        CliCommandDescriptor[] forceCommands = CliCommandCatalog.GetCommands()
            .Where(command => command.ForceRule != ForceRule.None)
            .Where(command => !bridgeGatedCommands.Contains(command.Command))
            .ToArray();
        Assert.NotEmpty(forceCommands);

        foreach (CliCommandDescriptor command in forceCommands)
        {
            ParsedCommand parsed = CreateForceRuleCommand(command.Command);
            bool expectedWithoutForce = command.ForceRule is ForceRule.Always or ForceRule.OnDestructiveOp;

            Assert.Equal(expectedWithoutForce, CliArgumentParser.ForceRequiredByCatalog(parsed));
            parsed.Force = true;
            Assert.False(CliArgumentParser.ForceRequiredByCatalog(parsed), command.Command);
        }
    }

    [Fact]
    public void ForceRequiredByCatalog_EditorStop_IsBridgeGated()
    {
        // The CLI cannot see unsaved editor state, so `editor stop` must parse and dispatch
        // without --force; the bridge is the sole gate (EDITOR_DIRTY). --force still has to
        // reach the wire so the bridge can discard unsaved changes when asked.
        ParsedCommand withoutForce = CliArgumentParser.Parse(["editor", "stop"]);
        Assert.False(CliArgumentParser.ForceRequiredByCatalog(withoutForce));

        ParsedCommand withForce = CliArgumentParser.Parse(["editor", "stop", "--force"]);
        Assert.False(CliArgumentParser.ForceRequiredByCatalog(withForce));

        CommandEnvelope envelope = withForce.ToEnvelope();
        using JsonDocument arguments = JsonDocument.Parse(envelope.argumentsJson);

        Assert.Equal(ProtocolConstants.CommandEditorQuit, envelope.command);
        Assert.True(arguments.RootElement.GetProperty("force").GetBoolean());
    }

    [Fact]
    public void ForceRequiredByCatalog_IgnoresNonDestructivePatchSpecs()
    {
        Assert.False(CliArgumentParser.ForceRequiredByCatalog(new ParsedCommand(CommandKind.ScenePatch)
        {
            SceneSpecJson = NonDestructiveScenePatchJson,
        }));
        Assert.False(CliArgumentParser.ForceRequiredByCatalog(new ParsedCommand(CommandKind.PrefabPatch)
        {
            PrefabSpecJson = NonDestructivePrefabPatchJson,
        }));
    }

    [Fact]
    public void GetCatalogDescriptor_ReturnsDescriptorForEveryCommandKindExceptHelp()
    {
        foreach (CommandKind kind in Enum.GetValues<CommandKind>().Where(kind => kind != CommandKind.Help))
        {
            CliCommandDescriptor descriptor = CliArgumentParser.GetCatalogDescriptor(kind);
            Assert.False(string.IsNullOrWhiteSpace(descriptor.Command), kind.ToString());
        }
    }

    [Theory]
    [MemberData(nameof(DestructiveCommandsWithoutForce))]
    public void Parse_DestructiveCommandWithoutForce_ThrowsUsage(string[] args)
    {
        var ex = Assert.Throws<CliUsageException>(() => CliArgumentParser.Parse(args));

        Assert.Contains("--force", ex.Message);
    }

    [Theory]
    [MemberData(nameof(ForceEnvelopeCases))]
    public void Parse_DestructiveCommandWithForce_SerializesForceTrue(string[] args, string protocolCommand)
    {
        var parsed = CliArgumentParser.Parse(args);

        CommandEnvelope envelope = parsed.ToEnvelope();
        using JsonDocument arguments = JsonDocument.Parse(envelope.argumentsJson);

        Assert.Equal(protocolCommand, envelope.command);
        Assert.True(arguments.RootElement.GetProperty("force").GetBoolean());
    }

    [Fact]
    public void Parse_RawWithForce_InjectsForceTrueIntoArguments()
    {
        var parsed = CliArgumentParser.Parse([
            "raw",
            "--json", "{\"command\":\"package-remove\",\"arguments\":{\"name\":\"com.example.demo\"}}",
            "--force"
        ]);

        CommandEnvelope envelope = parsed.ToEnvelope();
        using JsonDocument arguments = JsonDocument.Parse(envelope.argumentsJson);

        Assert.Equal(ProtocolConstants.CommandPackageRemove, envelope.command);
        Assert.Equal("com.example.demo", arguments.RootElement.GetProperty("name").GetString());
        Assert.True(arguments.RootElement.GetProperty("force").GetBoolean());
    }

    [Fact]
    public void Parse_RawWithForceAndPayloadForceTrue_NormalizesForceTrue()
    {
        var parsed = CliArgumentParser.Parse([
            "raw",
            "--json", "{\"command\":\"package-remove\",\"arguments\":{\"name\":\"com.example.demo\",\"force\":true,\"extra\":7}}",
            "--force"
        ]);

        CommandEnvelope envelope = parsed.ToEnvelope();
        using JsonDocument arguments = JsonDocument.Parse(envelope.argumentsJson);

        Assert.Equal(ProtocolConstants.CommandPackageRemove, envelope.command);
        Assert.True(arguments.RootElement.GetProperty("force").GetBoolean());
        Assert.Equal(7, arguments.RootElement.GetProperty("extra").GetInt32());
    }

    [Fact]
    public void Parse_RawWithForceAndPayloadForceFalse_ThrowsUsage()
    {
        var parsed = CliArgumentParser.Parse([
            "raw",
            "--json", "{\"command\":\"package-remove\",\"arguments\":{\"name\":\"com.example.demo\",\"force\":false}}",
            "--force"
        ]);

        var ex = Assert.Throws<CliUsageException>(() => parsed.ToEnvelope());

        Assert.Contains("force flag conflicts with raw payload", ex.Message);
    }

    [Fact]
    public void Parse_RawWithoutForce_PreservesPayloadForceValue()
    {
        var parsed = CliArgumentParser.Parse([
            "raw",
            "--json", "{\"command\":\"package-remove\",\"arguments\":{\"name\":\"com.example.demo\",\"force\":false,\"extra\":7}}"
        ]);

        CommandEnvelope envelope = parsed.ToEnvelope();
        using JsonDocument arguments = JsonDocument.Parse(envelope.argumentsJson);

        Assert.Equal(ProtocolConstants.CommandPackageRemove, envelope.command);
        Assert.Equal("com.example.demo", arguments.RootElement.GetProperty("name").GetString());
        Assert.False(arguments.RootElement.GetProperty("force").GetBoolean());
        Assert.Equal(7, arguments.RootElement.GetProperty("extra").GetInt32());
    }

    [Fact]
    public void Parse_RawWithoutForce_PreservesPayloadForceTrue()
    {
        var parsed = CliArgumentParser.Parse([
            "raw",
            "--json", "{\"command\":\"package-remove\",\"arguments\":{\"name\":\"com.example.demo\",\"force\":true}}"
        ]);

        CommandEnvelope envelope = parsed.ToEnvelope();
        using JsonDocument arguments = JsonDocument.Parse(envelope.argumentsJson);

        Assert.Equal(ProtocolConstants.CommandPackageRemove, envelope.command);
        Assert.True(arguments.RootElement.GetProperty("force").GetBoolean());
    }

    [Fact]
    public void Parse_RawWithoutForce_PreservesNonBooleanPayloadForce()
    {
        var parsed = CliArgumentParser.Parse([
            "raw",
            "--json", "{\"command\":\"package-remove\",\"arguments\":{\"name\":\"com.example.demo\",\"force\":\"true\"}}"
        ]);

        CommandEnvelope envelope = parsed.ToEnvelope();
        using JsonDocument arguments = JsonDocument.Parse(envelope.argumentsJson);

        Assert.Equal(ProtocolConstants.CommandPackageRemove, envelope.command);
        Assert.Equal("true", arguments.RootElement.GetProperty("force").GetString());
    }

    [Fact]
    public void Parse_RawWithForceAndNonBooleanPayloadForce_ThrowsUsage()
    {
        var parsed = CliArgumentParser.Parse([
            "raw",
            "--json", "{\"command\":\"package-remove\",\"arguments\":{\"name\":\"com.example.demo\",\"force\":\"true\"}}",
            "--force"
        ]);

        var ex = Assert.Throws<CliUsageException>(() => parsed.ToEnvelope());

        Assert.Contains("force flag conflicts with raw payload", ex.Message);
    }

    [Fact]
    public void Parse_RawDestructivePayloadWithoutForce_PassesCatalogValidation()
    {
        var parsed = CliArgumentParser.Parse([
            "raw",
            "--json", "{\"command\":\"package-remove\",\"arguments\":{\"name\":\"com.example.demo\"}}"
        ]);

        Assert.False(CliArgumentParser.ForceRequiredByCatalog(parsed));

        CommandEnvelope envelope = parsed.ToEnvelope();
        Assert.Equal(ProtocolConstants.CommandPackageRemove, envelope.command);
    }

    [Fact]
    public void ForceFields_RoundTripBetweenSystemTextJsonAndNewtonsoft()
    {
        Type[] forceTypes = typeof(CommandEnvelope).Assembly.GetTypes()
            .Where(type => type.Namespace == "UnityCli.Protocol")
            .Where(type => type.GetField("force", BindingFlags.Instance | BindingFlags.Public) != null)
            .OrderBy(type => type.Name, StringComparer.Ordinal)
            .ToArray();

        Assert.Contains(typeof(PackageRemoveArgs), forceTypes);

        foreach (Type type in forceTypes)
        {
            FieldInfo forceField = type.GetField("force", BindingFlags.Instance | BindingFlags.Public)!;
            object instance = Activator.CreateInstance(type)!;
            forceField.SetValue(instance, true);

            string systemTextJson = System.Text.Json.JsonSerializer.Serialize(instance, type, ProtocolJson.Default);
            Assert.Contains("\"force\":true", systemTextJson);
            Assert.DoesNotContain("\"Force\"", systemTextJson);

            object newtonsoftValue = JsonConvert.DeserializeObject(systemTextJson, type)!;
            Assert.True((bool)forceField.GetValue(newtonsoftValue)!);

            string newtonsoftJson = JsonConvert.SerializeObject(instance);
            var newtonsoftObject = JObject.Parse(newtonsoftJson);
            Assert.True(newtonsoftObject.Value<bool>("force"));
            Assert.Null(newtonsoftObject["Force"]);

            object? systemTextJsonValue = System.Text.Json.JsonSerializer.Deserialize(newtonsoftJson, type, ProtocolJson.Default);
            Assert.NotNull(systemTextJsonValue);
            Assert.True((bool)forceField.GetValue(systemTextJsonValue)!);
        }
    }

    public static TheoryData<string[]> DestructiveCommandsWithoutForce()
    {
        return new TheoryData<string[]>
        {
            new[] { "asset", "delete", "--path", "Assets/DeleteMe.asset" },
            new[] { "scene", "patch", "--path", "Assets/Scenes/Main.unity", "--spec-json", DestructiveScenePatchJson },
            new[] { "scene", "remove-component", "--path", "Assets/Scenes/Main.unity", "--node", "/Player[0]", "--type", "BoxCollider" },
            new[] { "prefab", "patch", "--path", "Assets/Prefabs/Enemy.prefab", "--spec-json", DestructivePrefabPatchJson },
            new[] { "prefab", "remove-component", "--path", "Assets/Prefabs/Enemy.prefab", "--node", "/Enemy[0]", "--type", "BoxCollider" },
            new[] { "execute", "--code", "Debug.Log(42);" },
            new[] { "package", "remove", "--name", "com.example.demo" },
        };
    }

    public static TheoryData<string[], string> ForceEnvelopeCases()
    {
        return new TheoryData<string[], string>
        {
            {
                new[] { "asset", "delete", "--path", "Assets/DeleteMe.asset", "--force" },
                ProtocolConstants.CommandAssetDelete
            },
            {
                new[] { "asset", "move", "--from", "Assets/Source.asset", "--to", "Assets/Existing.asset", "--force" },
                ProtocolConstants.CommandAssetMove
            },
            {
                new[] { "asset", "rename", "--path", "Assets/Source.asset", "--name", "Existing", "--force" },
                ProtocolConstants.CommandAssetRename
            },
            {
                new[] { "scene", "patch", "--path", "Assets/Scenes/Main.unity", "--spec-json", DestructiveScenePatchJson, "--force" },
                ProtocolConstants.CommandScenePatch
            },
            {
                new[] { "scene", "remove-component", "--path", "Assets/Scenes/Main.unity", "--node", "/Player[0]", "--type", "BoxCollider", "--force" },
                ProtocolConstants.CommandScenePatch
            },
            {
                new[] { "prefab", "patch", "--path", "Assets/Prefabs/Enemy.prefab", "--spec-json", DestructivePrefabPatchJson, "--force" },
                ProtocolConstants.CommandPrefabPatch
            },
            {
                new[] { "prefab", "remove-component", "--path", "Assets/Prefabs/Enemy.prefab", "--node", "/Enemy[0]", "--type", "BoxCollider", "--force" },
                ProtocolConstants.CommandPrefabPatch
            },
            {
                new[] { "execute", "--code", "Debug.Log(42);", "--force" },
                ProtocolConstants.CommandExecuteCode
            },
            {
                new[] { "package", "remove", "--name", "com.example.demo", "--force" },
                ProtocolConstants.CommandPackageRemove
            },
        };
    }

    private static ParsedCommand CreateForceRuleCommand(string command)
    {
        return command switch
        {
            "execute" => new ParsedCommand(CommandKind.ExecuteCode),
            "asset move" => new ParsedCommand(CommandKind.AssetMove),
            "asset rename" => new ParsedCommand(CommandKind.AssetRename),
            "asset delete" => new ParsedCommand(CommandKind.AssetDelete),
            "asset create" => new ParsedCommand(CommandKind.AssetCreate),
            "scene patch" => new ParsedCommand(CommandKind.ScenePatch) { SceneSpecJson = DestructiveScenePatchJson },
            "scene remove-component" => new ParsedCommand(CommandKind.SceneRemoveComponent),
            "prefab create" => new ParsedCommand(CommandKind.PrefabCreate),
            "prefab patch" => new ParsedCommand(CommandKind.PrefabPatch) { PrefabSpecJson = DestructivePrefabPatchJson },
            "prefab remove-component" => new ParsedCommand(CommandKind.PrefabRemoveComponent),
            "package remove" => new ParsedCommand(CommandKind.PackageRemove),
            _ => throw new InvalidOperationException("Force command has no test mapping: " + command),
        };
    }
}
