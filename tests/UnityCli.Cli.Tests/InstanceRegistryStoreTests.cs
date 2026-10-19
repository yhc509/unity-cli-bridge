using UnityCli.Cli.Models;
using UnityCli.Cli.Services;
using UnityCli.Protocol;

namespace UnityCli.Cli.Tests;

[Collection(CurrentDirectoryCollection.Name)]
public sealed class InstanceRegistryStoreTests
{
    [Fact]
    public void ResolveOrCreateTarget_CreatesOfflineEntryFromProjectPath()
    {
        using var temp = new TempDirectory();
        var projectRoot = System.IO.Path.Combine(temp.Path, "ProjectA");
        Directory.CreateDirectory(projectRoot);

        var store = new InstanceRegistryStore(System.IO.Path.Combine(temp.Path, "instances.json"));
        var registry = new InstanceRegistry();
        var target = store.ResolveOrCreateTarget(registry, projectRoot);

        Assert.Equal(ProtocolConstants.GetCanonicalPath(projectRoot), target.projectRoot);
        Assert.Single(registry.instances);
        Assert.Equal(target.projectHash, registry.instances[0].projectHash);
        Assert.Equal(ProtocolConstants.BuildPipeName(target.projectHash), target.pipeName);
    }

    [Fact]
    public void Load_ReadsInstanceTokenFromSidecarDuringSanitize()
    {
        using var temp = new TempDirectory();
        var projectRoot = Path.Combine(temp.Path, "ProjectA");
        Directory.CreateDirectory(projectRoot);
        string projectHash = ProtocolConstants.ComputeProjectHash(projectRoot);
        const string token = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        var registryPath = Path.Combine(temp.Path, "instances.json");
        var store = new InstanceRegistryStore(registryPath);
        store.Save(new InstanceRegistry
        {
            instances =
            [
                new InstanceRecord
                {
                    projectRoot = projectRoot,
                    projectName = "ProjectA",
                    projectHash = projectHash,
                    pipeName = ProtocolConstants.BuildPipeName(projectHash),
                    token = "registry-token-should-be-ignored",
                    state = "idle",
                    lastSeenUtc = DateTimeOffset.UtcNow.ToString("O"),
                },
            ],
        });
        InstanceRegistryFile.WriteTokenSidecar(registryPath, projectHash, token);

        InstanceRegistry registry = store.Load();

        Assert.Single(registry.instances);
        Assert.Equal(token, registry.instances[0].token);
    }

    [Fact]
    public void Load_PreservesEditorModeThroughSanitize()
    {
        using var temp = new TempDirectory();
        var projectRoot = Path.Combine(temp.Path, "ProjectA");
        Directory.CreateDirectory(projectRoot);
        string projectHash = ProtocolConstants.ComputeProjectHash(projectRoot);
        var store = new InstanceRegistryStore(Path.Combine(temp.Path, "instances.json"));
        store.Save(new InstanceRegistry
        {
            instances =
            [
                new InstanceRecord
                {
                    projectRoot = projectRoot,
                    projectName = "ProjectA",
                    projectHash = projectHash,
                    pipeName = ProtocolConstants.BuildPipeName(projectHash),
                    editorProcessId = Environment.ProcessId,
                    state = "idle",
                    lastSeenUtc = DateTimeOffset.UtcNow.ToString("O"),
                    editorMode = "headless",
                },
            ],
        });

        InstanceRegistry registry = store.Load();

        Assert.Single(registry.instances);
        Assert.Equal("headless", registry.instances[0].editorMode);
    }

    [Fact]
    public void ResolveOrCreateTarget_UsesRegisteredProjectName()
    {
        using var temp = new TempDirectory();
        var projectRoot = Path.Combine(temp.Path, "ProjectA");
        Directory.CreateDirectory(projectRoot);
        var projectHash = ProtocolConstants.ComputeProjectHash(projectRoot);

        var store = new InstanceRegistryStore(Path.Combine(temp.Path, "instances.json"));
        var registry = new InstanceRegistry
        {
            instances =
            [
                new InstanceRecord
                {
                    projectRoot = projectRoot,
                    projectName = "UnityCliBridge",
                    projectHash = projectHash,
                    pipeName = ProtocolConstants.BuildPipeName(projectHash),
                    state = "idle",
                    lastSeenUtc = DateTimeOffset.UtcNow.ToString("O"),
                },
            ],
        };

        var target = store.ResolveOrCreateTarget(registry, "unityclibridge");

        Assert.Equal(ProtocolConstants.GetCanonicalPath(projectRoot), target.projectRoot);
        Assert.Equal(projectHash, target.projectHash);
        Assert.Single(registry.instances);
    }

    [Fact]
    public void ResolveProjectRootOverride_ReturnsCanonicalRegisteredProjectPath()
    {
        using var temp = new TempDirectory();
        var projectRoot = Path.Combine(temp.Path, "ProjectA");
        Directory.CreateDirectory(projectRoot);

        var store = new InstanceRegistryStore(Path.Combine(temp.Path, "instances.json"));
        var registry = new InstanceRegistry
        {
            instances =
            [
                new InstanceRecord
                {
                    projectRoot = projectRoot,
                    projectName = "UnityCliBridge",
                    projectHash = ProtocolConstants.ComputeProjectHash(projectRoot),
                    pipeName = ProtocolConstants.BuildPipeName(ProtocolConstants.ComputeProjectHash(projectRoot)),
                    state = "idle",
                    lastSeenUtc = DateTimeOffset.UtcNow.ToString("O"),
                },
            ],
        };

        var resolved = store.ResolveProjectRootOverride(registry, "unityclibridge");

        Assert.Equal(ProtocolConstants.GetCanonicalPath(projectRoot), resolved);
    }

    [Fact]
    public void ResolveProjectRootOverride_PathTakesPrecedenceOverRegisteredProjectName()
    {
        using var temp = new TempDirectory();
        var pathProjectRoot = Path.Combine(temp.Path, "UnityCliBridge");
        var registeredProjectRoot = Path.Combine(temp.Path, "RegisteredProject");
        Directory.CreateDirectory(pathProjectRoot);
        Directory.CreateDirectory(registeredProjectRoot);

        var store = new InstanceRegistryStore(Path.Combine(temp.Path, "instances.json"));
        var registry = new InstanceRegistry
        {
            instances =
            [
                new InstanceRecord
                {
                    projectRoot = registeredProjectRoot,
                    projectName = "UnityCliBridge",
                    projectHash = ProtocolConstants.ComputeProjectHash(registeredProjectRoot),
                    pipeName = ProtocolConstants.BuildPipeName(ProtocolConstants.ComputeProjectHash(registeredProjectRoot)),
                    state = "idle",
                    lastSeenUtc = DateTimeOffset.UtcNow.ToString("O"),
                },
            ],
        };

        string originalCurrentDirectory = Environment.CurrentDirectory;

        try
        {
            Environment.CurrentDirectory = temp.Path;
            var resolved = store.ResolveProjectRootOverride(registry, "UnityCliBridge");

            Assert.Equal(ProtocolConstants.GetCanonicalPath(pathProjectRoot), resolved);
        }
        finally
        {
            Environment.CurrentDirectory = originalCurrentDirectory;
        }
    }

    [Fact]
    public void ResolveProjectRootOverride_WhenMultipleProjectsMatch_ThrowsUsageException()
    {
        using var temp = new TempDirectory();
        var firstProjectRoot = Path.Combine(temp.Path, "ProjectA");
        var secondProjectRoot = Path.Combine(temp.Path, "ProjectB");
        Directory.CreateDirectory(firstProjectRoot);
        Directory.CreateDirectory(secondProjectRoot);

        var store = new InstanceRegistryStore(Path.Combine(temp.Path, "instances.json"));
        var registry = new InstanceRegistry
        {
            instances =
            [
                new InstanceRecord
                {
                    projectRoot = firstProjectRoot,
                    projectName = "UnityCliBridge",
                    projectHash = ProtocolConstants.ComputeProjectHash(firstProjectRoot),
                    pipeName = ProtocolConstants.BuildPipeName(ProtocolConstants.ComputeProjectHash(firstProjectRoot)),
                    state = "idle",
                    lastSeenUtc = DateTimeOffset.UtcNow.ToString("O"),
                },
                new InstanceRecord
                {
                    projectRoot = secondProjectRoot,
                    projectName = "unityclibridge",
                    projectHash = ProtocolConstants.ComputeProjectHash(secondProjectRoot),
                    pipeName = ProtocolConstants.BuildPipeName(ProtocolConstants.ComputeProjectHash(secondProjectRoot)),
                    state = "idle",
                    lastSeenUtc = DateTimeOffset.UtcNow.ToString("O"),
                },
            ],
        };

        var exception = Assert.Throws<CliUsageException>(() => store.ResolveProjectRootOverride(registry, "UnityCliBridge"));

        Assert.Contains("ambiguous", exception.Message);
        Assert.Contains(ProtocolConstants.GetCanonicalPath(firstProjectRoot), exception.Message);
        Assert.Contains(ProtocolConstants.GetCanonicalPath(secondProjectRoot), exception.Message);
    }

    [Fact]
    public void ResolveProjectRootOverride_WhenProjectNameDoesNotMatch_ThrowsUsageException()
    {
        using var temp = new TempDirectory();
        var store = new InstanceRegistryStore(Path.Combine(temp.Path, "instances.json"));
        var registry = new InstanceRegistry();

        var exception = Assert.Throws<CliUsageException>(() => store.ResolveProjectRootOverride(registry, "UnityCliBridge"));

        Assert.Equal(
            "'UnityCliBridge' is not a registered project name or a valid directory path. Run 'unity-cli instances list' to see registered projects.",
            exception.Message);
    }

    [Fact]
    public void ResolveOrCreateTarget_WhenProjectNameDoesNotMatch_ThrowsUsageException()
    {
        using var temp = new TempDirectory();
        var store = new InstanceRegistryStore(Path.Combine(temp.Path, "instances.json"));
        var registry = new InstanceRegistry();

        var exception = Assert.Throws<CliUsageException>(() => store.ResolveOrCreateTarget(registry, "TypoProject"));

        Assert.Equal(
            "'TypoProject' is not a known project hash, a registered project name, or a valid directory path. Run 'unity-cli instances list' to see registered projects.",
            exception.Message);
    }

    [Fact]
    public void ResolveOrCreateTarget_AmbiguousHash_ThrowsCliUsage()
    {
        using var temp = new TempDirectory();
        var projectA = Path.Combine(temp.Path, "ProjA");
        var projectB = Path.Combine(temp.Path, "ProjB");
        Directory.CreateDirectory(projectA);
        Directory.CreateDirectory(projectB);

        var store = new InstanceRegistryStore(Path.Combine(temp.Path, "instances.json"));
        var registry = new InstanceRegistry
        {
            instances =
            [
                new InstanceRecord
                {
                    projectRoot = projectA,
                    projectName = "ProjA",
                    projectHash = "0000aaaa1111",
                    pipeName = "/tmp/unity-cli-0000aaaa1111.sock",
                    state = "offline",
                    lastSeenUtc = DateTimeOffset.UtcNow.ToString("O"),
                },
                new InstanceRecord
                {
                    projectRoot = projectB,
                    projectName = "ProjB",
                    projectHash = "0000aaaa1111",
                    pipeName = "/tmp/unity-cli-0000aaaa1111-1.sock",
                    state = "offline",
                    lastSeenUtc = DateTimeOffset.UtcNow.ToString("O"),
                },
            ],
        };

        var exception = Assert.Throws<CliUsageException>(
            () => store.ResolveOrCreateTarget(registry, "0000aaaa1111"));

        Assert.Contains("0000aaaa1111", exception.Message);
        Assert.Contains(ProtocolConstants.GetCanonicalPath(projectA), exception.Message);
        Assert.Contains(ProtocolConstants.GetCanonicalPath(projectB), exception.Message);
    }

    [Fact]
    public void ResolveOrCreateTarget_Base12CharHash_WithSuffixedSiblings_ReturnsAmbiguous()
    {
        using var temp = new TempDirectory();
        var projectA = Path.Combine(temp.Path, "ProjA");
        var projectB = Path.Combine(temp.Path, "ProjB");
        Directory.CreateDirectory(projectA);
        Directory.CreateDirectory(projectB);
        const string baseHash = "abc123def456";

        var store = new InstanceRegistryStore(Path.Combine(temp.Path, "instances.json"));
        var registry = new InstanceRegistry
        {
            instances =
            [
                new InstanceRecord
                {
                    projectRoot = projectA,
                    projectName = "ProjA",
                    projectHash = baseHash,
                    pipeName = ProtocolConstants.BuildPipeName(baseHash),
                    state = "idle",
                    lastSeenUtc = DateTimeOffset.UtcNow.ToString("O"),
                },
                new InstanceRecord
                {
                    projectRoot = projectB,
                    projectName = "ProjB",
                    projectHash = baseHash + "-1",
                    pipeName = ProtocolConstants.BuildPipeName(baseHash + "-1"),
                    state = "idle",
                    lastSeenUtc = DateTimeOffset.UtcNow.ToString("O"),
                },
            ],
        };

        var exception = Assert.Throws<CliUsageException>(
            () => store.ResolveOrCreateTarget(registry, baseHash));

        Assert.Contains(baseHash, exception.Message);
        Assert.Contains("suffixed project hash", exception.Message);
        Assert.Contains(ProtocolConstants.GetCanonicalPath(projectA), exception.Message);
        Assert.Contains(ProtocolConstants.GetCanonicalPath(projectB), exception.Message);
    }

    [Fact]
    public void ResolveOrCreateTarget_Base12CharHash_WithoutSuffixedSiblings_ReturnsExistingInstance()
    {
        using var temp = new TempDirectory();
        var projectRoot = Path.Combine(temp.Path, "ProjA");
        Directory.CreateDirectory(projectRoot);
        const string baseHash = "abc123def456";

        var store = new InstanceRegistryStore(Path.Combine(temp.Path, "instances.json"));
        var canonicalRoot = ProtocolConstants.GetCanonicalPath(projectRoot);
        var registry = new InstanceRegistry
        {
            instances =
            [
                new InstanceRecord
                {
                    projectRoot = canonicalRoot,
                    projectName = "ProjA",
                    projectHash = baseHash,
                    pipeName = ProtocolConstants.BuildPipeName(baseHash),
                    state = "idle",
                    lastSeenUtc = DateTimeOffset.UtcNow.ToString("O"),
                },
            ],
        };

        var resolved = store.ResolveOrCreateTarget(registry, baseHash);

        Assert.Equal(canonicalRoot, resolved.projectRoot);
        Assert.Equal(baseHash, resolved.projectHash);
        Assert.Single(registry.instances);
    }

    [Fact]
    public void ResolveOrCreateTarget_SuffixedHash_ReturnsExistingInstance()
    {
        using var temp = new TempDirectory();
        var projectA = Path.Combine(temp.Path, "ProjA");
        var projectB = Path.Combine(temp.Path, "ProjB");
        Directory.CreateDirectory(projectA);
        Directory.CreateDirectory(projectB);

        var store = new InstanceRegistryStore(Path.Combine(temp.Path, "instances.json"));
        var canonicalRoot = ProtocolConstants.GetCanonicalPath(projectB);
        var registry = new InstanceRegistry
        {
            instances =
            [
                new InstanceRecord
                {
                    projectRoot = ProtocolConstants.GetCanonicalPath(projectA),
                    projectName = "ProjA",
                    projectHash = "abc123def456",
                    pipeName = ProtocolConstants.BuildPipeName("abc123def456"),
                    state = "idle",
                    lastSeenUtc = DateTimeOffset.UtcNow.ToString("O"),
                },
                new InstanceRecord
                {
                    projectRoot = canonicalRoot,
                    projectName = "ProjB",
                    projectHash = "abc123def456-1",
                    pipeName = ProtocolConstants.BuildPipeName("abc123def456-1"),
                    state = "idle",
                    lastSeenUtc = DateTimeOffset.UtcNow.ToString("O"),
                },
            ],
        };

        var resolved = store.ResolveOrCreateTarget(registry, "abc123def456-1");

        Assert.Equal(canonicalRoot, resolved.projectRoot);
        Assert.Equal("abc123def456-1", resolved.projectHash);
        Assert.Equal(2, registry.instances.Length);
    }

    [Fact]
    public void ResolveOrCreateTarget_PathOverridesHashLookup_ReturnsExistingInstance()
    {
        using var temp = new TempDirectory();
        var projectRoot = Path.Combine(temp.Path, "ProjA");
        Directory.CreateDirectory(projectRoot);

        var store = new InstanceRegistryStore(Path.Combine(temp.Path, "instances.json"));
        var canonicalRoot = ProtocolConstants.GetCanonicalPath(projectRoot);
        var registry = new InstanceRegistry
        {
            instances =
            [
                new InstanceRecord
                {
                    projectRoot = canonicalRoot,
                    projectName = "ProjA",
                    projectHash = "0000aaaa1111",
                    pipeName = "/tmp/unity-cli-0000aaaa1111.sock",
                    state = "offline",
                    lastSeenUtc = DateTimeOffset.UtcNow.ToString("O"),
                },
            ],
        };

        var resolved = store.ResolveOrCreateTarget(registry, projectRoot);

        Assert.Equal(canonicalRoot, resolved.projectRoot);
        Assert.Equal("0000aaaa1111", resolved.projectHash);
        Assert.Single(registry.instances);
    }

    [Fact]
    public void Sanitize_KeepsBothInstances_WhenHashCollidesButPathsDiffer()
    {
        using var temp = new TempDirectory();
        var projectA = Path.Combine(temp.Path, "ProjA");
        var projectB = Path.Combine(temp.Path, "ProjB");
        Directory.CreateDirectory(projectA);
        Directory.CreateDirectory(projectB);

        var store = new InstanceRegistryStore(Path.Combine(temp.Path, "instances.json"));
        store.Save(new InstanceRegistry
        {
            instances =
            [
                new InstanceRecord
                {
                    projectRoot = projectA,
                    projectName = "ProjA",
                    projectHash = "0000aaaa1111",
                    pipeName = "/tmp/unity-cli-0000aaaa1111.sock",
                    state = "offline",
                    lastSeenUtc = DateTimeOffset.UtcNow.ToString("O"),
                },
                new InstanceRecord
                {
                    projectRoot = projectB,
                    projectName = "ProjB",
                    projectHash = "0000aaaa1111",
                    pipeName = "/tmp/unity-cli-0000aaaa1111-1.sock",
                    state = "offline",
                    lastSeenUtc = DateTimeOffset.UtcNow.ToString("O"),
                },
            ],
        });

        var loaded = store.Load();

        Assert.Equal(2, loaded.instances.Length);
        Assert.Contains(loaded.instances, i => i.projectName == "ProjA");
        Assert.Contains(loaded.instances, i => i.projectName == "ProjB");
        Assert.All(loaded.instances, i => Assert.Equal("0000aaaa1111", i.projectHash));
    }

    [Fact]
    public void Load_RemovesMissingProjectsAndPromotesLiveInstance()
    {
        using var temp = new TempDirectory();
        var existingProject = Path.Combine(temp.Path, "ProjectA");
        Directory.CreateDirectory(existingProject);

        var registryPath = Path.Combine(temp.Path, "instances.json");
        var store = new InstanceRegistryStore(registryPath);
        store.Save(new InstanceRegistry
        {
            activeProjectHash = "missing",
            instances =
            [
                new InstanceRecord
                {
                    projectRoot = Path.Combine(temp.Path, "MissingProject"),
                    projectName = "MissingProject",
                    projectHash = "missing",
                    pipeName = "missing.sock",
                    state = "offline",
                    lastSeenUtc = DateTimeOffset.UtcNow.AddMinutes(-5).ToString("O"),
                },
                new InstanceRecord
                {
                    projectRoot = existingProject,
                    projectName = "ProjectA",
                    projectHash = ProtocolConstants.ComputeProjectHash(existingProject),
                    pipeName = ProtocolConstants.BuildPipeName(ProtocolConstants.ComputeProjectHash(existingProject)),
                    state = "idle",
                    lastSeenUtc = DateTimeOffset.UtcNow.ToString("O"),
                },
            ],
        });

        var registry = store.Load();

        Assert.Single(registry.instances);
        Assert.Equal("ProjectA", registry.instances[0].projectName);
        Assert.Equal(registry.instances[0].projectRoot, registry.activeProjectRoot);
        Assert.True(string.IsNullOrEmpty(registry.activeProjectHash));
    }

    [Fact]
    public void IsStale_WithUnparseableLastSeenAndNoProcess_ReturnsTrue()
    {
        var record = new InstanceRecord
        {
            lastSeenUtc = "garbage",
            editorProcessId = 0,
        };

        Assert.True(InstanceRegistryStore.IsStale(record));
    }

    [Fact]
    public void IsStale_WithUnparseableLastSeenAndLiveProcess_ReturnsFalse()
    {
        var record = new InstanceRecord
        {
            lastSeenUtc = "garbage",
            editorProcessId = System.Diagnostics.Process.GetCurrentProcess().Id,
        };

        Assert.False(InstanceRegistryStore.IsStale(record));
    }

    [Fact]
    public void IsStale_WithOldLastSeenAndNoProcess_ReturnsTrue()
    {
        var record = new InstanceRecord
        {
            lastSeenUtc = DateTimeOffset.UtcNow.AddHours(-1).ToString("O"),
            editorProcessId = 0,
        };

        Assert.True(InstanceRegistryStore.IsStale(record));
    }

    [Fact]
    public void IsStale_WithRecentLastSeenAndNoProcess_ReturnsFalse()
    {
        var record = new InstanceRecord
        {
            lastSeenUtc = DateTimeOffset.UtcNow.ToString("O"),
            editorProcessId = 0,
        };

        Assert.False(InstanceRegistryStore.IsStale(record));
    }

    [Fact]
    public void Save_ThenLoad_PreservesPinnedFlag()
    {
        using var temp = new TempDirectory();
        var projectRoot = Path.Combine(temp.Path, "ProjectA");
        Directory.CreateDirectory(projectRoot);
        var store = new InstanceRegistryStore(Path.Combine(temp.Path, "instances.json"));
        store.Save(new InstanceRegistry
        {
            activeProjectRoot = projectRoot,
            activeProjectRootPinned = true,
            instances =
            [
                new InstanceRecord
                {
                    projectRoot = projectRoot,
                    projectName = "ProjectA",
                    projectHash = ProtocolConstants.ComputeProjectHash(projectRoot),
                    pipeName = ProtocolConstants.BuildPipeName(ProtocolConstants.ComputeProjectHash(projectRoot)),
                    state = "idle",
                    lastSeenUtc = DateTimeOffset.UtcNow.ToString("O"),
                },
            ],
        });

        var registry = store.Load();

        Assert.True(registry.activeProjectRootPinned);
    }

    [Fact]
    public void Load_DoesNotRepointPinnedActiveProjectRoot_EvenWhenOffline()
    {
        using var temp = new TempDirectory();
        var pinnedProject = Path.Combine(temp.Path, "PinnedProject");
        var liveProject = Path.Combine(temp.Path, "LiveProject");
        Directory.CreateDirectory(pinnedProject);
        Directory.CreateDirectory(liveProject);

        var registryPath = Path.Combine(temp.Path, "instances.json");
        var store = new InstanceRegistryStore(registryPath);
        store.Save(new InstanceRegistry
        {
            activeProjectRoot = pinnedProject,
            activeProjectRootPinned = true,
            instances =
            [
                new InstanceRecord
                {
                    projectRoot = pinnedProject,
                    projectName = "PinnedProject",
                    projectHash = ProtocolConstants.ComputeProjectHash(pinnedProject),
                    pipeName = ProtocolConstants.BuildPipeName(ProtocolConstants.ComputeProjectHash(pinnedProject)),
                    state = "offline",
                    lastSeenUtc = DateTimeOffset.UtcNow.ToString("O"),
                    editorProcessId = 0,
                },
                new InstanceRecord
                {
                    projectRoot = liveProject,
                    projectName = "LiveProject",
                    projectHash = ProtocolConstants.ComputeProjectHash(liveProject),
                    pipeName = ProtocolConstants.BuildPipeName(ProtocolConstants.ComputeProjectHash(liveProject)),
                    state = "idle",
                    lastSeenUtc = DateTimeOffset.UtcNow.ToString("O"),
                },
            ],
        });

        var registry = store.Load();

        Assert.Equal(ProtocolConstants.GetCanonicalPath(pinnedProject), registry.activeProjectRoot);
        Assert.True(registry.activeProjectRootPinned);
    }

    [Fact]
    public void Load_RepointsUnpinnedOfflineActiveProjectRoot_ToLiveInstance()
    {
        using var temp = new TempDirectory();
        var offlineProject = Path.Combine(temp.Path, "OfflineProject");
        var liveProject = Path.Combine(temp.Path, "LiveProject");
        Directory.CreateDirectory(offlineProject);
        Directory.CreateDirectory(liveProject);

        var registryPath = Path.Combine(temp.Path, "instances.json");
        var store = new InstanceRegistryStore(registryPath);
        store.Save(new InstanceRegistry
        {
            activeProjectRoot = offlineProject,
            activeProjectRootPinned = false,
            instances =
            [
                new InstanceRecord
                {
                    projectRoot = offlineProject,
                    projectName = "OfflineProject",
                    projectHash = ProtocolConstants.ComputeProjectHash(offlineProject),
                    pipeName = ProtocolConstants.BuildPipeName(ProtocolConstants.ComputeProjectHash(offlineProject)),
                    state = "offline",
                    lastSeenUtc = DateTimeOffset.UtcNow.ToString("O"),
                    editorProcessId = 0,
                },
                new InstanceRecord
                {
                    projectRoot = liveProject,
                    projectName = "LiveProject",
                    projectHash = ProtocolConstants.ComputeProjectHash(liveProject),
                    pipeName = ProtocolConstants.BuildPipeName(ProtocolConstants.ComputeProjectHash(liveProject)),
                    state = "idle",
                    lastSeenUtc = DateTimeOffset.UtcNow.ToString("O"),
                },
            ],
        });

        var registry = store.Load();

        Assert.Equal(ProtocolConstants.GetCanonicalPath(liveProject), registry.activeProjectRoot);
    }

    [Fact]
    public void Update_PinsActiveProject_AndPersistsThroughSanitize()
    {
        using var temp = new TempDirectory();
        var projectRoot = Path.Combine(temp.Path, "ProjectA");
        Directory.CreateDirectory(projectRoot);
        var projectHash = ProtocolConstants.ComputeProjectHash(projectRoot);
        var registryPath = Path.Combine(temp.Path, "instances.json");
        var store = new InstanceRegistryStore(registryPath);
        store.Save(new InstanceRegistry
        {
            instances =
            [
                new InstanceRecord
                {
                    projectRoot = projectRoot,
                    projectName = "ProjectA",
                    projectHash = projectHash,
                    pipeName = ProtocolConstants.BuildPipeName(projectHash),
                    state = "idle",
                    lastSeenUtc = DateTimeOffset.UtcNow.ToString("O"),
                },
            ],
        });

        store.Update(registry =>
        {
            var target = store.ResolveOrCreateTarget(registry, projectRoot);
            registry.activeProjectRoot = target.projectRoot;
            registry.activeProjectRootPinned = true;
            registry.activeProjectHash = null;
            return registry;
        });

        var reloaded = store.Load();
        Assert.Equal(ProtocolConstants.GetCanonicalPath(projectRoot), reloaded.activeProjectRoot);
        Assert.True(reloaded.activeProjectRootPinned);
        Assert.Single(reloaded.instances);
    }

    [Fact]
    public async Task Update_ConcurrentHeartbeatAppend_DoesNotLoseEitherMutation()
    {
        using var temp = new TempDirectory();
        var projectA = Path.Combine(temp.Path, "ProjectA");
        var projectB = Path.Combine(temp.Path, "ProjectB");
        Directory.CreateDirectory(projectA);
        Directory.CreateDirectory(projectB);
        var hashA = ProtocolConstants.ComputeProjectHash(projectA);
        var hashB = ProtocolConstants.ComputeProjectHash(projectB);
        var registryPath = Path.Combine(temp.Path, "instances.json");
        var store = new InstanceRegistryStore(registryPath);
        store.Save(new InstanceRegistry
        {
            instances =
            [
                new InstanceRecord
                {
                    projectRoot = projectA,
                    projectName = "ProjectA",
                    projectHash = hashA,
                    pipeName = ProtocolConstants.BuildPipeName(hashA),
                    state = "idle",
                    lastSeenUtc = DateTimeOffset.UtcNow.ToString("O"),
                },
            ],
        });

        using var barrier = new ManualResetEventSlim();

        // `instances use` pins the active project through the atomic Update path.
        var useTask = Task.Run(() =>
        {
            barrier.Wait();
            store.Update(registry =>
            {
                var target = store.ResolveOrCreateTarget(registry, projectA);
                registry.activeProjectRoot = target.projectRoot;
                registry.activeProjectRootPinned = true;
                registry.activeProjectHash = null;
                return registry;
            });
        });

        // A bridge heartbeat registers a second instance at the same moment. The old
        // load / mutate / save path could drop one of these two writes; the atomic
        // Update serializes them so both survive.
        var heartbeatTask = Task.Run(() =>
        {
            barrier.Wait();
            InstanceRegistryFile.Update(registryPath, registry =>
            {
                registry.instances ??= Array.Empty<InstanceRecord>();
                registry.instances = registry.instances
                    .Append(new InstanceRecord
                    {
                        projectRoot = projectB,
                        projectName = "ProjectB",
                        projectHash = hashB,
                        pipeName = ProtocolConstants.BuildPipeName(hashB),
                        state = "idle",
                        lastSeenUtc = DateTimeOffset.UtcNow.ToString("O"),
                    })
                    .ToArray();
                return registry;
            });
        });

        barrier.Set();
        await Task.WhenAll(useTask, heartbeatTask);

        var reloaded = store.Load();
        Assert.True(reloaded.activeProjectRootPinned);
        Assert.Equal(ProtocolConstants.GetCanonicalPath(projectA), reloaded.activeProjectRoot);
        Assert.Contains(
            reloaded.instances,
            item => string.Equals(
                item.projectRoot,
                ProtocolConstants.GetCanonicalPath(projectB),
                StringComparison.OrdinalIgnoreCase));
        Assert.Equal(2, reloaded.instances.Length);
    }
}
