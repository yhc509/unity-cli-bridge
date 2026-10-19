using UnityCliBridge.Bridge.Editor;

namespace UnityCli.Cli.Tests;

public sealed class SkillPluginCommandTests
{
    [Fact]
    public void PluginCommands_MatchMarketplaceNamesAndReadmeInstallLines()
    {
        Assert.Equal("unity-cli-bridge", SkillPluginCommands.MarketplaceName);
        Assert.Equal("unity-cli-bridge", SkillPluginCommands.PluginName);
        Assert.Equal("yhc509/unity-cli-bridge", SkillPluginCommands.RepositorySlug);

        SkillPluginCommandLine[] lines = SkillPluginCommands.All();
        Assert.Equal(8, lines.Length);
        Assert.Equal(SkillPluginCommands.ClaudeCodeGroup, lines[0].Group);
        Assert.Equal("/plugin marketplace add yhc509/unity-cli-bridge", lines[0].Command);
        Assert.Equal("/plugin install unity-cli-bridge@unity-cli-bridge", lines[1].Command);
        Assert.Equal(SkillPluginCommands.ClaudeCodeTerminalGroup, lines[2].Group);
        Assert.Equal("claude plugin marketplace add yhc509/unity-cli-bridge", lines[2].Command);
        Assert.Equal("claude plugin install unity-cli-bridge@unity-cli-bridge --scope user", lines[3].Command);
        Assert.Equal(SkillPluginCommands.CodexGroup, lines[4].Group);
        Assert.Equal("codex plugin marketplace add yhc509/unity-cli-bridge", lines[4].Command);
        Assert.Equal("codex plugin add unity-cli-bridge@unity-cli-bridge", lines[5].Command);
        Assert.Equal(SkillPluginCommands.GrokGroup, lines[6].Group);
        Assert.Equal("grok plugin marketplace add yhc509/unity-cli-bridge", lines[6].Command);
        Assert.Equal("grok plugin install yhc509/unity-cli-bridge --trust", lines[7].Command);
        Assert.DoesNotContain("@", lines[7].Command);
    }
}
