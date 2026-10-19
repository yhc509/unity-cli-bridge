using System.Text.Json;
using UnityCli.DocGen;

namespace UnityCli.Cli.Tests;

/// <summary>
/// Guards for what the UPM package contributes to a consuming project's *player* build.
///
/// The bridge itself is editor-only, but assembly definitions and package dependencies are plain
/// JSON that nothing else in the build validates — a one-character edit can silently push the
/// protocol layer (registry file I/O, the full command catalog, process spawning for chmod) into
/// a shipped game, or re-impose Unity Recorder on every consumer. These tests pin that surface.
/// </summary>
public sealed class ReleaseBuildSurfaceTests
{
    private static readonly string[] PlayerFacingRuntimeFiles =
    {
        "IQaQueryable.cs",
        "IQaTappable.cs",
        "QaTappable.cs",
        "QaTargetAttribute.cs",
    };

    private static string PackageRoot()
    {
        return Path.Combine(
            RepositoryPaths.FindRepoRoot(AppContext.BaseDirectory),
            "unity-package",
            "com.yhc509.unity-cli-bridge");
    }

    private static JsonElement ReadJson(params string[] relativeParts)
    {
        string path = Path.Combine(PackageRoot(), Path.Combine(relativeParts));
        return JsonDocument.Parse(File.ReadAllText(path)).RootElement.Clone();
    }

    private static string[] StringArray(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement array))
        {
            return Array.Empty<string>();
        }

        return array.EnumerateArray().Select(item => item.GetString() ?? string.Empty).ToArray();
    }

    [Fact]
    public void RuntimeAssembly_ShipsOnlyTheQaMarkersToPlayerBuilds()
    {
        // The runtime assembly is the one thing that reaches a shipped game, so its contents are
        // the contract: hand-authored marker types a project references on purpose, nothing else.
        string runtimeDirectory = Path.Combine(PackageRoot(), "Runtime");
        string[] topLevelSources = Directory
            .GetFiles(runtimeDirectory, "*.cs", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileName)
            .OfType<string>()
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(PlayerFacingRuntimeFiles.OrderBy(name => name, StringComparer.Ordinal), topLevelSources);
    }

    [Fact]
    public void RuntimeAssembly_StaysAvailableOnEveryPlatform()
    {
        JsonElement asmdef = ReadJson("Runtime", "UnityCliBridge.Bridge.Runtime.asmdef");

        Assert.Empty(StringArray(asmdef, "includePlatforms"));
        Assert.True(asmdef.GetProperty("autoReferenced").GetBoolean());
    }

    [Fact]
    public void ProtocolAssembly_IsEditorOnly()
    {
        // Everything under Runtime/Protocol/ is bridge infrastructure. It lives under Runtime/ only
        // because the CLI compiles the same files (see UnityCli.Protocol.csproj); its own asmdef is
        // what keeps it out of player builds.
        JsonElement asmdef = ReadJson("Runtime", "Protocol", "UnityCliBridge.Bridge.Protocol.asmdef");

        Assert.Equal(new[] { "Editor" }, StringArray(asmdef, "includePlatforms"));
        Assert.False(
            asmdef.GetProperty("autoReferenced").GetBoolean(),
            "Protocol types are internal plumbing; consuming projects must not pick them up implicitly.");
    }

    [Fact]
    public void ProtocolSources_StayWhereTheCliCompilesThemFrom()
    {
        string protocolDirectory = Path.Combine(PackageRoot(), "Runtime", "Protocol");

        Assert.True(Directory.Exists(protocolDirectory));
        Assert.NotEmpty(Directory.GetFiles(protocolDirectory, "*.cs", SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public void EditorAssembly_ReferencesTheProtocolAssembly()
    {
        JsonElement asmdef = ReadJson("Editor", "UnityCliBridge.Bridge.Editor.asmdef");
        string[] references = StringArray(asmdef, "references");

        Assert.Contains("UnityCliBridge.Bridge.Protocol", references);
        Assert.Contains("UnityCliBridge.Bridge.Runtime", references);
        Assert.Equal(new[] { "Editor" }, StringArray(asmdef, "includePlatforms"));
    }

    [Fact]
    public void Package_DoesNotForceUnityRecorderOnConsumers()
    {
        JsonElement dependencies = ReadJson("package.json").GetProperty("dependencies");

        Assert.False(
            dependencies.TryGetProperty("com.unity.recorder", out _),
            "Unity Recorder is optional; gate it with the UNITY_CLI_BRIDGE_RECORDER versionDefine instead.");
    }

    [Fact]
    public void Package_DeclaresTheTestFrameworkItActuallyRequires()
    {
        // The test handlers use TestRunnerApi types unguarded, so the package is only installable
        // in a project that has the test framework. It used to arrive transitively through Unity
        // Recorder; once Recorder became optional, the requirement had to be stated outright.
        JsonElement dependencies = ReadJson("package.json").GetProperty("dependencies");

        Assert.True(dependencies.TryGetProperty("com.unity.test-framework", out _));
    }

    [Fact]
    public void EditorAssembly_DefinesTheOptionalRecorderSymbol()
    {
        JsonElement asmdef = ReadJson("Editor", "UnityCliBridge.Bridge.Editor.asmdef");

        bool hasRecorderDefine = asmdef.GetProperty("versionDefines").EnumerateArray().Any(entry =>
            entry.GetProperty("name").GetString() == "com.unity.recorder"
            && entry.GetProperty("expression").GetString() == "3.0.0"
            && entry.GetProperty("define").GetString() == "UNITY_CLI_BRIDGE_RECORDER");

        Assert.True(
            hasRecorderDefine,
            "com.unity.recorder 3.0.0 or newer must map to UNITY_CLI_BRIDGE_RECORDER. An empty expression would compile record code against unverified 2.x releases.");
    }

    [Fact]
    public void RecordHandler_KeepsEveryRecorderApiBehindTheDefine()
    {
        string source = File.ReadAllText(
            Path.Combine(PackageRoot(), "Editor", "RecordCommandHandler.cs"));

        Assert.Contains("#if UNITY_CLI_BRIDGE_RECORDER\nusing UnityEditor.Recorder;", source.Replace("\r\n", "\n"));
        Assert.Contains("#if !UNITY_CLI_BRIDGE_RECORDER", source);
        Assert.Contains("unity-cli package add --name com.unity.recorder", source);

        // A bare `using UnityEditor.Recorder` outside the guard would break projects without the
        // package, which is exactly what the guard exists to prevent.
        int guardedUsings = source.Split("using UnityEditor.Recorder").Length - 1;
        Assert.Equal(2, guardedUsings);
    }
}
