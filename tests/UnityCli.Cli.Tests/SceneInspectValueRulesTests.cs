using UnityCli.Protocol;

namespace UnityCli.Cli.Tests;

public sealed class SceneInspectValueRulesTests
{
    [Theory]
    [InlineData(true, false, true)]
    [InlineData(false, false, true)]
    [InlineData(true, true, false)]
    [InlineData(false, true, true)]
    public void ShouldWriteEnabledFlag_KeepsNonDefaultFalse(bool enabled, bool omitDefaults, bool expected)
    {
        Assert.Equal(expected, SceneInspectValueRules.ShouldWriteEnabledFlag(enabled, omitDefaults));
    }

    [Fact]
    public void ShouldWriteEnabledFlag_SkipsComponentsWithoutEnabled()
    {
        Assert.False(SceneInspectValueRules.ShouldWriteEnabledFlag(null, omitDefaults: false));
        Assert.False(SceneInspectValueRules.ShouldWriteEnabledFlag(null, omitDefaults: true));
    }

    [Theory]
    [InlineData("m_Enabled", false, false)]
    [InlineData("m_Enabled", true, true)]
    [InlineData("enabled", false, false)]
    [InlineData("m_IsTrigger", false, true)]
    [InlineData("m_IsTrigger", true, false)]
    public void ShouldPruneBoolean_TreatsEnabledFalseAsNonDefault(string propertyName, bool value, bool prune)
    {
        Assert.Equal(prune, SceneInspectValueRules.ShouldPruneBoolean(propertyName, value));
    }

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public void ShouldWritePrefabFlag_OmitsFalseOnly(bool value, bool omitDefaults, bool expected)
    {
        Assert.Equal(expected, SceneInspectValueRules.ShouldWritePrefabFlag(value, omitDefaults));
    }
}
