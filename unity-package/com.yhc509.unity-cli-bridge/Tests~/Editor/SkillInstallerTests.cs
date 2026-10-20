#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace UnityCliBridge.Bridge.Editor.Tests
{
    public sealed class SkillInstallerTests
    {
        [TestCase("ClaudeCode", "Project", ".claude")]
        [TestCase("Codex", "Project", ".agents")]
        [TestCase("GrokBuild", "Project", ".grok")]
        [TestCase("ClaudeCode", "Global", ".claude")]
        [TestCase("Codex", "Global", ".agents")]
        [TestCase("GrokBuild", "Global", ".grok")]
        public void GetDestination_UsesExpectedTargetAndScopePath(string targetName, string scopeName, string toolDirectoryName)
        {
            var target = (SkillTarget)Enum.Parse(typeof(SkillTarget), targetName);
            var scope = (SkillScope)Enum.Parse(typeof(SkillScope), scopeName);
            string root = scope == SkillScope.Project
                ? GetProjectRoot()
                : GetHomeDirectory();
            string expected = Path.Combine(root, toolDirectoryName, "skills", "unity-cli-bridge");

            string actual = SkillInstaller.GetDestination(target, scope);

            Assert.AreEqual(expected, actual);
        }

        [Test]
        public void GetDestination_SupportsEverySkillTarget()
        {
            foreach (SkillTarget target in Enum.GetValues(typeof(SkillTarget)))
            {
                Assert.DoesNotThrow(() => SkillInstaller.GetDestination(target, SkillScope.Project));
                Assert.DoesNotThrow(() => SkillInstaller.GetDestination(target, SkillScope.Global));
            }
        }

        [Test]
        public void GetDestination_CodexUsesAgentsDirectoryNotLegacyCodex()
        {
            string destination = SkillInstaller.GetDestination(SkillTarget.Codex, GetProjectRoot());

            StringAssert.Contains(
                Path.Combine(".agents", "skills", "unity-cli-bridge"),
                destination);
            StringAssert.DoesNotContain(
                Path.Combine(".codex", "skills"),
                destination);
        }

        [Test]
        public void CodexTarget_IncludesAgentsMetadata()
        {
            Assert.IsTrue(SkillInstallPaths.IncludesAgentsMetadata(SkillTarget.Codex));
            Assert.IsFalse(SkillInstallPaths.IncludesAgentsMetadata(SkillTarget.ClaudeCode));
            Assert.IsFalse(SkillInstallPaths.IncludesAgentsMetadata(SkillTarget.GrokBuild));
        }

        [Test]
        public void TryFindGitRoot_WalksUpToDotGitDirectory()
        {
            string root = CreateTempDirectory();
            string nested = Path.Combine(root, "client", "Game");
            Directory.CreateDirectory(Path.Combine(root, ".git"));
            Directory.CreateDirectory(nested);
            try
            {
                Assert.IsTrue(SkillInstallPaths.PathsEqual(root, SkillInstallPaths.TryFindGitRoot(nested)));
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }

        [Test]
        public void TryFindGitRoot_AcceptsGitFileForWorktrees()
        {
            string root = CreateTempDirectory();
            string nested = Path.Combine(root, "Game");
            Directory.CreateDirectory(nested);
            File.WriteAllText(Path.Combine(root, ".git"), "gitdir: /somewhere/else\n");
            try
            {
                Assert.IsTrue(SkillInstallPaths.PathsEqual(root, SkillInstallPaths.TryFindGitRoot(nested)));
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }

        [Test]
        public void TryFindGitRoot_PrefersNearestGitDirectory()
        {
            string root = CreateTempDirectory();
            string child = Path.Combine(root, "child");
            string project = Path.Combine(child, "Game");
            Directory.CreateDirectory(Path.Combine(root, ".git"));
            Directory.CreateDirectory(Path.Combine(child, ".git"));
            Directory.CreateDirectory(project);
            try
            {
                Assert.IsTrue(SkillInstallPaths.PathsEqual(child, SkillInstallPaths.TryFindGitRoot(project)));
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }

        [Test]
        public void TryFindGitRoot_DoesNotSearchChildDirectories()
        {
            string root = CreateTempDirectory();
            Directory.CreateDirectory(Path.Combine(root, "child", ".git"));
            try
            {
                string? found = SkillInstallPaths.TryFindGitRoot(root);
                Assert.IsFalse(found != null && SkillInstallPaths.PathsEqual(Path.Combine(root, "child"), found));
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }

        [Test]
        public void TryFindGitRoot_ReturnsNullWhenStartIsEmpty()
        {
            Assert.IsNull(SkillInstallPaths.TryFindGitRoot(null));
            Assert.IsNull(SkillInstallPaths.TryFindGitRoot("   "));
        }

        [Test]
        public void TryResolveInstallRoot_UsesUnityProjectGitRootOrHome()
        {
            string unityProject = CreateTempDirectory();
            string gitRoot = CreateTempDirectory();
            string home = CreateTempDirectory();
            string chosen = CreateTempDirectory();
            try
            {
                AssertResolved(SkillInstallLocation.UnityProject, unityProject, gitRoot, null, home, unityProject);
                AssertResolved(SkillInstallLocation.GitRepository, unityProject, gitRoot, null, home, gitRoot);
                AssertResolved(SkillInstallLocation.GitRepository, unityProject, gitRoot, chosen, home, chosen);
                AssertResolved(SkillInstallLocation.UserHome, unityProject, gitRoot, null, home, home);

                bool resolved = SkillInstallPaths.TryResolveInstallRoot(
                    SkillInstallLocation.GitRepository,
                    unityProject,
                    null,
                    null,
                    home,
                    out _,
                    out string? error);
                Assert.IsFalse(resolved);
                string errorText = error ?? string.Empty;
                StringAssert.Contains(".git", errorText);
            }
            finally
            {
                Directory.Delete(unityProject, true);
                Directory.Delete(gitRoot, true);
                Directory.Delete(home, true);
                Directory.Delete(chosen, true);
            }
        }

        [Test]
        public void TryResolveInstallRoot_RejectsMissingChosenFolder()
        {
            string unityProject = CreateTempDirectory();
            try
            {
                bool resolved = SkillInstallPaths.TryResolveInstallRoot(
                    SkillInstallLocation.GitRepository,
                    unityProject,
                    unityProject,
                    Path.Combine(unityProject, "missing-folder"),
                    unityProject,
                    out _,
                    out string? error);

                Assert.IsFalse(resolved);
                string errorText = error ?? string.Empty;
                StringAssert.Contains("chosen folder", errorText);
            }
            finally
            {
                Directory.Delete(unityProject, true);
            }
        }

        [Test]
        public void GitRootMismatchWarning_OnlyWhenProjectLocationDiffersFromGitRoot()
        {
            string? warning = SkillInstallPaths.GitRootMismatchWarning(
                SkillInstallLocation.UnityProject,
                "/repo/client/Game",
                "/repo");
            Assert.IsNotNull(warning);
            string warningText = warning ?? string.Empty;
            StringAssert.Contains("/repo", warningText);
            StringAssert.Contains("will not see", warningText);

            Assert.IsNull(SkillInstallPaths.GitRootMismatchWarning(
                SkillInstallLocation.GitRepository,
                "/repo/client/Game",
                "/repo"));
            Assert.IsNull(SkillInstallPaths.GitRootMismatchWarning(
                SkillInstallLocation.UserHome,
                "/repo/client/Game",
                "/repo"));
            Assert.IsNull(SkillInstallPaths.GitRootMismatchWarning(
                SkillInstallLocation.UnityProject,
                "/repo/Game/",
                "/repo/Game"));
            Assert.IsNull(SkillInstallPaths.GitRootMismatchWarning(
                SkillInstallLocation.UnityProject,
                "/repo/Game",
                null));
        }

        [Test]
        public void DescribeDuplicateVisibility_WarnsWhenClaudeAndGrokShareARoot()
        {
            IReadOnlyList<string> messages = SkillInstallPaths.DescribeDuplicateVisibility(
                SkillTarget.GrokBuild,
                claudeAtRoot: true,
                agentsAtRoot: false,
                grokAtRoot: true,
                rootIsUserHome: false);

            Assert.AreEqual(1, messages.Count);
            StringAssert.Contains(".claude/skills", messages[0]);
            StringAssert.Contains("Grok", messages[0]);
        }

        [Test]
        public void DescribeDuplicateVisibility_WarnsBeforeInstallingGrokBesideClaude()
        {
            IReadOnlyList<string> messages = SkillInstallPaths.DescribeDuplicateVisibility(
                SkillTarget.GrokBuild,
                claudeAtRoot: true,
                agentsAtRoot: false,
                grokAtRoot: false,
                rootIsUserHome: false);

            Assert.AreEqual(1, messages.Count);
            StringAssert.Contains("already installed", messages[0]);
        }

        [Test]
        public void DescribeDuplicateVisibility_WarnsCursorWhenClaudeAndAgentsBothExist()
        {
            IReadOnlyList<string> messages = SkillInstallPaths.DescribeDuplicateVisibility(
                SkillTarget.Codex,
                claudeAtRoot: true,
                agentsAtRoot: true,
                grokAtRoot: false,
                rootIsUserHome: false);

            Assert.AreEqual(1, messages.Count);
            StringAssert.Contains("Cursor", messages[0]);
            StringAssert.Contains(".agents/skills", messages[0]);
        }

        [Test]
        public void DescribeDuplicateVisibility_IsQuietWhenOnlyTheSelectedTargetExists()
        {
            IReadOnlyList<string> messages = SkillInstallPaths.DescribeDuplicateVisibility(
                SkillTarget.ClaudeCode,
                claudeAtRoot: true,
                agentsAtRoot: false,
                grokAtRoot: false,
                rootIsUserHome: false);

            Assert.AreEqual(0, messages.Count);
        }

        [Test]
        public void DescribeDuplicateVisibility_WarnsWhenUserHomeHasGrokAndAgents()
        {
            IReadOnlyList<string> onHome = SkillInstallPaths.DescribeDuplicateVisibility(
                SkillTarget.Codex,
                claudeAtRoot: false,
                agentsAtRoot: true,
                grokAtRoot: true,
                rootIsUserHome: true);
            IReadOnlyList<string> inProject = SkillInstallPaths.DescribeDuplicateVisibility(
                SkillTarget.Codex,
                claudeAtRoot: false,
                agentsAtRoot: true,
                grokAtRoot: true,
                rootIsUserHome: false);

            Assert.AreEqual(1, onHome.Count);
            StringAssert.Contains("~/.agents/skills", onHome[0]);
            Assert.AreEqual(0, inProject.Count);
        }

        [Test]
        public void FindLegacyCodexInstalls_FindsSkillMarkdownAndRemoveDeletesOnlyThatCopy()
        {
            string root = CreateTempDirectory();
            string legacy = Path.Combine(root, ".codex", "skills", "unity-cli-bridge");
            string agents = Path.Combine(root, ".agents", "skills", "unity-cli-bridge");
            Directory.CreateDirectory(legacy);
            Directory.CreateDirectory(agents);
            File.WriteAllText(Path.Combine(legacy, "SKILL.md"), "# legacy\n");
            File.WriteAllText(Path.Combine(agents, "SKILL.md"), "# agents\n");
            try
            {
                IReadOnlyList<string> found = SkillInstaller.FindLegacyCodexInstalls(new[] { root, root + Path.DirectorySeparatorChar });
                Assert.AreEqual(1, found.Count);
                Assert.IsTrue(SkillInstallPaths.PathsEqual(legacy, found[0]));

                Assert.IsTrue(SkillInstaller.RemoveLegacyCodex(found[0]));
                Assert.IsFalse(Directory.Exists(legacy));
                Assert.IsFalse(Directory.Exists(Path.Combine(root, ".codex", "skills")));
                Assert.IsTrue(Directory.Exists(Path.Combine(root, ".codex")));
                Assert.IsTrue(File.Exists(Path.Combine(agents, "SKILL.md")));
                Assert.AreEqual(0, SkillInstaller.FindLegacyCodexInstalls(new[] { root }).Count);
            }
            finally
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, true);
                }
            }
        }

        [Test]
        public void Remove_DeletesEmptySkillsDirectoryAndKeepsAgentRoot()
        {
            string root = CreateTempDirectory();
            string skill = Path.Combine(root, ".agents", "skills", "unity-cli-bridge");
            string siblingSkill = Path.Combine(root, ".claude", "skills", "unity-cli-bridge");
            string siblingOther = Path.Combine(root, ".claude", "skills", "other-skill");
            Directory.CreateDirectory(skill);
            Directory.CreateDirectory(siblingOther);
            Directory.CreateDirectory(siblingSkill);
            File.WriteAllText(Path.Combine(skill, "SKILL.md"), "# skill\n");
            File.WriteAllText(Path.Combine(siblingSkill, "SKILL.md"), "# claude\n");
            try
            {
                Assert.IsTrue(SkillInstaller.Remove(SkillTarget.Codex, root));
                Assert.IsFalse(Directory.Exists(skill));
                Assert.IsFalse(Directory.Exists(Path.Combine(root, ".agents", "skills")));
                Assert.IsTrue(Directory.Exists(Path.Combine(root, ".agents")));
                Assert.IsTrue(Directory.Exists(root));

                Assert.IsTrue(SkillInstaller.Remove(SkillTarget.ClaudeCode, root));
                Assert.IsFalse(Directory.Exists(siblingSkill));
                Assert.IsTrue(Directory.Exists(siblingOther));
                Assert.IsTrue(Directory.Exists(Path.Combine(root, ".claude", "skills")));
                Assert.IsTrue(Directory.Exists(Path.Combine(root, ".claude")));
            }
            finally
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, true);
                }
            }
        }

        [Test]
        public void RemoveLegacyCodex_RefusesUnrelatedDirectory()
        {
            string directory = Path.Combine(Path.GetTempPath(), "unity-cli-bridge");
            Assert.Throws<InvalidOperationException>(() => SkillInstaller.RemoveLegacyCodex(directory));
        }

        [Test]
        public void PathsEqual_IgnoresTrailingSeparatorAndCase()
        {
            string root = CreateTempDirectory();
            try
            {
                Assert.IsTrue(SkillInstallPaths.PathsEqual(root, root + Path.DirectorySeparatorChar));
                Assert.IsTrue(SkillInstallPaths.PathsEqual(root.ToLowerInvariant(), root.ToUpperInvariant()));
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }

        [Test]
        public void ParsePackageVersionMarker_ReturnsVersion()
        {
            string? version = SkillInstaller.ParsePackageVersionMarker(
                "<!-- unity-cli-bridge-package-version: 0.4.0 -->\n# Unity CLI Bridge");

            Assert.AreEqual("0.4.0", version);
        }

        [Test]
        public void ParsePackageVersionMarker_ReturnsNullWhenMarkerMissing()
        {
            string? version = SkillInstaller.ParsePackageVersionMarker("# Unity CLI Bridge");

            Assert.IsNull(version);
        }

        [Test]
        public void ParsePackageVersionMarker_AcceptsWhitespaceVariants()
        {
            string? version = SkillInstaller.ParsePackageVersionMarker(
                "<!--unity-cli-bridge-package-version:0.4.0-->");

            Assert.AreEqual("0.4.0", version);
        }

        [Test]
        public void TryReadPackageVersionMarker_ReturnsNullWhenFileMissing()
        {
            string missingPath = Path.Combine(
                Path.GetTempPath(),
                "unity-cli-bridge-test-" + Guid.NewGuid().ToString("N"),
                "SKILL.md");

            string? version = SkillInstaller.TryReadPackageVersionMarker(missingPath);

            Assert.IsNull(version);
        }

        private static void AssertResolved(
            SkillInstallLocation location,
            string unityProject,
            string gitRoot,
            string? customRoot,
            string home,
            string expected)
        {
            bool resolved = SkillInstallPaths.TryResolveInstallRoot(
                location,
                unityProject,
                gitRoot,
                customRoot,
                home,
                out string installRoot,
                out string? error);

            Assert.IsTrue(resolved, error ?? string.Empty);
            Assert.IsTrue(SkillInstallPaths.PathsEqual(expected, installRoot));
        }

        [Test]
        public void PluginCommands_MatchMarketplaceNamesAndReadmeInstallLines()
        {
            Assert.AreEqual("unity-cli-bridge", SkillPluginCommands.MarketplaceName);
            Assert.AreEqual("unity-cli-bridge", SkillPluginCommands.PluginName);
            Assert.AreEqual("yhc509/unity-cli-bridge", SkillPluginCommands.RepositorySlug);

            SkillPluginCommandLine[] lines = SkillPluginCommands.All();
            Assert.AreEqual(8, lines.Length);
            Assert.AreEqual(SkillPluginCommands.ClaudeCodeGroup, lines[0].Group);
            Assert.AreEqual("/plugin marketplace add yhc509/unity-cli-bridge", lines[0].Command);
            Assert.AreEqual("/plugin install unity-cli-bridge@unity-cli-bridge", lines[1].Command);
            Assert.AreEqual(SkillPluginCommands.ClaudeCodeTerminalGroup, lines[2].Group);
            Assert.AreEqual("claude plugin marketplace add yhc509/unity-cli-bridge", lines[2].Command);
            Assert.AreEqual("claude plugin install unity-cli-bridge@unity-cli-bridge --scope user", lines[3].Command);
            Assert.AreEqual(SkillPluginCommands.CodexGroup, lines[4].Group);
            Assert.AreEqual("codex plugin marketplace add yhc509/unity-cli-bridge", lines[4].Command);
            Assert.AreEqual("codex plugin add unity-cli-bridge@unity-cli-bridge", lines[5].Command);
            Assert.AreEqual(SkillPluginCommands.GrokGroup, lines[6].Group);
            Assert.AreEqual("grok plugin marketplace add yhc509/unity-cli-bridge", lines[6].Command);
            Assert.AreEqual("grok plugin install yhc509/unity-cli-bridge --trust", lines[7].Command);
            Assert.IsFalse(lines[7].Command.Contains("@"));
        }

        private static string CreateTempDirectory()
        {
            string path = Path.Combine(Path.GetTempPath(), "unity-cli-bridge-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return path;
        }

        private static string GetProjectRoot()
        {
            DirectoryInfo? projectRoot = Directory.GetParent(Application.dataPath);
            if (projectRoot == null)
            {
                throw new InvalidOperationException("Test project root could not be resolved.");
            }

            return projectRoot.FullName;
        }

        private static string GetHomeDirectory()
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrWhiteSpace(home))
            {
                throw new InvalidOperationException("Test home directory could not be resolved.");
            }

            return home;
        }
    }
}
