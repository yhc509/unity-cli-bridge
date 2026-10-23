using System.Reflection;
using UnityCli.Protocol;

namespace UnityCli.Cli.Tests;

public sealed class BundledToolchainLocatorTests
{
    private const string RoslynVersion = "4.3.1-3.22526.13";
    private const string McsVersion = "6.13.0.0";
    private const string ScriptingRoot = "Resources/Scripting";

    private static readonly Func<string, string?> FakeVersionReader = path => Path.GetFileName(path) switch
    {
        BundledToolchainLocator.CscFileName => RoslynVersion,
        BundledToolchainLocator.McsFileName => McsVersion,
        _ => null,
    };

    [Fact]
    public void Locate_MacOs2021Layout_ReportsLayoutA()
    {
        using var temp = new TempDirectory();
        string contents = Path.Combine(temp.Path, "2021.3.45f2", "Unity.app", "Contents");
        string executable = CreateMacExecutable(contents);
        CreateToolchain(contents, BundledToolchainPlatform.MacOS);

        BundledToolchainReport report = BundledToolchainLocator.Locate(executable, BundledToolchainPlatform.MacOS, FakeVersionReader);

        Assert.True(report.Complete);
        Assert.Null(report.Error);
        Assert.Equal("A", report.Layout);
        Assert.Equal(contents, report.ContentsPath);
        Assert.Equal(contents, report.ToolchainRoot);
        Assert.Equal(Path.Combine(contents, "NetCoreRuntime", "dotnet"), report.Runtime!.Path);
        Assert.Equal("6.0.21", report.Runtime.Version);
        Assert.Equal(Path.Combine(contents, "DotNetSdkRoslyn"), report.Roslyn!.Directory);
        Assert.Equal(Path.Combine(contents, "DotNetSdkRoslyn", "csc.dll"), report.Roslyn.Path);
        Assert.Equal(RoslynVersion, report.Roslyn.Version);
        Assert.Equal(Path.Combine(contents, "MonoBleedingEdge", "lib", "mono", "4.5", "mcs.exe"), report.Mcs!.Path);
        Assert.Equal(McsVersion, report.Mcs.Version);
    }

    [Fact]
    public void Locate_MacOs6000Layout_ReportsLayoutB()
    {
        using var temp = new TempDirectory();
        string contents = Path.Combine(temp.Path, "6000.3.10f1", "Unity.app", "Contents");
        string executable = CreateMacExecutable(contents);
        string scripting = Path.Combine(contents, "Resources", "Scripting");
        CreateToolchain(scripting, BundledToolchainPlatform.MacOS);

        BundledToolchainReport report = BundledToolchainLocator.Locate(executable, BundledToolchainPlatform.MacOS, FakeVersionReader);

        Assert.True(report.Complete);
        Assert.Equal("B", report.Layout);
        Assert.Equal(contents, report.ContentsPath);
        Assert.Equal(scripting, report.ToolchainRoot);
        Assert.Equal(Path.Combine(scripting, "NetCoreRuntime", "dotnet"), report.Runtime!.Path);
        Assert.Equal(Path.Combine(scripting, "DotNetSdkRoslyn", "csc.dll"), report.Roslyn!.Path);
        Assert.Equal(Path.Combine(scripting, "MonoBleedingEdge", "lib", "mono", "4.5", "mcs.exe"), report.Mcs!.Path);
    }

    [Fact]
    public void Locate_WindowsLayout_ReportsLayoutCWithExeNames()
    {
        using var temp = new TempDirectory();
        string editor = Path.Combine(temp.Path, "2021.3.45f2", "Editor");
        string executable = CreateFile(Path.Combine(editor, "Unity.exe"));
        string data = Path.Combine(editor, "Data");
        CreateToolchain(data, BundledToolchainPlatform.Windows);

        BundledToolchainReport report = BundledToolchainLocator.Locate(executable, BundledToolchainPlatform.Windows, FakeVersionReader);

        Assert.True(report.Complete);
        Assert.Equal("C", report.Layout);
        Assert.Equal(data, report.ContentsPath);
        Assert.Equal(Path.Combine(data, "NetCoreRuntime", "dotnet.exe"), report.Runtime!.Path);
        Assert.Equal(Path.Combine(data, "DotNetSdkRoslyn", "csc.dll"), report.Roslyn!.Path);
        Assert.Equal(Path.Combine(data, "MonoBleedingEdge", "lib", "mono", "4.5", "mcs.exe"), report.Mcs!.Path);
    }

    [Fact]
    public void Locate_Windows6000Layout_ReportsLayoutD()
    {
        using var temp = new TempDirectory();
        string editor = Path.Combine(temp.Path, "6000.3.10f1", "Editor");
        string executable = CreateFile(Path.Combine(editor, "Unity.exe"));
        string scripting = Path.Combine(editor, "Data", "Resources", "Scripting");
        CreateToolchain(scripting, BundledToolchainPlatform.Windows);

        BundledToolchainReport report = BundledToolchainLocator.Locate(executable, BundledToolchainPlatform.Windows, FakeVersionReader);

        Assert.True(report.Complete);
        Assert.Equal("D", report.Layout);
        Assert.Equal(scripting, report.ToolchainRoot);
        Assert.Equal(Path.Combine(scripting, "NetCoreRuntime", "dotnet.exe"), report.Runtime!.Path);
    }

    [Fact]
    public void Locate_WindowsLayoutWithUnixMuxerName_ReportsRuntimeMissing()
    {
        using var temp = new TempDirectory();
        string editor = Path.Combine(temp.Path, "Editor");
        string executable = CreateFile(Path.Combine(editor, "Unity.exe"));
        string data = Path.Combine(editor, "Data");
        CreateToolchain(data, BundledToolchainPlatform.MacOS);

        BundledToolchainReport report = BundledToolchainLocator.Locate(executable, BundledToolchainPlatform.Windows, FakeVersionReader);

        Assert.False(report.Complete);
        Assert.Equal("C", report.Layout);
        Assert.False(report.Runtime!.Found);
        Assert.Contains("dotnet.exe", report.Runtime.Error);
        Assert.True(report.Roslyn!.Found);
        Assert.True(report.Mcs!.Found);
    }

    [Theory]
    [InlineData("")]
    [InlineData(ScriptingRoot)]
    public void Locate_LinuxLayouts_ReportLayoutE(string relativeRoot)
    {
        using var temp = new TempDirectory();
        string editor = Path.Combine(temp.Path, "Editor");
        string executable = CreateFile(Path.Combine(editor, "Unity"));
        string data = Path.Combine(editor, "Data");
        string root = Combine(data, relativeRoot);
        CreateToolchain(root, BundledToolchainPlatform.Linux);

        BundledToolchainReport report = BundledToolchainLocator.Locate(executable, BundledToolchainPlatform.Linux, FakeVersionReader);

        Assert.True(report.Complete);
        Assert.Equal("E", report.Layout);
        Assert.Equal(root, report.ToolchainRoot);
        Assert.Equal(Path.Combine(root, "NetCoreRuntime", "dotnet"), report.Runtime!.Path);
    }

    [Theory]
    [InlineData(BundledToolchainLocator.CodeAnalysisCSharpFileName)]
    [InlineData(BundledToolchainLocator.CodeAnalysisFileName)]
    [InlineData(BundledToolchainLocator.CscFileName)]
    public void Locate_MissingRoslynFile_ReportsRoslynFailureAndKeepsOtherComponents(string missingFile)
    {
        using var temp = new TempDirectory();
        string contents = Path.Combine(temp.Path, "Unity.app", "Contents");
        string executable = CreateMacExecutable(contents);
        CreateToolchain(contents, BundledToolchainPlatform.MacOS);
        File.Delete(Path.Combine(contents, "DotNetSdkRoslyn", missingFile));

        BundledToolchainReport report = BundledToolchainLocator.Locate(executable, BundledToolchainPlatform.MacOS, FakeVersionReader);

        Assert.False(report.Complete);
        Assert.Null(report.Error);
        Assert.Equal("A", report.Layout);
        Assert.False(report.Roslyn!.Found);
        Assert.Contains(missingFile, report.Roslyn.Error);
        Assert.Null(report.Roslyn.Version);
        Assert.True(report.Runtime!.Found);
        Assert.Equal("6.0.21", report.Runtime.Version);
        Assert.True(report.Mcs!.Found);
    }

    [Fact]
    public void Locate_MissingRuntime_ReportsRuntimeFailureAndKeepsOtherComponents()
    {
        using var temp = new TempDirectory();
        string contents = Path.Combine(temp.Path, "Unity.app", "Contents");
        string executable = CreateMacExecutable(contents);
        CreateToolchain(contents, BundledToolchainPlatform.MacOS);
        Directory.Delete(Path.Combine(contents, "NetCoreRuntime"), recursive: true);

        BundledToolchainReport report = BundledToolchainLocator.Locate(executable, BundledToolchainPlatform.MacOS, FakeVersionReader);

        Assert.False(report.Complete);
        Assert.Equal("A", report.Layout);
        Assert.False(report.Runtime!.Found);
        Assert.Contains("dotnet muxer not found", report.Runtime.Error);
        Assert.True(report.Roslyn!.Found);
        Assert.Equal(RoslynVersion, report.Roslyn.Version);
        Assert.True(report.Mcs!.Found);
    }

    [Fact]
    public void Locate_MuxerWithoutSharedFramework_ReportsMissingVersion()
    {
        using var temp = new TempDirectory();
        string contents = Path.Combine(temp.Path, "Unity.app", "Contents");
        string executable = CreateMacExecutable(contents);
        CreateToolchain(contents, BundledToolchainPlatform.MacOS);
        Directory.Delete(Path.Combine(contents, "NetCoreRuntime", "shared"), recursive: true);

        BundledToolchainReport report = BundledToolchainLocator.Locate(executable, BundledToolchainPlatform.MacOS, FakeVersionReader);

        Assert.False(report.Runtime!.Found);
        Assert.Equal(Path.Combine(contents, "NetCoreRuntime", "dotnet"), report.Runtime.Path);
        Assert.Null(report.Runtime.Version);
        Assert.Contains("Microsoft.NETCore.App", report.Runtime.Error);
    }

    [Fact]
    public void Locate_MultipleSharedFrameworkVersions_PicksHighestValidOne()
    {
        using var temp = new TempDirectory();
        string contents = Path.Combine(temp.Path, "Unity.app", "Contents");
        string executable = CreateMacExecutable(contents);
        CreateToolchain(contents, BundledToolchainPlatform.MacOS, frameworkVersions: ["6.0.9", "6.0.21", "6.0.3"]);

        BundledToolchainReport report = BundledToolchainLocator.Locate(executable, BundledToolchainPlatform.MacOS, FakeVersionReader);

        Assert.Equal("6.0.21", report.Runtime!.Version);
    }

    [Theory]
    [InlineData("6.0.21", new[] { "6.0.9", "6.0.21", "6.0.3" })]
    [InlineData("6.0.21", new[] { "6.0.21-rc.2.1", "6.0.21" })]
    [InlineData("7.0.0-rc.2", new[] { "6.0.21", "7.0.0-rc.1", "7.0.0-rc.2" })]
    [InlineData("6.0.21", new[] { "6.0.21", "latest", "6.0", "6.0.21.1", "6.0.30-" })]
    public void SelectSharedFrameworkVersion_IgnoresInvalidNamesAndOrdersNumerically(string expected, string[] versions)
    {
        using var temp = new TempDirectory();
        foreach (string version in versions)
        {
            CreateFile(Path.Combine(temp.Path, version, "System.Private.CoreLib.dll"));
        }

        Assert.Equal(expected, BundledToolchainLocator.SelectSharedFrameworkVersion(temp.Path));
    }

    [Fact]
    public void SelectSharedFrameworkVersion_SkipsVersionDirectoryWithoutCoreLib()
    {
        using var temp = new TempDirectory();
        CreateFile(Path.Combine(temp.Path, "6.0.21", "System.Private.CoreLib.dll"));
        Directory.CreateDirectory(Path.Combine(temp.Path, "8.0.0"));

        Assert.Equal("6.0.21", BundledToolchainLocator.SelectSharedFrameworkVersion(temp.Path));
    }

    [Fact]
    public void Locate_FirstCompleteCandidateWins_EvenWhenAnEarlierOneIsPartial()
    {
        using var temp = new TempDirectory();
        string contents = Path.Combine(temp.Path, "Unity.app", "Contents");
        string executable = CreateMacExecutable(contents);
        string scripting = Path.Combine(contents, "Resources", "Scripting");
        CreateToolchain(contents, BundledToolchainPlatform.MacOS);
        File.Delete(Path.Combine(contents, "MonoBleedingEdge", "lib", "mono", "4.5", "mcs.exe"));
        CreateToolchain(scripting, BundledToolchainPlatform.MacOS);

        BundledToolchainReport report = BundledToolchainLocator.Locate(executable, BundledToolchainPlatform.MacOS, FakeVersionReader);

        Assert.True(report.Complete);
        Assert.Equal("B", report.Layout);
    }

    [Fact]
    public void Locate_BothCandidatesComplete_PrefersFirst()
    {
        using var temp = new TempDirectory();
        string contents = Path.Combine(temp.Path, "Unity.app", "Contents");
        string executable = CreateMacExecutable(contents);
        CreateToolchain(contents, BundledToolchainPlatform.MacOS);
        CreateToolchain(Path.Combine(contents, "Resources", "Scripting"), BundledToolchainPlatform.MacOS);

        BundledToolchainReport report = BundledToolchainLocator.Locate(executable, BundledToolchainPlatform.MacOS, FakeVersionReader);

        Assert.Equal("A", report.Layout);
    }

    [Fact]
    public void Locate_NoToolchainAnywhere_ReportsEveryComponentWithCheckedRoots()
    {
        using var temp = new TempDirectory();
        string contents = Path.Combine(temp.Path, "Unity.app", "Contents");
        string executable = CreateMacExecutable(contents);

        BundledToolchainReport report = BundledToolchainLocator.Locate(executable, BundledToolchainPlatform.MacOS, FakeVersionReader);

        Assert.False(report.Complete);
        Assert.Null(report.Error);
        Assert.Null(report.Layout);
        Assert.Equal(contents, report.ContentsPath);
        foreach (BundledToolComponent component in new[] { report.Runtime!, report.Roslyn!, report.Mcs! })
        {
            Assert.False(component.Found);
            Assert.Contains(contents, component.Error);
            Assert.Contains(Path.Combine(contents, "Resources", "Scripting"), component.Error);
        }
    }

    [Fact]
    public void Locate_PathsWithSpacesAndNonAscii_AreFound()
    {
        using var temp = new TempDirectory();
        string contents = Path.Combine(temp.Path, "Unity Hub", "에디터 6000.3 (ü)", "Unity.app", "Contents");
        string executable = CreateMacExecutable(contents);
        string scripting = Path.Combine(contents, "Resources", "Scripting");
        CreateToolchain(scripting, BundledToolchainPlatform.MacOS);

        BundledToolchainReport report = BundledToolchainLocator.Locate(executable, BundledToolchainPlatform.MacOS, FakeVersionReader);

        Assert.True(report.Complete);
        Assert.Equal("B", report.Layout);
        Assert.Equal(Path.Combine(scripting, "DotNetSdkRoslyn", "csc.dll"), report.Roslyn!.Path);
        Assert.True(File.Exists(report.Runtime!.Path));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Locate_UnknownEditorPath_ReportsReasonWithoutComponents(string? editorPath)
    {
        BundledToolchainReport report = BundledToolchainLocator.Locate(editorPath, BundledToolchainPlatform.MacOS, FakeVersionReader);

        Assert.False(report.Complete);
        Assert.Contains("unknown", report.Error);
        Assert.Null(report.Runtime);
        Assert.Null(report.Roslyn);
        Assert.Null(report.Mcs);
    }

    [Fact]
    public void Locate_NonexistentEditorPath_ReportsReason()
    {
        using var temp = new TempDirectory();
        string missing = Path.Combine(temp.Path, "nope", "Unity.app", "Contents", "MacOS", "Unity");

        BundledToolchainReport report = BundledToolchainLocator.Locate(missing, BundledToolchainPlatform.MacOS, FakeVersionReader);

        Assert.False(report.Complete);
        Assert.Contains("does not exist", report.Error);
    }

    [Fact]
    public void ResolveContentsPath_MacOs_AcceptsExecutableBundleAndContents()
    {
        using var temp = new TempDirectory();
        string bundle = Path.Combine(temp.Path, "Unity.app");
        string contents = Path.Combine(bundle, "Contents");
        string executable = CreateMacExecutable(contents);

        Assert.Equal(contents, BundledToolchainLocator.ResolveContentsPath(executable, BundledToolchainPlatform.MacOS));
        Assert.Equal(contents, BundledToolchainLocator.ResolveContentsPath(bundle, BundledToolchainPlatform.MacOS));
        Assert.Equal(contents, BundledToolchainLocator.ResolveContentsPath(bundle + Path.DirectorySeparatorChar, BundledToolchainPlatform.MacOS));
        Assert.Equal(contents, BundledToolchainLocator.ResolveContentsPath(contents, BundledToolchainPlatform.MacOS));
    }

    [Theory]
    [InlineData(BundledToolchainPlatform.Windows, "Unity.exe")]
    [InlineData(BundledToolchainPlatform.Linux, "Unity")]
    public void ResolveContentsPath_WindowsAndLinux_AcceptExecutableEditorAndDataDirectories(BundledToolchainPlatform platform, string executableName)
    {
        using var temp = new TempDirectory();
        string editor = Path.Combine(temp.Path, "Editor");
        string data = Path.Combine(editor, "Data");
        Directory.CreateDirectory(data);
        string executable = CreateFile(Path.Combine(editor, executableName));

        Assert.Equal(data, BundledToolchainLocator.ResolveContentsPath(executable, platform));
        Assert.Equal(data, BundledToolchainLocator.ResolveContentsPath(editor, platform));
        Assert.Equal(data, BundledToolchainLocator.ResolveContentsPath(data, platform));
    }

    [Fact]
    public void Locate_DefaultVersionReader_ReadsProductVersionFromRealAssembly()
    {
        // A real managed assembly stands in for csc.dll so the default FileVersionInfo path runs
        // end to end; its informational version carries no "+commit" suffix once normalized.
        using var temp = new TempDirectory();
        string contents = Path.Combine(temp.Path, "Unity.app", "Contents");
        string executable = CreateMacExecutable(contents);
        CreateToolchain(contents, BundledToolchainPlatform.MacOS);
        Assembly source = typeof(BundledToolchainLocator).Assembly;
        File.Copy(source.Location, Path.Combine(contents, "DotNetSdkRoslyn", "csc.dll"), overwrite: true);
        string informational = source.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
        string expected = informational.Split('+')[0];

        BundledToolchainReport report = BundledToolchainLocator.Locate(executable, BundledToolchainPlatform.MacOS);

        Assert.True(report.Roslyn!.Found);
        Assert.Equal(expected, report.Roslyn.Version);
        Assert.DoesNotContain("+", report.Roslyn.Version);
        // The fake mcs.exe is not a PE file; an unreadable version is reported as null, not thrown.
        Assert.True(report.Mcs!.Found);
        Assert.Null(report.Mcs.Version);
    }

    [Fact]
    public void ReadProductVersion_MissingFile_ReturnsNull()
    {
        using var temp = new TempDirectory();

        Assert.Null(BundledToolchainLocator.ReadProductVersion(Path.Combine(temp.Path, "missing.dll")));
    }

    internal static string CreateMacExecutable(string contents)
    {
        return CreateFile(Path.Combine(contents, "MacOS", "Unity"));
    }

    /// <summary>Builds the files a toolchain root must hold; empty files are enough for presence checks.</summary>
    internal static void CreateToolchain(
        string root,
        BundledToolchainPlatform platform,
        string[]? frameworkVersions = null)
    {
        string runtime = Path.Combine(root, "NetCoreRuntime");
        CreateFile(Path.Combine(runtime, platform == BundledToolchainPlatform.Windows ? "dotnet.exe" : "dotnet"));
        foreach (string version in frameworkVersions ?? ["6.0.21"])
        {
            CreateFile(Path.Combine(runtime, "shared", "Microsoft.NETCore.App", version, "System.Private.CoreLib.dll"));
        }

        string roslyn = Path.Combine(root, "DotNetSdkRoslyn");
        CreateFile(Path.Combine(roslyn, BundledToolchainLocator.CscFileName));
        CreateFile(Path.Combine(roslyn, BundledToolchainLocator.CodeAnalysisFileName));
        CreateFile(Path.Combine(roslyn, BundledToolchainLocator.CodeAnalysisCSharpFileName));
        CreateFile(Path.Combine(root, "MonoBleedingEdge", "lib", "mono", "4.5", BundledToolchainLocator.McsFileName));
    }

    private static string CreateFile(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, string.Empty);
        return path;
    }

    private static string Combine(string basePath, string relative)
    {
        return relative.Length == 0
            ? basePath
            : Path.Combine(new[] { basePath }.Concat(relative.Split('/')).ToArray());
    }
}
