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
    public void Unset_IsAutoAndUsesTheHostChain()
    {
        ExecuteCompilerSettings settings = Parse();

        Assert.Equal(ExecuteCompilerMode.Auto, settings.Mode);
        Assert.Equal("auto", settings.RawCompiler);
        Assert.True(settings.UsesRoslynHost);
        Assert.True(settings.UsesCscShared);
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
    [InlineData("roslyn-host")]
    [InlineData("Roslyn-Host")]
    public void AutoAndRoslynHost_AreTheSameHostChain(string raw)
    {
        ExecuteCompilerSettings settings = Parse((ExecuteCompilerSettings.CompilerVariable, raw));

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
    [InlineData(null, true, true)]
    [InlineData("", true, true)]
    [InlineData("auto", true, true)]
    [InlineData("fastest", true, true)]
    [InlineData("roslyn-host", true, true)]
    [InlineData("csc-shared", false, true)]
    [InlineData("codedom", false, false)]
    public void Chain_ForEverySetting(string? raw, bool host, bool cscShared)
    {
        ExecuteCompilerSettings settings = raw == null ? Parse() : Parse((ExecuteCompilerSettings.CompilerVariable, raw));

        Assert.Equal(host, settings.UsesRoslynHost);
        Assert.Equal(cscShared, settings.UsesCscShared);
    }

    [Fact]
    public void UnknownCompiler_IsAutoWithAWarningAndReason()
    {
        ExecuteCompilerSettings settings = Parse((ExecuteCompilerSettings.CompilerVariable, "fastest"));

        Assert.Equal(ExecuteCompilerMode.Auto, settings.Mode);
        Assert.True(settings.UsesRoslynHost);
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
        Assert.True(ExecuteCompilerSettings.Default.UsesRoslynHost);
        Assert.True(ExecuteCompilerSettings.Default.UsesCscShared);
    }
}
