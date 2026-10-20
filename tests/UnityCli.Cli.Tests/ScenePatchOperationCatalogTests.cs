using UnityCli.Cli.Services;
using UnityCli.Protocol;

namespace UnityCli.Cli.Tests;

public sealed class ScenePatchOperationCatalogTests
{
    [Fact]
    public void Suggest_SetComponentValues_MeansModifyComponent()
    {
        Assert.Equal("modify-component", ScenePatchOperationCatalog.Suggest("set-component-values"));
    }

    [Theory]
    [InlineData("modify-componet", "modify-component")]
    [InlineData("add-compnent", "add-component")]
    [InlineData("set-node", "modify-gameobject")]
    [InlineData("delete-component", "remove-component")]
    public void Suggest_TyposAndPrefabNames_MapToSceneOps(string operation, string expected)
    {
        Assert.Equal(expected, ScenePatchOperationCatalog.Suggest(operation));
    }

    [Fact]
    public void BuildUnsupportedMessage_ListsEveryOperationAndSuggestion()
    {
        string message = ScenePatchOperationCatalog.BuildUnsupportedMessage("set-component-values");

        foreach (string operation in ScenePatchOperationCatalog.Supported)
        {
            Assert.Contains("`" + operation + "`", message);
        }

        Assert.Contains("Did you mean `modify-component`?", message);
    }

    [Fact]
    public void ScenePatchHelp_ListsSupportedOperations()
    {
        string? help = CliCommandMetadata.BuildTopicHelpText("scene patch");

        Assert.NotNull(help);
        foreach (string operation in ScenePatchOperationCatalog.Supported)
        {
            Assert.Contains(operation, help);
        }

        Assert.Contains("set-component-values", help);
        Assert.Contains("modify-component", help);
    }

    [Theory]
    [InlineData(".fbx", true)]
    [InlineData(".FBX", true)]
    [InlineData(".obj", true)]
    [InlineData(".dae", true)]
    [InlineData(".blend", true)]
    [InlineData(".prefab", true)]
    [InlineData(".mat", false)]
    [InlineData(".png", false)]
    [InlineData(".unity", false)]
    public void PrefabInspectPaths_AllowsPrefabAndModelExtensions(string extension, bool inspectable)
    {
        Assert.Equal(inspectable, PrefabInspectPaths.IsInspectableExtension(extension));
    }

    [Fact]
    public void UnsupportedExtensionMessage_NamesReadOnlyModelInspect()
    {
        string message = PrefabInspectPaths.UnsupportedExtensionMessage("Assets/Textures/Icon.png");

        Assert.Contains(".fbx", message);
        Assert.Contains("read-only", message);
        Assert.Contains("prefab inspect", message);
        Assert.Contains("Assets/Textures/Icon.png", message);
    }

    [Fact]
    public void PrefabInspectHelp_MentionsModelAssets()
    {
        string? help = CliCommandMetadata.BuildTopicHelpText("prefab inspect");

        Assert.NotNull(help);
        Assert.Contains(".fbx", help);
        Assert.Contains("read-only", help);
    }
}
