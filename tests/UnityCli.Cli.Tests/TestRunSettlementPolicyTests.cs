using UnityCli.DocGen;
using UnityCli.Protocol;

namespace UnityCli.Cli.Tests;

public sealed class TestRunSettlementPolicyTests
{
    private static TestRunSettlementObservation Observe(
        bool probeAvailable,
        bool frameworkRunActive,
        bool playing,
        int updatesSinceArmed,
        int quiescentUpdates,
        TimeSpan? elapsed = null)
    {
        return new TestRunSettlementObservation(
            probeAvailable,
            frameworkRunActive,
            playing,
            updatesSinceArmed,
            quiescentUpdates,
            elapsed ?? TimeSpan.Zero);
    }

    [Fact]
    public void ActiveFrameworkJob_IsNotPublished()
    {
        Assert.False(TestRunSettlementPolicy.ShouldPublish(
            Observe(probeAvailable: true, frameworkRunActive: true, playing: false, updatesSinceArmed: 5, quiescentUpdates: 5)));
    }

    [Fact]
    public void IdleFrameworkJob_PublishesOnTheNextEditorUpdate()
    {
        Assert.False(TestRunSettlementPolicy.ShouldPublish(
            Observe(probeAvailable: true, frameworkRunActive: false, playing: false, updatesSinceArmed: 0, quiescentUpdates: 0)));
        Assert.True(TestRunSettlementPolicy.ShouldPublish(
            Observe(probeAvailable: true, frameworkRunActive: false, playing: false, updatesSinceArmed: 1, quiescentUpdates: 1)));
    }

    [Fact]
    public void PlayMode_BlocksPublishUntilTheEditorLeavesPlayMode()
    {
        var playing = Observe(
            probeAvailable: true,
            frameworkRunActive: false,
            playing: true,
            updatesSinceArmed: 10,
            quiescentUpdates: 0,
            elapsed: TimeSpan.FromSeconds(5));

        Assert.False(TestRunSettlementPolicy.ShouldPublish(playing));
    }

    [Fact]
    public void MissingProbe_WaitsForQuiescentUpdatesAfterPlayModeExits()
    {
        Assert.False(TestRunSettlementPolicy.ShouldPublish(
            Observe(
                probeAvailable: false,
                frameworkRunActive: false,
                playing: false,
                updatesSinceArmed: TestRunSettlementPolicy.FallbackQuiescentUpdates,
                quiescentUpdates: TestRunSettlementPolicy.FallbackQuiescentUpdates - 1)));
        Assert.True(TestRunSettlementPolicy.ShouldPublish(
            Observe(
                probeAvailable: false,
                frameworkRunActive: false,
                playing: false,
                updatesSinceArmed: TestRunSettlementPolicy.FallbackQuiescentUpdates,
                quiescentUpdates: TestRunSettlementPolicy.FallbackQuiescentUpdates)));
    }

    [Fact]
    public void ForcePublish_ReleasesAStuckRun()
    {
        Assert.True(TestRunSettlementPolicy.ShouldPublish(
            Observe(
                probeAvailable: true,
                frameworkRunActive: true,
                playing: true,
                updatesSinceArmed: 1,
                quiescentUpdates: 0,
                elapsed: TestRunSettlementPolicy.ForcePublishAfter)));
    }

    [Theory]
    [InlineData("play", true)]
    [InlineData("pause", true)]
    [InlineData("stop", true)]
    [InlineData("scene-open", true)]
    [InlineData("scene-inspect", true)]
    [InlineData("scene-patch", true)]
    [InlineData("test-run", false)]
    [InlineData("test-results", false)]
    [InlineData("test-cancel", false)]
    [InlineData("status", false)]
    [InlineData("prefab-patch", false)]
    public void PlayAndSceneCommands_AreBlockedWhileATestRunIsActive(string command, bool blocked)
    {
        Assert.Equal(blocked, ProtocolHelpers.IsBlockedWhileTestRunActive(command));
    }

    [Fact]
    public void CompletedRun_StaysUnpublishedUntilSettlement()
    {
        string repoRoot = RepositoryPaths.FindRepoRoot(AppContext.BaseDirectory);
        string callbacks = File.ReadAllText(Path.Combine(
            repoRoot,
            "unity-package",
            "com.yhc509.unity-cli-bridge",
            "Editor",
            "TestRunnerCallbacks.cs"));
        string handler = File.ReadAllText(Path.Combine(
            repoRoot,
            "unity-package",
            "com.yhc509.unity-cli-bridge",
            "Editor",
            "TestCommandHandler.cs"));
        string host = File.ReadAllText(Path.Combine(
            repoRoot,
            "unity-package",
            "com.yhc509.unity-cli-bridge",
            "Editor",
            "BridgeHost.cs"));

        Assert.Contains("WritePendingSettlement", callbacks);
        Assert.Contains("ArmSettlement", callbacks);
        Assert.Contains("IsAnyRunActive", handler);
        Assert.Contains("IsRunActive", handler);
        Assert.Contains("ThrowIfEditorBusyWithTestRun", host);
    }
}
