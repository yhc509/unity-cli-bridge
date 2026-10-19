#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using PackageManagerInfo = UnityEditor.PackageManager.PackageInfo;

namespace UnityCliBridge.Bridge.Editor
{
    internal enum SkillTarget
    {
        [InspectorName("Claude Code")]
        ClaudeCode = 0,
        [InspectorName("Codex (.agents)")]
        Codex = 1,
        [InspectorName("Grok Build")]
        GrokBuild = 2,
    }

    internal enum SkillScope
    {
        [InspectorName("Project")]
        Project = 0,
        [InspectorName("Global")]
        Global = 1,
    }

    internal enum SkillInstallLocation
    {
        [InspectorName("Unity project")]
        UnityProject = 0,
        [InspectorName("Git repository")]
        GitRepository = 1,
        [InspectorName("User (home)")]
        UserHome = 2,
    }

    internal static class SkillInstaller
    {
        private const string AgentsDirectoryName = "agents";
        private const string SkillMarkdownFileName = "SKILL.md";
        private const string PackageVersionPlaceholder = "{{PACKAGE_VERSION}}";
        private const string MetaFileExtension = ".meta";
        private const string PackageVersionMarkerPattern = "<!--\\s*unity-cli-bridge-package-version\\s*:\\s*(?<version>.*?)\\s*-->";

        internal static void Install(SkillTarget target, SkillScope scope)
        {
            Install(target, GetScopeRoot(scope));
        }

        internal static void Install(SkillTarget target, string installRoot)
        {
            SkillTemplateInfo templateInfo = GetSkillTemplateInfo();
            SkillTargetLayout layout = GetLayout(target);
            string destination = GetDestination(target, installRoot);

            if (Directory.Exists(destination))
            {
                Directory.Delete(destination, true);
            }

            Directory.CreateDirectory(destination);
            CopyDirectory(templateInfo.TemplateRoot, destination, layout.IncludeAgents, templateInfo.PackageVersion);

            Debug.Log($"[SkillInstaller] Installed {target} skill to: {destination}");
        }

        internal static bool Remove(SkillTarget target, SkillScope scope)
        {
            return Remove(target, GetScopeRoot(scope));
        }

        internal static bool Remove(SkillTarget target, string installRoot)
        {
            string destination = GetDestination(target, installRoot);
            if (!Directory.Exists(destination))
            {
                return false;
            }

            Directory.Delete(destination, true);

            Debug.Log($"[SkillInstaller] Removed {target} skill from: {destination}");
            return true;
        }

        internal static string GetDestination(SkillTarget target, SkillScope scope)
        {
            return GetDestination(target, GetScopeRoot(scope));
        }

        internal static string GetDestination(SkillTarget target, string installRoot)
        {
            if (string.IsNullOrWhiteSpace(installRoot))
            {
                throw new ArgumentException("Skill install root is required.", nameof(installRoot));
            }

            return SkillInstallPaths.SkillDestination(installRoot, target);
        }

        internal static bool IsSkillInstalled(SkillTarget target, SkillScope scope)
        {
            return IsSkillInstalled(target, GetScopeRoot(scope));
        }

        internal static bool IsSkillInstalled(SkillTarget target, string installRoot)
        {
            try
            {
                return File.Exists(GetSkillMarkdownPath(target, installRoot));
            }
            catch
            {
                return false;
            }
        }

        internal static string? GetInstalledSkillVersion(SkillTarget target, SkillScope scope)
        {
            return GetInstalledSkillVersion(target, GetScopeRoot(scope));
        }

        internal static string? GetInstalledSkillVersion(SkillTarget target, string installRoot)
        {
            try
            {
                return TryReadPackageVersionMarker(GetSkillMarkdownPath(target, installRoot));
            }
            catch
            {
                return null;
            }
        }

        internal static IReadOnlyList<string> FindLegacyCodexInstalls(IEnumerable<string> roots)
        {
            List<string> found = new List<string>();
            List<string> seenRoots = new List<string>();
            foreach (string root in roots)
            {
                if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
                {
                    continue;
                }

                string fullRoot;
                try
                {
                    fullRoot = Path.GetFullPath(root);
                }
                catch (Exception)
                {
                    continue;
                }

                bool alreadySeen = false;
                for (int i = 0; i < seenRoots.Count; i++)
                {
                    if (SkillInstallPaths.PathsEqual(seenRoots[i], fullRoot))
                    {
                        alreadySeen = true;
                        break;
                    }
                }

                if (alreadySeen)
                {
                    continue;
                }

                seenRoots.Add(fullRoot);
                string candidate = SkillInstallPaths.LegacyCodexDestination(fullRoot);
                if (File.Exists(Path.Combine(candidate, SkillMarkdownFileName)))
                {
                    found.Add(candidate);
                }
            }

            return found;
        }

        internal static bool RemoveLegacyCodex(string legacySkillDirectory)
        {
            if (!SkillInstallPaths.IsLegacyCodexSkillPath(legacySkillDirectory))
            {
                throw new InvalidOperationException(
                    "Refusing to delete a path that is not a legacy .codex/skills/" + SkillInstallPaths.SkillName + " directory: " + legacySkillDirectory);
            }

            if (!Directory.Exists(legacySkillDirectory))
            {
                return false;
            }

            Directory.Delete(legacySkillDirectory, true);
            Debug.Log("[SkillInstaller] Removed legacy Codex skill from: " + legacySkillDirectory);
            return true;
        }

        internal static string GetUnityProjectRoot()
        {
            DirectoryInfo? projectRoot = Directory.GetParent(Application.dataPath);
            if (projectRoot == null || string.IsNullOrWhiteSpace(projectRoot.FullName))
            {
                throw new InvalidOperationException("Failed to resolve Unity project root from Application.dataPath: " + Application.dataPath);
            }

            return projectRoot.FullName;
        }

        internal static string GetUserHomeRoot()
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrWhiteSpace(home))
            {
                throw new InvalidOperationException("Failed to resolve user home directory.");
            }

            return home;
        }

        internal static string? ParsePackageVersionMarker(string skillMarkdown)
        {
            if (string.IsNullOrWhiteSpace(skillMarkdown))
            {
                return null;
            }

            Match match = Regex.Match(
                skillMarkdown,
                PackageVersionMarkerPattern,
                RegexOptions.CultureInvariant | RegexOptions.Singleline);
            if (!match.Success)
            {
                return null;
            }

            string version = match.Groups["version"].Value.Trim();
            return version.Length == 0 ? null : version;
        }

        internal static string? TryReadPackageVersionMarker(string skillMarkdownPath)
        {
            try
            {
                if (!File.Exists(skillMarkdownPath))
                {
                    return null;
                }

                return ParsePackageVersionMarker(File.ReadAllText(skillMarkdownPath));
            }
            catch
            {
                return null;
            }
        }

        private static string GetScopeRoot(SkillScope scope)
        {
            switch (scope)
            {
                case SkillScope.Project:
                    return GetUnityProjectRoot();
                case SkillScope.Global:
                    return GetUserHomeRoot();
                default:
                    throw new ArgumentOutOfRangeException(nameof(scope), scope, "Unsupported skill scope.");
            }
        }

        private static SkillTargetLayout GetLayout(SkillTarget target)
        {
            return new SkillTargetLayout(SkillInstallPaths.IncludesAgentsMetadata(target));
        }

        private static string GetSkillMarkdownPath(SkillTarget target, string installRoot)
        {
            return Path.Combine(GetDestination(target, installRoot), SkillMarkdownFileName);
        }

        private static SkillTemplateInfo GetSkillTemplateInfo()
        {
            PackageManagerInfo? packageInfo = PackageManagerInfo.FindForAssembly(typeof(SkillInstaller).Assembly);
            if (packageInfo == null || string.IsNullOrWhiteSpace(packageInfo.resolvedPath))
            {
                throw new InvalidOperationException("Could not resolve the Unity package path.");
            }

            if (string.IsNullOrWhiteSpace(packageInfo.version))
            {
                throw new InvalidOperationException("Could not resolve the Unity package version.");
            }

            return new SkillTemplateInfo(
                Path.Combine(packageInfo.resolvedPath, "SkillTemplates~"),
                packageInfo.version.Trim());
        }

        private static void CopyDirectory(string source, string destination, bool includeAgents, string packageVersion)
        {
            if (!Directory.Exists(source))
            {
                throw new DirectoryNotFoundException("Skill template not found: " + source);
            }

            Directory.CreateDirectory(destination);

            foreach (string file in Directory.GetFiles(source))
            {
                if (string.Equals(Path.GetExtension(file), MetaFileExtension, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string destinationFile = Path.Combine(destination, Path.GetFileName(file));
                if (string.Equals(Path.GetFileName(file), SkillMarkdownFileName, StringComparison.OrdinalIgnoreCase))
                {
                    string skillMarkdown = File.ReadAllText(file);
                    File.WriteAllText(destinationFile, skillMarkdown.Replace(PackageVersionPlaceholder, packageVersion));
                }
                else
                {
                    File.Copy(file, destinationFile, overwrite: true);
                }
            }

            foreach (string directory in Directory.GetDirectories(source))
            {
                string directoryName = Path.GetFileName(directory);
                if (!includeAgents && string.Equals(directoryName, AgentsDirectoryName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                CopyDirectory(directory, Path.Combine(destination, directoryName), includeAgents, packageVersion);
            }
        }

        private readonly struct SkillTargetLayout
        {
            internal SkillTargetLayout(bool includeAgents)
            {
                IncludeAgents = includeAgents;
            }

            internal bool IncludeAgents { get; }
        }

        private readonly struct SkillTemplateInfo
        {
            internal SkillTemplateInfo(string templateRoot, string packageVersion)
            {
                TemplateRoot = templateRoot;
                PackageVersion = packageVersion;
            }

            internal string TemplateRoot { get; }
            internal string PackageVersion { get; }
        }
    }
}
