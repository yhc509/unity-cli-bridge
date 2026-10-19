using UnityCli.Cli.Models;
using UnityCli.Protocol;

namespace UnityCli.Cli.Services;

public sealed class InstanceRegistryStore
{
    private readonly string _registryPath;

    public InstanceRegistryStore()
        : this(RegistryPathUtility.GetRegistryFilePath())
    {
    }

    public InstanceRegistryStore(string registryPath)
    {
        _registryPath = registryPath;
    }

    public InstanceRegistry Load()
    {
        return Sanitize(InstanceRegistryFile.Load(_registryPath));
    }

    public void Save(InstanceRegistry registry)
    {
        InstanceRegistryFile.Save(_registryPath, registry);
    }

    // Atomic read-modify-write under the registry's exclusive lock. The raw on-disk registry is
    // sanitized before the mutation runs so callers see the same shape Load() would return, and a
    // concurrent heartbeat or CLI write cannot slip in between the read and the write.
    public void Update(Func<InstanceRegistry, InstanceRegistry> mutate)
    {
        InstanceRegistryFile.Update(_registryPath, current => mutate(Sanitize(current)));
    }

    private static bool TryResolveProjectRootByName(
        InstanceRegistry registry,
        string projectName,
        out string? projectRoot,
        out InstanceRecord? match)
    {
        projectRoot = null;
        match = null;

        if (string.IsNullOrWhiteSpace(projectName))
        {
            return false;
        }

        var trimmedProjectName = projectName.Trim();
        registry.instances ??= Array.Empty<InstanceRecord>();

        var matches = registry.instances
            // Registered project names are matched case-insensitively so shell casing does not change target selection.
            .Where(item => string.Equals(item.projectName, trimmedProjectName, StringComparison.OrdinalIgnoreCase))
            .GroupBy(item => ProtocolConstants.GetCanonicalPath(item.projectRoot), StringComparer.OrdinalIgnoreCase)
            .Select(group => (projectRoot: group.Key, match: group.First()))
            .ToArray();

        if (matches.Length == 0)
        {
            return false;
        }

        if (matches.Length > 1)
        {
            throw CreateAmbiguousProjectNameException(trimmedProjectName, matches.Select(item => item.projectRoot).ToArray());
        }

        projectRoot = matches[0].projectRoot;
        match = matches[0].match;
        return true;
    }

    private static bool TryResolveProjectRootOverride(
        InstanceRegistry registry,
        string input,
        out string? projectRoot,
        out InstanceRecord? match)
    {
        projectRoot = null;
        match = null;

        var trimmed = input.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return false;
        }

        registry.instances ??= Array.Empty<InstanceRecord>();

        if (Directory.Exists(trimmed))
        {
            var canonicalProjectRoot = ProtocolConstants.GetCanonicalPath(trimmed);
            projectRoot = canonicalProjectRoot;

            // A literal directory path always wins over same-text registry name matches.
            match = registry.instances.FirstOrDefault(item =>
                string.Equals(item.projectRoot, canonicalProjectRoot, StringComparison.OrdinalIgnoreCase));
            return true;
        }

        return TryResolveProjectRootByName(registry, trimmed, out projectRoot, out match);
    }

    private static CliUsageException CreateUnknownProjectOverrideException(string input)
    {
        return new CliUsageException(
            $"'{input}' is not a registered project name or a valid directory path. Run 'unity-cli instances list' to see registered projects.");
    }

    private static CliUsageException CreateUnknownInstanceTargetException(string input)
    {
        return new CliUsageException(
            $"'{input}' is not a known project hash, a registered project name, or a valid directory path. Run 'unity-cli instances list' to see registered projects.");
    }

    public InstanceRecord ResolveOrCreateTarget(InstanceRegistry registry, string input)
    {
        registry.instances ??= Array.Empty<InstanceRecord>();

        var trimmed = input.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            throw new CliUsageException("A project hash, project path, or project name is required.");
        }

        if (!ContainsDirectorySeparator(trimmed) && IsProjectHashInput(trimmed))
        {
            var isUnsuffixedHash = trimmed.Length == 12;
            var suffixedHashPrefix = trimmed + "-";
            var hashMatches = registry.instances
                .Where(item =>
                    string.Equals(item.projectHash, trimmed, StringComparison.OrdinalIgnoreCase)
                    || (isUnsuffixedHash
                        && !string.IsNullOrWhiteSpace(item.projectHash)
                        && item.projectHash.StartsWith(suffixedHashPrefix, StringComparison.OrdinalIgnoreCase)))
                .ToArray();
            if (hashMatches.Length > 1)
            {
                throw CreateAmbiguousProjectHashException(
                    trimmed,
                    hashMatches.Select(item => ProtocolConstants.GetCanonicalPath(item.projectRoot)).ToArray());
            }

            if (hashMatches.Length == 1)
            {
                return hashMatches[0];
            }
        }

        if (!TryResolveProjectRootOverride(registry, trimmed, out var projectRoot, out var resolvedMatch)
            || string.IsNullOrWhiteSpace(projectRoot))
        {
            throw CreateUnknownInstanceTargetException(trimmed);
        }

        var projectHash = ProtocolConstants.ComputeProjectHash(projectRoot);
        var match = resolvedMatch
            ?? registry.instances.FirstOrDefault(item =>
                string.Equals(item.projectRoot, projectRoot, StringComparison.OrdinalIgnoreCase));

        if (match is not null)
        {
            // Intentionally mutate the loaded registry entry in place so callers keep using the same snapshot object.
            match.projectRoot = projectRoot;
            match.projectName = string.IsNullOrWhiteSpace(match.projectName) ? Path.GetFileName(projectRoot) : match.projectName;
            if (string.IsNullOrWhiteSpace(match.projectHash))
            {
                match.projectHash = projectHash;
            }

            if (string.IsNullOrWhiteSpace(match.pipeName))
            {
                match.pipeName = ProtocolConstants.BuildPipeName(match.projectHash);
            }

            return match;
        }

        var created = new InstanceRecord
        {
            projectRoot = projectRoot,
            projectName = Path.GetFileName(projectRoot),
            projectHash = projectHash,
            pipeName = ProtocolConstants.BuildPipeName(projectHash),
            state = "offline",
            lastSeenUtc = DateTimeOffset.UtcNow.ToString("O"),
        };

        registry.instances = registry.instances.Append(created).ToArray();
        return created;
    }

    public string ResolveProjectRootOverride(InstanceRegistry registry, string input)
    {
        var trimmed = input.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            throw new CliUsageException("A project path or project name is required.");
        }

        return TryResolveProjectRootOverride(registry, trimmed, out var projectRoot, out _)
            && !string.IsNullOrWhiteSpace(projectRoot)
            ? projectRoot
            : throw CreateUnknownProjectOverrideException(trimmed);
    }

    private static bool ContainsDirectorySeparator(string value)
    {
        return value.Contains(Path.DirectorySeparatorChar)
            || value.Contains(Path.AltDirectorySeparatorChar);
    }

    private static bool IsProjectHashInput(string value)
    {
        if (value.Length < 12)
        {
            return false;
        }

        for (var index = 0; index < 12; index++)
        {
            if (!IsHexDigit(value[index]))
            {
                return false;
            }
        }

        if (value.Length == 12)
        {
            return true;
        }

        if (value[12] != '-' || value.Length == 13)
        {
            return false;
        }

        for (var index = 13; index < value.Length; index++)
        {
            if (!char.IsDigit(value[index]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsHexDigit(char value)
    {
        return value is >= '0' and <= '9'
            or >= 'a' and <= 'f'
            or >= 'A' and <= 'F';
    }

    private InstanceRegistry Sanitize(InstanceRegistry registry)
    {
        var changed = false;
        registry.instances ??= Array.Empty<InstanceRecord>();
        var instancesByPath = new Dictionary<string, InstanceRecord>(StringComparer.OrdinalIgnoreCase);

        foreach (var instance in registry.instances)
        {
            if (string.IsNullOrWhiteSpace(instance.projectRoot) || !Directory.Exists(instance.projectRoot))
            {
                changed = true;
                continue;
            }

            var projectRoot = ProtocolConstants.GetCanonicalPath(instance.projectRoot);
            var projectHash = ProtocolConstants.ComputeProjectHash(projectRoot);
            var normalizedProjectHash = string.IsNullOrWhiteSpace(instance.projectHash) ? projectHash : instance.projectHash;
            var normalized = new InstanceRecord
            {
                projectRoot = projectRoot,
                projectName = string.IsNullOrWhiteSpace(instance.projectName) ? Path.GetFileName(projectRoot) : instance.projectName,
                projectHash = normalizedProjectHash,
                pipeName = string.IsNullOrWhiteSpace(instance.pipeName)
                    ? ProtocolConstants.BuildPipeName(normalizedProjectHash)
                    : instance.pipeName,
                token = InstanceRegistryFile.ReadTokenSidecar(_registryPath, normalizedProjectHash),
                editorProcessId = instance.editorProcessId,
                unityVersion = instance.unityVersion ?? string.Empty,
                state = instance.state ?? "offline",
                lastSeenUtc = instance.lastSeenUtc ?? string.Empty,
                capabilities = instance.capabilities ?? Array.Empty<string>(),
                editorMode = instance.editorMode ?? string.Empty,
            };

            if (IsStale(normalized))
            {
                normalized.state = "offline";
                normalized.editorProcessId = 0;
                changed = true;
            }

            if (!instancesByPath.TryGetValue(normalized.projectRoot, out var existing)
                || CompareLastSeen(normalized.lastSeenUtc, existing.lastSeenUtc) >= 0)
            {
                instancesByPath[normalized.projectRoot] = normalized;
            }
        }

        var instances = instancesByPath.Values
            .OrderBy(item => item.projectName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.projectRoot, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (instances.Length != registry.instances.Length)
        {
            changed = true;
        }

        registry.instances = instances;
        if (!string.IsNullOrWhiteSpace(registry.activeProjectRoot))
        {
            var activeProjectRoot = ProtocolConstants.GetCanonicalPath(registry.activeProjectRoot);
            if (!string.Equals(registry.activeProjectRoot, activeProjectRoot, StringComparison.OrdinalIgnoreCase))
            {
                registry.activeProjectRoot = activeProjectRoot;
                changed = true;
            }
        }

        var active = registry.instances.FirstOrDefault(item =>
            string.Equals(item.projectRoot, registry.activeProjectRoot, StringComparison.OrdinalIgnoreCase));
        if (!registry.activeProjectRootPinned
            && (string.IsNullOrWhiteSpace(registry.activeProjectRoot)
                || active is null
                || string.Equals(active.state, "offline", StringComparison.OrdinalIgnoreCase)))
        {
            registry.activeProjectRoot = registry.instances.FirstOrDefault(item => !string.Equals(item.state, "offline", StringComparison.OrdinalIgnoreCase))?.projectRoot
                ?? registry.instances.FirstOrDefault()?.projectRoot
                ?? string.Empty;
            changed = true;
        }

        registry.activeProjectHash = null;

        if (changed)
        {
            registry.instances ??= Array.Empty<InstanceRecord>();
        }

        return registry;
    }

    internal static bool IsStale(InstanceRecord record)
    {
        bool timestampStale;
        if (!DateTimeOffset.TryParse(record.lastSeenUtc, out var lastSeen))
        {
            timestampStale = true;
        }
        else
        {
            var maxAgeSeconds = ProtocolConstants.RegistryHeartbeatSeconds * 3;
            timestampStale = (DateTimeOffset.UtcNow - lastSeen).TotalSeconds > maxAgeSeconds;
        }

        if (!timestampStale)
        {
            return false;
        }

        if (record.editorProcessId > 0 && IsProcessAlive(record.editorProcessId))
        {
            return false;
        }

        return true;
    }

    private static bool IsProcessAlive(int processId)
    {
        try
        {
            var process = System.Diagnostics.Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch
        {
            return false;
        }
    }

    private static int CompareLastSeen(string? left, string? right)
    {
        var leftParsed = DateTimeOffset.TryParse(left, out var leftValue);
        var rightParsed = DateTimeOffset.TryParse(right, out var rightValue);

        return (leftParsed, rightParsed) switch
        {
            (true, true) => leftValue.CompareTo(rightValue),
            (true, false) => 1,
            (false, true) => -1,
            _ => 0,
        };
    }

    private static CliUsageException CreateAmbiguousProjectNameException(string projectName, string[] candidatePaths)
    {
        return new CliUsageException(
            $"Registered project name is ambiguous, so the target cannot be resolved: {projectName}. Use a project path. Candidates: {string.Join(", ", candidatePaths)}");
    }

    private static CliUsageException CreateAmbiguousProjectHashException(string projectHash, string[] candidatePaths)
    {
        return new CliUsageException(
            $"projectHash '{projectHash}' matches more than one instance. Specify a project path or a suffixed project hash. Candidates: {string.Join(", ", candidatePaths)}");
    }
}
