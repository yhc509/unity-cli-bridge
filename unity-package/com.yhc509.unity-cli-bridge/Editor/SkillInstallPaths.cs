#nullable enable
using System;
using System.Collections.Generic;
using System.IO;

namespace UnityCliBridge.Bridge.Editor
{
    /// <summary>
    /// Pure path rules for the CLI Manager skill installer. Unity project and home roots are
    /// resolved by <see cref="SkillInstaller"/>; everything that can be checked without the Editor
    /// lives here so EditMode tests can drive it with temp directories.
    /// </summary>
    internal static class SkillInstallPaths
    {
        internal const string SkillName = "unity-cli-bridge";
        internal const string LegacyCodexDirectoryName = ".codex";

        internal static string ToolDirectoryName(SkillTarget target)
        {
            switch (target)
            {
                case SkillTarget.ClaudeCode:
                    return ".claude";
                case SkillTarget.Codex:
                    return ".agents";
                case SkillTarget.GrokBuild:
                    return ".grok";
                default:
                    throw new ArgumentOutOfRangeException(nameof(target), target, "Unsupported skill target.");
            }
        }

        internal static bool IncludesAgentsMetadata(SkillTarget target)
        {
            return target == SkillTarget.Codex;
        }

        internal static string SkillDestination(string installRoot, SkillTarget target)
        {
            return Path.Combine(installRoot, ToolDirectoryName(target), "skills", SkillName);
        }

        internal static string LegacyCodexDestination(string installRoot)
        {
            return Path.Combine(installRoot, LegacyCodexDirectoryName, "skills", SkillName);
        }

        internal static bool IsLegacyCodexSkillPath(string? path)
        {
            string? normalized = NormalizeSeparators(path);
            if (normalized == null)
            {
                return false;
            }

            return normalized.EndsWith("/.codex/skills/" + SkillName, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Walks <paramref name="startDirectory"/> and its parents for a <c>.git</c> directory or
        /// file (worktrees and submodules use a file). The nearest match wins.
        /// </summary>
        internal static string? TryFindGitRoot(string? startDirectory)
        {
            string? current = NormalizeExistingOrFullPath(startDirectory);
            if (current == null)
            {
                return null;
            }

            while (!string.IsNullOrEmpty(current))
            {
                string gitPath = Path.Combine(current, ".git");
                if (Directory.Exists(gitPath) || File.Exists(gitPath))
                {
                    return current;
                }

                DirectoryInfo? parent = Directory.GetParent(current);
                if (parent == null)
                {
                    return null;
                }

                string parentPath = TrimTrailingSeparator(parent.FullName);
                if (PathsEqual(parentPath, current))
                {
                    return null;
                }

                current = parentPath;
            }

            return null;
        }

        internal static bool TryResolveInstallRoot(
            SkillInstallLocation location,
            string? unityProjectRoot,
            string? detectedGitRoot,
            string? customRoot,
            string? userHome,
            out string installRoot,
            out string? error)
        {
            installRoot = string.Empty;
            error = null;
            switch (location)
            {
                case SkillInstallLocation.UnityProject:
                    return RequireDirectory(unityProjectRoot, "Unity project root", out installRoot, out error);
                case SkillInstallLocation.UserHome:
                    return RequireDirectory(userHome, "user home directory", out installRoot, out error);
                case SkillInstallLocation.GitRepository:
                    if (!string.IsNullOrWhiteSpace(customRoot))
                    {
                        return RequireDirectory(customRoot, "chosen folder", out installRoot, out error);
                    }

                    if (string.IsNullOrWhiteSpace(detectedGitRoot))
                    {
                        error = "No .git directory was found above this Unity project. Choose a folder to use as the install root.";
                        return false;
                    }

                    return RequireDirectory(detectedGitRoot, "git repository root", out installRoot, out error);
                default:
                    error = "Unsupported skill install location.";
                    return false;
            }
        }

        internal static string? GitRootMismatchWarning(
            SkillInstallLocation location,
            string? unityProjectRoot,
            string? gitRoot)
        {
            if (location != SkillInstallLocation.UnityProject)
            {
                return null;
            }

            if (string.IsNullOrWhiteSpace(gitRoot) || string.IsNullOrWhiteSpace(unityProjectRoot))
            {
                return null;
            }

            if (PathsEqual(unityProjectRoot, gitRoot))
            {
                return null;
            }

            return "The git repository root (" + gitRoot
                + ") is above this Unity project. Agents opened at the repository root will not see a skill installed here. Set Location to Git repository or User (home) if you open the agent there.";
        }

        internal static IReadOnlyList<string> DescribeDuplicateVisibility(
            SkillTarget selected,
            bool claudeAtRoot,
            bool agentsAtRoot,
            bool grokAtRoot,
            bool rootIsUserHome)
        {
            List<string> messages = new List<string>();
            if (claudeAtRoot && grokAtRoot)
            {
                messages.Add(
                    "Grok Build also reads .claude/skills. The Claude Code copy and the Grok Build copy at this root are both visible to Grok under the same skill name. Remove one of them.");
            }
            else if (selected == SkillTarget.GrokBuild && claudeAtRoot)
            {
                messages.Add(
                    "A Claude Code skill is already installed at this root. Grok Build reads .claude/skills, so a separate Grok copy here would show the same skill twice. Use the Claude Code copy, or remove it before installing Grok.");
            }
            else if (selected == SkillTarget.ClaudeCode && grokAtRoot)
            {
                messages.Add(
                    "A Grok Build skill is already installed at this root. Grok Build also reads .claude/skills, so a Claude Code copy here would show the same skill twice in Grok.");
            }

            if (claudeAtRoot && agentsAtRoot)
            {
                messages.Add(
                    "Cursor reads both .claude/skills and .agents/skills. Both copies are installed at this root, so Cursor can show the same skill twice. Grok Build also reads .claude/skills.");
            }
            else if (selected == SkillTarget.Codex && claudeAtRoot)
            {
                messages.Add(
                    "A Claude Code skill is already installed at this root. Cursor reads both .claude/skills and .agents/skills, so the Codex copy would show up beside it.");
            }
            else if (selected == SkillTarget.ClaudeCode && agentsAtRoot)
            {
                messages.Add(
                    "A Codex skill (.agents/skills) is already installed at this root. Cursor reads both that folder and .claude/skills, so a Claude Code copy here would show up beside it.");
            }

            if (rootIsUserHome && grokAtRoot && agentsAtRoot)
            {
                messages.Add(
                    "Grok Build reads ~/.agents/skills as well as ~/.grok/skills. Both copies are installed for this user, so Grok can show the same skill twice.");
            }
            else if (rootIsUserHome && selected == SkillTarget.Codex && grokAtRoot && !agentsAtRoot)
            {
                messages.Add(
                    "Grok Build also reads ~/.agents/skills. A Grok copy is already installed for this user, so the Codex copy would show up beside it.");
            }
            else if (rootIsUserHome && selected == SkillTarget.GrokBuild && agentsAtRoot && !grokAtRoot)
            {
                messages.Add(
                    "Grok Build also reads ~/.agents/skills. A Codex copy is already installed for this user, so a Grok copy would show up beside it.");
            }

            return messages;
        }

        internal static bool PathsEqual(string? left, string? right)
        {
            string? normalizedLeft = NormalizeExistingOrFullPath(left);
            string? normalizedRight = NormalizeExistingOrFullPath(right);
            if (normalizedLeft == null || normalizedRight == null)
            {
                return false;
            }

            return string.Equals(normalizedLeft, normalizedRight, StringComparison.OrdinalIgnoreCase);
        }

        private static bool RequireDirectory(string? path, string label, out string installRoot, out string? error)
        {
            installRoot = string.Empty;
            if (string.IsNullOrWhiteSpace(path))
            {
                error = "Could not resolve the " + label + ".";
                return false;
            }

            string? fullPath = NormalizeExistingOrFullPath(path);
            if (fullPath == null || !Directory.Exists(fullPath))
            {
                error = "The " + label + " does not exist: " + path;
                return false;
            }

            installRoot = fullPath;
            error = null;
            return true;
        }

        private static string? NormalizeSeparators(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            return path.Trim().Replace('\\', '/').TrimEnd('/');
        }

        private static string? NormalizeExistingOrFullPath(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            try
            {
                return TrimTrailingSeparator(Path.GetFullPath(path.Trim()));
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static string TrimTrailingSeparator(string path)
        {
            return path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
    }
}
