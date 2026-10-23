using System.Text.Json;
using UnityCli.Cli.Models;
using UnityCli.Cli.Services;
using UnityCli.Protocol;

namespace UnityCli.Cli.Tests;

public sealed class DoctorBundledToolchainTests
{
    [Fact]
    public async Task RunDoctorAsync_WithEditorInstall_ReportsBundledToolchain()
    {
        using var temp = new TempDirectory();
        string projectRoot = CreateUnityProject(temp.Path);
        (string executable, string toolchainRoot) = CreateEditorInstall(temp.Path);

        JsonElement data = await RunDoctorAsync(temp.Path, projectRoot, _ => executable);

        Assert.Equal(executable, data.GetProperty("unityPath").GetString());
        JsonElement toolchain = data.GetProperty("bundledToolchain");
        Assert.True(toolchain.GetProperty("complete").GetBoolean());
        Assert.False(toolchain.TryGetProperty("error", out _));
        Assert.Equal(toolchainRoot, toolchain.GetProperty("toolchainRoot").GetString());
        JsonElement runtime = toolchain.GetProperty("runtime");
        Assert.Equal("6.0.21", runtime.GetProperty("netCoreAppVersion").GetString());
        Assert.StartsWith(Path.Combine(toolchainRoot, "NetCoreRuntime", "dotnet"), runtime.GetProperty("dotnetPath").GetString());
        JsonElement roslyn = toolchain.GetProperty("roslyn");
        Assert.Equal(Path.Combine(toolchainRoot, "DotNetSdkRoslyn"), roslyn.GetProperty("directory").GetString());
        Assert.Equal(Path.Combine(toolchainRoot, "DotNetSdkRoslyn", "csc.dll"), roslyn.GetProperty("cscPath").GetString());
        Assert.Equal(
            Path.Combine(toolchainRoot, "MonoBleedingEdge", "lib", "mono", "4.5", "mcs.exe"),
            toolchain.GetProperty("mcs").GetProperty("path").GetString());
    }

    [Fact]
    public async Task RunDoctorAsync_PartialInstall_ReportsComponentFailureAndStillSucceeds()
    {
        using var temp = new TempDirectory();
        string projectRoot = CreateUnityProject(temp.Path);
        (string executable, string toolchainRoot) = CreateEditorInstall(temp.Path);
        File.Delete(Path.Combine(toolchainRoot, "DotNetSdkRoslyn", BundledToolchainLocator.CodeAnalysisCSharpFileName));

        var result = await InvokeDoctorAsync(temp.Path, projectRoot, _ => executable);

        Assert.Equal(ProtocolConstants.StatusSuccess, result.status);
        JsonElement toolchain = result.data!.Value.GetProperty("bundledToolchain");
        Assert.False(toolchain.GetProperty("complete").GetBoolean());
        Assert.Contains(
            BundledToolchainLocator.CodeAnalysisCSharpFileName,
            toolchain.GetProperty("roslyn").GetProperty("error").GetString());
        Assert.False(toolchain.GetProperty("runtime").TryGetProperty("error", out _));
    }

    [Fact]
    public async Task RunDoctorAsync_UnityPathUnresolved_ReportsReasonInsteadOfFailing()
    {
        using var temp = new TempDirectory();
        string projectRoot = CreateUnityProject(temp.Path);

        var result = await InvokeDoctorAsync(temp.Path, projectRoot, _ => null);

        Assert.Equal(ProtocolConstants.StatusSuccess, result.status);
        JsonElement data = result.data!.Value;
        Assert.False(data.TryGetProperty("unityPath", out _));
        JsonElement toolchain = data.GetProperty("bundledToolchain");
        Assert.False(toolchain.GetProperty("complete").GetBoolean());
        Assert.Contains("unknown", toolchain.GetProperty("error").GetString());
        Assert.False(toolchain.TryGetProperty("runtime", out _));
    }

    [Fact]
    public async Task RunDoctorAsync_NoProjectRoot_ReportsReasonInsteadOfFailing()
    {
        using var temp = new TempDirectory();

        var result = await InvokeDoctorAsync(temp.Path, projectRoot: null, _ => throw new InvalidOperationException("not called"));

        Assert.Equal(ProtocolConstants.StatusSuccess, result.status);
        JsonElement toolchain = result.data!.Value.GetProperty("bundledToolchain");
        Assert.Contains("unknown", toolchain.GetProperty("error").GetString());
    }

    [Fact]
    public async Task RunDoctorAsync_JsonOutput_CarriesBundledToolchainObject()
    {
        using var temp = new TempDirectory();
        string projectRoot = CreateUnityProject(temp.Path);
        (string executable, _) = CreateEditorInstall(temp.Path);

        var result = await InvokeDoctorAsync(temp.Path, projectRoot, _ => executable);
        string json = ResponseFormatter.Format(OutputMode.Json, result);

        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement toolchain = document.RootElement.GetProperty("data").GetProperty("bundledToolchain");
        Assert.Equal(JsonValueKind.Object, toolchain.ValueKind);
        Assert.Equal(JsonValueKind.Object, toolchain.GetProperty("runtime").ValueKind);
        Assert.Equal(JsonValueKind.Object, toolchain.GetProperty("roslyn").ValueKind);
        Assert.Equal(JsonValueKind.Object, toolchain.GetProperty("mcs").ValueKind);
        Assert.Equal(ProtocolConstants.ProtocolVersion, document.RootElement.GetProperty("protocolVersion").GetString());
    }

    private static async Task<JsonElement> RunDoctorAsync(string tempRoot, string projectRoot, Func<string, string?> resolveUnityPath)
    {
        var result = await InvokeDoctorAsync(tempRoot, projectRoot, resolveUnityPath);
        Assert.Equal(ProtocolConstants.StatusSuccess, result.status);
        return result.data!.Value;
    }

    private static Task<ResponseEnvelope> InvokeDoctorAsync(string tempRoot, string? projectRoot, Func<string, string?> resolveUnityPath)
    {
        var registryStore = new InstanceRegistryStore(Path.Combine(tempRoot, "instances.json"));
        registryStore.Save(new InstanceRegistry { instances = [] });
        return UnityCli.Cli.CliApp.RunDoctorAsync(
            registryStore,
            new UnityProjectLocator(),
            new ParsedCommand(CommandKind.Doctor),
            projectRoot,
            (_, _, _, _) => throw new IOException("no live editor in this test"),
            resolveUnityPath);
    }

    /// <summary>Fake editor install shaped for the platform the test runs on, since doctor probes the host layout.</summary>
    private static (string Executable, string ToolchainRoot) CreateEditorInstall(string tempRoot)
    {
        BundledToolchainPlatform platform = BundledToolchainLocator.CurrentPlatform;
        string installRoot = Path.Combine(tempRoot, "Hub Editor", "6000.3.10f1");
        string executable;
        string contents;
        if (platform == BundledToolchainPlatform.MacOS)
        {
            contents = Path.Combine(installRoot, "Unity.app", "Contents");
            executable = BundledToolchainLocatorTests.CreateMacExecutable(contents);
        }
        else
        {
            string editor = Path.Combine(installRoot, "Editor");
            contents = Path.Combine(editor, "Data");
            executable = Path.Combine(editor, platform == BundledToolchainPlatform.Windows ? "Unity.exe" : "Unity");
            Directory.CreateDirectory(editor);
            File.WriteAllText(executable, string.Empty);
        }

        string toolchainRoot = Path.Combine(contents, "Resources", "Scripting");
        BundledToolchainLocatorTests.CreateToolchain(toolchainRoot, platform);
        return (executable, toolchainRoot);
    }

    private static string CreateUnityProject(string root)
    {
        string projectRoot = Path.Combine(root, "Project");
        Directory.CreateDirectory(Path.Combine(projectRoot, "Assets"));
        Directory.CreateDirectory(Path.Combine(projectRoot, "Packages"));
        return projectRoot;
    }
}
