#nullable enable

namespace UnityCliBridge.Bridge.Editor
{
    /// <summary>
    /// Plugin install commands for Window &gt; Unity CLI Manager.
    /// Marketplace name is <c>.claude-plugin/marketplace.json</c> and
    /// <c>.agents/plugins/marketplace.json</c> <c>name</c> (plugin entry <c>name</c> matches).
    /// Plugin name is <c>plugins/unity-cli-bridge/plugin.json</c> <c>name</c>.
    /// The repository slug is the README install target <c>yhc509/unity-cli-bridge</c>.
    /// Grok's install line passes that slug to <c>grok plugin install</c>. That command
    /// takes a GitHub shorthand, a git URL, or a local path.
    /// </summary>
    internal static class SkillPluginCommands
    {
        internal const string RepositorySlug = "yhc509/unity-cli-bridge";
        internal const string MarketplaceName = "unity-cli-bridge";
        internal const string PluginName = "unity-cli-bridge";

        internal const string ClaudeCodeGroup = "Claude Code";
        internal const string ClaudeCodeTerminalGroup = "Claude Code (terminal)";
        internal const string CodexGroup = "Codex";
        internal const string GrokGroup = "Grok Build";

        internal static SkillPluginCommandLine[] All()
        {
            return new SkillPluginCommandLine[]
            {
                new SkillPluginCommandLine(ClaudeCodeGroup, ClaudeCodeMarketplaceAdd()),
                new SkillPluginCommandLine(ClaudeCodeGroup, ClaudeCodeInstall()),
                new SkillPluginCommandLine(ClaudeCodeTerminalGroup, ClaudeCodeShellMarketplaceAdd()),
                new SkillPluginCommandLine(ClaudeCodeTerminalGroup, ClaudeCodeShellInstall()),
                new SkillPluginCommandLine(CodexGroup, CodexMarketplaceAdd()),
                new SkillPluginCommandLine(CodexGroup, CodexInstall()),
                new SkillPluginCommandLine(GrokGroup, GrokMarketplaceAdd()),
                new SkillPluginCommandLine(GrokGroup, GrokInstall()),
            };
        }

        internal static string ClaudeCodeMarketplaceAdd()
        {
            return "/plugin marketplace add " + RepositorySlug;
        }

        internal static string ClaudeCodeInstall()
        {
            return "/plugin install " + PluginName + "@" + MarketplaceName;
        }

        internal static string ClaudeCodeShellMarketplaceAdd()
        {
            return "claude plugin marketplace add " + RepositorySlug;
        }

        internal static string ClaudeCodeShellInstall()
        {
            return "claude plugin install " + PluginName + "@" + MarketplaceName + " --scope user";
        }

        internal static string CodexMarketplaceAdd()
        {
            return "codex plugin marketplace add " + RepositorySlug;
        }

        internal static string CodexInstall()
        {
            return "codex plugin add " + PluginName + "@" + MarketplaceName;
        }

        internal static string GrokMarketplaceAdd()
        {
            return "grok plugin marketplace add " + RepositorySlug;
        }

        internal static string GrokInstall()
        {
            return "grok plugin install " + RepositorySlug + " --trust";
        }
    }

    internal readonly struct SkillPluginCommandLine
    {
        internal SkillPluginCommandLine(string group, string command)
        {
            Group = group;
            Command = command;
        }

        internal string Group { get; }
        internal string Command { get; }
    }
}
