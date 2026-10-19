using System.Diagnostics;
using System.IO.Pipes;
using System.Net.Sockets;
using UnityCli.Cli.Services;
using UnityCli.Protocol;

namespace UnityCli.Cli.Tests;

public sealed class UnityProjectLocatorTests
{
    [Fact]
    public void TryFindProjectRoot_WhenCwdIsInsideProject_ReturnsThatProject()
    {
        using var temp = new TempDirectory();
        string projectRoot = Path.Combine(temp.Path, "Game");
        CreateProject(projectRoot);
        CreateProject(Path.Combine(temp.Path, "Other"));
        CreateProject(Path.Combine(projectRoot, "Extra", "Nested"));
        string scripts = Path.Combine(projectRoot, "Assets", "Scripts");
        Directory.CreateDirectory(scripts);

        var locator = new UnityProjectLocator();

        Assert.Equal(
            ProtocolConstants.GetCanonicalPath(projectRoot),
            locator.TryFindProjectRoot(scripts));
        Assert.Equal(
            ProtocolConstants.GetCanonicalPath(projectRoot),
            locator.TryFindProjectRoot(projectRoot));
    }

    [Fact]
    public void FindCandidateProjectRoots_WhenMonorepoRootHasOneProjectTwoLevelsDown_ReturnsThatProject()
    {
        using var temp = new TempDirectory();
        string projectRoot = Path.Combine(temp.Path, "client", "Game");
        CreateProject(projectRoot);

        var locator = new UnityProjectLocator();
        IReadOnlyList<string> found = locator.FindCandidateProjectRoots(temp.Path);

        Assert.Equal(new[] { ProtocolConstants.GetCanonicalPath(projectRoot) }, found);
    }

    [Fact]
    public void FindCandidateProjectRoots_WhenSeveralProjectsMatch_IsSortedAndResolveThrows()
    {
        using var temp = new TempDirectory();
        string zeta = Path.Combine(temp.Path, "Zeta");
        string alpha = Path.Combine(temp.Path, "Alpha");
        CreateProject(zeta);
        CreateProject(alpha);
        string canonicalZeta = ProtocolConstants.GetCanonicalPath(zeta);
        string canonicalAlpha = ProtocolConstants.GetCanonicalPath(alpha);

        var locator = new UnityProjectLocator();
        IReadOnlyList<string> found = locator.FindCandidateProjectRoots(temp.Path);

        Assert.Equal(new[] { canonicalAlpha, canonicalZeta }, found);
        Assert.Null(locator.TryFindProjectRoot(temp.Path));

        AmbiguousProjectException exception = Assert.Throws<AmbiguousProjectException>(() =>
            UnityProjectLocator.SelectProjectRoot(found, new InstanceRegistry()));

        Assert.Equal(AmbiguousProjectException.ErrorCode, "AMBIGUOUS_PROJECT");
        Assert.Equal(new[] { canonicalAlpha, canonicalZeta }, exception.Candidates);
        Assert.Contains("--project", exception.Message);
        Assert.True(
            exception.Message.IndexOf(canonicalAlpha, StringComparison.Ordinal)
            < exception.Message.IndexOf(canonicalZeta, StringComparison.Ordinal));
    }

    [Fact]
    public void FindCandidateProjectRoots_SkipsHeavyAndHiddenDirectories()
    {
        using var temp = new TempDirectory();
        string visible = Path.Combine(temp.Path, "src", "Game");
        CreateProject(visible);
        CreateProject(Path.Combine(temp.Path, "node_modules", "left-pad"));
        CreateProject(Path.Combine(temp.Path, "Library", "PackageCache", "com.example"));
        CreateProject(Path.Combine(temp.Path, "Temp", "Staging"));
        CreateProject(Path.Combine(temp.Path, "Logs", "Session"));
        CreateProject(Path.Combine(temp.Path, "obj", "Debug"));
        CreateProject(Path.Combine(temp.Path, "bin", "Debug"));
        CreateProject(Path.Combine(temp.Path, "Build", "Player"));
        CreateProject(Path.Combine(temp.Path, "Builds", "Android"));
        CreateProject(Path.Combine(temp.Path, ".git", "modules", "Game"));
        CreateProject(Path.Combine(temp.Path, ".hidden", "Game"));

        var locator = new UnityProjectLocator();
        IReadOnlyList<string> found = locator.FindCandidateProjectRoots(temp.Path);

        Assert.Equal(new[] { ProtocolConstants.GetCanonicalPath(visible) }, found);
    }

    [Fact]
    public void FindCandidateProjectRoots_IncludesDepthThreeAndSkipsAnythingDeeper()
    {
        using var temp = new TempDirectory();
        string withinLimit = Path.Combine(temp.Path, "a", "b", "Game");
        string tooDeep = Path.Combine(temp.Path, "a", "b", "c", "TooDeep");
        CreateProject(withinLimit);
        CreateProject(tooDeep);

        var locator = new UnityProjectLocator();
        IReadOnlyList<string> found = locator.FindCandidateProjectRoots(temp.Path);

        Assert.Equal(UnityProjectLocator.MaxDescendantDepth, 3);
        Assert.Equal(new[] { ProtocolConstants.GetCanonicalPath(withinLimit) }, found);
    }

    [Fact]
    public void FindCandidateProjectRoots_DoesNotSearchInsideAMatchedProject()
    {
        using var temp = new TempDirectory();
        string outer = Path.Combine(temp.Path, "Game");
        CreateProject(outer);
        CreateProject(Path.Combine(outer, "Nested", "Inner"));

        var locator = new UnityProjectLocator();
        IReadOnlyList<string> found = locator.FindCandidateProjectRoots(temp.Path);

        Assert.Equal(new[] { ProtocolConstants.GetCanonicalPath(outer) }, found);
    }

    [Fact]
    public void FindCandidateProjectRoots_DedupesSymlinkToTheSameProject()
    {
        using var temp = new TempDirectory();
        string projectRoot = Path.Combine(temp.Path, "games", "Game");
        CreateProject(projectRoot);
        string link = Path.Combine(temp.Path, "link");
        try
        {
            Directory.CreateSymbolicLink(link, projectRoot);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // Windows can refuse symlink creation without Developer Mode. The assertion below
            // still runs on hosts that allow it, including GitHub-hosted Windows runners.
            return;
        }

        var locator = new UnityProjectLocator();
        IReadOnlyList<string> found = locator.FindCandidateProjectRoots(temp.Path);

        Assert.Equal(new[] { ProtocolConstants.GetCanonicalPath(projectRoot) }, found);
    }

    [Fact]
    public void SelectProjectRoot_WhenOnlyOneCandidateIsLive_ReturnsThatCandidate()
    {
        using var temp = new TempDirectory();
        string live = Canonical(temp.Path, "Live");
        string idle = Canonical(temp.Path, "Idle");
        var registry = new InstanceRegistry
        {
            instances =
            [
                Instance(live, "idle", Environment.ProcessId),
                Instance(idle, "offline"),
            ],
        };

        string? selected = UnityProjectLocator.SelectProjectRoot([idle, live], registry);

        Assert.Equal(live, selected);
    }

    [Fact]
    public void SelectProjectRoot_WhenPinnedLiveCandidateExists_PrefersThePin()
    {
        using var temp = new TempDirectory();
        string pinned = Canonical(temp.Path, "Pinned");
        string other = Canonical(temp.Path, "Other");
        var registry = new InstanceRegistry
        {
            activeProjectRoot = pinned,
            activeProjectRootPinned = true,
            instances =
            [
                Instance(pinned, "idle", Environment.ProcessId),
                Instance(other, "playing", Environment.ProcessId),
            ],
        };

        string? selected = UnityProjectLocator.SelectProjectRoot([other, pinned], registry);

        Assert.Equal(pinned, selected);
    }

    [Fact]
    public void SelectProjectRoot_WhenPinnedCandidateIsOffline_DoesNotTreatThePinAsUnique()
    {
        using var temp = new TempDirectory();
        string pinned = Canonical(temp.Path, "Pinned");
        string other = Canonical(temp.Path, "Other");
        var registry = new InstanceRegistry
        {
            activeProjectRoot = pinned,
            activeProjectRootPinned = true,
            instances =
            [
                Instance(pinned, "offline"),
                Instance(other, "offline"),
            ],
        };

        AmbiguousProjectException exception = Assert.Throws<AmbiguousProjectException>(() =>
            UnityProjectLocator.SelectProjectRoot([pinned, other], registry));

        Assert.Contains(pinned, exception.Candidates);
        Assert.Contains(other, exception.Candidates);
    }

    [Fact]
    public void SelectProjectRoot_WhenPinIsOffline_UsesTheOtherLiveCandidate()
    {
        using var temp = new TempDirectory();
        string pinned = Canonical(temp.Path, "Pinned");
        string live = Canonical(temp.Path, "Live");
        var registry = new InstanceRegistry
        {
            activeProjectRoot = pinned,
            activeProjectRootPinned = true,
            instances =
            [
                Instance(pinned, "offline"),
                Instance(live, "idle", Environment.ProcessId),
            ],
        };

        string? selected = UnityProjectLocator.SelectProjectRoot([pinned, live], registry);

        Assert.Equal(live, selected);
    }

    [Fact]
    public void SelectProjectRoot_WhenSingleFilesystemMatch_IgnoresOtherLiveEditors()
    {
        using var temp = new TempDirectory();
        string only = Canonical(temp.Path, "Only");
        string elsewhere = Canonical(temp.Path, "Elsewhere");
        var registry = new InstanceRegistry
        {
            instances = [Instance(elsewhere, "idle")],
        };

        Assert.Equal(only, UnityProjectLocator.SelectProjectRoot([only], registry));
    }

    [Fact]
    public void SelectProjectRoot_WhenRegistryProcessIsDead_DoesNotDisambiguate()
    {
        using var temp = new TempDirectory();
        string dead = Canonical(temp.Path, "Dead");
        string other = Canonical(temp.Path, "Other");
        int deadPid = StartAndKillProcess();
        var registry = new InstanceRegistry
        {
            instances =
            [
                Instance(dead, "idle", deadPid, pipeName: MissingEndpoint()),
            ],
        };

        AmbiguousProjectException exception = Assert.Throws<AmbiguousProjectException>(() =>
            UnityProjectLocator.SelectProjectRoot([dead, other], registry));

        Assert.Contains(dead, exception.Candidates);
        Assert.Contains(other, exception.Candidates);
        Assert.Contains("--project", exception.Message);
    }

    [Fact]
    public void SelectProjectRoot_WhenProcessIsDeadButEndpointAcceptsConnections_ReturnsThatCandidate()
    {
        using var temp = new TempDirectory();
        string live = Canonical(temp.Path, "Live");
        string other = Canonical(temp.Path, "Other");
        using IDisposable endpoint = StartEndpoint(out string endpointName);
        var registry = new InstanceRegistry
        {
            instances =
            [
                Instance(live, "idle", pid: 0, pipeName: endpointName),
            ],
        };

        string? selected = UnityProjectLocator.SelectProjectRoot([other, live], registry);

        Assert.Equal(live, selected);
    }

    private static string Canonical(string root, string name)
    {
        return ProtocolConstants.GetCanonicalPath(Path.Combine(root, name));
    }

    private static InstanceRecord Instance(string projectRoot, string state, int pid = 0, string? pipeName = null)
    {
        return new InstanceRecord
        {
            projectRoot = projectRoot,
            projectName = Path.GetFileName(projectRoot),
            state = state,
            editorProcessId = pid,
            pipeName = pipeName ?? string.Empty,
        };
    }

    private static string MissingEndpoint()
    {
        return OperatingSystem.IsWindows()
            ? "unity-cli-missing-" + Guid.NewGuid().ToString("N")
            : Path.Combine(Path.GetTempPath(), "ucb-missing-" + Guid.NewGuid().ToString("N") + ".sock");
    }

    private static int StartAndKillProcess()
    {
        var start = new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sleep",
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (OperatingSystem.IsWindows())
        {
            start.ArgumentList.Add("/c");
            start.ArgumentList.Add("ping -n 30 127.0.0.1 > nul");
        }
        else
        {
            start.ArgumentList.Add("30");
        }

        using Process process = Process.Start(start)!;
        int pid = process.Id;
        process.Kill(entireProcessTree: true);
        process.WaitForExit();
        Assert.False(EditorLauncher.IsProcessAlive(pid));
        return pid;
    }

    private static IDisposable StartEndpoint(out string endpointName)
    {
        if (OperatingSystem.IsWindows())
        {
            endpointName = "ucb-reach-" + Guid.NewGuid().ToString("N");
            var server = new NamedPipeServerStream(
                endpointName,
                PipeDirection.InOut,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);
            _ = server.WaitForConnectionAsync();
            return server;
        }

        endpointName = Path.Combine(Path.GetTempPath(), "ucb-reach-" + Guid.NewGuid().ToString("N") + ".sock");
        var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(endpointName));
        listener.Listen(1);
        return new UnixListener(listener, endpointName);
    }

    private sealed class UnixListener : IDisposable
    {
        private readonly Socket _listener;
        private readonly string _path;

        public UnixListener(Socket listener, string path)
        {
            _listener = listener;
            _path = path;
        }

        public void Dispose()
        {
            _listener.Dispose();
            if (File.Exists(_path))
            {
                File.Delete(_path);
            }
        }
    }

    private static void CreateProject(string projectRoot)
    {
        Directory.CreateDirectory(Path.Combine(projectRoot, "Assets"));
        Directory.CreateDirectory(Path.Combine(projectRoot, "Packages"));
    }
}
