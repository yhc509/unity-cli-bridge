using System.IO;
using UnityCli.Protocol;

namespace UnityCli.Cli.Tests;

public sealed class AssetPathUtilityTests
{
    [Theory]
    [InlineData(" Assets/Materials/Wood.mat ", "Assets/Materials/Wood.mat")]
    [InlineData("Assets/Materials/Wood.mat/", "Assets/Materials/Wood.mat")]
    [InlineData("Assets\\Materials\\Wood.mat", "Assets/Materials/Wood.mat")]
    [InlineData("Assets/Foo/Bar.png", "Assets/Foo/Bar.png")]
    public void Normalize_AcceptsAssetsPaths(string input, string expected)
    {
        Assert.Equal(expected, AssetPathUtility.Normalize(input));
    }

    [Theory]
    [InlineData(" Packages/com.test/Runtime/Foo.asset ", "Packages/com.test/Runtime/Foo.asset")]
    [InlineData("Packages/com.test/Runtime/Foo.asset/", "Packages/com.test/Runtime/Foo.asset")]
    [InlineData("Packages\\com.test\\Runtime\\Foo.asset", "Packages/com.test/Runtime/Foo.asset")]
    [InlineData("Packages/com.x/Foo.cs", "Packages/com.x/Foo.cs")]
    public void Normalize_WithAllowPackages_AcceptsPackagePaths(string input, string expected)
    {
        Assert.Equal(expected, AssetPathUtility.Normalize(input, allowPackages: true));
    }

    [Fact]
    public void Normalize_WithoutAllowPackages_RejectsPackagePaths()
    {
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
            AssetPathUtility.Normalize("Packages/com.test/Runtime/Foo.asset"));

        Assert.Equal("Asset paths must use the `Assets/...` form.", exception.Message);
    }

    [Fact]
    public void Normalize_WithAllowPackages_RejectsUnsupportedRoots()
    {
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
            AssetPathUtility.Normalize("ProjectSettings/TagManager.asset", allowPackages: true));

        Assert.Equal("Asset paths must use the `Assets/...` or `Packages/...` form.", exception.Message);
    }

    [Theory]
    [InlineData("Assets/../ProjectSettings/foo", false)]
    [InlineData("Assets/./Foo", false)]
    [InlineData("Assets//Foo", false)]
    [InlineData("Packages/../Assets/foo", true)]
    [InlineData("Assets\\..\\ProjectSettings\\foo", false)]
    [InlineData("/etc/passwd", false)]
    [InlineData("C:\\Windows\\foo", false)]
    public void Normalize_RejectsUnsafePaths(string input, bool allowPackages)
    {
        Assert.Throws<InvalidOperationException>(() =>
            AssetPathUtility.Normalize(input, allowPackages));
    }

    [Fact]
    public void IsPhysicalPathWithinRoot_AllowsRootItself()
    {
        string root = TestPath("RootSelfProject", "Assets");

        Assert.True(AssetPathUtility.IsPhysicalPathWithinRoot(root, root));
    }

    [Fact]
    public void IsPhysicalPathWithinRoot_AllowsFileDirectlyUnderRoot()
    {
        string root = TestPath("ChildFileProject", "Assets");
        string childFile = Path.Combine(root, "Texture.png");

        Assert.True(AssetPathUtility.IsPhysicalPathWithinRoot(childFile, root));
    }

    [Fact]
    public void IsPhysicalPathWithinRoot_AllowsFolderDirectlyUnderRoot()
    {
        string root = TestPath("ChildFolderProject", "Assets");
        string childFolder = Path.Combine(root, "Materials");

        Assert.True(AssetPathUtility.IsPhysicalPathWithinRoot(childFolder, root));
    }

    [Fact]
    public void IsPhysicalPathWithinRoot_RejectsSiblingDirectoryWithSharedPrefix()
    {
        string projectRoot = TestPath("PrefixProject");
        string root = Path.Combine(projectRoot, "Assets");
        string siblingWithPrefix = Path.Combine(projectRoot, "AssetsFoo", "bar");

        Assert.False(AssetPathUtility.IsPhysicalPathWithinRoot(siblingWithPrefix, root));
    }

    [Fact]
    public void IsPhysicalPathWithinRoot_RejectsDifferentTree()
    {
        string root = TestPath("DifferentTreeProject", "Assets");
        string otherTree = TestPath("OtherTreeProject", "Assets", "Texture.png");

        Assert.False(AssetPathUtility.IsPhysicalPathWithinRoot(otherTree, root));
    }

    [Fact]
    public void IsPhysicalPathWithinRoot_HandlesTrailingSeparatorOnRootForChildren()
    {
        string root = TestPath("TrailingSeparatorProject", "Assets");
        string childFile = Path.Combine(root, "Texture.png");
        string rootWithSeparator = root + Path.DirectorySeparatorChar;

        Assert.Equal(
            AssetPathUtility.IsPhysicalPathWithinRoot(childFile, root),
            AssetPathUtility.IsPhysicalPathWithinRoot(childFile, rootWithSeparator));
        Assert.True(AssetPathUtility.IsPhysicalPathWithinRoot(childFile, rootWithSeparator));
    }

    private static string TestPath(params string[] segments)
    {
        string path = Path.Combine(Path.GetTempPath(), "UnityCliBridgeAssetPathUtilityTests");
        foreach (string segment in segments)
        {
            path = Path.Combine(path, segment);
        }

        return Path.GetFullPath(path);
    }
}
