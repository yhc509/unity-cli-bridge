#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace UnityCli.Protocol
{
    /// <summary>
    /// Names of the <c>csc /shared</c> compiler server that <c>execute</c> uses as a fallback. The pipe
    /// is <c>ucb-&lt;projectHash[:8]&gt;-&lt;sha256(unityVersion)[:6]&gt;</c>: short because on macOS and
    /// Linux it becomes a Unix socket path (104-byte limit), and never the default pipe of the editor's
    /// own script compiler server, which is how only our server is ever shut down or killed.
    /// </summary>
    public static class CscSharedPipe
    {
        public const string Prefix = "ucb-";
        public const string PipeNameOption = "-pipename:";

        public static string BuildName(string projectHash, string unityVersion)
        {
            if (projectHash == null || projectHash.Length < 8)
            {
                throw new ArgumentException("A project hash of at least 8 characters is required.", nameof(projectHash));
            }

            return Prefix + projectHash.Substring(0, 8).ToLowerInvariant() + "-" + HashVersion(unityVersion ?? string.Empty);
        }

        /// <summary>True for a name <see cref="BuildName"/> could produce for <paramref name="projectHash"/>, whatever the Unity version.</summary>
        public static bool IsProjectPipe(string? pipeName, string projectHash)
        {
            if (string.IsNullOrEmpty(pipeName) || projectHash == null || projectHash.Length < 8)
            {
                return false;
            }

            string expectedStart = Prefix + projectHash.Substring(0, 8).ToLowerInvariant() + "-";
            if (pipeName!.Length != expectedStart.Length + 6 || !pipeName.StartsWith(expectedStart, StringComparison.Ordinal))
            {
                return false;
            }

            for (int index = expectedStart.Length; index < pipeName.Length; index++)
            {
                char c = pipeName[index];
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// True when <paramref name="commandLine"/> is a compiler server (or shutdown client) for exactly
        /// <paramref name="pipeName"/>: a <c>-pipename:&lt;pipe&gt;</c> token, optionally quoted, ending at
        /// whitespace, a quote, or the end. A longer pipe that merely starts with ours does not match.
        /// </summary>
        public static bool CommandLineHasPipe(string? commandLine, string? pipeName)
        {
            if (string.IsNullOrEmpty(commandLine) || string.IsNullOrEmpty(pipeName))
            {
                return false;
            }

            string token = PipeNameOption + pipeName;
            int start = 0;
            while (true)
            {
                int index = commandLine!.IndexOf(token, start, StringComparison.Ordinal);
                if (index < 0)
                {
                    return false;
                }

                bool startOk = index == 0 || char.IsWhiteSpace(commandLine[index - 1]) || commandLine[index - 1] == '"';
                int end = index + token.Length;
                bool endOk = end == commandLine.Length || char.IsWhiteSpace(commandLine[end]) || commandLine[end] == '"';
                if (startOk && endOk)
                {
                    return true;
                }

                start = index + 1;
            }
        }

        /// <summary>A running compiler server for <paramref name="pipeName"/> (not the <c>-shutdown</c> client).</summary>
        public static bool IsServerCommandLine(string? commandLine, string? pipeName)
        {
            return CommandLineHasPipe(commandLine, pipeName)
                && commandLine!.IndexOf("VBCSCompiler", StringComparison.OrdinalIgnoreCase) >= 0
                && commandLine.IndexOf(" -shutdown", StringComparison.OrdinalIgnoreCase) < 0;
        }

        private static string HashVersion(string unityVersion)
        {
            using (SHA256 sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(unityVersion));
                var builder = new StringBuilder(6);
                for (int index = 0; index < 3; index++)
                {
                    builder.Append(hash[index].ToString("x2"));
                }

                return builder.ToString();
            }
        }
    }

    /// <summary>
    /// Snapshot of the editor's <c>csc /shared</c> fallback, written by the bridge to
    /// <c>Library/com.yhc509.unity-cli-bridge/csc-shared/status.json</c> when its state changes, and
    /// read by <c>doctor</c> locally (no wire change). The default chain and <c>csc-shared</c> write it.
    /// <c>UCB_EXECUTE_COMPILER=codedom</c> writes one short off status when the domain loads and does
    /// not start a compiler server. Unity-free and System.Text.Json-free so both sides share it.
    /// </summary>
    public sealed class CscSharedStatus
    {
        public const string FileName = "status.json";
        public const string ServerRecordFileName = "server.json";
        public const string OffState = "off";
        public const int CurrentSchema = 1;

        /// <summary>
        /// One short status for <c>UCB_EXECUTE_COMPILER=codedom</c>, written once per domain load.
        /// </summary>
        public static void WriteCodedomOff(string projectRoot, int editorPid, DateTime? utcNow = null)
        {
            DateTime now = utcNow ?? DateTime.UtcNow;
            new CscSharedStatus
            {
                UpdatedUtc = RoslynHostStatus.FormatUtc(now),
                EditorPid = editorPid,
                ConfiguredCompiler = "codedom",
                State = OffState,
                Note = "UCB_EXECUTE_COMPILER=codedom turns csc /shared off.",
            }.Write(GetStatusPath(projectRoot));
        }

        /// <summary><c>Library/com.yhc509.unity-cli-bridge/csc-shared</c> under <paramref name="projectRoot"/>.</summary>
        public static string GetDirectory(string projectRoot)
        {
            return Path.Combine(Path.Combine(Path.Combine(projectRoot, "Library"), "com.yhc509.unity-cli-bridge"), "csc-shared");
        }

        public static string GetStatusPath(string projectRoot)
        {
            return Path.Combine(GetDirectory(projectRoot), FileName);
        }

        public int Schema { get; set; } = CurrentSchema;

        public string UpdatedUtc { get; set; } = string.Empty;

        public int EditorPid { get; set; }

        /// <summary>The raw <c>UCB_EXECUTE_COMPILER</c> value.</summary>
        public string ConfiguredCompiler { get; set; } = string.Empty;

        public string PipeName { get; set; } = string.Empty;

        /// <summary><c>idle</c> (not used yet in this domain), <c>ready</c>, <c>failed</c>, or <c>stopped</c>.</summary>
        public string State { get; set; } = string.Empty;

        /// <summary>Why the server was stopped (<c>domain-reload</c>, <c>editor-quit</c>).</summary>
        public string Note { get; set; } = string.Empty;

        /// <summary>The compiler server's pid once found (0 = not known). JSON null while <see cref="ServerPidPending"/>.</summary>
        public int ServerPid { get; set; }

        /// <summary>
        /// True while the background pid lookup is still running. The status file then has
        /// <c>serverPid</c> null instead of 0. False once the pid is known, or when there is no lookup.
        /// </summary>
        public bool ServerPidPending { get; set; }

        public string DotnetPath { get; set; } = string.Empty;

        public string CscPath { get; set; } = string.Empty;

        public string RoslynVersion { get; set; } = string.Empty;

        public int CompileTimeoutMs { get; set; }

        /// <summary>Compiles through csc-shared in this domain (successful or user errors).</summary>
        public int Compiles { get; set; }

        /// <summary>The first compile of the domain, which also starts the server when none is running.</summary>
        public double FirstCompileMs { get; set; }

        /// <summary>Main-thread time of the last stop (<c>-shutdown</c> client plus the leftover check), 0 before the first.</summary>
        public double LastShutdownMs { get; set; }

        public string LastError { get; set; } = string.Empty;

        public string LastErrorUtc { get; set; } = string.Empty;

        public string ToJson()
        {
            var obj = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["schema"] = Schema,
                ["updatedUtc"] = UpdatedUtc,
                ["editorPid"] = EditorPid,
                ["configuredCompiler"] = ConfiguredCompiler,
                ["pipeName"] = PipeName,
                ["state"] = State,
                ["note"] = Note,
                ["serverPid"] = ServerPidPending ? null : (object?)ServerPid,
                ["serverPidPending"] = ServerPidPending,
                ["dotnetPath"] = DotnetPath,
                ["cscPath"] = CscPath,
                ["roslynVersion"] = RoslynVersion,
                ["compileTimeoutMs"] = CompileTimeoutMs,
                ["compiles"] = Compiles,
                ["firstCompileMs"] = Math.Round(FirstCompileMs, 1),
                ["lastShutdownMs"] = Math.Round(LastShutdownMs, 1),
                ["lastError"] = LastError,
                ["lastErrorUtc"] = LastErrorUtc,
            };
            return RoslynHostJson.Serialize(obj) + "\n";
        }

        /// <summary>Null when <paramref name="json"/> is not a status object.</summary>
        public static CscSharedStatus? Parse(string json)
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

            return new CscSharedStatus
            {
                Schema = (int)RoslynHostJson.GetInt64(h, "schema", 0, 0, int.MaxValue),
                UpdatedUtc = RoslynHostJson.GetString(h, "updatedUtc") ?? string.Empty,
                EditorPid = (int)RoslynHostJson.GetInt64(h, "editorPid", 0, 0, int.MaxValue),
                ConfiguredCompiler = RoslynHostJson.GetString(h, "configuredCompiler") ?? string.Empty,
                PipeName = RoslynHostJson.GetString(h, "pipeName") ?? string.Empty,
                State = RoslynHostJson.GetString(h, "state") ?? string.Empty,
                Note = RoslynHostJson.GetString(h, "note") ?? string.Empty,
                ServerPidPending = RoslynHostJson.GetBool(h, "serverPidPending"),
                ServerPid = (int)RoslynHostJson.GetInt64(h, "serverPid", 0, 0, int.MaxValue),
                DotnetPath = RoslynHostJson.GetString(h, "dotnetPath") ?? string.Empty,
                CscPath = RoslynHostJson.GetString(h, "cscPath") ?? string.Empty,
                RoslynVersion = RoslynHostJson.GetString(h, "roslynVersion") ?? string.Empty,
                CompileTimeoutMs = (int)RoslynHostJson.GetInt64(h, "compileTimeoutMs", 0, 0, int.MaxValue),
                Compiles = (int)RoslynHostJson.GetInt64(h, "compiles", 0, 0, int.MaxValue),
                FirstCompileMs = RoslynHostJson.GetDouble(h, "firstCompileMs"),
                LastShutdownMs = RoslynHostJson.GetDouble(h, "lastShutdownMs"),
                LastError = RoslynHostJson.GetString(h, "lastError") ?? string.Empty,
                LastErrorUtc = RoslynHostJson.GetString(h, "lastErrorUtc") ?? string.Empty,
            };
        }

        /// <summary>Null when the file is missing or unreadable.</summary>
        public static CscSharedStatus? TryRead(string path)
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
                    File.Replace(temp, path, null);
                }
                else
                {
                    File.Move(temp, path);
                }
            }
            catch (Exception)
            {
                try
                {
                    File.Delete(temp);
                }
                catch (Exception)
                {
                    // Best effort.
                }

                throw;
            }
        }
    }
}
