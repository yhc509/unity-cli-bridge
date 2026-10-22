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
    public void Unset_IsAutoAndStaysOnCodeDom()
    {
        ExecuteCompilerSettings settings = Parse();

        Assert.Equal(ExecuteCompilerMode.Auto, settings.Mode);
        Assert.Equal("auto", settings.RawCompiler);
        Assert.False(settings.UsesRoslynHost);
        Assert.Null(settings.CodeDomReason);
        Assert.Equal(RoslynHostPrewarmPolicy.Auto, settings.Prewarm);
        Assert.Equal(1500, settings.HostWaitMs);
        Assert.Equal(10000, settings.CompileTimeoutMs);
        Assert.Equal(0, settings.IdleExitMinutes);
        Assert.Empty(settings.Warnings);
    }

    [Theory]
    [InlineData("auto", "Auto")]
    [InlineData("AUTO", "Auto")]
    [InlineData("codedom", "CodeDom")]
    [InlineData(" CodeDom ", "CodeDom")]
    public void AutoAndCodeDom_DoNotUseTheHost(string raw, string expectedName)
    {
        var expected = Enum.Parse<ExecuteCompilerMode>(expectedName);
        ExecuteCompilerSettings settings = Parse((ExecuteCompilerSettings.CompilerVariable, raw));

        Assert.Equal(expected, settings.Mode);
        Assert.False(settings.UsesRoslynHost);
        Assert.Null(settings.CodeDomReason);
        Assert.Empty(settings.Warnings);
    }

    [Theory]
    [InlineData("roslyn-host")]
    [InlineData("Roslyn-Host")]
    public void RoslynHost_IsTheOnlyOptIn(string raw)
    {
        ExecuteCompilerSettings settings = Parse((ExecuteCompilerSettings.CompilerVariable, raw));

        Assert.Equal(ExecuteCompilerMode.RoslynHost, settings.Mode);
        Assert.True(settings.UsesRoslynHost);
        Assert.Null(settings.CodeDomReason);
    }

    [Fact]
    public void CscShared_FallsBackToCodeDomWithAReason()
    {
        ExecuteCompilerSettings settings = Parse((ExecuteCompilerSettings.CompilerVariable, "csc-shared"));

        Assert.Equal(ExecuteCompilerMode.CscShared, settings.Mode);
        Assert.False(settings.UsesRoslynHost);
        Assert.StartsWith("csc-shared-unavailable: ", settings.CodeDomReason);
    }

    [Fact]
    public void UnknownCompiler_IsAutoWithAWarningAndReason()
    {
        ExecuteCompilerSettings settings = Parse((ExecuteCompilerSettings.CompilerVariable, "fastest"));

        Assert.Equal(ExecuteCompilerMode.Auto, settings.Mode);
        Assert.False(settings.UsesRoslynHost);
        Assert.Single(settings.Warnings);
        Assert.Contains("UCB_EXECUTE_COMPILER='fastest'", settings.Warnings[0]);
        Assert.StartsWith("invalid-setting: UCB_EXECUTE_COMPILER", settings.CodeDomReason);
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
        Assert.Null(settings.CodeDomReason);
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
        Assert.Null(settings.CodeDomReason);
    }

    [Fact]
    public void Default_MatchesAnEmptyEnvironment()
    {
        Assert.Equal(ExecuteCompilerMode.Auto, ExecuteCompilerSettings.Default.Mode);
        Assert.False(ExecuteCompilerSettings.Default.UsesRoslynHost);
    }
}
