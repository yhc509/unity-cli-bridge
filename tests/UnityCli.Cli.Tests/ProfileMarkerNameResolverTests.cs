using UnityCli.Protocol;

namespace UnityCli.Cli.Tests;

public sealed class ProfileMarkerNameResolverTests
{
    private const string Threat = "Assembly-CSharp.dll!::ThreatField.Update() [Invoke]";
    private const string Minimap = "Assembly-CSharp.dll!Game.Ui::MinimapView.Update() [Invoke]";
    private const string Hud = "Assembly-CSharp.dll!::SkirmishHud.LateUpdate() [Invoke]";

    private static readonly string[] Registered = { "PlayerLoop", Threat, Minimap, Hud, "GC.Alloc", Threat };

    [Theory]
    [InlineData(Threat, "ThreatField.Update")]
    [InlineData(Minimap, "MinimapView.Update")]
    [InlineData("ThreatField.Update()", "ThreatField.Update")]
    [InlineData("PlayerLoop", "PlayerLoop")]
    [InlineData("", "")]
    public void Normalize_StripsAssemblyNamespaceInvokeAndParentheses(string name, string expected)
    {
        Assert.Equal(expected, ProfileMarkerNameResolver.Normalize(name));
    }

    [Fact]
    public void Resolve_ExactNameWins()
    {
        ProfileMarkerResolution resolution = ProfileMarkerNameResolver.Resolve(Registered, Threat);

        Assert.Equal(ProfileMarkerMatchKind.Exact, resolution.Kind);
        Assert.Equal(Threat, resolution.Marker);
        Assert.True(resolution.IsMatch);
    }

    [Theory]
    [InlineData("ThreatField.Update")]
    [InlineData("ThreatField.Update()")]
    [InlineData("Game.Ui::ThreatField.Update")]
    public void Resolve_ShortScriptName_ResolvesTheFullName(string query)
    {
        ProfileMarkerResolution resolution = ProfileMarkerNameResolver.Resolve(Registered, query);

        Assert.Equal(ProfileMarkerMatchKind.Resolved, resolution.Kind);
        Assert.Equal(Threat, resolution.Marker);
    }

    [Fact]
    public void Resolve_SeveralMatches_IsAmbiguousWithSortedCandidates()
    {
        ProfileMarkerResolution resolution = ProfileMarkerNameResolver.Resolve(Registered, "Update");

        Assert.Equal(ProfileMarkerMatchKind.Ambiguous, resolution.Kind);
        Assert.Equal(new[] { Threat, Minimap }, resolution.Candidates);
        Assert.Equal(ProtocolConstants.ErrorProfileMarkerAmbiguous, resolution.ErrorCode);
        string message = resolution.BuildMessage("registered profiler marker", "the Editor's registered profiler markers");
        Assert.StartsWith("Marker `Update` matches more than one registered profiler marker.", message);
        Assert.Contains(Minimap, message);
    }

    [Fact]
    public void Resolve_Typo_ListsSimilarNamesClosestFirst()
    {
        ProfileMarkerResolution resolution = ProfileMarkerNameResolver.Resolve(Registered, "ThreatFeld.Update");

        Assert.Equal(ProfileMarkerMatchKind.NotFound, resolution.Kind);
        Assert.Equal(ProtocolConstants.ErrorProfileNotFound, resolution.ErrorCode);
        Assert.Equal(Threat, resolution.Similar[0]);
        Assert.True(resolution.Similar.Length <= ProfileMarkerNameResolver.SimilarNameLimit);
        Assert.Equal(resolution.Similar.Distinct().Count(), resolution.Similar.Length);
        Assert.Contains("Similar names:", resolution.BuildMessage("capture marker", "the capture"));
    }

    [Fact]
    public void Resolve_NoNames_SaysTheSourceHasNoMarkers()
    {
        ProfileMarkerResolution resolution = ProfileMarkerNameResolver.Resolve(Array.Empty<string>(), "ThreatField.Update");

        Assert.Equal(ProfileMarkerMatchKind.NotFound, resolution.Kind);
        Assert.Equal(
            "Marker `ThreatField.Update` is not in the capture. The capture has no markers.",
            resolution.BuildMessage("capture marker", "the capture"));
    }
}
