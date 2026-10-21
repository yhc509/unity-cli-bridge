using UnityCli.Protocol;
using Xunit;

namespace UnityCli.Cli.Tests;

public class ProfileMemoryCatalogTests
{
    [Fact]
    public void ProfileMemory_IsLiveWireCommand_NoForce_NoGraphics()
    {
        var descriptor = CliCommandCatalog.FindByCommand("profile memory");
        Assert.NotNull(descriptor);
        Assert.Equal(ProtocolConstants.CommandProfileMemory, descriptor!.ProtocolCommand);
        Assert.Equal(ForceRule.None, descriptor.ForceRule);
        Assert.True(descriptor.CanUseLive);
        Assert.False(descriptor.CanUseLocal);
        Assert.False(CliCommandCatalog.RequiresGraphics(ProtocolConstants.CommandProfileMemory));
    }

    [Fact]
    public void ProfileMemoryCompare_IsLocalOnly()
    {
        var descriptor = CliCommandCatalog.FindByCommand("profile memory compare");
        Assert.NotNull(descriptor);
        Assert.Null(descriptor!.ProtocolCommand);
        Assert.True(descriptor.CanUseLocal);
        Assert.False(descriptor.CanUseLive);
        Assert.Equal(ForceRule.None, descriptor.ForceRule);
    }

    [Fact]
    public void ProfileMemorySnapshot_IsLiveWireCommand_NoForce_NoGraphics()
    {
        var descriptor = CliCommandCatalog.FindByCommand("profile memory snapshot");
        Assert.NotNull(descriptor);
        Assert.Equal(ProtocolConstants.CommandProfileMemorySnapshot, descriptor!.ProtocolCommand);
        Assert.Equal(ForceRule.None, descriptor.ForceRule);
        Assert.True(descriptor.CanUseLive);
        Assert.False(descriptor.CanUseLocal);
        Assert.False(CliCommandCatalog.RequiresGraphics(ProtocolConstants.CommandProfileMemorySnapshot));
    }

    [Fact]
    public void ProfileMemory_IsInSupportedProtocolCommands()
    {
        Assert.Contains(ProtocolConstants.CommandProfileMemory, CliCommandCatalog.GetSupportedProtocolCommands());
    }

    [Fact]
    public void ProfileMemoryTop_IsLiveWireCommand_NoForce_NoGraphics()
    {
        var descriptor = CliCommandCatalog.FindByCommand("profile memory top");
        Assert.NotNull(descriptor);
        Assert.Equal(ProtocolConstants.CommandProfileMemoryTop, descriptor!.ProtocolCommand);
        Assert.Equal(ForceRule.None, descriptor.ForceRule);
        Assert.True(descriptor.CanUseLive);
        Assert.False(descriptor.CanUseLocal);
        Assert.True(descriptor.IsAllowedWhileBusy);
        Assert.False(CliCommandCatalog.RequiresGraphics(ProtocolConstants.CommandProfileMemoryTop));
        Assert.Contains(ProtocolConstants.CommandProfileMemoryTop, CliCommandCatalog.GetSupportedProtocolCommands());
        Assert.Contains("Editor-measured", descriptor.Summary);
    }
}
