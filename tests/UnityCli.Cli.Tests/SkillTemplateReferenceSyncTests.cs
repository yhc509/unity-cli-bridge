using UnityCli.Cli.Services;
using UnityCli.DocGen;

namespace UnityCli.Cli.Tests;

public sealed class SkillTemplateReferenceSyncTests
{
    [Fact]
    public void MaintainerAndShippedSkillReferences_AreIdentical()
    {
        string repoRoot = RepositoryPaths.FindRepoRoot(AppContext.BaseDirectory);
        string maintainerRoot = Path.Combine(repoRoot, "tools", "skills", "unity-cli-operator", "references");
        string shippedRoot = Path.Combine(
            repoRoot,
            "unity-package",
            "com.yhc509.unity-cli-bridge",
            "SkillTemplates~",
            "references");

        Assert.True(Directory.Exists(maintainerRoot), "Maintainer skill references are missing: " + maintainerRoot);
        Assert.True(Directory.Exists(shippedRoot), "Shipped skill references are missing: " + shippedRoot);
        Assert.Equal(ReadTree(maintainerRoot), ReadTree(shippedRoot));
    }

    [Fact]
    public void BundledSceneSpec_MatchesDocsAndHelpPointsAtIt()
    {
        string repoRoot = RepositoryPaths.FindRepoRoot(AppContext.BaseDirectory);
        string docs = File.ReadAllText(Path.Combine(repoRoot, "docs", "scene-spec.md")).Replace("\r\n", "\n");
        string maintainer = File.ReadAllText(Path.Combine(repoRoot, "tools", "skills", "unity-cli-operator", "references", "scene-spec.md")).Replace("\r\n", "\n");
        string shipped = File.ReadAllText(Path.Combine(
            repoRoot,
            "unity-package",
            "com.yhc509.unity-cli-bridge",
            "SkillTemplates~",
            "references",
            "scene-spec.md")).Replace("\r\n", "\n");
        string plugin = File.ReadAllText(Path.Combine(
            repoRoot,
            "plugins",
            "unity-cli-bridge",
            "skills",
            "unity-cli-bridge",
            "references",
            "scene-spec.md")).Replace("\r\n", "\n");

        Assert.Equal(docs, maintainer);
        Assert.Equal(docs, shipped);
        Assert.Equal(docs, plugin);

        string? patchHelp = CliCommandMetadata.BuildTopicHelpText("scene patch");
        string? inspectHelp = CliCommandMetadata.BuildTopicHelpText("scene inspect");
        Assert.NotNull(patchHelp);
        Assert.NotNull(inspectHelp);
        Assert.Contains("references/scene-spec.md", patchHelp);
        Assert.Contains("references/scene-spec.md", inspectHelp);
        Assert.DoesNotContain("docs/scene-spec.md", patchHelp);
        Assert.DoesNotContain("docs/scene-spec.md", inspectHelp);
    }

    private static Dictionary<string, string> ReadTree(string root)
    {
        return Directory.GetFiles(root, "*", SearchOption.AllDirectories)
            .ToDictionary(
                path => Path.GetRelativePath(root, path).Replace('\\', '/'),
                path => File.ReadAllText(path).Replace("\r\n", "\n"),
                StringComparer.Ordinal);
    }
}
