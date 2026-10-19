using System.Diagnostics;
using System.Text.Json;
using UnityCli.Cli.Models;
using UnityCli.Protocol;

namespace UnityCli.Cli.Services;

/// <summary>
/// Local `editor launch` flow: pre-flight double-launch detection, editor spawn,
/// and bridge-readiness polling. Never talks IPC itself — readiness is observed
/// through the shared instance registry, which the bridge writes only after the
/// listener and token sidecar are ready.
/// </summary>
public static class EditorLauncher
{
    private const int DefaultLaunchWaitTimeoutSeconds = 300;
    private const int PollIntervalMilliseconds = 2000;

    public static async Task<ResponseEnvelope> LaunchAsync(
        ParsedCommand parsed,
        InstanceRegistryStore registryStore,
        string? projectRoot)
    {
        var stopwatch = Stopwatch.StartNew();
        string requestId = Guid.NewGuid().ToString("N");

        if (string.IsNullOrWhiteSpace(projectRoot))
        {
            throw new CliUsageException("editor launch requires a target project. Pass --project <path>.");
        }

        string canonicalRoot = ProtocolConstants.GetCanonicalPath(projectRoot);
        if (!File.Exists(Path.Combine(canonicalRoot, "ProjectSettings", "ProjectVersion.txt")))
        {
            return Failure(
                requestId,
                ProtocolConstants.ErrorEditorLaunchFailed,
                "Not a Unity project (ProjectSettings/ProjectVersion.txt is missing): " + canonicalRoot,
                retryable: false,
                stopwatch);
        }

        // Pre-flight ①: live registry match → idempotent reuse.
        InstanceRecord? live = FindLiveInstance(registryStore, canonicalRoot);
        if (live != null)
        {
            string requestedMode = RequestedModeLabel(parsed);
            string note = string.IsNullOrEmpty(live.editorMode) || string.Equals(live.editorMode, requestedMode, StringComparison.Ordinal)
                ? string.Empty
                : $"Requested mode ({requestedMode}) does not match the running editor mode ({live.editorMode}). Run editor stop, then launch again, to switch modes.";

            return Success(requestId, new
            {
                launched = false,
                reused = true,
                pid = live.editorProcessId,
                mode = string.IsNullOrEmpty(live.editorMode) ? "unknown" : live.editorMode,
                unityVersion = live.unityVersion,
                projectRoot = canonicalRoot,
                waitedMs = 0L,
                note,
            }, stopwatch);
        }

        // Pre-flight ②: a main-editor process on this project that never registered
        // (bridge package missing, or still booting). Launching again would hit Unity's
        // own lock — batch fails fast, GUI blocks on a modal. Refuse with context.
        int? strayPid = FindStrayEditorProcessId(canonicalRoot);
        if (strayPid.HasValue)
        {
            return Failure(
                requestId,
                ProtocolConstants.ErrorEditorAlreadyRunning,
                $"An editor process (PID {strayPid.Value}) already has this project open, but it is not registered as a bridge instance. "
                + "If it is still booting or compiling, try again shortly. If the package is not installed in that project, use that editor directly.",
                retryable: true,
                stopwatch);
        }

        string? editorBinary = !string.IsNullOrWhiteSpace(parsed.EditorPathOverride)
            ? (File.Exists(parsed.EditorPathOverride) ? parsed.EditorPathOverride : null)
            : UnityEditorLocator.TryResolve(canonicalRoot);
        if (editorBinary == null)
        {
            return Failure(
                requestId,
                ProtocolConstants.ErrorEditorLaunchFailed,
                "Could not find a Unity editor binary for the project version. Install that version with Unity Hub, or pass --editor-path.",
                retryable: false,
                stopwatch);
        }

        string logDirectory = Path.Combine(canonicalRoot, "Library", "com.yhc509.unity-cli-bridge");
        Directory.CreateDirectory(logDirectory);
        string logFile = Path.Combine(logDirectory, "editor-launch.log");

        ProcessStartInfo startInfo = BuildStartInfo(editorBinary, BuildLaunchArguments(parsed, canonicalRoot, logFile));

        Process editorProcess;
        try
        {
            editorProcess = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Process.Start returned null.");
        }
        catch (Exception exception)
        {
            return Failure(
                requestId,
                ProtocolConstants.ErrorEditorLaunchFailed,
                "Failed to start the editor: " + exception.Message,
                retryable: false,
                stopwatch);
        }

        if (parsed.EditorNoWait)
        {
            return Success(requestId, new
            {
                launched = true,
                reused = false,
                pid = editorProcess.Id,
                mode = RequestedModeLabel(parsed),
                unityVersion = string.Empty,
                projectRoot = canonicalRoot,
                waitedMs = 0L,
                note = "--no-wait: did not wait for the bridge to become ready. Check editor-launch.log and instances list.",
            }, stopwatch);
        }

        int timeoutSeconds = parsed.EditorWaitTimeoutSeconds ?? DefaultLaunchWaitTimeoutSeconds;
        var deadline = DateTimeOffset.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (editorProcess.HasExited)
            {
                return Failure(
                    requestId,
                    ProtocolConstants.ErrorEditorLaunchFailed,
                    $"The editor process exited before it was ready (exit {editorProcess.ExitCode}). Log: {logFile}",
                    retryable: false,
                    stopwatch);
            }

            InstanceRecord? ready = FindLiveInstance(registryStore, canonicalRoot);
            if (ready != null)
            {
                return Success(requestId, new
                {
                    launched = true,
                    reused = false,
                    pid = ready.editorProcessId,
                    mode = string.IsNullOrEmpty(ready.editorMode) ? RequestedModeLabel(parsed) : ready.editorMode,
                    unityVersion = ready.unityVersion,
                    projectRoot = canonicalRoot,
                    waitedMs = stopwatch.ElapsedMilliseconds,
                    note = string.Empty,
                }, stopwatch);
            }

            await Task.Delay(PollIntervalMilliseconds);
        }

        return Failure(
            requestId,
            ProtocolConstants.ErrorEditorWaitTimeout,
            $"Editor (PID {editorProcess.Id}) is running, but the bridge was not ready within {timeoutSeconds} seconds. "
            + $"Increase --timeout when the first import is long. Log: {logFile}",
            retryable: true,
            stopwatch);
    }

    /// <summary>
    /// Spawns the editor with detached stdio. Inheriting the CLI's stdout/stderr
    /// leaves the editor holding the pipe write-end, so `unity-cli editor launch | grep …`
    /// never sees EOF and hangs after the CLI exits (measured).
    /// On Unix, wrap with `sh -c 'exec …'` and redirect to /dev/null — exec replaces
    /// the shell, so Process.Id stays the editor PID. -logFile owns editor output.
    /// </summary>
    public static ProcessStartInfo BuildStartInfo(string editorBinary, string[] arguments)
    {
        if (OperatingSystem.IsWindows())
        {
            var windowsStartInfo = new ProcessStartInfo
            {
                FileName = editorBinary,
                UseShellExecute = false,
            };
            foreach (string argument in arguments)
            {
                windowsStartInfo.ArgumentList.Add(argument);
            }

            return windowsStartInfo;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = "/bin/sh",
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("exec \"$0\" \"$@\" </dev/null >/dev/null 2>&1");
        startInfo.ArgumentList.Add(editorBinary);
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }

    public static string[] BuildLaunchArguments(ParsedCommand parsed, string projectRoot, string logFile)
    {
        var arguments = new List<string> { "-projectPath", projectRoot, "-logFile", logFile };
        if (!parsed.EditorGui)
        {
            arguments.Add("-batchmode");
        }

        if (parsed.EditorNoGraphics)
        {
            if (!arguments.Contains("-batchmode"))
            {
                arguments.Add("-batchmode");
            }

            arguments.Add("-nographics");
        }

        return arguments.ToArray();
    }

    public static string RequestedModeLabel(ParsedCommand parsed)
    {
        if (parsed.EditorNoGraphics)
        {
            return "headless-nographics";
        }

        return parsed.EditorGui ? "gui" : "headless";
    }

    /// <summary>
    /// Decides whether one ps line is the main editor process for this project.
    /// Filters two measured false positives: -adb2 AssetImportWorker shares the
    /// same -projectPath, and Unity Hub Helper lists recent project paths in argv.
    /// </summary>
    public static bool IsMainEditorProcessLine(string psLine, string projectRoot)
    {
        if (psLine.Contains("-adb2", StringComparison.OrdinalIgnoreCase)
            || psLine.Contains("Unity Hub", StringComparison.Ordinal))
        {
            return false;
        }

        int flagIndex = psLine.IndexOf("-projectpath", StringComparison.OrdinalIgnoreCase);
        if (flagIndex < 0)
        {
            return false;
        }

        string afterFlag = psLine[(flagIndex + "-projectpath".Length)..].TrimStart();
        return afterFlag.StartsWith(projectRoot, StringComparison.Ordinal)
            && (afterFlag.Length == projectRoot.Length
                || afterFlag[projectRoot.Length] == ' ');
    }

    private static InstanceRecord? FindLiveInstance(InstanceRegistryStore registryStore, string canonicalRoot)
    {
        InstanceRegistry registry = registryStore.Load();
        foreach (InstanceRecord record in registry.instances ?? Array.Empty<InstanceRecord>())
        {
            if (!string.Equals(record.projectRoot, canonicalRoot, StringComparison.Ordinal))
            {
                continue;
            }

            if (record.editorProcessId <= 0 || !IsProcessAlive(record.editorProcessId))
            {
                continue;
            }

            return record;
        }

        return null;
    }

    private static int? FindStrayEditorProcessId(string canonicalRoot)
    {
        if (OperatingSystem.IsWindows())
        {
            // Command-line inspection needs Win32 APIs; registry + Unity's own lock
            // cover the double-launch case there. (Live-verified surface is macOS.)
            return null;
        }

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "/bin/ps",
                UseShellExecute = false,
                RedirectStandardOutput = true,
            };
            startInfo.ArgumentList.Add("-axo");
            startInfo.ArgumentList.Add("pid=,command=");

            using Process? ps = Process.Start(startInfo);
            if (ps == null)
            {
                return null;
            }

            string output = ps.StandardOutput.ReadToEnd();
            ps.WaitForExit(5000);
            foreach (string line in output.Split('\n'))
            {
                string trimmed = line.Trim();
                if (trimmed.Length == 0 || !IsMainEditorProcessLine(trimmed, canonicalRoot))
                {
                    continue;
                }

                int spaceIndex = trimmed.IndexOf(' ');
                if (spaceIndex > 0 && int.TryParse(trimmed[..spaceIndex], out int pid))
                {
                    return pid;
                }
            }
        }
        catch (Exception)
        {
            // Pre-flight scan is best-effort; Unity's own project lock is the backstop.
        }

        return null;
    }

    internal static bool IsProcessAlive(int pid)
    {
        try
        {
            Process process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static ResponseEnvelope Success(string requestId, object payload, Stopwatch stopwatch)
    {
        stopwatch.Stop();
        return ResponseEnvelope.Success(
            requestId,
            null,
            JsonSerializer.SerializeToElement(payload, ProtocolJson.Default),
            stopwatch.ElapsedMilliseconds,
            "cli");
    }

    private static ResponseEnvelope Failure(
        string requestId,
        string errorCode,
        string message,
        bool retryable,
        Stopwatch stopwatch)
    {
        stopwatch.Stop();
        return ResponseEnvelope.Failure(
            requestId,
            null,
            errorCode,
            message,
            retryable: retryable,
            durationMs: stopwatch.ElapsedMilliseconds,
            transport: "cli");
    }
}
