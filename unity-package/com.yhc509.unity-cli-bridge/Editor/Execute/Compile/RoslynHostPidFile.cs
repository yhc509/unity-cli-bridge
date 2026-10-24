#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
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

    /// <summary>
    /// Process probes for orphan cleanup and the compiler-server listing. Never throws, except
    /// <see cref="FindByCommandLine"/> when a reload or quit cancels the listing: the caller abandons
    /// the retire instead of treating an interrupted read as an empty process table.
    /// A domain reload must not meet a thread blocked in <c>StandardOutput.ReadToEnd</c>. That read
    /// sits in native pipe I/O, Mono cannot abort it, and the editor hangs in
    /// <c>Begin MonoManager ReloadAssembly</c>. Listings therefore read asynchronously, with a bounded
    /// wait, and <see cref="CancelInFlightListings"/> (the reload and quit hooks) kills the child and
    /// closes the pipes so the read returns before the domain unloads.
    /// </summary>
    internal static class RoslynHostProcessInspector
    {
        private const int ListingTimeoutMs = 10000;
        private const int CancelJoinMs = 250;

        private static readonly object StartGate = new object();
        private static readonly List<Process> InFlightListings = new List<Process>();
        private static CancellationTokenSource DomainListings = new CancellationTokenSource();
        private static int CancelDepth;
        private static int ListingScopeDepth;

        /// <summary>How many process listings are past start. The reload hook waits until this drops.</summary>
        internal static int InFlightListingCount
        {
            get
            {
                lock (StartGate)
                {
                    return InFlightListings.Count;
                }
            }
        }

        /// <summary>
        /// Holds new listings until <see cref="IDisposable.Dispose"/>. Tests use this so a cancel does
        /// not kill a listing another test already started, and so a listing started during the test waits.
        /// </summary>
        internal static IDisposable AcquireListingScope()
        {
            lock (StartGate)
            {
                while (ListingScopeDepth > 0 || InFlightListings.Count > 0)
                {
                    Monitor.Wait(StartGate, 20);
                }

                ListingScopeDepth++;
            }

            return new ListingScope();
        }

        /// <summary>
        /// Cancels every listing still running. Called from the reload and quit hooks before the
        /// domain unloads. Kills the child and closes its redirected pipes, then waits briefly for
        /// the reader to leave the pipe. A new listing started afterwards is not cancelled.
        /// </summary>
        public static void CancelInFlightListings()
        {
            Interlocked.Increment(ref CancelDepth);
            CancellationTokenSource previous = Interlocked.Exchange(ref DomainListings, new CancellationTokenSource());
            try
            {
                previous.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }

            var watch = Stopwatch.StartNew();
            while (InFlightListingCount > 0 && watch.ElapsedMilliseconds < CancelJoinMs)
            {
                Thread.Sleep(10);
            }

            Interlocked.Decrement(ref CancelDepth);
            lock (StartGate)
            {
                Monitor.PulseAll(StartGate);
            }

            try
            {
                previous.Dispose();
            }
            catch (ObjectDisposedException)
            {
            }
        }
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

            string? output = Capture("/bin/ps", "-o pid= -p " + pid.ToString(CultureInfo.InvariantCulture), null, CancellationToken.None, true, false, out bool _);
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
                    WindowsPowerShellArguments(
                        WindowsUtf8Prelude
                        + "$p = Get-CimInstance Win32_Process -Filter ('ProcessId="
                        + pid.ToString(CultureInfo.InvariantCulture)
                        + "')\r\n"
                        + "if ($null -ne $p -and $null -ne $p.CommandLine) { [Console]::Out.WriteLine([string]$p.CommandLine) }\r\n"),
                    Utf8NoBom,
                    CancellationToken.None,
                    true,
                    false,
                    out bool _)
                : Capture("/bin/ps", "-ww -o command= -p " + pid.ToString(CultureInfo.InvariantCulture), null, CancellationToken.None, true, false, out bool _);
            if (output == null)
            {
                return null;
            }

            string trimmed = NormalizeProcessListing(output).Trim();
            return trimmed.Length == 0 ? null : trimmed;
        }

        /// <summary>
        /// Every process whose command line contains <paramref name="needle"/> (ordinal, ignoring case):
        /// <c>ps</c> on macOS/Linux, CIM through PowerShell on Windows. Empty when the table cannot be read.
        /// </summary>
        public static List<KeyValuePair<int, string>> FindByCommandLine(string needle)
        {
            var found = new List<KeyValuePair<int, string>>();
            if (string.IsNullOrEmpty(needle))
            {
                return found;
            }

            bool cancelled;
            string? output = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? Capture(
                    "powershell.exe",
                    WindowsPowerShellArguments(
                        WindowsUtf8Prelude
                        + "Get-CimInstance Win32_Process | ForEach-Object { [Console]::Out.WriteLine(('{0} {1}' -f $_.ProcessId, $_.CommandLine)) }\r\n"),
                    Utf8NoBom,
                    CancellationToken.None,
                    true,
                    false,
                    out cancelled)
                : Capture("/bin/ps", "-axww -o pid= -o command=", null, CancellationToken.None, true, false, out cancelled);
            if (cancelled)
            {
                throw new OperationCanceledException();
            }

            return ParseCommandLineListing(output, needle);
        }

        /// <summary>
        /// Pids whose command line contains <paramref name="needle"/>. One process per line
        /// (<c>pid command</c>). A Windows PowerShell host wraps long lines and, when its UTF-16
        /// output is read as a single-byte encoding, interleaves NULs; both used to drop the
        /// compiler server so the recorded pid stayed 0. Wrapped continuations are joined, and
        /// NULs are stripped, before the pid is parsed. A pid of 0 is never returned.
        /// </summary>
        internal static List<KeyValuePair<int, string>> ParseCommandLineListing(string? output, string needle)
        {
            var found = new List<KeyValuePair<int, string>>();
            if (string.IsNullOrEmpty(output) || string.IsNullOrEmpty(needle))
            {
                return found;
            }

            string? pending = null;
            foreach (string rawLine in NormalizeProcessListing(output!).Split('\n'))
            {
                string line = rawLine.Trim().TrimEnd('\r');
                if (line.Length == 0)
                {
                    continue;
                }

                if (pending != null && !StartsWithPid(line))
                {
                    // PowerShell inserts the break at the console width and the trailing space
                    // was trimmed off the previous fragment, so join without adding one.
                    pending += line;
                    continue;
                }

                if (pending != null)
                {
                    ConsiderCommandLine(found, pending, needle);
                }

                pending = line;
            }

            if (pending != null)
            {
                ConsiderCommandLine(found, pending, needle);
            }

            return found;
        }

        private static void ConsiderCommandLine(List<KeyValuePair<int, string>> found, string line, string needle)
        {
            int space = line.IndexOf(' ');
            if (space <= 0 || line.IndexOf(needle, StringComparison.OrdinalIgnoreCase) < 0)
            {
                return;
            }

            if (int.TryParse(line.Substring(0, space), NumberStyles.Integer, CultureInfo.InvariantCulture, out int pid) && pid > 0)
            {
                found.Add(new KeyValuePair<int, string>(pid, line.Substring(space + 1).Trim()));
            }
        }

        private static bool StartsWithPid(string line)
        {
            int space = line.IndexOf(' ');
            string token = space > 0 ? line.Substring(0, space) : line;
            return int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out int pid) && pid > 0;
        }

        /// <summary>
        /// Drops NULs left when UTF-16 LE process text was decoded as single-byte characters, and a
        /// leading BOM. ASCII command lines round-trip; a listing with no NULs is unchanged.
        /// </summary>
        internal static string NormalizeProcessListing(string output)
        {
            if (output.IndexOf('\0') < 0 && output.IndexOf('\uFEFF') < 0)
            {
                return output;
            }

            var builder = new StringBuilder(output.Length);
            foreach (char c in output)
            {
                if (c != '\0' && c != '\uFEFF')
                {
                    builder.Append(c);
                }
            }

            return builder.ToString();
        }

        private const string WindowsUtf8Prelude =
            "$OutputEncoding = [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding $false\r\n";

        private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);

        /// <summary><c>-EncodedCommand</c> so <c>$_</c> and quotes reach Windows PowerShell unchanged.</summary>
        private static string WindowsPowerShellArguments(string script)
        {
            return "-NoProfile -NonInteractive -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        }

        /// <summary>Test entry. <paramref name="ignoreListingScope"/> lets the sleeper run inside <see cref="AcquireListingScope"/>.</summary>
        internal static string? RunListing(string fileName, string arguments, CancellationToken cancellation, bool linkDomainCancellation, bool ignoreListingScope)
        {
            return Capture(fileName, arguments, null, cancellation, linkDomainCancellation, ignoreListingScope, out bool _);
        }

        /// <summary>
        /// Runs <paramref name="fileName"/> and returns its stdout. The calling thread waits on the
        /// line event, not inside a pipe read. <paramref name="cancelled"/> is true when a reload,
        /// quit, or <paramref name="cancellation"/> stopped the read; the output is then null so a
        /// caller cannot mistake the interrupt for an empty process table.
        /// </summary>
        private static string? Capture(
            string fileName,
            string arguments,
            Encoding? standardOutputEncoding,
            CancellationToken cancellation,
            bool linkDomainCancellation,
            bool ignoreListingScope,
            out bool cancelled)
        {
            cancelled = false;
            Process? process = null;
            bool registered = false;
            CancellationTokenRegistration registration = default;
            CancellationTokenSource? linked = null;
            ManualResetEvent? done = null;
            try
            {
                if (!WaitToStart(cancellation, ignoreListingScope))
                {
                    cancelled = true;
                    return null;
                }

                CancellationTokenSource domain = Volatile.Read(ref DomainListings);
                linked = linkDomainCancellation
                    ? CancellationTokenSource.CreateLinkedTokenSource(domain.Token, cancellation)
                    : CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                CancellationToken token = linked.Token;
                var startInfo = new ProcessStartInfo(fileName, arguments)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                if (standardOutputEncoding != null)
                {
                    startInfo.StandardOutputEncoding = standardOutputEncoding;
                }

                process = Process.Start(startInfo);
                if (process == null)
                {
                    return null;
                }

                Process child = process;
                lock (StartGate)
                {
                    InFlightListings.Add(child);
                    registered = true;
                    Monitor.PulseAll(StartGate);
                }

                if (token.IsCancellationRequested)
                {
                    cancelled = true;
                    return null;
                }

                var stdout = new StringBuilder();
                done = new ManualResetEvent(false);
                child.OutputDataReceived += (_, e) =>
                {
                    try
                    {
                        if (e.Data == null)
                        {
                            done.Set();
                            return;
                        }

                        stdout.Append(e.Data);
                        stdout.Append('\n');
                    }
                    catch (ObjectDisposedException)
                    {
                    }
                };
                child.ErrorDataReceived += (_, __) => { };
                child.BeginOutputReadLine();
                child.BeginErrorReadLine();
                registration = token.Register(() => StopListingProcess(child));
                var watch = Stopwatch.StartNew();
                while (!done.WaitOne(50))
                {
                    if (token.IsCancellationRequested)
                    {
                        cancelled = true;
                        return null;
                    }

                    if (watch.ElapsedMilliseconds >= ListingTimeoutMs)
                    {
                        return null;
                    }
                }

                if (token.IsCancellationRequested)
                {
                    cancelled = true;
                    return null;
                }

                var exit = Stopwatch.StartNew();
                while (!process.WaitForExit(50))
                {
                    if (token.IsCancellationRequested)
                    {
                        cancelled = true;
                        return null;
                    }

                    if (exit.ElapsedMilliseconds >= 2000)
                    {
                        return null;
                    }
                }

                return stdout.ToString();
            }
            catch (Exception)
            {
                if (cancellation.IsCancellationRequested || (linked != null && linked.IsCancellationRequested))
                {
                    cancelled = true;
                }

                return null;
            }
            finally
            {
                registration.Dispose();
                if (process != null)
                {
                    if (cancelled || !HasExited(process))
                    {
                        StopListingProcess(process);
                    }

                    if (registered)
                    {
                        lock (StartGate)
                        {
                            InFlightListings.Remove(process);
                            Monitor.PulseAll(StartGate);
                        }
                    }

                    // Dispose waits for the async readers. Skip it when the child is still alive so
                    // a reload hook is not stuck behind the same pipe read it just cancelled.
                    if (HasExited(process))
                    {
                        try
                        {
                            process.Dispose();
                        }
                        catch (Exception)
                        {
                        }
                    }
                }

                linked?.Dispose();
                if (done != null)
                {
                    try
                    {
                        done.Dispose();
                    }
                    catch (Exception)
                    {
                    }
                }
            }
        }

        private static bool WaitToStart(CancellationToken cancellation, bool ignoreListingScope)
        {
            var watch = Stopwatch.StartNew();
            lock (StartGate)
            {
                while (CancelDepth > 0 || (!ignoreListingScope && ListingScopeDepth > 0))
                {
                    if (cancellation.IsCancellationRequested || watch.ElapsedMilliseconds > ListingTimeoutMs)
                    {
                        return false;
                    }

                    Monitor.Wait(StartGate, 20);
                }
            }

            return true;
        }

        /// <summary>Kills a listing child and closes its pipes so a reader blocked on stdout returns.</summary>
        private static void StopListingProcess(Process process)
        {
            try
            {
                if (!HasExited(process))
                {
                    process.Kill();
                }
            }
            catch (Exception)
            {
            }

            try
            {
                process.CancelOutputRead();
            }
            catch (Exception)
            {
            }

            try
            {
                process.CancelErrorRead();
            }
            catch (Exception)
            {
            }

            try
            {
                process.StandardOutput.Close();
            }
            catch (Exception)
            {
            }

            try
            {
                process.StandardError.Close();
            }
            catch (Exception)
            {
            }
        }

        private static bool HasExited(Process process)
        {
            try
            {
                return process.HasExited;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private sealed class ListingScope : IDisposable
        {
            public void Dispose()
            {
                lock (StartGate)
                {
                    if (ListingScopeDepth > 0)
                    {
                        ListingScopeDepth--;
                    }

                    Monitor.PulseAll(StartGate);
                }
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
                Capture("/bin/kill", "-KILL " + pid.ToString(CultureInfo.InvariantCulture), null, CancellationToken.None, true, false, out bool _);
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
