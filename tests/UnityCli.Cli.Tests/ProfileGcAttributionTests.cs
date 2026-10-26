using UnityCli.Protocol;

namespace UnityCli.Cli.Tests;

public sealed class ProfileGcAttributionTests
{
    private const string Threat = "Assembly-CSharp.dll!::ThreatField.Update() [Invoke]";
    private const string Hud = "Assembly-CSharp.dll!::SkirmishHud.LateUpdate() [Invoke]";

    [Theory]
    [InlineData("PlayerLoop")]
    [InlineData("UpdateScene")]
    [InlineData("BehaviourUpdate")]
    [InlineData("Update.ScriptRunBehaviourUpdate")]
    [InlineData("GC.Alloc")]
    [InlineData("PlayerEndOfFrame")]
    [InlineData("PostLateUpdate")]
    [InlineData("PostLateUpdate.FinishFrameRendering")]
    [InlineData("EarlyUpdate.PlayerCleanupCachedData")]
    public void BuildGcTop_ExcludesContainersAndGcAlloc(string container)
    {
        var markers = new[]
        {
            new ProfileMarkerEntry { m = container, gcBytes = 9000, frames = 10 },
            new ProfileMarkerEntry { m = Threat, gcBytes = 5000, frames = 10 },
            new ProfileMarkerEntry { m = "Camera.Render", gcBytes = 0, frames = 10 },
        };

        ProfileGcEntry[] top = ProfileGcAttribution.BuildGcTop(markers, 5, out bool truncated);

        Assert.Single(top);
        Assert.Equal(Threat, top[0].marker);
        Assert.Equal(5000, top[0].bytesTotal);
        Assert.False(truncated);
    }

    [Fact]
    public void BuildGcTop_SortsByBytesThenNameAndTruncates()
    {
        var markers = new[]
        {
            new ProfileMarkerEntry { m = "B", gcBytes = 10 },
            new ProfileMarkerEntry { m = "A", gcBytes = 10 },
            new ProfileMarkerEntry { m = "C", gcBytes = 30 },
        };

        ProfileGcEntry[] top = ProfileGcAttribution.BuildGcTop(markers, 2, out bool truncated);

        Assert.Equal(new[] { "C", "A" }, top.Select(entry => entry.marker));
        Assert.True(truncated);
    }

    [Fact]
    public void ContainerList_IsTheSingleSourceUsedEverywhere()
    {
        foreach (string marker in ProfileGcAttribution.GcTopExcludedMarkers)
        {
            Assert.True(ProfileGcAttribution.IsExcludedFromGcTop(marker));
            Assert.False(ProfileGcAttribution.IsScriptMarker(marker, scriptFlag: true));
        }

        foreach (string phase in ProfileGcAttribution.PlayerLoopPhaseNames)
        {
            Assert.True(ProfileGcAttribution.IsEnginePlayerLoopMarker(phase));
            Assert.True(ProfileGcAttribution.IsEnginePlayerLoopMarker(phase + ".Subsystem"));
            Assert.False(ProfileGcAttribution.IsScriptMarker(phase, scriptFlag: true));
        }

        foreach (string endOfFrame in ProfileGcAttribution.EndOfFrameMarkerNames)
        {
            Assert.True(ProfileGcAttribution.IsEnginePlayerLoopMarker(endOfFrame));
            Assert.False(ProfileGcAttribution.IsScriptMarker(endOfFrame, scriptFlag: true));
        }
    }

    [Theory]
    [InlineData(Threat, false)]
    [InlineData("ThreatField.Update() [Invoke]", false)]
    [InlineData("Assembly-CSharp.dll!::ThreatField.Update() [Coroutine: MoveNext]", false)]
    [InlineData("Camera.Render", false)]
    [InlineData("PlayerController.Update", false)]
    public void IsEnginePlayerLoopMarker_KeepsScriptNames(string name, bool expected)
    {
        Assert.Equal(expected, ProfileGcAttribution.IsEnginePlayerLoopMarker(name));
        Assert.False(ProfileGcAttribution.IsExcludedFromGcTop(name));
    }

    [Theory]
    [InlineData(Threat, false, true)]
    [InlineData("ThreatField.Update() [Invoke]", false, true)]
    [InlineData("MyGame.Tick", true, true)]
    [InlineData("Camera.Render", false, false)]
    [InlineData("BehaviourUpdate", true, false)]
    public void IsScriptMarker_UsesFlagOrScriptDecoration(string name, bool flag, bool expected)
    {
        Assert.Equal(expected, ProfileGcAttribution.IsScriptMarker(name, flag));
    }

    [Fact]
    public void Walker_AttributesGcAllocToTheNearestScriptAncestor()
    {
        // PlayerLoop
        //   Update.ScriptRunBehaviourUpdate
        //     BehaviourUpdate
        //       ThreatField.Update [Invoke]
        //         GC.Alloc (400 B, 2 calls)
        //         MyGame.Pathfind (user marker, script flag)
        //           GC.Alloc (100 B)
        //       SkirmishHud.LateUpdate [Invoke]
        //         Physics.Raycast
        //           GC.Alloc (50 B)
        //   EditorLoop
        //     GC.Alloc (70 B)
        var tree = new FakeHierarchy();
        int playerLoop = tree.Add(0, "PlayerLoop");
        int scriptRun = tree.Add(playerLoop, "Update.ScriptRunBehaviourUpdate");
        int behaviour = tree.Add(scriptRun, "BehaviourUpdate", scriptFlag: true);
        int threat = tree.Add(behaviour, Threat, scriptFlag: true);
        tree.Add(threat, "GC.Alloc", gc: 400, calls: 2);
        int pathfind = tree.Add(threat, "MyGame.Pathfind", scriptFlag: true);
        tree.Add(pathfind, "GC.Alloc", gc: 100, calls: 1);
        int hud = tree.Add(behaviour, Hud, scriptFlag: true);
        int raycast = tree.Add(hud, "Physics.Raycast");
        tree.Add(raycast, "GC.Alloc", gc: 50, calls: 1);
        int editorLoop = tree.Add(0, "EditorLoop");
        tree.Add(editorLoop, "GC.Alloc", gc: 70, calls: 1);

        var accumulator = new ProfileGcByScriptAccumulator();
        accumulator.BeginFrame();
        Walk(tree, accumulator);
        accumulator.BeginFrame();
        Walk(tree, accumulator);

        ProfileGcScriptEntry[] entries = accumulator.Build(capturedFrames: 2);

        Assert.Equal(new[] { Threat, "MyGame.Pathfind", Hud }, entries.Select(entry => entry.marker));
        Assert.Equal(800, entries[0].bytesTotal);
        Assert.Equal(400, entries[0].bytesPerFrame);
        Assert.Equal(4, entries[0].allocations);
        Assert.Equal(2, entries[0].framesWithAlloc);
        Assert.Equal(200, entries[1].bytesTotal);
        Assert.Equal(100, entries[2].bytesTotal);
        Assert.Equal(140, accumulator.UnattributedBytes);
    }

    [Fact]
    public void Walker_EndOfFrameAndPhaseMarkers_AreNotScriptAncestors()
    {
        // PlayerLoop
        //   PostLateUpdate (script flag, as 6000.3 can flag engine phases)
        //     PlayerEndOfFrame (script flag, 440 B on its own)
        //       GC.Alloc 440
        //     ThreatField.Update [Invoke]
        //       GC.Alloc 17000
        //   PostLateUpdate.FinishFrameRendering
        //     GC.Alloc 16
        var tree = new FakeHierarchy();
        int player = tree.Add(0, "PlayerLoop");
        int post = tree.Add(player, "PostLateUpdate", scriptFlag: true);
        int end = tree.Add(post, "PlayerEndOfFrame", scriptFlag: true);
        tree.Add(end, "GC.Alloc", gc: 440, calls: 1);
        int threat = tree.Add(post, Threat, scriptFlag: true);
        tree.Add(threat, "GC.Alloc", gc: 17000, calls: 1);
        int finish = tree.Add(player, "PostLateUpdate.FinishFrameRendering");
        tree.Add(finish, "GC.Alloc", gc: 16, calls: 1);

        var accumulator = new ProfileGcByScriptAccumulator();
        accumulator.BeginFrame();
        Walk(tree, accumulator);

        ProfileGcScriptEntry[] entries = accumulator.Build(capturedFrames: 1);

        Assert.Equal(new[] { Threat }, entries.Select(entry => entry.marker));
        Assert.Equal(17000, entries[0].bytesTotal);
        Assert.Equal(456, accumulator.UnattributedBytes);
    }

    [Fact]
    public void Walker_EndOfFrameUnderAScript_AttributesToThatScript()
    {
        // PlayerLoop
        //   Update.ScriptRunBehaviourUpdate
        //     ThreatField.Update
        //       PlayerEndOfFrame
        //         GC.Alloc 440
        var tree = new FakeHierarchy();
        int player = tree.Add(0, "PlayerLoop");
        int scriptRun = tree.Add(player, "Update.ScriptRunBehaviourUpdate");
        int threat = tree.Add(scriptRun, Threat, scriptFlag: true);
        int end = tree.Add(threat, "PlayerEndOfFrame", scriptFlag: true);
        tree.Add(end, "GC.Alloc", gc: 440, calls: 1);

        var accumulator = new ProfileGcByScriptAccumulator();
        accumulator.BeginFrame();
        Walk(tree, accumulator);

        ProfileGcScriptEntry[] entries = accumulator.Build(capturedFrames: 1);

        Assert.Equal(new[] { Threat }, entries.Select(entry => entry.marker));
        Assert.Equal(440, entries[0].bytesTotal);
        Assert.Equal(0, accumulator.UnattributedBytes);
    }

    [Fact]
    public void OmitEngineMarkers_MovesPhaseBytesToUnattributedAndKeepsScripts()
    {
        var entries = new[]
        {
            new ProfileGcScriptEntry { marker = "PlayerEndOfFrame", bytesTotal = 440 },
            new ProfileGcScriptEntry { marker = Threat, bytesTotal = 17000 },
            new ProfileGcScriptEntry { marker = "PostLateUpdate", bytesTotal = 12 },
        };
        long unattributed = 100;

        ProfileGcScriptEntry[] kept = ProfileGcAttribution.OmitEngineMarkers(entries, ref unattributed);

        Assert.Equal(new[] { Threat }, kept.Select(entry => entry.marker));
        Assert.Equal(552, unattributed);
    }

    [Fact]
    public void Accumulator_CountsFramesWithAllocationPerScript()
    {
        var accumulator = new ProfileGcByScriptAccumulator();
        accumulator.BeginFrame();
        accumulator.Record(Threat, 10, 1);
        accumulator.Record(Threat, 10, 1);
        accumulator.BeginFrame();
        accumulator.BeginFrame();
        accumulator.Record(Threat, 20, 1);
        accumulator.Record(null, 5, 1);
        accumulator.Record(Hud, 0, 1);

        ProfileGcScriptEntry[] entries = accumulator.Build(capturedFrames: 3);

        Assert.Single(entries);
        Assert.Equal(2, entries[0].framesWithAlloc);
        Assert.Equal(40, entries[0].bytesTotal);
        Assert.Equal(40 / 3.0, entries[0].bytesPerFrame, precision: 6);
        Assert.Equal(5, accumulator.UnattributedBytes);
        Assert.Equal(3, accumulator.FrameCount);
    }

    private static void Walk(FakeHierarchy tree, ProfileGcByScriptAccumulator accumulator)
    {
        ProfileHierarchyWalker.Walk(tree, (itemId, name, scriptAncestor) =>
        {
            if (name == ProfileGcAttribution.GcAllocMarker)
            {
                accumulator.Record(scriptAncestor, tree.Gc[itemId], tree.Calls[itemId]);
            }
        });
    }

    private sealed class FakeHierarchy : IProfileHierarchy
    {
        private readonly Dictionary<int, List<int>> _children = new() { [0] = new List<int>() };
        private readonly Dictionary<int, string> _names = new();
        private readonly HashSet<int> _scripts = new();
        public readonly Dictionary<int, long> Gc = new();
        public readonly Dictionary<int, long> Calls = new();

        public int RootId => 0;

        public int Add(int parent, string name, bool scriptFlag = false, long gc = 0, long calls = 0)
        {
            int id = _names.Count + 1;
            _names[id] = name;
            _children[id] = new List<int>();
            _children[parent].Add(id);
            Gc[id] = gc;
            Calls[id] = calls;
            if (scriptFlag)
            {
                _scripts.Add(id);
            }

            return id;
        }

        public void GetChildren(int itemId, List<int> children) => children.AddRange(_children[itemId]);

        public string GetName(int itemId) => _names[itemId];

        public bool HasScriptFlag(int itemId) => _scripts.Contains(itemId);
    }
}
