using System.Text.Json;
using System.Text.Json.Nodes;
using UnityCli.Cli.Models;
using UnityCli.Cli.Services;
using UnityCli.Protocol;

namespace UnityCli.Cli;

public static class CliApp
{
    /// <summary>The editor process <c>doctor</c> is comparing a status file against.</summary>
    internal readonly struct DoctorRunningEditor
    {
        public DoctorRunningEditor(int pid, DateTime startedUtc, bool hasStartTime = true)
        {
            Pid = pid;
            StartedUtc = startedUtc;
            HasStartTime = hasStartTime;
        }

        public int Pid { get; }

        public DateTime StartedUtc { get; }

        public bool HasStartTime { get; }
    }

    public static Task<int> RunAsync(string[] args) => RunAsync(args, dispatcher: null);

    internal static async Task<int> RunAsync(string[] args, ICliVersionDispatcher? dispatcher)
    {
        var outputMode = CliCommandMetadata.DetectOutputMode(args);
        ParsedCommand? parsed = null;

        try
        {
            parsed = CliArgumentParser.Parse(args);
            outputMode = parsed.OutputMode;
            if (parsed.Kind == CommandKind.Help)
            {
                string? topicHelp = parsed.HelpTopic is null ? null : CliCommandMetadata.BuildTopicHelpText(parsed.HelpTopic);
                Console.WriteLine(topicHelp ?? CliArgumentParser.BuildHelpText());
                return 0;
            }

            if (parsed.Kind == CommandKind.Version)
            {
                Console.WriteLine(CliVersion.InformationalVersion);
                return 0;
            }

            var registryStore = new InstanceRegistryStore();
            var locator = new UnityProjectLocator();
            var projectRoot = ResolveProjectRoot(parsed, locator, registryStore);

            var response = parsed.Kind switch
            {
                CommandKind.Status => await RunStatusAsync(registryStore, projectRoot),
                CommandKind.InstancesList => ListInstances(registryStore, projectRoot, parsed),
                CommandKind.InstancesUse => UseInstance(registryStore, parsed),
                CommandKind.Doctor => await RunDoctorAsync(registryStore, locator, parsed, projectRoot),
                CommandKind.QaWait => await RunQaWait(parsed),
                CommandKind.ProfileAnalyze => ProfileAnalyzer.Run(parsed, projectRoot),
                CommandKind.ProfileCompare => ProfileComparer.Run(parsed, projectRoot),
                CommandKind.ProfileMemoryCompare => ProfileMemoryComparer.Run(parsed, projectRoot),
                CommandKind.EditorLaunch => await EditorLauncher.LaunchAsync(parsed, registryStore, projectRoot),
                CommandKind.EditorStop => await RunEditorStopAsync(parsed, registryStore, projectRoot),
                _ => await ExecuteUnityCommandAsync(parsed, registryStore, projectRoot),
            };

            // Zero cost unless the bridge answered PROTOCOL_MISMATCH: Decide() short-circuits before
            // it touches the filesystem or the environment.
            var versionDispatcher = dispatcher ?? new ProcessCliVersionDispatcher();
            var decision = CliDispatchPolicy.Decide(parsed.Kind, response, versionDispatcher);
            switch (decision.Action)
            {
                case DispatchAction.Exec:
                    try
                    {
                        // Unix: replaces this process and never returns. Windows: child's exit code.
                        return versionDispatcher.Exec(decision.Match!.ExecutablePath, args);
                    }
                    catch (CliDispatchException ex)
                    {
                        // Routing around the mismatch failed; the mismatch is still the real problem.
                        response = CliDispatchPolicy.BuildHandoffFailedResponse(
                            response,
                            decision.Match!,
                            ex.Message);
                    }

                    break;
                case DispatchAction.NoMatchingVersion:
                    response = CliDispatchPolicy.BuildNoMatchingVersionResponse(
                        response,
                        decision.RequiredProtocolVersion,
                        decision.InstalledVersions);
                    break;
            }

            Console.WriteLine(ResponseFormatter.Format(parsed.OutputMode, response));
            return response.status == "success" ? 0 : 1;
        }
        catch (CliUsageException ex)
        {
            var presentation = CliUsageHelp.Build(args, ex.Message, parsed);
            var response = ResponseEnvelope.Failure(
                Guid.NewGuid().ToString("N"),
                null,
                "CLI_USAGE",
                presentation.Message,
                retryable: false,
                details: presentation.BuildDetailsJson(),
                transport: "cli");

            WriteUsageError(outputMode, response, presentation);
            return 2;
        }
        catch (AmbiguousProjectException ex)
        {
            var response = ResponseEnvelope.Failure(
                Guid.NewGuid().ToString("N"),
                null,
                AmbiguousProjectException.ErrorCode,
                ex.Message,
                retryable: false,
                details: ex.ToDetailsJson(),
                transport: "cli");

            WriteErrorResponse(outputMode, response);
            return 2;
        }
        catch (Exception ex)
        {
            var response = ResponseEnvelope.Failure(
                Guid.NewGuid().ToString("N"),
                null,
                "CLI_ERROR",
                ex.Message,
                retryable: false,
                details: ex.ToString(),
                transport: "cli");

            WriteErrorResponse(outputMode, response);
            return 1;
        }
    }

    private static string? ResolveProjectRoot(
        ParsedCommand parsed,
        UnityProjectLocator locator,
        InstanceRegistryStore registryStore)
    {
        if (!string.IsNullOrWhiteSpace(parsed.ProjectOverride))
        {
            var projectOverride = parsed.ProjectOverride.Trim();
            if (Directory.Exists(projectOverride))
            {
                return ProtocolConstants.GetCanonicalPath(projectOverride);
            }

            var registry = registryStore.Load();
            return registryStore.ResolveProjectRootOverride(registry, projectOverride);
        }

        IReadOnlyList<string> candidates = locator.FindCandidateProjectRoots(Environment.CurrentDirectory);
        if (candidates.Count <= 1)
        {
            return candidates.Count == 0 ? null : candidates[0];
        }

        return UnityProjectLocator.SelectProjectRoot(candidates, registryStore.Load());
    }

    private static ResponseEnvelope ListInstances(InstanceRegistryStore registryStore, string? projectRoot, ParsedCommand parsed)
    {
        var registry = registryStore.Load();
        var canonicalCurrent = !string.IsNullOrWhiteSpace(projectRoot)
            ? ProtocolConstants.GetCanonicalPath(projectRoot)
            : null;
        var sortedInstances = registry.instances
            .OrderBy(item => item.projectName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.projectRoot, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        object data = parsed.InstancesBrief
            ? new
            {
                activeProjectRoot = registry.activeProjectRoot,
                currentProjectRoot = canonicalCurrent,
                currentProjectHash = canonicalCurrent != null ? ProtocolConstants.ComputeProjectHash(canonicalCurrent) : null,
                instances = sortedInstances.Select(item => new
                {
                    item.projectName,
                    item.projectRoot,
                    item.projectHash,
                    item.state,
                }).ToArray(),
            }
            : new
            {
                activeProjectRoot = registry.activeProjectRoot,
                currentProjectRoot = canonicalCurrent,
                currentProjectHash = canonicalCurrent != null ? ProtocolConstants.ComputeProjectHash(canonicalCurrent) : null,
                instances = sortedInstances,
            };

        return ResponseEnvelope.Success(
            Guid.NewGuid().ToString("N"),
            null,
            CreateDataElement(data),
            durationMs: 0,
            transport: "cli");
    }

    private static ResponseEnvelope UseInstance(InstanceRegistryStore registryStore, ParsedCommand parsed)
    {
        if (string.IsNullOrWhiteSpace(parsed.InstanceTarget))
        {
            throw new CliUsageException("`instances use` requires a project hash, project path, or project name.");
        }

        InstanceRecord? target = null;
        registryStore.Update(registry =>
        {
            target = registryStore.ResolveOrCreateTarget(registry, parsed.InstanceTarget!);
            registry.activeProjectRoot = target.projectRoot;
            registry.activeProjectRootPinned = true;
            registry.activeProjectHash = null;
            return registry;
        });

        var data = new
        {
            activeProjectRoot = target!.projectRoot,
            target.projectName,
            target.projectRoot,
            target.projectHash,
            target.pipeName,
            target.state,
        };

        return ResponseEnvelope.Success(
            Guid.NewGuid().ToString("N"),
            target.projectHash,
            CreateDataElement(data),
            durationMs: 0,
            transport: "cli");
    }

    internal static async Task<ResponseEnvelope> RunStatusAsync(
        InstanceRegistryStore registryStore,
        string? projectRoot,
        Func<InstanceRecord, CommandEnvelope, int, CancellationToken, Task<ResponseEnvelope>>? sendAsync = null)
    {
        var registry = registryStore.Load();
        var candidates = ResolveTargetCandidates(registry, projectRoot);
        var primaryTarget = candidates.Length > 0 ? candidates[0] : null;
        var sendCommandAsync = sendAsync ?? new LocalIpcClient().SendAsync;

        foreach (var candidate in candidates)
        {
            if (IsMissingAuthTokenForSyntheticTarget(registry, projectRoot, candidate))
            {
                continue;
            }

            try
            {
                using var cts = new CancellationTokenSource(5_000);
                var response = await sendCommandAsync(
                    candidate,
                    new CommandEnvelope
                    {
                        requestId = Guid.NewGuid().ToString("N"),
                        command = ProtocolConstants.CommandStatus,
                        argumentsJson = "{}",
                    },
                    5_000,
                    cts.Token);

                // A diagnostic read-only command: honest about it if a fallback candidate answered
                // instead of the originally resolved (pinned/active) target.
                return candidate == primaryTarget
                    ? response
                    : AttachFailoverInfo(response, primaryTarget!, candidate);
            }
            catch
            {
            }
        }

        var data = new
        {
            projectRoot,
            projectHash = !string.IsNullOrWhiteSpace(projectRoot) ? ProtocolConstants.ComputeProjectHash(projectRoot) : null,
            activeProjectRoot = registry.activeProjectRoot,
            liveReachable = false,
            unityPath = !string.IsNullOrWhiteSpace(projectRoot) ? UnityEditorLocator.TryResolve(projectRoot) : null,
            registryPath = RegistryPathUtility.GetRegistryFilePath(),
        };

        return ResponseEnvelope.Success(
            Guid.NewGuid().ToString("N"),
            primaryTarget?.projectHash,
            CreateDataElement(data),
            durationMs: 0,
            transport: "cli");
    }

    internal static async Task<ResponseEnvelope> RunDoctorAsync(
        InstanceRegistryStore registryStore,
        UnityProjectLocator locator,
        ParsedCommand parsed,
        string? projectRoot,
        Func<InstanceRecord, CommandEnvelope, int, CancellationToken, Task<ResponseEnvelope>>? sendAsync = null,
        Func<string, string?>? resolveUnityPath = null)
    {
        var registry = registryStore.Load();
        var candidates = ResolveTargetCandidates(registry, projectRoot);
        var primaryTarget = candidates.Length > 0 ? candidates[0] : null;
        var unityPath = !string.IsNullOrWhiteSpace(projectRoot)
            ? (resolveUnityPath ?? UnityEditorLocator.TryResolve)(projectRoot)
            : null;
        // Local file probe only (no process, no system .NET), so doctor stays fast and offline.
        var bundledToolchain = BuildBundledToolchainData(BundledToolchainLocator.Locate(unityPath));
        var sendCommandAsync = sendAsync ?? new LocalIpcClient().SendAsync;

        var liveReachable = false;
        string? liveErrorCode = null;
        string? liveErrorMessage = null;
        InstanceRecord? respondedTarget = null;

        foreach (var candidate in candidates)
        {
            if (IsMissingAuthTokenForSyntheticTarget(registry, projectRoot, candidate))
            {
                continue;
            }

            try
            {
                using var cts = new CancellationTokenSource(5_000);
                var ping = new CommandEnvelope
                {
                    requestId = Guid.NewGuid().ToString("N"),
                    command = "ping",
                    argumentsJson = "{}",
                };
                var response = await sendCommandAsync(candidate, ping, 5_000, cts.Token);
                respondedTarget = candidate;
                liveReachable = response.status == "success";
                if (!liveReachable && response.error is not null)
                {
                    liveErrorCode = response.error.code;
                    liveErrorMessage = response.error.message;
                }

                // Candidate answered (even if with an error) — connectivity itself is resolved,
                // no point trying further fallbacks.
                break;
            }
            catch
            {
                liveReachable = false;
            }
        }

        var reportedTarget = respondedTarget ?? primaryTarget;
        DoctorRunningEditor? runningEditor = TryResolveRunningEditor(reportedTarget);

        var data = new
        {
            registryPath = RegistryPathUtility.GetRegistryFilePath(),
            workingDirectory = Environment.CurrentDirectory,
            projectRoot,
            projectDetectedFromChildren = string.IsNullOrWhiteSpace(projectRoot) ? locator.TryFindProjectRoot(Environment.CurrentDirectory) : projectRoot,
            activeProjectRoot = registry.activeProjectRoot,
            targetProjectHash = reportedTarget?.projectHash,
            targetProjectName = reportedTarget?.projectName,
            pipeName = reportedTarget?.pipeName,
            liveReachable,
            liveErrorCode,
            liveErrorMessage,
            unityPath,
            bundledToolchain,
            roslynHost = BuildRoslynHostData(projectRoot, runningEditor: runningEditor),
            cscShared = BuildCscSharedData(projectRoot, runningEditor: runningEditor),
            instanceCount = registry.instances.Length,
        };

        var result = ResponseEnvelope.Success(
            Guid.NewGuid().ToString("N"),
            reportedTarget?.projectHash,
            CreateDataElement(data),
            durationMs: 0,
            transport: "cli");

        // Diagnostic honesty: surface that the reported live instance is a fallback, not the
        // originally resolved (pinned/active) target. Deliberately not gated on `liveReachable` —
        // a fallback that answers with an error (PROTOCOL_MISMATCH on a mixed-version machine, say)
        // still has its identity and error code reported here, so without the marker the user
        // would debug the wrong project.
        if (respondedTarget is not null
            && primaryTarget is not null
            && !string.Equals(respondedTarget.projectRoot, primaryTarget.projectRoot, StringComparison.OrdinalIgnoreCase))
        {
            result = AttachFailoverInfo(result, primaryTarget, respondedTarget);
        }

        return result;
    }

    /// <summary>
    /// The editor's Roslyn compile host, from the status file the bridge writes under the project's
    /// Library. Local file read only; liveness is a process-table probe. A file whose editor pid or
    /// write time does not match <paramref name="runningEditor"/> is an old-session record.
    /// </summary>
    internal static object? BuildRoslynHostData(
        string? projectRoot,
        Func<int, bool>? isAlive = null,
        DateTime? nowUtc = null,
        DoctorRunningEditor? runningEditor = null)
    {
        if (string.IsNullOrWhiteSpace(projectRoot))
        {
            return null;
        }

        string statusPath = RoslynHostStatus.GetStatusPath(projectRoot);
        RoslynHostStatus? status = RoslynHostStatus.TryRead(statusPath);
        if (status is null)
        {
            return new
            {
                statusPath,
                present = false,
                activeBackend = "roslyn-host",
                note = "No Roslyn host status for this project. The default compiler is the Roslyn host. "
                    + "Set UCB_EXECUTE_COMPILER=codedom to turn it off.",
            };
        }

        Func<int, bool> alive = isAlive ?? IsProcessAlive;
        bool current = IsCurrentCompilerSession(status.EditorPid, File.GetLastWriteTimeUtc(statusPath), runningEditor);
        bool editorAlive = current && status.EditorPid > 0 && alive(status.EditorPid);
        bool hostAlive = status.Pid > 0 && alive(status.Pid);
        DateTime? started = RoslynHostStatus.ParseUtc(status.HostStartedUtc);
        double? uptimeSeconds = hostAlive && started.HasValue
            ? Math.Round(((nowUtc ?? DateTime.UtcNow) - started.Value).TotalSeconds, 1)
            : null;
        return new
        {
            statusPath,
            present = true,
            oldSession = !current,
            stale = !current || !editorAlive,
            updatedUtc = status.UpdatedUtc,
            editorPid = status.EditorPid,
            runningEditorPid = runningEditor is { Pid: > 0 } editor ? (int?)editor.Pid : null,
            editorAlive,
            configuredCompiler = status.ConfiguredCompiler,
            activeBackend = current ? status.ActiveBackend : string.Empty,
            prewarmPolicy = status.PrewarmPolicy,
            hostWaitMs = status.HostWaitMs,
            compileTimeoutMs = status.CompileTimeoutMs,
            idleExitMinutes = status.IdleExitMinutes,
            state = status.State,
            note = current ? status.Note : OldCompilerSessionNote,
            pid = status.Pid,
            hostAlive,
            hostStartedUtc = status.HostStartedUtc,
            uptimeSeconds,
            startMs = status.StartMs,
            prewarmMs = status.PrewarmMs,
            runtimeDirectory = status.RuntimeDirectory,
            runtimeVersion = status.RuntimeVersion,
            roslynVersion = status.RoslynVersion,
            build = new
            {
                key = status.BuildKey,
                cacheHit = status.BuildCacheHit,
                buildMs = status.BuildMs,
            },
            lastError = status.LastError,
            lastErrorUtc = status.LastErrorUtc,
            sessionDisabledReason = status.SessionDisabledReason,
            recentCrashes = status.RecentCrashes,
            starts = status.Starts,
            lastCompiler = status.LastCompiler,
            lastFallbackReason = status.LastFallbackReason,
            ignoredProjectOptions = status.IgnoredProjectOptions,
            stderrTail = status.StderrTail,
        };
    }

    /// <summary>
    /// The editor's <c>csc /shared</c> fallback, from the status file the bridge writes under the
    /// project's Library. Local file read only; <c>serverAlive</c> looks for a compiler server on the
    /// recorded pipe in the process table (by its recorded pid where command lines cannot be read),
    /// including when the status file is an old session and the server is a leftover. A file whose
    /// editor pid or write time does not match <paramref name="runningEditor"/> is an old-session
    /// record. <c>serverPid</c> is null and <c>serverPidPending</c> is true while the editor is still
    /// looking up the compiler-server pid. A lookup that times out stays pending and is retried; when
    /// the retries are exhausted <c>serverPid</c> is null and <c>serverPidUnknown</c> is true, not 0.
    /// </summary>
    internal static object? BuildCscSharedData(
        string? projectRoot,
        Func<string, IReadOnlyList<int>>? findServers = null,
        Func<int, bool>? isAlive = null,
        DoctorRunningEditor? runningEditor = null)
    {
        if (string.IsNullOrWhiteSpace(projectRoot))
        {
            return null;
        }

        string statusPath = CscSharedStatus.GetStatusPath(projectRoot);
        CscSharedStatus? status = CscSharedStatus.TryRead(statusPath);
        if (status is null)
        {
            return new
            {
                statusPath,
                present = false,
                note = "No csc-shared status for this project. The default compiler is the Roslyn host, "
                    + "which uses csc /shared as a fallback. Set UCB_EXECUTE_COMPILER=codedom to turn it off.",
            };
        }

        Func<int, bool> alive = isAlive ?? IsProcessAlive;
        bool current = IsCurrentCompilerSession(status.EditorPid, File.GetLastWriteTimeUtc(statusPath), runningEditor);
        bool editorAlive = current && status.EditorPid > 0 && alive(status.EditorPid);
        IReadOnlyList<int> servers = string.IsNullOrEmpty(status.PipeName)
            ? Array.Empty<int>()
            : (findServers ?? FindCscSharedServers)(status.PipeName);
        // An old session still reports a leftover compiler server. On Windows the process table
        // listing is empty, so the recorded pid is what proves the orphan is running.
        bool recordedPidCounts = findServers is null && OperatingSystem.IsWindows();
        bool serverAlive = IsCscServerProcessAlive(servers, status.ServerPid, recordedPidCounts, alive);
        bool serverPidPending = current && status.ServerPidPending && servers.Count == 0;
        bool serverPidUnknown = status.ServerPidUnknown && servers.Count == 0 && !serverPidPending;
        int? serverPid = servers.Count > 0
            ? servers[0]
            : (serverPidPending || serverPidUnknown ? null : (int?)status.ServerPid);
        return new
        {
            statusPath,
            present = true,
            oldSession = !current,
            stale = !current || !editorAlive,
            updatedUtc = status.UpdatedUtc,
            editorPid = status.EditorPid,
            runningEditorPid = runningEditor is { Pid: > 0 } editor ? (int?)editor.Pid : null,
            editorAlive,
            configuredCompiler = status.ConfiguredCompiler,
            pipeName = status.PipeName,
            state = status.State,
            note = current ? status.Note : OldCompilerSessionNote,
            serverPid,
            serverPidPending,
            serverPidUnknown,
            serverAlive,
            dotnetPath = status.DotnetPath,
            cscPath = status.CscPath,
            roslynVersion = status.RoslynVersion,
            compileTimeoutMs = status.CompileTimeoutMs,
            compiles = status.Compiles,
            firstCompileMs = status.FirstCompileMs,
            lastShutdownMs = status.LastShutdownMs,
            lastError = status.LastError,
            lastErrorUtc = status.LastErrorUtc,
        };
    }

    /// <summary>
    /// True when a compiler server from the status file is still running. A pipe listing wins.
    /// <paramref name="recordedPidCounts"/> is the Windows doctor path, which cannot list command
    /// lines cheaply and uses <paramref name="recordedPid"/> instead. An old session uses the same check.
    /// </summary>
    internal static bool IsCscServerProcessAlive(
        IReadOnlyList<int> listedServers,
        int recordedPid,
        bool recordedPidCounts,
        Func<int, bool> isAlive)
    {
        if (listedServers.Count > 0)
        {
            return true;
        }

        return recordedPidCounts && recordedPid > 0 && isAlive(recordedPid);
    }

    internal const string OldCompilerSessionNote =
        "Old-session record. This status.json does not match the running editor (editor pid or file write time).";

    /// <summary>How far a status file's write time may sit before the editor process start and still be this session.</summary>
    private const double CompilerStatusSessionSkewSeconds = 2;

    /// <summary>
    /// True when <paramref name="statusEditorPid"/> is the running editor and the status file was
    /// written after that process started. No running editor, a different pid, or an older write is
    /// a previous session (including a reused pid).
    /// </summary>
    internal static bool IsCurrentCompilerSession(int statusEditorPid, DateTime statusWrittenUtc, DoctorRunningEditor? runningEditor)
    {
        if (runningEditor is null || runningEditor.Value.Pid <= 0 || statusEditorPid != runningEditor.Value.Pid)
        {
            return false;
        }

        if (!runningEditor.Value.HasStartTime)
        {
            return true;
        }

        DateTime started = runningEditor.Value.StartedUtc.Kind == DateTimeKind.Utc
            ? runningEditor.Value.StartedUtc
            : runningEditor.Value.StartedUtc.ToUniversalTime();
        DateTime written = statusWrittenUtc.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(statusWrittenUtc, DateTimeKind.Utc)
            : statusWrittenUtc.ToUniversalTime();
        return written >= started.AddSeconds(-CompilerStatusSessionSkewSeconds);
    }

    private static DoctorRunningEditor? TryResolveRunningEditor(InstanceRecord? record)
    {
        if (record == null || record.editorProcessId <= 0 || !IsProcessAlive(record.editorProcessId))
        {
            return null;
        }

        DateTime? started = TryProcessStartUtc(record.editorProcessId);
        return new DoctorRunningEditor(record.editorProcessId, started ?? default, started.HasValue);
    }

    private static DateTime? TryProcessStartUtc(int pid)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            return process.StartTime.ToUniversalTime();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Pids of compiler servers on <paramref name="pipeName"/> (macOS/Linux <c>ps</c>; empty on Windows or on error).</summary>
    private static IReadOnlyList<int> FindCscSharedServers(string pipeName)
    {
        if (OperatingSystem.IsWindows())
        {
            return Array.Empty<int>();
        }

        try
        {
            var startInfo = new System.Diagnostics.ProcessStartInfo("/bin/ps", "-axww -o pid= -o command=")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using var process = System.Diagnostics.Process.Start(startInfo);
            if (process is null)
            {
                return Array.Empty<int>();
            }

            string output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(5000);
            var pids = new List<int>();
            foreach (string rawLine in output.Split('\n'))
            {
                string line = rawLine.Trim();
                int space = line.IndexOf(' ');
                if (space > 0
                    && CscSharedPipe.IsServerCommandLine(line.Substring(space + 1), pipeName)
                    && int.TryParse(line.AsSpan(0, space), out int pid))
                {
                    pids.Add(pid);
                }
            }

            return pids;
        }
        catch
        {
            return Array.Empty<int>();
        }
    }

    private static bool IsProcessAlive(int pid)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch
        {
            return false;
        }
    }

    private static object BuildBundledToolchainData(BundledToolchainReport report)
    {
        return new
        {
            contentsPath = report.ContentsPath,
            layout = report.Layout,
            toolchainRoot = report.ToolchainRoot,
            complete = report.Complete,
            error = report.Error,
            runtime = report.Runtime is null
                ? null
                : new
                {
                    dotnetPath = report.Runtime.Path,
                    directory = report.Runtime.Directory,
                    netCoreAppVersion = report.Runtime.Version,
                    error = report.Runtime.Error,
                },
            roslyn = report.Roslyn is null
                ? null
                : new
                {
                    directory = report.Roslyn.Directory,
                    cscPath = report.Roslyn.Path,
                    version = report.Roslyn.Version,
                    error = report.Roslyn.Error,
                },
            mcs = report.Mcs is null
                ? null
                : new
                {
                    path = report.Mcs.Path,
                    version = report.Mcs.Version,
                    error = report.Mcs.Error,
                },
        };
    }

    /// <summary>
    /// Merges a `failedOverFrom` marker into a diagnostic response's data payload, identifying the
    /// originally resolved target that failed to connect. Read-only-diagnostic-only (`status`/`doctor`);
    /// mutating command dispatch (<see cref="ExecuteUnityCommandAsync"/>) never fails over.
    /// </summary>
    private static ResponseEnvelope AttachFailoverInfo(
        ResponseEnvelope response,
        InstanceRecord attemptedTarget,
        InstanceRecord respondedTarget)
    {
        var failedOverFrom = new
        {
            projectHash = attemptedTarget.projectHash,
            projectRoot = attemptedTarget.projectRoot,
            projectName = attemptedTarget.projectName,
        };

        JsonNode? node = response.data.HasValue
            ? JsonNode.Parse(response.data.Value.GetRawText())
            : null;

        JsonObject obj;
        if (node is JsonObject existing)
        {
            obj = existing;
        }
        else if (node is null)
        {
            obj = new JsonObject();
        }
        else
        {
            // status/doctor payloads are objects today, so this is unreachable — but a non-object
            // payload has nowhere to hold a `failedOverFrom` sibling, and dropping it would be
            // silent data loss. Nest it verbatim instead.
            obj = new JsonObject { ["data"] = node };
        }

        obj["failedOverFrom"] = JsonSerializer.SerializeToNode(failedOverFrom, ProtocolJson.Default);

        response.data = JsonSerializer.SerializeToElement(obj, ProtocolJson.Default);
        return response;
    }

    internal static async Task<ResponseEnvelope> ExecuteUnityCommandAsync(
        ParsedCommand parsed,
        InstanceRegistryStore registryStore,
        string? projectRoot,
        Func<InstanceRecord, CommandEnvelope, int, CancellationToken, Task<ResponseEnvelope>>? sendAsync = null)
    {
        var command = parsed.ToEnvelope();
        var registry = registryStore.Load();
        var target = ResolveTarget(registry, projectRoot);

        if (target is not null)
        {
            if (IsMissingAuthTokenForSyntheticTarget(registry, projectRoot, target))
            {
                return CreateLiveUnavailableResponse(
                    target.projectHash,
                    "Could not read the bridge auth token. The Unity Editor may be closed, starting, restarting, or recompiling scripts. If the Editor is open, try again shortly.");
            }

            try
            {
                int liveTimeoutMs = ResolveLiveTimeoutMs(parsed);
                using var cts = new CancellationTokenSource(ResolveCommandCancellationTimeoutMs(parsed, liveTimeoutMs));
                var ipcClient = new LocalIpcClient();
                var sendCommandAsync = sendAsync ?? ipcClient.SendAsync;
                var response = await sendCommandAsync(target, command, liveTimeoutMs, cts.Token);
                if (IsUnauthorizedResponse(response))
                {
                    (response, target) = await RetryUnauthorizedOnceAsync(
                        registryStore,
                        projectRoot,
                        target,
                        command,
                        liveTimeoutMs,
                        cts.Token,
                        sendCommandAsync,
                        response);
                }

                if (ShouldPollTestResults(parsed, response))
                {
                    return await PollTestResultsAsync(parsed, target, ipcClient, response, cts.Token);
                }

                if (ShouldPollRecordStatus(parsed, response))
                {
                    return await PollRecordStatusAsync(parsed, target, sendCommandAsync, response, cts.Token);
                }

                if (ShouldWaitProfileCapture(parsed, response))
                {
                    return await WaitProfileCaptureAsync(parsed, target, sendCommandAsync, response, cts.Token);
                }

                if (ShouldPollProfileStop(parsed, response))
                {
                    var stopSummary = DeserializeData<ProfileSummaryPayload>(response);
                    if (stopSummary != null && !string.IsNullOrEmpty(stopSummary.captureId))
                    {
                        try
                        {
                            response = await PollProfileStatusAsync(target, sendCommandAsync, stopSummary.captureId, cts.Token);
                        }
                        catch (Exception)
                        {
                            // stop itself already succeeded (Processing), so a cancel or transport throw during polling
                            // keeps the original response — the captureId can be read later with `profile status`.
                        }
                    }
                }

                if (parsed.Kind == CommandKind.QaRunSequence
                    && parsed.QaSequenceProfile
                    && string.Equals(response.status, ProtocolConstants.StatusSuccess, StringComparison.Ordinal))
                {
                    response = await MergeQaProfileSummaryAsync(target, sendCommandAsync, response, cts.Token);
                }

                if (ShouldPollEditorReady(parsed, response))
                {
                    TimeSpan waitTimeout = ResolveEditorReadyWaitTimeout(parsed);
                    return await PollEditorReadyAsync(
                        parsed,
                        response,
                        (statusCommand, timeoutMs, cancellationToken) =>
                            sendCommandAsync(target, statusCommand, timeoutMs, cancellationToken),
                        cts.Token,
                        timeout: waitTimeout,
                        pollInterval: ResolveEditorReadyPollInterval(parsed.Kind));
                }

                response = RequireMarkerStatsSupport(parsed, response);
                return NormalizeResultEnvelope(parsed.Kind, response);
            }
            catch (Exception ex)
            {
                return CreateLiveUnavailableResponse(
                    target.projectHash,
                    "Check whether Unity is importing or compiling a local package, then try again. Cause: " + ex.Message);
            }
        }

        if (!string.IsNullOrWhiteSpace(projectRoot))
        {
            return CreateLiveUnavailableResponse(
                ProtocolConstants.ComputeProjectHash(projectRoot),
                "Open the Unity Editor and try again after the bridge finishes importing and compiling.");
        }

        var offlineCandidates = registry.instances
            .OrderBy(item => item.projectName, StringComparer.OrdinalIgnoreCase)
            .Select(item => $"  - {item.projectName} ({item.state})  {item.projectRoot}")
            .ToArray();
        string noTargetDetails = offlineCandidates.Length > 0
            ? "Run from a Unity project root, or pin a target with `unity-cli instances use <projectHash|projectPath|projectName>`.\n"
              + "Registered instances that are not running:\n" + string.Join("\n", offlineCandidates)
            : "Run from a Unity project root, or pin a target with `unity-cli instances use <projectHash|projectPath|projectName>`.";

        return ResponseEnvelope.Failure(
            Guid.NewGuid().ToString("N"),
            target?.projectHash,
            "NO_TARGET",
            "The Unity Editor is not running, or the bridge is not active.",
            retryable: false,
            transport: "cli",
            details: noTargetDetails);
    }

    private static async Task<ResponseEnvelope> RunEditorStopAsync(
        ParsedCommand parsed,
        InstanceRegistryStore registryStore,
        string? projectRoot)
    {
        ResponseEnvelope response = await ExecuteUnityCommandAsync(parsed, registryStore, projectRoot);
        if (!string.Equals(response.status, ProtocolConstants.StatusSuccess, StringComparison.Ordinal)
            || parsed.EditorNoWait)
        {
            return response;
        }

        int editorProcessId;
        try
        {
            editorProcessId = DeserializeData<EditorQuitPayload>(response)?.editorProcessId ?? 0;
        }
        catch (JsonException)
        {
            return response;
        }

        if (editorProcessId <= 0)
        {
            return response;
        }

        int timeoutSeconds = parsed.EditorWaitTimeoutSeconds ?? 30;
        var deadline = DateTimeOffset.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (!EditorLauncher.IsProcessAlive(editorProcessId))
            {
                return response;
            }

            await Task.Delay(1000);
        }

        return ResponseEnvelope.Failure(
            response.requestId,
            response.target,
            ProtocolConstants.ErrorEditorStopTimeout,
            $"The editor did not exit within {timeoutSeconds} seconds after the quit response (PID {editorProcessId}). Use kill if you need to force it.",
            retryable: true,
            transport: "cli");
    }

    private static async Task<(ResponseEnvelope Response, InstanceRecord Target)> RetryUnauthorizedOnceAsync(
        InstanceRegistryStore registryStore,
        string? projectRoot,
        InstanceRecord originalTarget,
        CommandEnvelope command,
        int liveTimeoutMs,
        CancellationToken cancellationToken,
        Func<InstanceRecord, CommandEnvelope, int, CancellationToken, Task<ResponseEnvelope>> sendCommandAsync,
        ResponseEnvelope unauthorizedResponse)
    {
        InstanceRecord? refreshedTarget = ResolveSameTargetForUnauthorizedRetry(
            registryStore.Load(),
            projectRoot,
            originalTarget);
        if (refreshedTarget is null
            || string.Equals(refreshedTarget.token, originalTarget.token, StringComparison.Ordinal))
        {
            return (AddUnauthorizedRetryHint(unauthorizedResponse), originalTarget);
        }

        var retryResponse = await sendCommandAsync(refreshedTarget, command, liveTimeoutMs, cancellationToken);
        retryResponse = IsUnauthorizedResponse(retryResponse)
            ? AddUnauthorizedRetryHint(retryResponse)
            : retryResponse;
        return (retryResponse, refreshedTarget);
    }

    private static InstanceRecord? ResolveSameTargetForUnauthorizedRetry(
        InstanceRegistry registry,
        string? projectRoot,
        InstanceRecord originalTarget)
    {
        registry.instances ??= Array.Empty<InstanceRecord>();

        string? targetRoot = !string.IsNullOrWhiteSpace(originalTarget.projectRoot)
            ? ProtocolConstants.GetCanonicalPath(originalTarget.projectRoot)
            : !string.IsNullOrWhiteSpace(projectRoot)
                ? ProtocolConstants.GetCanonicalPath(projectRoot)
                : null;

        if (!string.IsNullOrWhiteSpace(targetRoot))
        {
            var rootMatch = registry.instances.FirstOrDefault(item =>
                string.Equals(
                    ProtocolConstants.GetCanonicalPath(item.projectRoot),
                    targetRoot,
                    StringComparison.OrdinalIgnoreCase));
            if (rootMatch is not null)
            {
                return rootMatch;
            }
        }

        if (!string.IsNullOrWhiteSpace(originalTarget.projectHash))
        {
            var hashMatch = registry.instances.FirstOrDefault(item =>
                string.Equals(item.projectHash, originalTarget.projectHash, StringComparison.OrdinalIgnoreCase));
            if (hashMatch is not null)
            {
                return hashMatch;
            }
        }

        return string.IsNullOrWhiteSpace(originalTarget.pipeName)
            ? null
            : registry.instances.FirstOrDefault(item =>
                string.Equals(item.pipeName, originalTarget.pipeName, StringComparison.Ordinal));
    }

    private static bool IsMissingAuthTokenForSyntheticTarget(
        InstanceRegistry registry,
        string? projectRoot,
        InstanceRecord target)
    {
        if (!string.IsNullOrEmpty(target.token) || string.IsNullOrWhiteSpace(projectRoot))
        {
            return false;
        }

        registry.instances ??= Array.Empty<InstanceRecord>();
        string canonicalProjectRoot = ProtocolConstants.GetCanonicalPath(projectRoot);
        return !registry.instances.Any(item =>
            !string.IsNullOrWhiteSpace(item.projectRoot) &&
            string.Equals(
                ProtocolConstants.GetCanonicalPath(item.projectRoot),
                canonicalProjectRoot,
                StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsUnauthorizedResponse(ResponseEnvelope response)
    {
        return string.Equals(response.error?.code, ProtocolConstants.ErrorUnauthorized, StringComparison.Ordinal);
    }

    private static ResponseEnvelope AddUnauthorizedRetryHint(ResponseEnvelope response)
    {
        if (response.error is null)
        {
            return response;
        }

        string hint = " Editor may have restarted; retry after the registry heartbeat refreshes (~"
            + ProtocolConstants.RegistryHeartbeatSeconds
            + " seconds).";
        if (!response.error.message.Contains("registry heartbeat", StringComparison.OrdinalIgnoreCase))
        {
            response.error.message = response.error.message.TrimEnd() + hint;
        }

        return response;
    }

    internal static int ResolveLiveTimeoutMs(ParsedCommand parsed)
    {
        if (parsed.Kind == CommandKind.TestRun)
        {
            int timeoutSeconds = parsed.TestTimeoutSeconds ?? ProtocolConstants.DefaultTestRunTimeoutSeconds;
            int testTimeoutMs = ((timeoutSeconds + ProtocolConstants.TestRunCancelGraceSeconds) * 1000)
                + ProtocolConstants.DefaultLiveTimeoutMs;
            return Math.Max(parsed.TimeoutMs, testTimeoutMs);
        }

        if (parsed.Kind == CommandKind.ExecuteCode)
        {
            // The editor cancels user code cooperatively at this deadline and still has to
            // report EXECUTE_TIMEOUT, so the IPC read must outlive it or the CLI gives up
            // first and reports a transport timeout instead of the real error.
            int executeTimeoutMs = parsed.ExecuteCodeTimeoutSeconds.HasValue
                ? parsed.ExecuteCodeTimeoutSeconds.Value * 1000
                : ProtocolConstants.DefaultExecuteTimeoutMs;
            return Math.Max(parsed.TimeoutMs, executeTimeoutMs + ProtocolConstants.DefaultLiveTimeoutMs);
        }

        if (parsed.Kind is CommandKind.ProfileStats or CommandKind.ProfileMemory)
        {
            // stats/memory wait N editor frames before the bridge responds; an unfocused editor
            // can tick as slow as ~4fps, so budget 250ms per frame. Floor it at the editor's
            // own stats timeout (+base) so the CLI always outlives the bridge's PROFILE_TIMEOUT
            // instead of giving up first and reporting a generic transport error.
            int frames = parsed.ProfileFrames ?? (parsed.Kind == CommandKind.ProfileMemory
                ? ProtocolConstants.DefaultProfileMemoryFrames
                : ProtocolConstants.DefaultProfileStatsFrames);
            int frameBudgetMs = frames * 250 + ProtocolConstants.DefaultLiveTimeoutMs;
            int editorFloorMs = ProtocolConstants.ProfileStatsTimeoutSeconds * 1000 + ProtocolConstants.DefaultLiveTimeoutMs;
            return Math.Max(parsed.TimeoutMs, Math.Max(frameBudgetMs, editorFloorMs));
        }

        if (parsed.Kind == CommandKind.ProfileMemorySnapshot)
        {
            // TakeSnapshot blocks the editor main thread, so the CLI must outlive the bridge's own
            // snapshot watchdog or it gives up first and reports a transport timeout instead of
            // the bridge's PROFILE_TIMEOUT.
            int editorFloorMs = ProtocolConstants.ProfileMemorySnapshotTimeoutSeconds * 1000
                + ProtocolConstants.DefaultLiveTimeoutMs;
            return Math.Max(parsed.TimeoutMs, editorFloorMs);
        }

        return parsed.TimeoutMs;
    }

    private static int ResolveCommandCancellationTimeoutMs(ParsedCommand parsed, int liveTimeoutMs)
    {
        if (parsed.Wait && parsed.Kind is CommandKind.Compile or CommandKind.Refresh or CommandKind.Play or CommandKind.Stop)
        {
            double totalMs = liveTimeoutMs + ResolveEditorReadyWaitTimeout(parsed).TotalMilliseconds;
            return totalMs >= int.MaxValue ? int.MaxValue : Math.Max(1, (int)Math.Ceiling(totalMs));
        }

        if (parsed.Kind == CommandKind.RecordStart && parsed.RecordWait)
        {
            int durationSeconds = parsed.RecordDuration ?? ProtocolConstants.MaxRecordDurationSeconds;
            double totalMs = Math.Max(liveTimeoutMs, (durationSeconds + 15) * 1000d);
            return totalMs >= int.MaxValue ? int.MaxValue : Math.Max(1, (int)Math.Ceiling(totalMs));
        }

        if (parsed.Kind == CommandKind.ProfileCaptureStart && parsed.ProfileWait)
        {
            int waitSeconds = ProfileCaptureWaiter.ResolveTimeoutSeconds(
                parsed.ProfileFrames, parsed.ProfileDuration, parsed.ProfileWaitTimeoutSeconds);
            double totalMs = liveTimeoutMs + (waitSeconds + 15) * 1000d;
            return totalMs >= int.MaxValue ? int.MaxValue : Math.Max(1, (int)Math.Ceiling(totalMs));
        }

        if (parsed.Kind == CommandKind.ProfileCaptureStop && parsed.ProfileWait)
        {
            double totalMs = Math.Max(liveTimeoutMs, (ProtocolConstants.ProfileStopWaitTimeoutSeconds + 15) * 1000d);
            return totalMs >= int.MaxValue ? int.MaxValue : Math.Max(1, (int)Math.Ceiling(totalMs));
        }

        if (parsed.Kind == CommandKind.QaRunSequence && parsed.QaSequenceProfile)
        {
            // After the sequence returns, the CLI polls profile status for up to
            // ProfileStopWaitTimeoutSeconds while the editor walks the captured frames.
            // Add that window on top of the sequence budget so a slow walk never cancels
            // an already-successful sequence run.
            double totalMs = liveTimeoutMs + (ProtocolConstants.ProfileStopWaitTimeoutSeconds + 15) * 1000d;
            return totalMs >= int.MaxValue ? int.MaxValue : Math.Max(1, (int)Math.Ceiling(totalMs));
        }

        return liveTimeoutMs;
    }

    private static bool ShouldPollTestResults(ParsedCommand parsed, ResponseEnvelope response)
    {
        return parsed.Kind == CommandKind.TestRun
            && parsed.TestWait
            && string.Equals(parsed.TestMode, "play", StringComparison.Ordinal)
            && string.Equals(response.status, ProtocolConstants.StatusSuccess, StringComparison.Ordinal);
    }

    internal static bool ShouldPollRecordStatus(ParsedCommand parsed, ResponseEnvelope response)
    {
        return parsed.Kind == CommandKind.RecordStart
            && parsed.RecordWait
            && string.Equals(response.status, ProtocolConstants.StatusSuccess, StringComparison.Ordinal);
    }

    /// <summary>
    /// Only a stop that left the capture Capturing/Processing needs polling. A stop on a capture that already
    /// finished on its own returns the finished result (status Completed) and is printed as-is.
    /// </summary>
    internal static bool ShouldPollProfileStop(ParsedCommand parsed, ResponseEnvelope response)
    {
        if (parsed.Kind != CommandKind.ProfileCaptureStop
            || !parsed.ProfileWait
            || !string.Equals(response.status, ProtocolConstants.StatusSuccess, StringComparison.Ordinal))
        {
            return false;
        }

        string? status = DeserializeData<ProfileSummaryPayload>(response)?.status;
        return status is "Capturing" or "Processing";
    }

    /// <summary>
    /// A bridge that predates `profile stats --marker` ignores the markers and answers with counters only;
    /// its payload has no markerStats field at all (a current bridge always writes it).
    /// </summary>
    internal static ResponseEnvelope RequireMarkerStatsSupport(ParsedCommand parsed, ResponseEnvelope response)
    {
        if (parsed.Kind != CommandKind.ProfileStats
            || parsed.ProfileStatsMarkers.Count == 0
            || !string.Equals(response.status, ProtocolConstants.StatusSuccess, StringComparison.Ordinal))
        {
            return response;
        }

        string dataJson = GetDataJsonString(response);
        bool hasMarkerStats = false;
        if (!string.IsNullOrWhiteSpace(dataJson))
        {
            using JsonDocument document = JsonDocument.Parse(dataJson);
            hasMarkerStats = document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("markerStats", out _);
        }

        if (hasMarkerStats)
        {
            return response;
        }

        return ResponseEnvelope.Failure(
            response.requestId,
            response.target,
            ProtocolConstants.ErrorProfileFailed,
            "The Unity package in this project does not support `profile stats --marker`. Update com.yhc509.unity-cli-bridge to the version that matches this CLI.",
            false,
            response.durationMs,
            response.transport);
    }

    internal static bool ShouldWaitProfileCapture(ParsedCommand parsed, ResponseEnvelope response)
    {
        return parsed.Kind == CommandKind.ProfileCaptureStart
            && parsed.ProfileWait
            && string.Equals(response.status, ProtocolConstants.StatusSuccess, StringComparison.Ordinal);
    }

    private static async Task<ResponseEnvelope> WaitProfileCaptureAsync(
        ParsedCommand parsed,
        InstanceRecord target,
        Func<InstanceRecord, CommandEnvelope, int, CancellationToken, Task<ResponseEnvelope>> sendCommandAsync,
        ResponseEnvelope startResponse,
        CancellationToken cancellationToken)
    {
        int timeoutSeconds = ProfileCaptureWaiter.ResolveTimeoutSeconds(
            parsed.ProfileFrames, parsed.ProfileDuration, parsed.ProfileWaitTimeoutSeconds);
        try
        {
            return await ProfileCaptureWaiter.WaitAsync(
                target, sendCommandAsync, startResponse, timeoutSeconds, cancellationToken);
        }
        catch (Exception ex)
        {
            // The capture itself started; keep its id so the caller can stop or read it.
            string captureId = DeserializeData<ProfileCaptureStartedPayload>(startResponse)?.captureId ?? string.Empty;
            return CreateLiveUnavailableResponse(
                target.projectHash,
                $"Waiting for profile capture `{captureId}` stopped: {ex.Message} The capture was started; read it with `profile status {captureId}` or end it with `profile capture stop --wait`.");
        }
    }

    private static bool ShouldPollEditorReady(ParsedCommand parsed, ResponseEnvelope response)
    {
        return parsed.Wait
            && parsed.Kind is CommandKind.Compile or CommandKind.Refresh or CommandKind.Play or CommandKind.Stop
            && string.Equals(response.status, ProtocolConstants.StatusSuccess, StringComparison.Ordinal);
    }

    /// <summary>
    /// Play/stop transitions finish in well under a second when no domain reload is involved, so they
    /// poll quickly; compile/refresh keep a coarser interval.
    /// </summary>
    internal static TimeSpan ResolveEditorReadyPollInterval(CommandKind kind)
    {
        // compile/refresh keep the original 2s: a compile can start a few frames after the request,
        // and polling sooner could catch the Editor before it reports isCompiling.
        return kind is CommandKind.Play or CommandKind.Stop
            ? TimeSpan.FromMilliseconds(200)
            : TimeSpan.FromSeconds(2);
    }

    private static TimeSpan ResolveEditorReadyWaitTimeout(ParsedCommand parsed)
    {
        return parsed.TimeoutMsSpecified
            ? TimeSpan.FromMilliseconds(parsed.TimeoutMs)
            : TimeSpan.FromSeconds(ProtocolConstants.DefaultCompileRefreshWaitTimeoutSeconds);
    }

    private static async Task<ResponseEnvelope> PollTestResultsAsync(
        ParsedCommand original,
        InstanceRecord target,
        LocalIpcClient ipcClient,
        ResponseEnvelope startedResponse,
        CancellationToken cancellationToken)
    {
        var started = DeserializeData<TestRunStartedPayload>(startedResponse);
        if (started is null || string.IsNullOrWhiteSpace(started.runId))
        {
            return startedResponse;
        }

        int timeoutSeconds = original.TestTimeoutSeconds ?? ProtocolConstants.DefaultTestRunTimeoutSeconds;
        DateTime deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds + ProtocolConstants.TestRunCancelGraceSeconds);
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);

            var poll = await SendTestResultsPollAsync(
                target,
                ipcClient,
                started.runId,
                original.TestFailuresOnly,
                cancellationToken);
            if (!string.Equals(poll.status, ProtocolConstants.StatusSuccess, StringComparison.Ordinal))
            {
                return poll;
            }

            poll = NormalizeTestResultEnvelope(CommandKind.TestRun, poll);
            if (!string.Equals(poll.status, ProtocolConstants.StatusSuccess, StringComparison.Ordinal))
            {
                return poll;
            }

            var result = DeserializeData<TestRunResultPayload>(poll);
            if (result is null || !string.Equals(result.status, "Running", StringComparison.Ordinal))
            {
                return poll;
            }

            if (!original.JsonOutput)
            {
                Console.Error.WriteLine($"progress: {result.summary.completed}/{result.summary.total}");
            }
        }

        var finalPoll = await SendTestResultsPollAsync(
            target,
            ipcClient,
            started.runId,
            original.TestFailuresOnly,
            cancellationToken);
        finalPoll = NormalizeTestResultEnvelope(CommandKind.TestRun, finalPoll);
        if (!string.Equals(finalPoll.status, ProtocolConstants.StatusSuccess, StringComparison.Ordinal))
        {
            return finalPoll;
        }

        var finalResult = DeserializeData<TestRunResultPayload>(finalPoll);
        if (finalResult is not null && string.Equals(finalResult.status, "Running", StringComparison.Ordinal))
        {
            return BuildTestResultFailureEnvelope(
                finalPoll,
                finalResult,
                ProtocolConstants.ErrorTestTimeout,
                string.IsNullOrWhiteSpace(finalResult.runId)
                    ? "Test run timed out."
                    : "Test run " + finalResult.runId + " timed out.");
        }

        return finalPoll;
    }

    private static async Task<ResponseEnvelope> SendTestResultsPollAsync(
        InstanceRecord target,
        LocalIpcClient ipcClient,
        string runId,
        bool failuresOnly,
        CancellationToken cancellationToken)
    {
        var command = new ParsedCommand(CommandKind.TestResults)
        {
            TestRunId = runId,
            TestFailuresOnly = failuresOnly,
        }.ToEnvelope();

        return await ipcClient.SendAsync(target, command, ProtocolConstants.DefaultLiveTimeoutMs, cancellationToken);
    }

    internal static async Task<ResponseEnvelope> PollRecordStatusAsync(
        ParsedCommand original,
        InstanceRecord target,
        Func<InstanceRecord, CommandEnvelope, int, CancellationToken, Task<ResponseEnvelope>> sendAsync,
        ResponseEnvelope startedResponse,
        CancellationToken cancellationToken,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null,
        TimeSpan? pollInterval = null)
    {
        var started = DeserializeData<RecordStartedPayload>(startedResponse);
        if (started is null || string.IsNullOrWhiteSpace(started.recordingId))
        {
            return startedResponse;
        }

        delayAsync ??= Task.Delay;
        TimeSpan interval = pollInterval ?? TimeSpan.FromSeconds(1);
        int durationSeconds = original.RecordDuration ?? ProtocolConstants.MaxRecordDurationSeconds;
        DateTime deadline = DateTime.UtcNow.AddSeconds(durationSeconds + 15);
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await delayAsync(interval, cancellationToken);

            var poll = await SendRecordStatusPollAsync(target, sendAsync, started.recordingId, cancellationToken);
            if (!string.Equals(poll.status, ProtocolConstants.StatusSuccess, StringComparison.Ordinal))
            {
                return poll;
            }

            poll = NormalizeRecordResultEnvelope(CommandKind.RecordStatus, poll);
            if (!string.Equals(poll.status, ProtocolConstants.StatusSuccess, StringComparison.Ordinal))
            {
                return poll;
            }

            var result = DeserializeData<RecordResultPayload>(poll);
            if (result is null || !string.Equals(result.status, "Recording", StringComparison.Ordinal))
            {
                return poll;
            }
        }

        var finalPoll = await SendRecordStatusPollAsync(target, sendAsync, started.recordingId, cancellationToken);
        if (!string.Equals(finalPoll.status, ProtocolConstants.StatusSuccess, StringComparison.Ordinal))
        {
            return finalPoll;
        }

        finalPoll = NormalizeRecordResultEnvelope(CommandKind.RecordStatus, finalPoll);
        if (!string.Equals(finalPoll.status, ProtocolConstants.StatusSuccess, StringComparison.Ordinal))
        {
            return finalPoll;
        }

        var finalResult = DeserializeData<RecordResultPayload>(finalPoll);
        if (finalResult is not null && string.Equals(finalResult.status, "Recording", StringComparison.Ordinal))
        {
            return ResponseEnvelope.Failure(
                finalPoll.requestId,
                finalPoll.target,
                ProtocolConstants.ErrorRecordTimeout,
                "Recording " + started.recordingId + " did not finalize before the wait timeout.",
                retryable: false,
                finalPoll.durationMs,
                finalPoll.transport,
                finalPoll.data?.GetRawText());
        }

        return finalPoll;
    }

    internal static async Task<ResponseEnvelope> PollProfileStatusAsync(
        InstanceRecord target,
        Func<InstanceRecord, CommandEnvelope, int, CancellationToken, Task<ResponseEnvelope>> sendAsync,
        string captureId,
        CancellationToken cancellationToken,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null,
        TimeSpan? pollInterval = null)
    {
        delayAsync ??= Task.Delay;
        TimeSpan interval = pollInterval ?? TimeSpan.FromSeconds(1);
        DateTime deadline = DateTime.UtcNow.AddSeconds(ProtocolConstants.ProfileStopWaitTimeoutSeconds);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var command = new ParsedCommand(CommandKind.ProfileStatus)
            {
                ProfileCaptureId = captureId,
            }.ToEnvelope();
            ResponseEnvelope poll = await sendAsync(target, command, ProtocolConstants.DefaultLiveTimeoutMs, cancellationToken);
            if (!string.Equals(poll.status, ProtocolConstants.StatusSuccess, StringComparison.Ordinal))
            {
                return poll;
            }

            var summary = DeserializeData<ProfileSummaryPayload>(poll);
            if (summary is null)
            {
                return poll;
            }

            if (string.Equals(summary.status, "Completed", StringComparison.Ordinal))
            {
                return poll;
            }

            if (!string.Equals(summary.status, "Capturing", StringComparison.Ordinal)
                && !string.Equals(summary.status, "Processing", StringComparison.Ordinal))
            {
                return ProfileCaptureWaiter.TerminalFailure(poll, captureId, summary);
            }

            if (DateTime.UtcNow >= deadline)
            {
                return ResponseEnvelope.Failure(
                    poll.requestId,
                    poll.target,
                    ProtocolConstants.ErrorProfileTimeout,
                    $"profile capture `{captureId}` summary did not finish within {ProtocolConstants.ProfileStopWaitTimeoutSeconds} seconds.",
                    true,
                    poll.durationMs,
                    poll.transport);
            }

            await delayAsync(interval, cancellationToken);
        }
    }

    internal static async Task<ResponseEnvelope> MergeQaProfileSummaryAsync(
        InstanceRecord target,
        Func<InstanceRecord, CommandEnvelope, int, CancellationToken, Task<ResponseEnvelope>> sendAsync,
        ResponseEnvelope response,
        CancellationToken cancellationToken)
    {
        var payload = DeserializeData<QaRunSequencePayload>(response);
        if (payload is null || string.IsNullOrEmpty(payload.profileCaptureId))
        {
            return response;
        }

        ResponseEnvelope poll;
        try
        {
            poll = await PollProfileStatusAsync(
                target, sendAsync, payload.profileCaptureId, cancellationToken);
        }
        catch (Exception)
        {
            // A cancel (cts) or transport throw during polling must not drop a successful sequence result.
            // The captureId stays on the response, so the summary can be read later with `profile status`.
            return response;
        }

        if (!string.Equals(poll.status, ProtocolConstants.StatusSuccess, StringComparison.Ordinal))
        {
            // A summary failure must not drop the sequence result — look it up later with the captureId.
            return response;
        }

        var summary = DeserializeData<ProfileSummaryPayload>(poll);
        if (summary is null)
        {
            return response;
        }

        return InjectProfileSummary(response, summary);
    }

    internal static ResponseEnvelope InjectProfileSummary(ResponseEnvelope response, ProfileSummaryPayload summary)
    {
        string dataJson = GetDataJsonString(response);
        if (string.IsNullOrWhiteSpace(dataJson))
        {
            return response;
        }

        System.Text.Json.Nodes.JsonNode? root = System.Text.Json.Nodes.JsonNode.Parse(dataJson);
        if (root is null)
        {
            return response;
        }

        root["profileSummary"] = System.Text.Json.Nodes.JsonNode.Parse(
            JsonSerializer.Serialize(summary, ProtocolJson.Default));

        return ResponseEnvelope.Success(
            response.requestId,
            response.target,
            JsonSerializer.SerializeToElement(root, ProtocolJson.Default),
            response.durationMs,
            response.transport);
    }

    private static string GetDataJsonString(ResponseEnvelope response)
    {
        if (!response.data.HasValue)
        {
            return string.Empty;
        }

        JsonElement data = response.data.Value;
        return data.ValueKind == JsonValueKind.String
            ? data.GetString() ?? string.Empty
            : data.GetRawText();
    }

    private static async Task<ResponseEnvelope> SendRecordStatusPollAsync(
        InstanceRecord target,
        Func<InstanceRecord, CommandEnvelope, int, CancellationToken, Task<ResponseEnvelope>> sendAsync,
        string recordingId,
        CancellationToken cancellationToken)
    {
        var command = new ParsedCommand(CommandKind.RecordStatus)
        {
            RecordRunId = recordingId,
        }.ToEnvelope();

        return await sendAsync(target, command, ProtocolConstants.DefaultLiveTimeoutMs, cancellationToken);
    }

    internal static async Task<ResponseEnvelope> PollEditorReadyAsync(
        ParsedCommand original,
        ResponseEnvelope startedResponse,
        Func<CommandEnvelope, int, CancellationToken, Task<ResponseEnvelope>> sendAsync,
        CancellationToken cancellationToken = default,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null,
        TimeSpan? timeout = null,
        TimeSpan? pollInterval = null)
    {
        delayAsync ??= Task.Delay;
        TimeSpan waitTimeout = timeout ?? TimeSpan.FromSeconds(ProtocolConstants.DefaultCompileRefreshWaitTimeoutSeconds);
        TimeSpan interval = pollInterval ?? TimeSpan.FromSeconds(2);
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        const int maxConsecutiveTransportFailures = 6;
        int consecutiveTransportFailures = 0;
        bool hadPermanentTransportFailure = false;
        // Entering Play Mode reloads the domain: the bridge socket goes away for a few seconds and
        // comes back. Play/stop therefore tolerate transport failures for a time window instead of a
        // fixed count, which would run out in ~1s at their short poll interval.
        bool isPlayModeWait = original.Kind is CommandKind.Play or CommandKind.Stop;
        TimeSpan transportGrace = TimeSpan.FromSeconds(30);
        long? firstTransportFailureMs = null;
        PlayStatePayload? playStarted = original.Kind == CommandKind.Play ? TryDeserializeData<PlayStatePayload>(startedResponse) : null;

        while (stopwatch.Elapsed < waitTimeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TimeSpan remainingBeforeDelay = waitTimeout - stopwatch.Elapsed;
            TimeSpan delay = remainingBeforeDelay < interval ? remainingBeforeDelay : interval;
            if (delay > TimeSpan.Zero)
            {
                await delayAsync(delay, cancellationToken);
            }

            TimeSpan remaining = waitTimeout - stopwatch.Elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                break;
            }

            try
            {
                using var pollCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                pollCts.CancelAfter(remaining);
                int timeoutMs = Math.Max(1, (int)Math.Min(ProtocolConstants.DefaultLiveTimeoutMs, remaining.TotalMilliseconds));
                var poll = await sendAsync(BuildStatusCommand(), timeoutMs, pollCts.Token);

                switch (GetStatusPollResult(poll, original.Kind, playStarted))
                {
                    case StatusPollResult.Ready:
                        return BuildEditorReadyResponse(startedResponse, stopwatch.ElapsedMilliseconds);
                    case StatusPollResult.StillBusy:
                        consecutiveTransportFailures = 0;
                        hadPermanentTransportFailure = false;
                        firstTransportFailureMs = null;
                        break;
                    case StatusPollResult.InvalidStatusPayload:
                        return BuildInvalidStatusPayloadResponse(startedResponse, stopwatch.ElapsedMilliseconds);
                    case StatusPollResult.TransientFailure when isPlayModeWait:
                        firstTransportFailureMs ??= stopwatch.ElapsedMilliseconds;
                        if (stopwatch.ElapsedMilliseconds - firstTransportFailureMs.Value > transportGrace.TotalMilliseconds)
                        {
                            return BuildStatusUnavailableResponse(
                                startedResponse,
                                stopwatch.ElapsedMilliseconds,
                                "The bridge did not come back within " + transportGrace.TotalSeconds + "s after the Play Mode change.");
                        }

                        break;
                    case StatusPollResult.TransientFailure:
                        consecutiveTransportFailures++;
                        if (consecutiveTransportFailures >= maxConsecutiveTransportFailures)
                        {
                            return BuildStatusUnavailableResponse(
                                startedResponse,
                                stopwatch.ElapsedMilliseconds,
                                "Unity status polling did not receive a valid ready/busy response after "
                                    + consecutiveTransportFailures
                                    + " consecutive transport failures.");
                        }

                        break;
                    case StatusPollResult.NonRetryableFailure:
                        return poll;
                }
            }
            catch (OperationCanceledException)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }

                break;
            }
            catch (Exception ex) when (isPlayModeWait)
            {
                firstTransportFailureMs ??= stopwatch.ElapsedMilliseconds;
                if (stopwatch.ElapsedMilliseconds - firstTransportFailureMs.Value > transportGrace.TotalMilliseconds)
                {
                    return BuildStatusUnavailableResponse(startedResponse, stopwatch.ElapsedMilliseconds, ex.Message);
                }
            }
            catch (Exception ex)
            {
                if (IsPermanentTransportException(ex))
                {
                    if (hadPermanentTransportFailure)
                    {
                        return BuildStatusUnavailableResponse(startedResponse, stopwatch.ElapsedMilliseconds, ex.Message);
                    }

                    hadPermanentTransportFailure = true;
                    continue;
                }

                consecutiveTransportFailures++;
                if (consecutiveTransportFailures >= maxConsecutiveTransportFailures)
                {
                    return BuildStatusUnavailableResponse(startedResponse, stopwatch.ElapsedMilliseconds, ex.Message);
                }
            }
        }

        return BuildEditorReadyTimeoutResponse(original, startedResponse, stopwatch.ElapsedMilliseconds, waitTimeout);
    }

    private static CommandEnvelope BuildStatusCommand()
    {
        return new CommandEnvelope
        {
            requestId = Guid.NewGuid().ToString("N"),
            command = ProtocolConstants.CommandStatus,
            argumentsJson = "{}",
        };
    }

    private enum StatusPollResult
    {
        Ready,
        StillBusy,
        InvalidStatusPayload,
        TransientFailure,
        NonRetryableFailure,
    }

    private static T? TryDeserializeData<T>(ResponseEnvelope response)
        where T : class
    {
        try
        {
            return DeserializeData<T>(response);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Readiness after compile/refresh: not compiling or importing. After play: Play Mode has fully
    /// entered (no pending transition) and, when entering reloads the domain, the bridge answering is
    /// the post-reload one (its domain id differs from the one `play` reported). After stop: Play Mode
    /// has fully exited. Older bridges that do not report the transition fields fall back to isPlaying.
    /// </summary>
    internal static bool IsSettledFor(CommandKind kind, StatusPayload status, PlayStatePayload? playStarted)
    {
        if (status.isCompiling || status.isUpdating)
        {
            return false;
        }

        switch (kind)
        {
            case CommandKind.Play:
                if (!status.isPlaying || status.isChangingPlayMode)
                {
                    return false;
                }

                bool domainKnown = playStarted != null
                    && !string.IsNullOrEmpty(playStarted.domainId)
                    && !string.IsNullOrEmpty(status.domainId);
                return !(domainKnown && playStarted!.reloadsDomain
                    && string.Equals(playStarted.domainId, status.domainId, StringComparison.Ordinal));
            case CommandKind.Stop:
                return !status.isPlaying && !status.isChangingPlayMode;
            default:
                return true;
        }
    }

    private static StatusPollResult GetStatusPollResult(ResponseEnvelope response, CommandKind kind, PlayStatePayload? playStarted)
    {
        if (!string.Equals(response.status, ProtocolConstants.StatusSuccess, StringComparison.Ordinal))
        {
            return IsTransientStatusPollFailure(response)
                ? StatusPollResult.TransientFailure
                : StatusPollResult.NonRetryableFailure;
        }

        StatusPayload? status;
        try
        {
            status = DeserializeData<StatusPayload>(response);
        }
        catch (JsonException)
        {
            return StatusPollResult.InvalidStatusPayload;
        }

        if (status is null)
        {
            return StatusPollResult.InvalidStatusPayload;
        }

        return IsSettledFor(kind, status, playStarted)
            ? StatusPollResult.Ready
            : StatusPollResult.StillBusy;
    }

    private static bool IsTransientStatusPollFailure(ResponseEnvelope response)
    {
        string? code = response.error?.code;
        return string.Equals(code, "LIVE_UNAVAILABLE", StringComparison.Ordinal)
            || response.retryable;
    }

    private static bool IsPermanentTransportException(Exception ex)
    {
        return ex is FileNotFoundException
            or DirectoryNotFoundException
            || (ex is System.Net.Sockets.SocketException socketException && socketException.NativeErrorCode == 2);
    }

    private static ResponseEnvelope BuildEditorReadyResponse(ResponseEnvelope startedResponse, long waitedMs)
    {
        JsonObject data = CloneDataObject(startedResponse.data);
        // Internal handshake fields from `play`; not useful to callers.
        data.Remove("domainId");
        data.Remove("reloadsDomain");
        data["ready"] = true;
        data["readyMessage"] = "ready";
        data["waitedMs"] = waitedMs;

        return ResponseEnvelope.Success(
            startedResponse.requestId,
            startedResponse.target,
            CreateDataElement(data),
            startedResponse.durationMs + waitedMs,
            startedResponse.transport);
    }

    private static JsonObject CloneDataObject(JsonElement? data)
    {
        if (!data.HasValue)
        {
            return new JsonObject();
        }

        JsonElement element = data.Value;
        if (element.ValueKind == JsonValueKind.String)
        {
            string? json = element.GetString();
            if (string.IsNullOrWhiteSpace(json))
            {
                return new JsonObject();
            }

            var node = JsonNode.Parse(json);
            return node as JsonObject ?? new JsonObject();
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            return new JsonObject();
        }

        var clone = JsonNode.Parse(element.GetRawText());
        return clone as JsonObject ?? new JsonObject();
    }

    private static ResponseEnvelope BuildEditorReadyTimeoutResponse(
        ParsedCommand original,
        ResponseEnvelope startedResponse,
        long waitedMs,
        TimeSpan waitTimeout)
    {
        string code = original.Kind switch
        {
            CommandKind.Compile => ProtocolConstants.ErrorCompileWaitTimeout,
            CommandKind.Play => ProtocolConstants.ErrorPlayWaitTimeout,
            CommandKind.Stop => ProtocolConstants.ErrorStopWaitTimeout,
            _ => ProtocolConstants.ErrorRefreshWaitTimeout,
        };
        string command = original.Kind switch
        {
            CommandKind.Compile => "compile",
            CommandKind.Play => "play",
            CommandKind.Stop => "stop",
            _ => "refresh",
        };
        string timeoutSeconds = FormatSeconds(waitTimeout);

        var details = new
        {
            command,
            waitedMs,
            timeoutSeconds = waitTimeout.TotalSeconds,
        };

        return ResponseEnvelope.Failure(
            startedResponse.requestId,
            startedResponse.target,
            code,
            command + " timed out after "
                + timeoutSeconds
                + (original.Kind is CommandKind.Play or CommandKind.Stop
                    ? " seconds waiting for the Play Mode change to finish and the bridge to become reachable. Use --no-wait to return immediately."
                    : " seconds waiting for the Editor to finish compiling/importing and become reachable. Use --no-wait to return immediately."),
            retryable: true,
            durationMs: waitedMs,
            transport: "cli",
            details: ProtocolJson.Serialize(details));
    }

    private static ResponseEnvelope BuildInvalidStatusPayloadResponse(ResponseEnvelope startedResponse, long waitedMs)
    {
        return BuildStatusUnavailableResponse(
            startedResponse,
            waitedMs,
            "Unity status returned success but did not include a valid status payload.");
    }

    private static ResponseEnvelope BuildStatusUnavailableResponse(
        ResponseEnvelope startedResponse,
        long waitedMs,
        string details)
    {
        return ResponseEnvelope.Failure(
            startedResponse.requestId,
            startedResponse.target,
            "LIVE_UNAVAILABLE",
            "The Unity Editor is not running, or the bridge is not active.",
            retryable: true,
            durationMs: waitedMs,
            transport: "cli",
            details: details);
    }

    private static string FormatSeconds(TimeSpan value)
    {
        double totalSeconds = value.TotalSeconds;
        return Math.Abs(totalSeconds - Math.Round(totalSeconds)) < 0.001
            ? ((int)Math.Round(totalSeconds)).ToString()
            : totalSeconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
    }

    internal static T? DeserializeData<T>(ResponseEnvelope response)
    {
        if (!response.data.HasValue)
        {
            return default;
        }

        JsonElement data = response.data.Value;
        if (data.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return default;
        }

        if (data.ValueKind == JsonValueKind.String)
        {
            string? json = data.GetString();
            return string.IsNullOrWhiteSpace(json)
                ? default
                : ProtocolJson.Deserialize<T>(json);
        }

        return JsonSerializer.Deserialize<T>(data.GetRawText(), ProtocolJson.Default);
    }

    private static ResponseEnvelope NormalizeResultEnvelope(CommandKind kind, ResponseEnvelope response)
    {
        response = NormalizeTestResultEnvelope(kind, response);
        return NormalizeRecordResultEnvelope(kind, response);
    }

    internal static ResponseEnvelope NormalizeTestResultEnvelope(CommandKind kind, ResponseEnvelope response)
    {
        if (kind != CommandKind.TestRun)
        {
            return response;
        }

        if (!string.Equals(response.status, ProtocolConstants.StatusSuccess, StringComparison.Ordinal))
        {
            return response;
        }

        var result = DeserializeData<TestRunResultPayload>(response);
        if (result is null || !ProtocolHelpers.IsTestRunResultStatusError(result.status))
        {
            return response;
        }

        return BuildTestResultFailureEnvelope(
            response,
            result,
            ProtocolHelpers.GetTestRunResultErrorCode(result.status, result.warnings),
            ProtocolHelpers.BuildTestRunResultErrorMessage(result));
    }

    internal static ResponseEnvelope NormalizeRecordResultEnvelope(CommandKind kind, ResponseEnvelope response)
    {
        if (kind is not (CommandKind.RecordStatus or CommandKind.RecordStop))
        {
            return response;
        }

        if (!string.Equals(response.status, ProtocolConstants.StatusSuccess, StringComparison.Ordinal))
        {
            return response;
        }

        var result = DeserializeData<RecordResultPayload>(response);
        if (result is null
            || string.Equals(result.status, "Recording", StringComparison.Ordinal)
            || string.Equals(result.status, "Completed", StringComparison.Ordinal))
        {
            return response;
        }

        return BuildRecordResultFailureEnvelope(
            response,
            result,
            GetRecordResultErrorCode(result.status),
            BuildRecordResultErrorMessage(result));
    }

    private static ResponseEnvelope BuildTestResultFailureEnvelope(
        ResponseEnvelope source,
        TestRunResultPayload result,
        string errorCode,
        string message)
    {
        return ResponseEnvelope.Failure(
            source.requestId,
            source.target,
            errorCode,
            message,
            retryable: false,
            source.durationMs,
            source.transport,
            GetDataDetailsJson(source, result));
    }

    private static string GetDataDetailsJson(ResponseEnvelope source, TestRunResultPayload fallback)
    {
        if (!source.data.HasValue)
        {
            return ProtocolJson.Serialize(fallback);
        }

        JsonElement data = source.data.Value;
        if (data.ValueKind == JsonValueKind.String)
        {
            string? json = data.GetString();
            return string.IsNullOrWhiteSpace(json)
                ? ProtocolJson.Serialize(fallback)
                : json;
        }

        return data.GetRawText();
    }

    private static ResponseEnvelope BuildRecordResultFailureEnvelope(
        ResponseEnvelope source,
        RecordResultPayload result,
        string errorCode,
        string message)
    {
        return ResponseEnvelope.Failure(
            source.requestId,
            source.target,
            errorCode,
            message,
            retryable: false,
            source.durationMs,
            source.transport,
            GetRecordDataDetailsJson(source, result));
    }

    private static string GetRecordResultErrorCode(string? status)
    {
        return status switch
        {
            "Interrupted" => ProtocolConstants.ErrorRecordInterrupted,
            "NotFound" => ProtocolConstants.ErrorRecordNotFound,
            "Failed" => ProtocolConstants.ErrorRecordFailed,
            _ => ProtocolConstants.ErrorRecordFailed,
        };
    }

    private static string BuildRecordResultErrorMessage(RecordResultPayload result)
    {
        string id = string.IsNullOrWhiteSpace(result.recordingId) ? "recording" : "Recording " + result.recordingId;
        return result.status switch
        {
            "Interrupted" => id + " was interrupted before it completed.",
            "NotFound" => id + " was not found.",
            "Failed" => id + " failed to finalize.",
            _ => id + " ended with status " + result.status + ".",
        };
    }

    private static string GetRecordDataDetailsJson(ResponseEnvelope source, RecordResultPayload fallback)
    {
        if (!source.data.HasValue)
        {
            return ProtocolJson.Serialize(fallback);
        }

        JsonElement data = source.data.Value;
        if (data.ValueKind == JsonValueKind.String)
        {
            string? json = data.GetString();
            return string.IsNullOrWhiteSpace(json)
                ? ProtocolJson.Serialize(fallback)
                : json;
        }

        return data.GetRawText();
    }

    private static async Task<ResponseEnvelope> RunQaWait(ParsedCommand parsed)
    {
        if (parsed.QaWaitMs <= 0)
        {
            throw new CliUsageException("qa wait requires --ms <milliseconds>.");
        }

        await Task.Delay(parsed.QaWaitMs);
        var payload = new { waited = true, ms = parsed.QaWaitMs };
        return ResponseEnvelope.Success(
            Guid.NewGuid().ToString("N"),
            null,
            CreateDataElement(payload),
            parsed.QaWaitMs,
            "cli");
    }

    private static JsonElement CreateDataElement(object data)
    {
        return JsonSerializer.SerializeToElement(data, ProtocolJson.Default);
    }

    private static ResponseEnvelope CreateLiveUnavailableResponse(string? projectHash, string? details)
    {
        return ResponseEnvelope.Failure(
            Guid.NewGuid().ToString("N"),
            projectHash,
            "LIVE_UNAVAILABLE",
            "The Unity Editor is not running, or the bridge is not active.",
            retryable: true,
            transport: "cli",
            details: details);
    }

    internal static InstanceRecord? ResolveTarget(InstanceRegistry registry, string? projectRoot)
    {
        if (!string.IsNullOrWhiteSpace(projectRoot))
        {
            var canonicalProjectRoot = ProtocolConstants.GetCanonicalPath(projectRoot);
            var projectHash = ProtocolConstants.ComputeProjectHash(canonicalProjectRoot);
            var match = registry.instances.FirstOrDefault(item =>
                string.Equals(item.projectRoot, canonicalProjectRoot, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                return match;
            }

            return new InstanceRecord
            {
                projectRoot = canonicalProjectRoot,
                projectName = Path.GetFileName(canonicalProjectRoot),
                projectHash = projectHash,
                pipeName = ProtocolConstants.BuildPipeName(projectHash),
                state = "offline",
                lastSeenUtc = DateTimeOffset.UtcNow.ToString("O"),
                capabilities = Array.Empty<string>(),
            };
        }

        var liveInstances = registry.instances
            .Where(item => !string.Equals(item.state, "offline", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (registry.activeProjectRootPinned
            && !string.IsNullOrWhiteSpace(registry.activeProjectRoot))
        {
            var pinned = liveInstances.FirstOrDefault(item =>
                string.Equals(item.projectRoot, registry.activeProjectRoot, StringComparison.OrdinalIgnoreCase));
            if (pinned is not null)
            {
                return pinned;
            }
        }

        if (liveInstances.Length == 1)
        {
            return liveInstances[0];
        }

        if (liveInstances.Length >= 2)
        {
            throw CreateAmbiguousTargetException(liveInstances);
        }

        return null;
    }

    /// <summary>
    /// Ordered failover candidates for read-only diagnostic commands (`status`/`doctor`) only.
    /// The first element always matches what <see cref="ResolveTarget"/> would return, so mutating
    /// command dispatch (<see cref="ExecuteUnityCommandAsync"/>), which calls
    /// <see cref="ResolveTarget"/> directly, is completely unaffected by this method's existence.
    /// With an explicit/detected <paramref name="projectRoot"/>, targeting is explicit and no
    /// fallback candidates are offered — only the resolved (possibly synthetic/offline) target.
    /// With no project root, remaining live instances (excluding the primary) are appended as
    /// fallbacks, ordered the same way ambiguous-target candidate listings are elsewhere.
    /// </summary>
    internal static InstanceRecord[] ResolveTargetCandidates(InstanceRegistry registry, string? projectRoot)
    {
        var primary = ResolveTarget(registry, projectRoot);
        if (primary is null)
        {
            return Array.Empty<InstanceRecord>();
        }

        if (!string.IsNullOrWhiteSpace(projectRoot))
        {
            return new[] { primary };
        }

        var fallbacks = registry.instances
            .Where(item => !string.Equals(item.state, "offline", StringComparison.OrdinalIgnoreCase))
            .Where(item => !string.Equals(item.projectRoot, primary.projectRoot, StringComparison.OrdinalIgnoreCase))
            .OrderBy(item => item.projectName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.projectRoot, StringComparer.OrdinalIgnoreCase);

        return new[] { primary }.Concat(fallbacks).ToArray();
    }

    private static CliUsageException CreateAmbiguousTargetException(InstanceRecord[] candidates)
    {
        var lines = candidates
            .OrderBy(item => item.projectName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.projectRoot, StringComparer.OrdinalIgnoreCase)
            .Select(item => $"  - {item.projectName} ({item.state})  {item.projectRoot}");
        return new CliUsageException(
            "More than one Unity instance is running, so the target cannot be resolved. "
            + "Pass --project or pin a default with `unity-cli instances use <projectPath|projectName>`.\nCandidates:\n"
            + string.Join("\n", lines));
    }

    private static void WriteErrorResponse(OutputMode outputMode, ResponseEnvelope response)
    {
        if (outputMode != OutputMode.Default)
        {
            Console.Out.WriteLine(ResponseFormatter.Format(outputMode, response));
            return;
        }

        if (response.error is not null)
        {
            Console.Error.WriteLine(response.error.message);

            if (response.error.details is JsonElement details && details.ValueKind != JsonValueKind.Null)
            {
                Console.Error.WriteLine();
                Console.Error.WriteLine(details.ValueKind == JsonValueKind.String
                    ? details.GetString()
                    : JsonSerializer.Serialize(details, ProtocolJson.Default));
            }

            return;
        }

        Console.Error.WriteLine(ResponseFormatter.Format(OutputMode.Default, response));
    }

    private static void WriteUsageError(OutputMode outputMode, ResponseEnvelope response, CliUsagePresentation presentation)
    {
        if (outputMode != OutputMode.Default)
        {
            Console.Out.WriteLine(ResponseFormatter.Format(outputMode, response));
            return;
        }

        CliUsageHelp.WriteTo(Console.Error, presentation);
    }
}
