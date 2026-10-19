using UnityCli.Protocol;

namespace UnityCli.Cli.Services;

public sealed class UnityProjectLocator
{
    /// <summary>
    /// How many directory levels below the start folder are searched when walk-up finds nothing.
    /// The start folder itself is depth 0 and is covered by the walk-up.
    /// </summary>
    public const int MaxDescendantDepth = 3;

    private static readonly StringComparer PathComparer = Path.DirectorySeparatorChar == '\\'
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private static readonly HashSet<string> SkippedDirectoryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Library",
        "Temp",
        "Logs",
        "obj",
        "bin",
        "node_modules",
        "Build",
        "Builds",
    };

    /// <summary>
    /// Returns the project root when walk-up or the descendant search yields exactly one match.
    /// Returns null when nothing matches or more than one project matches. Command dispatch uses
    /// <see cref="SelectProjectRoot"/> so several matches become <see cref="AmbiguousProjectException"/>
    /// unless a live Editor uniquely selects one.
    /// </summary>
    public string? TryFindProjectRoot(string startDirectory)
    {
        IReadOnlyList<string> candidates = FindCandidateProjectRoots(startDirectory);
        return candidates.Count == 1 ? candidates[0] : null;
    }

    public IReadOnlyList<string> FindCandidateProjectRoots(string startDirectory)
    {
        if (string.IsNullOrWhiteSpace(startDirectory))
        {
            return Array.Empty<string>();
        }

        string canonicalStart = ProtocolConstants.GetCanonicalPath(startDirectory);
        string? walked = FindByWalkingUp(canonicalStart);
        if (walked is not null)
        {
            return new[] { walked };
        }

        var found = new List<string>();
        // Windows paths are case-insensitive, and GetCanonicalPath does not resolve links there.
        // Distinct casing of the same directory must not become two candidates. Unix paths stay
        // case-sensitive so /Foo and /foo remain different projects.
        var visited = new HashSet<string>(PathComparer) { canonicalStart };
        SearchDescendants(canonicalStart, MaxDescendantDepth, found, visited);
        found.Sort(StringComparer.Ordinal);
        return found;
    }

    /// <summary>
    /// Picks one project from <paramref name="candidates"/>. A single candidate wins on its own.
    /// Several candidates resolve to the explicit <c>instances use</c> pin when that Editor is
    /// reachable, or to the only candidate with a reachable Editor. Reachable means the recorded
    /// process is alive or the pipe/socket accepts a connection — a non-offline registry row is
    /// not enough. Otherwise this throws <see cref="AmbiguousProjectException"/>. An empty list
    /// returns null so the caller can fall back to the single running Editor.
    /// </summary>
    public static string? SelectProjectRoot(IReadOnlyList<string> candidates, InstanceRegistry registry)
    {
        if (candidates.Count == 0)
        {
            return null;
        }

        if (candidates.Count == 1)
        {
            return candidates[0];
        }

        var candidateSet = new HashSet<string>(candidates, StringComparer.OrdinalIgnoreCase);
        var liveRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        registry.instances ??= Array.Empty<InstanceRecord>();
        foreach (InstanceRecord instance in registry.instances)
        {
            if (string.IsNullOrWhiteSpace(instance.projectRoot) || !EditorReachability.IsReachable(instance))
            {
                continue;
            }

            string canonical = ProtocolConstants.GetCanonicalPath(instance.projectRoot);
            if (candidateSet.Contains(canonical))
            {
                liveRoots.Add(canonical);
            }
        }

        if (registry.activeProjectRootPinned
            && !string.IsNullOrWhiteSpace(registry.activeProjectRoot))
        {
            string pinned = ProtocolConstants.GetCanonicalPath(registry.activeProjectRoot);
            if (liveRoots.Contains(pinned))
            {
                return candidates.First(candidate =>
                    string.Equals(candidate, pinned, StringComparison.OrdinalIgnoreCase));
            }
        }

        if (liveRoots.Count == 1)
        {
            string onlyLive = liveRoots.First();
            return candidates.First(candidate =>
                string.Equals(candidate, onlyLive, StringComparison.OrdinalIgnoreCase));
        }

        throw new AmbiguousProjectException(candidates);
    }

    public bool LooksLikeUnityProject(string path)
    {
        return LooksLikeProject(path);
    }

    private static string? FindByWalkingUp(string canonicalStart)
    {
        var current = new DirectoryInfo(canonicalStart);
        while (current is not null)
        {
            if (LooksLikeProject(current.FullName))
            {
                return ProtocolConstants.GetCanonicalPath(current.FullName);
            }

            current = current.Parent;
        }

        return null;
    }

    private static void SearchDescendants(
        string directory,
        int depthRemaining,
        List<string> found,
        HashSet<string> visited)
    {
        if (depthRemaining <= 0)
        {
            return;
        }

        string[] children;
        try
        {
            children = Directory.EnumerateDirectories(directory)
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return;
        }

        foreach (string child in children)
        {
            string name = Path.GetFileName(child.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (ShouldSkipDirectory(name))
            {
                continue;
            }

            // Resolve directory links before deduping. On macOS and Linux, GetCanonicalPath
            // already calls realpath. On Windows it does not, so a symlink and its target would
            // otherwise count as two projects.
            string canonical = CanonicalDirectory(child);
            if (!visited.Add(canonical))
            {
                continue;
            }

            if (LooksLikeProject(canonical))
            {
                found.Add(canonical);
                continue;
            }

            SearchDescendants(canonical, depthRemaining - 1, found, visited);
        }
    }

    private static string CanonicalDirectory(string path)
    {
        string resolved = path;
        try
        {
            FileSystemInfo? target = new DirectoryInfo(path).ResolveLinkTarget(returnFinalTarget: true);
            if (target is not null && !string.IsNullOrWhiteSpace(target.FullName))
            {
                resolved = target.FullName;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }

        return ProtocolConstants.GetCanonicalPath(resolved);
    }

    private static bool ShouldSkipDirectory(string name)
    {
        return name.Length > 0 && (name[0] == '.' || SkippedDirectoryNames.Contains(name));
    }

    private static bool LooksLikeProject(string path)
    {
        return Directory.Exists(Path.Combine(path, "Assets")) &&
               Directory.Exists(Path.Combine(path, "Packages"));
    }
}
