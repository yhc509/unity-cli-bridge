using UnityCli.Protocol;

namespace UnityCli.Cli.Tests;

public sealed class ProtocolHelpersTests
{
    [Fact]
    public void GetSupportedCommands_IncludesSceneCommands()
    {
        string[] commands = ProtocolHelpers.GetSupportedCommands();

        Assert.Contains(ProtocolConstants.CommandExecuteCode, commands);
        Assert.Contains(ProtocolConstants.CommandCustom, commands);
        Assert.Contains(ProtocolConstants.CommandSceneOpen, commands);
        Assert.Contains(ProtocolConstants.CommandSceneInspect, commands);
        Assert.Contains(ProtocolConstants.CommandScenePatch, commands);
        Assert.Contains(ProtocolConstants.CommandSceneSetTransform, commands);
        Assert.Contains(ProtocolConstants.CommandSceneAssignMaterial, commands);
        Assert.Contains(ProtocolConstants.CommandSceneListComponents, commands);
    }

    [Fact]
    public void IsSceneCommand_RecognizesSceneSurface()
    {
        Assert.True(ProtocolHelpers.IsSceneCommand(ProtocolConstants.CommandSceneOpen));
        Assert.True(ProtocolHelpers.IsSceneCommand(ProtocolConstants.CommandSceneInspect));
        Assert.True(ProtocolHelpers.IsSceneCommand(ProtocolConstants.CommandScenePatch));
        Assert.True(ProtocolHelpers.IsSceneCommand(ProtocolConstants.CommandSceneSetTransform));
        Assert.True(ProtocolHelpers.IsSceneCommand(ProtocolConstants.CommandSceneAssignMaterial));
        Assert.True(ProtocolHelpers.IsSceneCommand(ProtocolConstants.CommandSceneListComponents));
        Assert.False(ProtocolHelpers.IsSceneCommand(ProtocolConstants.CommandPrefabPatch));
    }

    [Fact]
    public void BuiltInAssetCreateCatalog_NormalizesAliases()
    {
        Assert.True(BuiltInAssetCreateCatalog.TryNormalizeTypeId("controller", out string animatorController));
        Assert.True(BuiltInAssetCreateCatalog.TryNormalizeTypeId("rendertexture", out string renderTexture));
        Assert.True(BuiltInAssetCreateCatalog.TryNormalizeTypeId("scriptableobject", out string scriptableObject));

        Assert.Equal("animator-controller", animatorController);
        Assert.Equal("render-texture", renderTexture);
        Assert.Equal("scriptable-object", scriptableObject);
    }

    [Fact]
    public void BuiltInAssetCreateCatalog_DescriptorsIncludeSceneAndPrefab()
    {
        AssetCreateTypeDescriptor[] descriptors = BuiltInAssetCreateCatalog.GetDescriptors();

        Assert.Contains(descriptors, descriptor => descriptor.typeId == "scene");
        Assert.Contains(descriptors, descriptor => descriptor.typeId == "prefab");
    }

    [Fact]
    public void TestFullNameMatchesFilter_MatchesCaseInsensitiveSubstring()
    {
        const string fullName = "UnityCliBridge.Sample.EditMode.Tests.SmokeTests.Smoke_Arithmetic_Passes";

        Assert.True(ProtocolHelpers.TestFullNameMatchesFilter(fullName, "smoke"));
        Assert.True(ProtocolHelpers.TestFullNameMatchesFilter(fullName, "SMOKE"));
        Assert.True(ProtocolHelpers.TestFullNameMatchesFilter(fullName, "Smoke_Arithmetic_Passes"));
    }

    [Fact]
    public void TestFullNameMatchesFilter_RejectsMissingSubstring()
    {
        const string fullName = "UnityCliBridge.Sample.EditMode.Tests.SmokeTests.Smoke_Arithmetic_Passes";

        Assert.False(ProtocolHelpers.TestFullNameMatchesFilter(fullName, "NonExistent"));
        Assert.False(ProtocolHelpers.TestFullNameMatchesFilter(fullName, string.Empty));
        Assert.False(ProtocolHelpers.TestFullNameMatchesFilter(string.Empty, "Smoke"));
    }

    [Theory]
    [InlineData("Game.Assembly Some.Ns.Outer/InnerBase", "Game.Assembly Some.Ns.Outer+InnerBase")]
    [InlineData("Some.Ns.Outer/InnerBase, Game.Assembly", "Some.Ns.Outer+InnerBase, Game.Assembly")]
    [InlineData("Some.Ns.PlainType, Game.Assembly", "Some.Ns.PlainType, Game.Assembly")]
    [InlineData("Game/Assembly Some.Ns.Outer/InnerBase", "Game/Assembly Some.Ns.Outer+InnerBase")]
    [InlineData("", "")]
    [InlineData("/", "+")]
    public void NormalizeManagedReferenceTypeNameForClrLookup_NormalizesOnlyTypePart(string input, string expected)
    {
        Assert.Equal(expected, ProtocolHelpers.NormalizeManagedReferenceTypeNameForClrLookup(input));
    }

    [Theory]
    [InlineData("Completed", false)]
    [InlineData("Running", false)]
    [InlineData("STARTED", false)]
    [InlineData("TimedOut", true)]
    [InlineData("Cancelled", true)]
    [InlineData("Failed", true)]
    public void IsTestRunResultStatusError_MapsTerminalStatus(string status, bool expected)
    {
        Assert.Equal(expected, ProtocolHelpers.IsTestRunResultStatusError(status));
    }

    [Fact]
    public void IsTestCommand_IncludesTestCancel()
    {
        Assert.True(ProtocolHelpers.IsTestCommand(ProtocolConstants.CommandTestCancel));
    }

    [Fact]
    public void IsDeferredTestCommand_ExcludesTestCancel()
    {
        // test cancel must resolve synchronously so it can release a stuck run
        // lock without waiting behind the deferred (test list/run) queue.
        Assert.False(ProtocolHelpers.IsDeferredTestCommand(ProtocolConstants.CommandTestCancel));
    }

    [Fact]
    public void TestCancel_IsAllowedWhileBusy()
    {
        // The command that releases a stuck test-run lock must itself bypass
        // BridgeHost's busy gate, or it can never run when it is needed most.
        Assert.True(ProtocolHelpers.IsCommandAllowedWhileBusy(ProtocolConstants.CommandTestCancel));

        CliCommandDescriptor descriptor = CliCommandCatalog.FindByCommand("test cancel")
            ?? throw new InvalidOperationException("Could not find the test cancel descriptor.");
        Assert.True(descriptor.IsAllowedWhileBusy);
        Assert.Equal(ForceRule.None, descriptor.ForceRule);
        Assert.Equal(ProtocolConstants.CommandTestCancel, descriptor.ProtocolCommand);
    }

    [Fact]
    public void GetTestRunResultErrorCode_MapsKnownStatuses()
    {
        Assert.Equal(
            ProtocolConstants.ErrorTestTimeout,
            ProtocolHelpers.GetTestRunResultErrorCode("TimedOut", Array.Empty<string>()));
        Assert.Equal(
            ProtocolConstants.ErrorTestCancelled,
            ProtocolHelpers.GetTestRunResultErrorCode("Cancelled", Array.Empty<string>()));
        Assert.Equal(
            ProtocolConstants.ErrorTestRunFailed,
            ProtocolHelpers.GetTestRunResultErrorCode("Failed", Array.Empty<string>()));
        Assert.Equal(
            ProtocolConstants.ErrorTestInterrupted,
            ProtocolHelpers.GetTestRunResultErrorCode(
                "Failed",
                [ProtocolConstants.TestRunInterruptedMessage]));
    }
}
