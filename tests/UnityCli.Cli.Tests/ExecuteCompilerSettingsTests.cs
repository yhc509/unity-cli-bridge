using UnityCli.Protocol;
using UnityCliBridge.Bridge.Editor;

namespace UnityCli.Cli.Tests;

public sealed class ExecuteCompilerSettingsTests
{
    private static ExecuteCompilerSettings Parse(params (string Name, string Value)[] values)
    {
        var map = values.ToDictionary(v => v.Name, v => v.Value);
        return ExecuteCompilerSettings.Parse(name => map.TryGetValue(name, out string? value) ? value : null);
    }

    [Fact]
    public void TheDefaultIsCodeDom()
    {
        Assert.False(ExecuteCompilerSettings.AutoUsesRoslynHost);
    }

    [Fact]
    public void Unset_IsAutoAndFollowsTheDefault()
    {
        ExecuteCompilerSettings settings = Parse();

        Assert.Equal(ExecuteCompilerMode.Auto, settings.Mode);
        Assert.Equal("auto", settings.RawCompiler);
        Assert.Equal(ExecuteCompilerSettings.AutoUsesRoslynHost, settings.UsesRoslynHost);
        Assert.Equal(ExecuteCompilerSettings.AutoUsesRoslynHost, settings.UsesCscShared);
        Assert.Null(settings.InvalidSettingReason);
        Assert.Equal(RoslynHostPrewarmPolicy.Auto, settings.Prewarm);
        Assert.Equal(1500, settings.HostWaitMs);
        Assert.Equal(10000, settings.CompileTimeoutMs);
        Assert.Equal(0, settings.IdleExitMinutes);
        Assert.Empty(settings.Warnings);
    }

    [Theory]
    [InlineData("auto")]
    [InlineData("AUTO")]
    [InlineData(" Auto ")]
    public void Auto_IsTheDefault(string raw)
    {
        ExecuteCompilerSettings settings = Parse((ExecuteCompilerSettings.CompilerVariable, raw));

        Assert.Equal(ExecuteCompilerMode.Auto, settings.Mode);
        Assert.Equal(ExecuteCompilerSettings.AutoUsesRoslynHost, settings.UsesRoslynHost);
        Assert.Equal(ExecuteCompilerSettings.AutoUsesRoslynHost, settings.UsesCscShared);
        Assert.Null(settings.InvalidSettingReason);
        Assert.Empty(settings.Warnings);
    }

    [Theory]
    [InlineData("roslyn-host")]
    [InlineData("Roslyn-Host")]
    public void RoslynHost_UsesTheHostChain(string raw)
    {
        ExecuteCompilerSettings settings = Parse((ExecuteCompilerSettings.CompilerVariable, raw));

        Assert.Equal(ExecuteCompilerMode.RoslynHost, settings.Mode);
        Assert.True(settings.UsesRoslynHost);
        Assert.True(settings.UsesCscShared);
        Assert.Null(settings.InvalidSettingReason);
        Assert.Empty(settings.Warnings);
    }

    [Theory]
    [InlineData("codedom")]
    [InlineData(" CodeDom ")]
    [InlineData("CODEDOM")]
    public void CodeDom_IsTheKillSwitch(string raw)
    {
        ExecuteCompilerSettings settings = Parse((ExecuteCompilerSettings.CompilerVariable, raw));

        Assert.Equal(ExecuteCompilerMode.CodeDom, settings.Mode);
        Assert.False(settings.UsesRoslynHost);
        Assert.False(settings.UsesCscShared);
        Assert.Null(settings.InvalidSettingReason);
        Assert.Empty(settings.Warnings);
    }

    [Fact]
    public void CscShared_StartsAtCscSharedWithoutTheHost()
    {
        ExecuteCompilerSettings settings = Parse((ExecuteCompilerSettings.CompilerVariable, "csc-shared"));

        Assert.Equal(ExecuteCompilerMode.CscShared, settings.Mode);
        Assert.False(settings.UsesRoslynHost);
        Assert.True(settings.UsesCscShared);
        Assert.Null(settings.InvalidSettingReason);
    }

    [Theory]
    [InlineData(null, null, null)]
    [InlineData("", null, null)]
    [InlineData("auto", null, null)]
    [InlineData("fastest", null, null)]
    [InlineData("roslyn-host", true, true)]
    [InlineData("csc-shared", false, true)]
    [InlineData("codedom", false, false)]
    public void Chain_ForEverySetting(string? raw, bool? host, bool? cscShared)
    {
        // null: the default decides (auto, unset, invalid).
        ExecuteCompilerSettings settings = raw == null ? Parse() : Parse((ExecuteCompilerSettings.CompilerVariable, raw));

        Assert.Equal(host ?? ExecuteCompilerSettings.AutoUsesRoslynHost, settings.UsesRoslynHost);
        Assert.Equal(cscShared ?? ExecuteCompilerSettings.AutoUsesRoslynHost, settings.UsesCscShared);
    }

    [Fact]
    public void UnknownCompiler_IsAutoWithAWarningAndReason()
    {
        ExecuteCompilerSettings settings = Parse((ExecuteCompilerSettings.CompilerVariable, "fastest"));

        Assert.Equal(ExecuteCompilerMode.Auto, settings.Mode);
        Assert.Equal(ExecuteCompilerSettings.AutoUsesRoslynHost, settings.UsesRoslynHost);
        Assert.Single(settings.Warnings);
        Assert.Contains("UCB_EXECUTE_COMPILER='fastest'", settings.Warnings[0]);
        Assert.StartsWith("invalid-setting: UCB_EXECUTE_COMPILER", settings.InvalidSettingReason);
    }

    [Theory]
    [InlineData("", "Auto")]
    [InlineData("auto", "Auto")]
    [InlineData("eager", "Eager")]
    [InlineData("LAZY", "Lazy")]
    [InlineData("off", "Off")]
    public void Prewarm_ParsesEveryPolicy(string raw, string expectedName)
    {
        var expected = Enum.Parse<RoslynHostPrewarmPolicy>(expectedName);
        ExecuteCompilerSettings settings = Parse((ExecuteCompilerSettings.PrewarmVariable, raw));

        Assert.Equal(expected, settings.Prewarm);
        Assert.Equal(expected.ToString().ToLowerInvariant(), settings.PrewarmName);
        Assert.Empty(settings.Warnings);
    }

    [Fact]
    public void Prewarm_Unknown_IsAutoWithAWarning()
    {
        ExecuteCompilerSettings settings = Parse((ExecuteCompilerSettings.PrewarmVariable, "always"));

        Assert.Equal(RoslynHostPrewarmPolicy.Auto, settings.Prewarm);
        Assert.Single(settings.Warnings);
        Assert.Null(settings.InvalidSettingReason);
    }

    [Fact]
    public void Numbers_InRangeAreUsed()
    {
        ExecuteCompilerSettings settings = Parse(
            (ExecuteCompilerSettings.HostWaitVariable, "0"),
            (ExecuteCompilerSettings.CompileTimeoutVariable, "2500"),
            (ExecuteCompilerSettings.IdleExitVariable, "15"));

        Assert.Equal(0, settings.HostWaitMs);
        Assert.Equal(2500, settings.CompileTimeoutMs);
        Assert.Equal(15, settings.IdleExitMinutes);
        Assert.Empty(settings.Warnings);
    }

    [Theory]
    [InlineData(ExecuteCompilerSettings.HostWaitVariable, "-1")]
    [InlineData(ExecuteCompilerSettings.HostWaitVariable, "60001")]
    [InlineData(ExecuteCompilerSettings.CompileTimeoutVariable, "999")]
    [InlineData(ExecuteCompilerSettings.CompileTimeoutVariable, "ten")]
    [InlineData(ExecuteCompilerSettings.IdleExitVariable, "1441")]
    [InlineData(ExecuteCompilerSettings.IdleExitVariable, "1.5")]
    public void Numbers_OutOfRangeOrInvalid_KeepTheDefaultWithAWarning(string name, string raw)
    {
        ExecuteCompilerSettings settings = Parse((name, raw));

        Assert.Equal(ExecuteCompilerSettings.DefaultHostWaitMs, settings.HostWaitMs);
        Assert.Equal(ExecuteCompilerSettings.DefaultCompileTimeoutMs, settings.CompileTimeoutMs);
        Assert.Equal(ExecuteCompilerSettings.DefaultIdleExitMinutes, settings.IdleExitMinutes);
        Assert.Single(settings.Warnings);
        Assert.StartsWith(name + "='" + raw + "'", settings.Warnings[0]);
        Assert.Null(settings.InvalidSettingReason);
    }

    [Fact]
    public void Default_MatchesAnEmptyEnvironment()
    {
        Assert.Equal(ExecuteCompilerMode.Auto, ExecuteCompilerSettings.Default.Mode);
        Assert.Equal(ExecuteCompilerSettings.AutoUsesRoslynHost, ExecuteCompilerSettings.Default.UsesRoslynHost);
        Assert.Equal(ExecuteCompilerSettings.AutoUsesRoslynHost, ExecuteCompilerSettings.Default.UsesCscShared);
    }
}

public sealed class CompileChainPlanTests
{
    private static ExecuteCompilerSettings Settings(string? compiler)
    {
        return ExecuteCompilerSettings.Parse(name => name == ExecuteCompilerSettings.CompilerVariable ? compiler : null);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("auto")]
    [InlineData("roslyn-host")]
    public void HostSettings_UseTheHostThenCscShared(string? compiler)
    {
        ExecuteCompilerSettings settings = Settings(compiler);
        CompileChainPlan plan = CompileChainPlan.Create(settings, null, null, () => null);

        Assert.Equal(settings.UsesRoslynHost, plan.UseRoslynHost);
        Assert.Equal(settings.UsesRoslynHost, plan.UseCscShared);
        Assert.Equal(compiler == "roslyn-host" || ExecuteCompilerSettings.AutoUsesRoslynHost, plan.UseRoslynHost);
        Assert.Null(plan.Reason);
        Assert.Null(plan.SkippedBackend);
    }

    [Theory]
    [InlineData("roslyn-host", "roslyn-host")]
    [InlineData("csc-shared", "csc-shared")]
    public void MissingToolchain_GoesStraightToCodeDomWithTheReason(string? compiler, string skipped)
    {
        CompileChainPlan plan = CompileChainPlan.Create(
            Settings(compiler),
            null,
            null,
            () => "bundled .NET runtime not found: No bundled NetCoreRuntime found; checked /opt/unity/Editor/Data");

        Assert.False(plan.UseRoslynHost);
        Assert.False(plan.UseCscShared);
        Assert.Equal("toolchain-missing: bundled .NET runtime not found: No bundled NetCoreRuntime found; checked /opt/unity/Editor/Data", plan.Reason);
        Assert.Equal(skipped, plan.SkippedBackend);
    }

    [Fact]
    public void MissingToolchain_SelectorCompilesOnceWithCodeDomAndNotesTheFallback()
    {
        CompileChainPlan plan = CompileChainPlan.Create(Settings("roslyn-host"), null, null, () => "bundled Roslyn not found: no csc.dll");
        var host = new CountingBackend("roslyn-host", CompileResult.InfrastructureFailure("roslyn-host", "", null, null, "must not run"));
        var cscShared = new CountingBackend("csc-shared", CompileResult.InfrastructureFailure("csc-shared", "", null, null, "must not run"));
        var codeDom = new CountingBackend("codedom", CompileResult.CompileError("codedom", "/langversion:latest", new[] { "Line 1: CS1525: Unexpected symbol `not'" }, null));
        var preferred = new List<ICompileBackend>();
        if (plan.UseRoslynHost)
        {
            preferred.Add(host);
        }

        if (plan.UseCscShared)
        {
            preferred.Add(cscShared);
        }

        CompileOutcome outcome = new CompileBackendSelector(codeDom, preferred, plan.Reason, plan.SkippedBackend)
            .Compile(new CompileRequest("class A {}", "9.0", Array.Empty<string>(), Array.Empty<string>()));

        Assert.Equal(0, host.Calls);
        Assert.Equal(0, cscShared.Calls);
        Assert.Equal(1, codeDom.Calls);
        Assert.Equal("codedom", outcome.Compiler);
        Assert.Equal("toolchain-missing: bundled Roslyn not found: no csc.dll", outcome.FallbackReason);
        Assert.EndsWith(
            "\ncompiled with mcs fallback (C# 7.x); Roslyn host unavailable: toolchain-missing: bundled Roslyn not found: no csc.dll",
            outcome.FormatFailureMessage());
    }

    [Fact]
    public void CodeDom_NeverProbesTheToolchain()
    {
        bool probed = false;

        CompileChainPlan plan = CompileChainPlan.Create(Settings("codedom"), "no host", "no csc", () =>
        {
            probed = true;
            return "missing";
        });

        Assert.False(probed);
        Assert.False(plan.UseRoslynHost);
        Assert.False(plan.UseCscShared);
        Assert.Null(plan.Reason);
        Assert.Null(plan.SkippedBackend);
    }

    [Fact]
    public void ToolchainProbeThatThrows_IsAMissingToolchain()
    {
        CompileChainPlan plan = CompileChainPlan.Create(Settings("roslyn-host"), null, null, () => throw new IOException("denied"));

        Assert.False(plan.UseRoslynHost);
        Assert.Equal("toolchain-missing: IOException: denied", plan.Reason);
    }

    [Fact]
    public void InvalidSetting_IsTheDefaultChainAndReportsTheWarning()
    {
        CompileChainPlan plan = CompileChainPlan.Create(Settings("fastest"), null, null, () => null);

        Assert.Equal(ExecuteCompilerSettings.AutoUsesRoslynHost, plan.UseRoslynHost);
        Assert.Equal(ExecuteCompilerSettings.AutoUsesRoslynHost, plan.UseCscShared);
        Assert.StartsWith("invalid-setting: UCB_EXECUTE_COMPILER='fastest'", plan.Reason);
        Assert.Null(plan.SkippedBackend);
    }

    [Fact]
    public void HostSetupFailure_StartsAtCscShared()
    {
        CompileChainPlan plan = CompileChainPlan.Create(Settings("roslyn-host"), "IOException: read-only Library", null, () => null);

        Assert.False(plan.UseRoslynHost);
        Assert.True(plan.UseCscShared);
        Assert.Equal("start-failed: IOException: read-only Library", plan.Reason);
        Assert.Equal("roslyn-host", plan.SkippedBackend);
    }

    [Fact]
    public void CscSharedSetupFailure_InCscSharedMode_IsCodeDomWithTheReason()
    {
        CompileChainPlan plan = CompileChainPlan.Create(Settings("csc-shared"), "unused", "not started in a secondary editor process", () => null);

        Assert.False(plan.UseRoslynHost);
        Assert.False(plan.UseCscShared);
        Assert.Equal("csc-shared-unavailable: not started in a secondary editor process", plan.Reason);
        Assert.Equal("csc-shared", plan.SkippedBackend);
    }

    [Fact]
    public void DescribeMissingToolchain_NeedsTheRuntimeAndRoslynOnly()
    {
        var runtime = new BundledToolComponent("/U/NetCoreRuntime/dotnet", "/U/NetCoreRuntime", "6.0.21", null);
        var roslyn = new BundledToolComponent("/U/DotNetSdkRoslyn/csc.dll", "/U/DotNetSdkRoslyn", "4.3.1", null);
        var noMcs = new BundledToolComponent(null, null, null, "No bundled mcs.exe found");

        Assert.Null(CompileChainPlan.DescribeMissingToolchain(new BundledToolchainReport("/U", "/U", "E", "/U", runtime, roslyn, noMcs, null)));
        Assert.StartsWith(
            "bundled .NET runtime not found: No bundled NetCoreRuntime",
            CompileChainPlan.DescribeMissingToolchain(new BundledToolchainReport(
                "/U", "/U", null, null, new BundledToolComponent(null, null, null, "No bundled NetCoreRuntime found"), roslyn, null, null)));
        Assert.StartsWith(
            "bundled Roslyn not found: No bundled DotNetSdkRoslyn",
            CompileChainPlan.DescribeMissingToolchain(new BundledToolchainReport(
                "/U", "/U", "E", "/U", runtime, new BundledToolComponent(null, null, null, "No bundled DotNetSdkRoslyn found"), null, null)));
        Assert.Equal(
            "bundled .NET runtime not found: Unity editor path is unknown",
            CompileChainPlan.DescribeMissingToolchain(new BundledToolchainReport(null, null, null, null, null, null, null, "Unity editor path is unknown")));
    }

    [Fact]
    public void LinuxInstallWithoutTheBundledTools_TheDefaultGoesStraightToMcs()
    {
        string root = Path.Combine(Path.GetTempPath(), "ucb-linux-" + Guid.NewGuid().ToString("N"));
        string data = Path.Combine(root, "Editor", "Data");
        Directory.CreateDirectory(Path.Combine(data, "MonoBleedingEdge", "lib", "mono", "4.5"));
        File.WriteAllText(Path.Combine(root, "Editor", "Unity"), string.Empty);
        try
        {
            BundledToolchainReport report = BundledToolchainLocator.Locate(Path.Combine(root, "Editor", "Unity"), BundledToolchainPlatform.Linux);

            string? missing = CompileChainPlan.DescribeMissingToolchain(report);
            CompileChainPlan plan = CompileChainPlan.Create(Settings(null), null, null, () => missing);
            CompileChainPlan optIn = CompileChainPlan.Create(Settings("roslyn-host"), null, null, () => missing);

            Assert.StartsWith("bundled .NET runtime not found: ", missing);
            Assert.False(plan.UseRoslynHost);
            Assert.False(plan.UseCscShared);
            if (ExecuteCompilerSettings.AutoUsesRoslynHost)
            {
                Assert.StartsWith("toolchain-missing: bundled .NET runtime not found: ", plan.Reason);
            }
            else
            {
                Assert.Null(plan.Reason);
            }

            Assert.False(optIn.UseRoslynHost);
            Assert.False(optIn.UseCscShared);
            Assert.StartsWith("toolchain-missing: bundled .NET runtime not found: ", optIn.Reason);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private sealed class CountingBackend : ICompileBackend
    {
        private readonly CompileResult _result;

        public CountingBackend(string id, CompileResult result)
        {
            Id = id;
            _result = result;
        }

        public string Id { get; }

        public int Calls { get; private set; }

        public CompileResult TryCompile(CompileRequest request)
        {
            Calls++;
            return _result;
        }
    }
}
