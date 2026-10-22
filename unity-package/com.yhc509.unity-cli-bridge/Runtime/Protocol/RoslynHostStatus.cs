#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace UnityCli.Protocol
{
    /// <summary>
    /// Snapshot of the editor's Roslyn compile host, written by the bridge to
    /// <c>Library/com.yhc509.unity-cli-bridge/roslyn-host/status.json</c> whenever the host changes
    /// state, and read by <c>doctor</c> locally (no IPC round trip, no wire change). Only an editor
    /// that opted into the host (<c>UCB_EXECUTE_COMPILER=roslyn-host</c>) writes it, so a missing
    /// file means the default CodeDOM compiler. Unity-free and System.Text.Json-free so both sides
    /// share it.
    /// </summary>
    public sealed class RoslynHostStatus
    {
        public const string FileName = "status.json";
        public const string PidFileName = "host.pid";
        public const int CurrentSchema = 1;

        /// <summary><c>Library/com.yhc509.unity-cli-bridge/roslyn-host</c> under <paramref name="projectRoot"/>.</summary>
        public static string GetHostDirectory(string projectRoot)
        {
            return Path.Combine(Path.Combine(Path.Combine(projectRoot, "Library"), "com.yhc509.unity-cli-bridge"), "roslyn-host");
        }

        public static string GetStatusPath(string projectRoot)
        {
            return Path.Combine(GetHostDirectory(projectRoot), FileName);
        }

        public int Schema { get; set; } = CurrentSchema;

        public string UpdatedUtc { get; set; } = string.Empty;

        public int EditorPid { get; set; }

        /// <summary>The raw <c>UCB_EXECUTE_COMPILER</c> value (<c>auto</c> when unset).</summary>
        public string ConfiguredCompiler { get; set; } = string.Empty;

        /// <summary>Backend the next cache-miss compile starts with: <c>roslyn-host</c> or <c>codedom</c>.</summary>
        public string ActiveBackend { get; set; } = string.Empty;

        public string PrewarmPolicy { get; set; } = string.Empty;

        public int HostWaitMs { get; set; }

        public int CompileTimeoutMs { get; set; }

        public int IdleExitMinutes { get; set; }

        /// <summary><c>idle</c>, <c>starting</c>, <c>ready</c>, <c>stopped</c>, <c>failed</c>, or <c>disabled</c>.</summary>
        public string State { get; set; } = string.Empty;

        /// <summary>Why the host is stopped, when it is (<c>domain-reload</c>, <c>editor-quit</c>, <c>idle-exit</c>).</summary>
        public string Note { get; set; } = string.Empty;

        public int Pid { get; set; }

        public string HostStartedUtc { get; set; } = string.Empty;

        /// <summary>Process start to hello (includes the framework prewarm when it ran).</summary>
        public double StartMs { get; set; }

        /// <summary>Framework prewarm (before hello) plus reference prewarm (after the first setRefs).</summary>
        public double PrewarmMs { get; set; }

        public string RuntimeDirectory { get; set; } = string.Empty;

        public string RuntimeVersion { get; set; } = string.Empty;

        public string RoslynVersion { get; set; } = string.Empty;

        public string BuildKey { get; set; } = string.Empty;

        public bool BuildCacheHit { get; set; }

        public double BuildMs { get; set; }

        public string LastError { get; set; } = string.Empty;

        public string LastErrorUtc { get; set; } = string.Empty;

        public string SessionDisabledReason { get; set; } = string.Empty;

        /// <summary>Host crashes (exit, frame error, timeout) inside the current 60 s window.</summary>
        public int RecentCrashes { get; set; }

        public int Starts { get; set; }

        public string LastCompiler { get; set; } = string.Empty;

        public string LastFallbackReason { get; set; } = string.Empty;

        /// <summary>Assets/csc.rsp or mcs.rsp options the host does not apply (only -define, -unsafe, -nowarn are).</summary>
        public List<string> IgnoredProjectOptions { get; set; } = new List<string>();

        /// <summary>Last lines (at most 64) the host wrote to stderr.</summary>
        public List<string> StderrTail { get; set; } = new List<string>();

        public string ToJson()
        {
            var obj = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["schema"] = Schema,
                ["updatedUtc"] = UpdatedUtc,
                ["editorPid"] = EditorPid,
                ["configuredCompiler"] = ConfiguredCompiler,
                ["activeBackend"] = ActiveBackend,
                ["prewarmPolicy"] = PrewarmPolicy,
                ["hostWaitMs"] = HostWaitMs,
                ["compileTimeoutMs"] = CompileTimeoutMs,
                ["idleExitMinutes"] = IdleExitMinutes,
                ["state"] = State,
                ["note"] = Note,
                ["pid"] = Pid,
                ["hostStartedUtc"] = HostStartedUtc,
                ["startMs"] = Math.Round(StartMs, 1),
                ["prewarmMs"] = Math.Round(PrewarmMs, 1),
                ["runtimeDirectory"] = RuntimeDirectory,
                ["runtimeVersion"] = RuntimeVersion,
                ["roslynVersion"] = RoslynVersion,
                ["buildKey"] = BuildKey,
                ["buildCacheHit"] = BuildCacheHit,
                ["buildMs"] = Math.Round(BuildMs, 1),
                ["lastError"] = LastError,
                ["lastErrorUtc"] = LastErrorUtc,
                ["sessionDisabledReason"] = SessionDisabledReason,
                ["recentCrashes"] = RecentCrashes,
                ["starts"] = Starts,
                ["lastCompiler"] = LastCompiler,
                ["lastFallbackReason"] = LastFallbackReason,
                ["ignoredProjectOptions"] = IgnoredProjectOptions,
                ["stderrTail"] = StderrTail,
            };
            return RoslynHostJson.Serialize(obj) + "\n";
        }

        /// <summary>Null when <paramref name="json"/> is not a status object.</summary>
        public static RoslynHostStatus? Parse(string json)
        {
            object? parsed;
            try
            {
                parsed = RoslynHostJson.Parse(json);
            }
            catch (FormatException)
            {
                return null;
            }

            if (!(parsed is Dictionary<string, object?> h))
            {
                return null;
            }

            return new RoslynHostStatus
            {
                Schema = (int)RoslynHostJson.GetInt64(h, "schema", 0, 0, int.MaxValue),
                UpdatedUtc = RoslynHostJson.GetString(h, "updatedUtc") ?? string.Empty,
                EditorPid = (int)RoslynHostJson.GetInt64(h, "editorPid", 0, 0, int.MaxValue),
                ConfiguredCompiler = RoslynHostJson.GetString(h, "configuredCompiler") ?? string.Empty,
                ActiveBackend = RoslynHostJson.GetString(h, "activeBackend") ?? string.Empty,
                PrewarmPolicy = RoslynHostJson.GetString(h, "prewarmPolicy") ?? string.Empty,
                HostWaitMs = (int)RoslynHostJson.GetInt64(h, "hostWaitMs", 0, 0, int.MaxValue),
                CompileTimeoutMs = (int)RoslynHostJson.GetInt64(h, "compileTimeoutMs", 0, 0, int.MaxValue),
                IdleExitMinutes = (int)RoslynHostJson.GetInt64(h, "idleExitMinutes", 0, 0, int.MaxValue),
                State = RoslynHostJson.GetString(h, "state") ?? string.Empty,
                Note = RoslynHostJson.GetString(h, "note") ?? string.Empty,
                Pid = (int)RoslynHostJson.GetInt64(h, "pid", 0, 0, int.MaxValue),
                HostStartedUtc = RoslynHostJson.GetString(h, "hostStartedUtc") ?? string.Empty,
                StartMs = RoslynHostJson.GetDouble(h, "startMs"),
                PrewarmMs = RoslynHostJson.GetDouble(h, "prewarmMs"),
                RuntimeDirectory = RoslynHostJson.GetString(h, "runtimeDirectory") ?? string.Empty,
                RuntimeVersion = RoslynHostJson.GetString(h, "runtimeVersion") ?? string.Empty,
                RoslynVersion = RoslynHostJson.GetString(h, "roslynVersion") ?? string.Empty,
                BuildKey = RoslynHostJson.GetString(h, "buildKey") ?? string.Empty,
                BuildCacheHit = RoslynHostJson.GetBool(h, "buildCacheHit"),
                BuildMs = RoslynHostJson.GetDouble(h, "buildMs"),
                LastError = RoslynHostJson.GetString(h, "lastError") ?? string.Empty,
                LastErrorUtc = RoslynHostJson.GetString(h, "lastErrorUtc") ?? string.Empty,
                SessionDisabledReason = RoslynHostJson.GetString(h, "sessionDisabledReason") ?? string.Empty,
                RecentCrashes = (int)RoslynHostJson.GetInt64(h, "recentCrashes", 0, 0, int.MaxValue),
                Starts = (int)RoslynHostJson.GetInt64(h, "starts", 0, 0, int.MaxValue),
                LastCompiler = RoslynHostJson.GetString(h, "lastCompiler") ?? string.Empty,
                LastFallbackReason = RoslynHostJson.GetString(h, "lastFallbackReason") ?? string.Empty,
                IgnoredProjectOptions = RoslynHostJson.GetStringList(h, "ignoredProjectOptions"),
                StderrTail = RoslynHostJson.GetStringList(h, "stderrTail"),
            };
        }

        /// <summary>Null when the file is missing or unreadable.</summary>
        public static RoslynHostStatus? TryRead(string path)
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

        /// <summary>Temp file + replace, so a reader never sees a half-written file.</summary>
        public void Write(string path)
        {
            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            string temp = path + "." + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp";
            File.WriteAllText(temp, ToJson(), new UTF8Encoding(false));
            try
            {
                if (File.Exists(path))
                {
                    // rename(2) over the old file: a reader sees the old or the new content, never neither.
                    File.Replace(temp, path, null);
                }
                else
                {
                    File.Move(temp, path);
                }
            }
            catch (Exception)
            {
                TryDelete(temp);
                throw;
            }
        }

        public static string FormatUtc(DateTime utc)
        {
            return utc.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);
        }

        public static DateTime? ParseUtc(string? text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return null;
            }

            return DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTime value)
                ? value
                : (DateTime?)null;
        }

        private static void TryDelete(string path)
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception)
            {
                // Best effort.
            }
        }
    }
}
