using UnityCli.Protocol;

namespace UnityCli.RoslynHost.Tests;

public sealed class BundledDotnetProcessTests
{
    [Theory]
    [InlineData("DOTNET_ROOT")]
    [InlineData("DOTNET_ROOT(x86)")]
    [InlineData("DOTNET_ROOT_X64")]
    [InlineData("DOTNET_ROOT_ARM64")]
    [InlineData("DOTNET_HOST_PATH")]
    [InlineData("DOTNET_ROLL_FORWARD")]
    [InlineData("DOTNET_ROLL_FORWARD_TO_PRERELEASE")]
    [InlineData("DOTNET_ADDITIONAL_DEPS")]
    [InlineData("DOTNET_SHARED_STORE")]
    [InlineData("DOTNET_STARTUP_HOOKS")]
    [InlineData("COMPlus_TieredCompilation")]
    [InlineData("COMPLUS_gcServer")]
    [InlineData("DOTNET_gcServer")]
    [InlineData("DOTNET_GCgen0size")]
    [InlineData("MSBuildSDKsPath")]
    [InlineData("MSBUILD_EXE_PATH")]
    [InlineData("dotnet_root")]
    public void ShouldRemove_CoversTheListedVariablesAndPrefixes(string name)
    {
        Assert.True(BundledDotnetProcess.ShouldRemove(name));
    }

    [Theory]
    [InlineData("PATH")]
    [InlineData("HOME")]
    [InlineData("DOTNET_ROOTX")]
    [InlineData("DOTNET_CLI_HOME")]
    [InlineData("DOTNET_TieredCompilation")]
    [InlineData("UNITY_CLI_REGISTRY_PATH")]
    [InlineData("")]
    public void ShouldRemove_LeavesEverythingElse(string name)
    {
        Assert.False(BundledDotnetProcess.ShouldRemove(name));
    }

    [Fact]
    public void SanitizeEnvironment_RemovesSteeringVariables_AndForcesTheBundledDefaults()
    {
        var environment = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["PATH"] = "/opt/homebrew/bin:/usr/bin",
            ["DOTNET_ROOT"] = "/usr/local/share/dotnet",
            ["DOTNET_ROLL_FORWARD"] = "LatestMajor",
            ["COMPlus_TieredCompilation"] = "0",
            ["DOTNET_gcServer"] = "1",
            ["MSBuildExtensionsPath"] = "/x",
            ["DOTNET_MULTILEVEL_LOOKUP"] = "1",
            ["dotnet_tieredpgo"] = "1",
            ["UNITY_CLI_INSTALL_ROOT"] = "/scratch",
        };

        BundledDotnetProcess.SanitizeEnvironment(environment);

        Assert.Equal(
            new Dictionary<string, string?>
            {
                ["PATH"] = "/opt/homebrew/bin:/usr/bin",
                ["UNITY_CLI_INSTALL_ROOT"] = "/scratch",
                ["DOTNET_MULTILEVEL_LOOKUP"] = "0",
                ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
                ["DOTNET_NOLOGO"] = "1",
                ["DOTNET_TieredPGO"] = "0",
            },
            environment.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToDictionary(pair => pair.Key, pair => pair.Value));
    }

    [Fact]
    public void CreateStartInfo_RequiresAnAbsoluteExistingMuxer()
    {
        Assert.Throws<ArgumentException>(() => BundledDotnetProcess.CreateStartInfo("dotnet", new[] { "--info" }, null));
        Assert.Throws<FileNotFoundException>(() => BundledDotnetProcess.CreateStartInfo(
            Path.Combine(Path.GetTempPath(), "no-such-dotnet-" + Guid.NewGuid().ToString("N")), new[] { "--info" }, null));
    }

    [Fact]
    public void CreateStartInfo_RedirectsStdioWithoutShellOrWindow_AndSanitizesTheEnvironment()
    {
        string muxer = Path.Combine(Path.GetTempPath(), "fake-dotnet-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(muxer, string.Empty);
        string? previous = Environment.GetEnvironmentVariable("DOTNET_ROLL_FORWARD");
        try
        {
            Environment.SetEnvironmentVariable("DOTNET_ROLL_FORWARD", "LatestMajor");

            var startInfo = BundledDotnetProcess.CreateStartInfo(muxer, new[] { "exec", "/a b/host.dll", "--x" }, Path.GetTempPath());

            Assert.Equal(muxer, startInfo.FileName);
            Assert.Equal("exec \"/a b/host.dll\" --x", startInfo.Arguments);
            Assert.False(startInfo.UseShellExecute);
            Assert.True(startInfo.CreateNoWindow);
            Assert.True(startInfo.RedirectStandardInput && startInfo.RedirectStandardOutput && startInfo.RedirectStandardError);
            Assert.False(startInfo.Environment.ContainsKey("DOTNET_ROLL_FORWARD"));
            Assert.Equal("0", startInfo.Environment["DOTNET_TieredPGO"]);
        }
        finally
        {
            Environment.SetEnvironmentVariable("DOTNET_ROLL_FORWARD", previous);
            File.Delete(muxer);
        }
    }

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("", "\"\"")]
    [InlineData("has space", "\"has space\"")]
    [InlineData("C:\\Program Files\\Unity\\", "\"C:\\Program Files\\Unity\\\\\"")]
    [InlineData("say \"hi\"", "\"say \\\"hi\\\"\"")]
    [InlineData("back\\\"quote", "\"back\\\\\\\"quote\"")]
    [InlineData("C:\\no\\spaces", "C:\\no\\spaces")]
    [InlineData("/Applications/Unity/Hub/Editor/6000.3.10f1/Unity.app/Contents/Resources/Scripting/DotNetSdkRoslyn/csc.dll", "/Applications/Unity/Hub/Editor/6000.3.10f1/Unity.app/Contents/Resources/Scripting/DotNetSdkRoslyn/csc.dll")]
    public void QuoteArgument_FollowsTheStandardArgvRules(string argument, string expected)
    {
        Assert.Equal(expected, BundledDotnetProcess.QuoteArgument(argument));
    }
}
