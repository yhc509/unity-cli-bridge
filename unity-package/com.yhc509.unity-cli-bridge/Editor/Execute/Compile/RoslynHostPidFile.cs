#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using UnityCli.Protocol;

namespace UnityCliBridge.Bridge.Editor
{
    /// <summary>
    /// <c>Library/com.yhc509.unity-cli-bridge/roslyn-host/host.pid</c>: which host process this
    /// project's editor started, so the next domain load can kill one that a reload or crash left
    /// behind. Written right after the host starts and deleted when it is stopped.
    /// </summary>
    internal sealed class RoslynHostPidRecord
    {
        public int Pid { get; set; }

        public int EditorPid { get; set; }

        public long StartTimeUnixMs { get; set; }

        /// <summary><c>--ucb-roslyn-host &lt;projectHash&gt;</c>, as it appears on the host's command line.</summary>
        public string Marker { get; set; } = string.Empty;

        public static string FormatMarker(string projectHash)
        {
            return RoslynHostArguments.MarkerOption + " " + projectHash;
        }

        /// <summary>The project hash inside <see cref="Marker"/>; empty when the marker is malformed.</summary>
        public string MarkerProjectHash
        {
            get
            {
                string prefix = RoslynHostArguments.MarkerOption + " ";
                if (Marker == null || !Marker.StartsWith(prefix, StringComparison.Ordinal))
                {
                    return string.Empty;
                }

                string hash = Marker.Substring(prefix.Length).Trim();
                return hash.IndexOfAny(new[] { ' ', '\t', '"' }) >= 0 ? string.Empty : hash;
            }
        }

        public string ToJson()
        {
            var obj = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["pid"] = Pid,
                ["editorPid"] = EditorPid,
                ["startTime"] = StartTimeUnixMs,
                ["marker"] = Marker,
            };
            return RoslynHostJson.Serialize(obj) + "\n";
        }

        public static RoslynHostPidRecord? Parse(string json)
        {
            try
            {
                if (!(RoslynHostJson.Parse(json) is Dictionary<string, object?> h))
                {
                    return null;
                }

                return new RoslynHostPidRecord
                {
                    Pid = (int)RoslynHostJson.GetInt64(h, "pid", 0, 0, int.MaxValue),
                    EditorPid = (int)RoslynHostJson.GetInt64(h, "editorPid", 0, 0, int.MaxValue),
                    StartTimeUnixMs = RoslynHostJson.GetInt64(h, "startTime", 0, 0, long.MaxValue),
                    Marker = RoslynHostJson.GetString(h, "marker") ?? string.Empty,
                };
            }
            catch (FormatException)
            {
                return null;
            }
        }

        public static RoslynHostPidRecord? TryRead(string path)
        {
            try
            {
                return File.Exists(path) ? Parse(File.ReadAllText(path, Encoding.UTF8)) : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        public void Write(string path)
        {
            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(path, ToJson(), new UTF8Encoding(false));
        }

        public static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception)
            {
                // Best effort; a stale pidfile is re-checked (and never trusted blindly) on the next load.
            }
        }
    }

    internal enum OrphanVerdict
    {
        /// <summary>Delete the pidfile; nothing to kill (process gone or never written).</summary>
        Gone,

        /// <summary>Kill the process, then delete the pidfile.</summary>
        Kill,

        /// <summary>Leave the process alone (not ours, or still owned by another live editor).</summary>
        Spare,
    }

    /// <summary>
    /// The orphan rule (plan §2.5): kill only when the recorded pid is alive, its command line carries
    /// <c>--ucb-roslyn-host &lt;projectHash&gt;</c> for this project, and the recorded editor is this
    /// editor or no longer running. A process without the marker is never killed, whatever the pidfile says.
    /// </summary>
    internal static class RoslynHostOrphanPolicy
    {
        public static OrphanVerdict Evaluate(
            RoslynHostPidRecord? record,
            string projectHash,
            int currentEditorPid,
            Func<int, bool> isAlive,
            Func<int, string?> readCommandLine,
            out string reason)
        {
            if (record == null || record.Pid <= 0)
            {
                reason = "no usable pidfile";
                return OrphanVerdict.Gone;
            }

            if (!isAlive(record.Pid))
            {
                reason = "pid " + record.Pid + " is not running";
                return OrphanVerdict.Gone;
            }

            if (record.Pid == currentEditorPid)
            {
                reason = "pid " + record.Pid + " is this editor";
                return OrphanVerdict.Spare;
            }

            if (string.IsNullOrEmpty(projectHash) || !string.Equals(record.MarkerProjectHash, projectHash, StringComparison.Ordinal))
            {
                reason = "pidfile marker is not this project's";
                return OrphanVerdict.Spare;
            }

            string? commandLine = readCommandLine(record.Pid);
            if (!RoslynHostArguments.CommandLineHasMarker(commandLine ?? string.Empty, projectHash))
            {
                // Pid reuse, or the command line could not be read: not provably ours.
                reason = "pid " + record.Pid + " does not carry " + RoslynHostPidRecord.FormatMarker(projectHash);
                return OrphanVerdict.Spare;
            }

            if (record.EditorPid != currentEditorPid && record.EditorPid > 0 && isAlive(record.EditorPid))
            {
                reason = "editor pid " + record.EditorPid + " that started it is still running";
                return OrphanVerdict.Spare;
            }

            reason = "pid " + record.Pid + " is a leftover host of this project";
            return OrphanVerdict.Kill;
        }
    }

    /// <summary>Process probes for orphan cleanup and doctor. Never throws.</summary>
    internal static class RoslynHostProcessInspector
    {
        /// <summary>
        /// True while <paramref name="pid"/> exists. Windows asks the process table directly; macOS and
        /// Linux ask <c>ps</c>, because Mono's <see cref="Process.HasExited"/> is unreliable for
        /// processes this domain did not start.
        /// </summary>
        public static bool IsAlive(int pid)
        {
            if (pid <= 0)
            {
                return false;
            }

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                try
                {
                    using (Process process = Process.GetProcessById(pid))
                    {
                        return !process.HasExited;
                    }
                }
                catch (Exception)
                {
                    return false;
                }
            }

            string? output = Capture("/bin/ps", "-o pid= -p " + pid.ToString(CultureInfo.InvariantCulture));
            return output != null && output.Trim() == pid.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Full command line of <paramref name="pid"/>: <c>ps</c> on macOS/Linux, CIM through PowerShell
        /// on Windows (Mono has no API for another process's arguments). Null when it cannot be read.
        /// </summary>
        public static string? ReadCommandLine(int pid)
        {
            if (pid <= 0)
            {
                return null;
            }

            string? output = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? Capture(
                    "powershell.exe",
                    "-NoProfile -NonInteractive -Command \"(Get-CimInstance Win32_Process -Filter 'ProcessId="
                    + pid.ToString(CultureInfo.InvariantCulture) + "').CommandLine\"")
                : Capture("/bin/ps", "-ww -o command= -p " + pid.ToString(CultureInfo.InvariantCulture));
            if (output == null)
            {
                return null;
            }

            string trimmed = output.Trim();
            return trimmed.Length == 0 ? null : trimmed;
        }

        private static string? Capture(string fileName, string arguments)
        {
            try
            {
                var startInfo = new ProcessStartInfo(fileName, arguments)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                using (Process process = Process.Start(startInfo)!)
                {
                    process.ErrorDataReceived += (_, __) => { };
                    process.BeginErrorReadLine();
                    string output = process.StandardOutput.ReadToEnd();
                    if (!process.WaitForExit(10000))
                    {
                        try
                        {
                            process.Kill();
                        }
                        catch (Exception)
                        {
                        }

                        return null;
                    }

                    return output;
                }
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>Kills <paramref name="pid"/> (SIGKILL on macOS/Linux). Only called after the orphan rule said so.</summary>
        public static void TryKill(int pid)
        {
            if (pid <= 0)
            {
                return;
            }

            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                Capture("/bin/kill", "-KILL " + pid.ToString(CultureInfo.InvariantCulture));
                return;
            }

            try
            {
                using (Process process = Process.GetProcessById(pid))
                {
                    process.Kill();
                    process.WaitForExit(2000);
                }
            }
            catch (Exception)
            {
                // Already gone or not ours to touch.
            }
        }
    }
}
