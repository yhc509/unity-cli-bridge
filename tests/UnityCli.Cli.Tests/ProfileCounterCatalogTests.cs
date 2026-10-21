using System.Linq;
using UnityCli.Protocol;
using Xunit;

namespace UnityCli.Cli.Tests;

public class ProfileCounterCatalogTests
{
    [Fact]
    public void MemoryReport_IncludesExtendedCountersWithoutDroppingTheOriginalSet()
    {
        string[] names = Names(ProfileCounterCatalog.MemoryReport);
        Assert.Contains("Total Used Memory", names);
        Assert.Contains("Texture Count", names);
        Assert.Contains("Object Count", names);
        Assert.Contains("App Resident Memory", names);
        Assert.Contains("App Committed Memory", names);
        Assert.Contains("AudioClip Count", names);
        Assert.Contains("AudioClip Memory", names);
        Assert.Contains("Physics Used Memory", names);
        Assert.Contains("Profiler Used Memory", names);
        Assert.Contains("Profiler Reserved Memory", names);
        Assert.Contains("System Total Used Memory", names);
        Assert.Equal(names.Length, names.Distinct().Count());
        Assert.All(ProfileCounterCatalog.MemoryReport, counter =>
            Assert.True(counter.Unit is "bytes" or "count"));
    }

    [Fact]
    public void StatsPresets_IncludeTheExtendedRenderAndMemoryCounters()
    {
        string[] render = Names(ProfileCounterCatalog.RenderStats);
        Assert.Contains("Draw Calls Count", render);
        Assert.Contains("Shadow Casters Count", render);
        Assert.Contains("Visible Skinned Meshes Count", render);
        Assert.Contains("Render Textures Count", render);
        Assert.Contains("Render Textures Bytes", render);
        Assert.Contains("Used Textures Count", render);
        Assert.Contains("Used Textures Bytes", render);

        string[] memory = Names(ProfileCounterCatalog.MemoryStats);
        Assert.Contains("Texture Memory", memory);
        Assert.Contains("App Resident Memory", memory);
        Assert.Contains("AudioClip Memory", memory);
        Assert.Contains("Physics Used Memory", memory);
        Assert.Contains("System Total Used Memory", memory);
        Assert.Equal(render.Length, render.Distinct().Count());
        Assert.Equal(memory.Length, memory.Distinct().Count());
    }

    private static string[] Names(ProfileCounterName[] counters)
    {
        return counters.Select(counter => counter.Name).ToArray();
    }
}
