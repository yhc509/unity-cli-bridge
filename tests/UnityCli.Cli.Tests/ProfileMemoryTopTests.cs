using UnityCli.Protocol;
using Xunit;

namespace UnityCli.Cli.Tests;

public class ProfileMemoryTopTests
{
    [Fact]
    public void Ranking_GroupsTotalsAndKeepsTheLargestObjects()
    {
        ProfileMemoryTopPayload payload = ProfileMemoryTopRanking.Build(
            new[]
            {
                Sample("Texture2D", "small", 10, "Assets/Small.png", width: 2, height: 2, format: "RGBA32", mipCount: 1),
                Sample("Texture2D", "large", 50, "Assets/Large.png", width: 8, height: 4, format: "RGBA32", mipCount: 1),
                Sample("Mesh", "rock", 40, "Assets/Rock.fbx", vertexCount: 12, subMeshCount: 2),
                Sample("Mesh", "hidden-path", 5, null),
            },
            limit: 2,
            mode: "editmode",
            unityVersion: "6000.3.10f1",
            scope: "loaded",
            type: null,
            scanTruncated: false,
            warnings: null);

        Assert.Equal(ProfileMemoryTopCatalog.EditorSizeNote, payload.note);
        Assert.Equal("editmode", payload.mode);
        Assert.Equal("loaded", payload.scope);
        Assert.Null(payload.type);
        Assert.True(payload.truncated);
        Assert.Null(payload.warnings);
        Assert.Equal(2, payload.totals.Length);
        Assert.Equal("Texture2D", payload.totals[0].type);
        Assert.Equal(2, payload.totals[0].count);
        Assert.Equal(60, payload.totals[0].bytes);
        Assert.Equal("Mesh", payload.totals[1].type);
        Assert.Equal(45, payload.totals[1].bytes);

        Assert.Equal(2, payload.top.Length);
        Assert.Equal("large", payload.top[0].name);
        Assert.Equal("Assets/Large.png", payload.top[0].path);
        ProfileMemoryTopDetail largeDetail = payload.top[0].detail!;
        Assert.Equal(8, largeDetail.width);
        Assert.Equal(4, largeDetail.height);
        Assert.Equal("RGBA32", largeDetail.format);
        Assert.Equal(1, largeDetail.mipCount);
        Assert.Null(largeDetail.vertexCount);
        Assert.Equal("rock", payload.top[1].name);
        ProfileMemoryTopDetail rockDetail = payload.top[1].detail!;
        Assert.Equal(12, rockDetail.vertexCount);
        Assert.Equal(2, rockDetail.subMeshCount);
        Assert.Null(rockDetail.width);
    }

    [Fact]
    public void Ranking_BreaksByteTiesByNameAndMarksAPartialScan()
    {
        ProfileMemoryTopPayload payload = ProfileMemoryTopRanking.Build(
            new[]
            {
                Sample("Mesh", "b", 10, null),
                Sample("Mesh", "a", 10, null),
            },
            limit: 20,
            mode: "playmode",
            unityVersion: "2021.3.45f2",
            scope: "scene",
            type: "Mesh",
            scanTruncated: true,
            warnings: new[] { "Scan stopped after 2000 Mesh objects; totals and the top list may be incomplete." });

        Assert.False(payload.top.Length > 2);
        Assert.Equal("a", payload.top[0].name);
        Assert.Equal("b", payload.top[1].name);
        Assert.Null(payload.top[0].path);
        Assert.Null(payload.top[0].detail);
        Assert.True(payload.truncated);
        Assert.Equal("Mesh", payload.type);
        Assert.NotNull(payload.warnings);
        Assert.Contains("2000", payload.warnings![0]);
    }

    [Theory]
    [InlineData("loaded", false, "Assets/Art/A.png", true, true)]
    [InlineData("loaded", true, "Assets/Art/A.png", true, false)]
    [InlineData("loaded", false, "Library/unity editor resources", true, false)]
    [InlineData("loaded", false, "Resources/unity_builtin_extra", true, false)]
    [InlineData("loaded", false, "Library/unity default resources", true, false)]
    [InlineData("scene", false, null, false, true)]
    [InlineData("scene", false, "Assets/Art/A.png", true, false)]
    [InlineData("assets", false, "Assets/Art/A.png", true, true)]
    [InlineData("assets", false, "Packages/com.example/A.png", true, true)]
    [InlineData("assets", false, null, false, false)]
    [InlineData("assets", false, "Library/Cache/A.png", true, false)]
    public void Filter_SkipsHiddenBuiltinAndAppliesScope(
        string scope,
        bool hideAndDontSave,
        string? path,
        bool persistent,
        bool expected)
    {
        Assert.Equal(expected, ProfileMemoryTopFilter.Include(scope, hideAndDontSave, path, persistent));
    }

    [Theory]
    [InlineData("texture2d", "Texture2D", true)]
    [InlineData("Mesh", "Mesh", true)]
    [InlineData("audioclip", "AudioClip", true)]
    [InlineData("GameObject", "", false)]
    public void Catalog_NormalizesAcceptedTypes(string raw, string expected, bool ok)
    {
        bool parsed = ProfileMemoryTopCatalog.TryNormalizeType(raw, out string normalized);
        Assert.Equal(ok, parsed);
        Assert.Equal(expected, normalized);
    }

    [Fact]
    public void Catalog_DefaultTypesAreAccepted()
    {
        foreach (string typeName in ProfileMemoryTopCatalog.DefaultTypeNames)
        {
            Assert.True(ProfileMemoryTopCatalog.TryNormalizeType(typeName, out string normalized));
            Assert.Equal(typeName, normalized);
        }

        Assert.DoesNotContain("Texture", ProfileMemoryTopCatalog.DefaultTypeNames);
    }

    private static ProfileMemoryTopMeasurement Sample(
        string type,
        string name,
        long bytes,
        string? path,
        int? width = null,
        int? height = null,
        string? format = null,
        int? mipCount = null,
        int? vertexCount = null,
        int? subMeshCount = null)
    {
        return new ProfileMemoryTopMeasurement
        {
            type = type,
            name = name,
            bytes = bytes,
            path = path,
            width = width,
            height = height,
            format = format,
            mipCount = mipCount,
            vertexCount = vertexCount,
            subMeshCount = subMeshCount,
        };
    }
}
