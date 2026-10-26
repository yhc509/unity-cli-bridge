#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
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
    /// <c>Begin MonoManager ReloadAssembly</c>. Listings therefore poll bytes already in the pipe,
    /// with a bounded wait, and <see cref="CancelInFlightListings"/> (the reload and quit hooks) kills
    /// the child and closes the pipes so the read returns before the domain unloads. The wait is
    /// <c>poll</c> (and <c>PeekNamedPipe</c> on Windows). <c>ioctl(FIONREAD)</c> is variadic, and on
    /// macOS arm64 a normal P/Invoke passes the output pointer in a register while the kernel reads
    /// it from the stack, so the process dies with an access violation. Completion is the
    /// child exiting and the pipe going quiet, not stdout EOF: a host or csc process started while the
    /// redirected pipe exists can inherit the write end, so EOF never arrives after the listing
    /// process has already exited.
    /// </summary>
    /// <summary>
    /// The process listing did not finish (timeout, non-zero exit, or no output). An empty list
    /// would mean "no compiler server", so callers must not record pid 0.
    /// </summary>
    internal sealed class ProcessListingFailedException : Exception
    {
        public ProcessListingFailedException()
            : base("process listing did not finish")
        {
        }
    }

    internal static class RoslynHostProcessInspector
    {
        private const int ListingTimeoutMs = 10000;
        private const int CancelJoinMs = 250;

        private static readonly object StartGate = new object();
        private static readonly List<Process> InFlightListings = new List<Process>();
        private static CancellationTokenSource DomainListings = new CancellationTokenSource();
        private static int CancelDepth;
        private static int ListingScopeDepth;
        private static int ListingReservations;

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
                while (ListingScopeDepth > 0 || InFlightListings.Count > 0 || ListingReservations > 0)
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

            string? output = Capture("/bin/ps", "-o pid= -p " + pid.ToString(CultureInfo.InvariantCulture), null, CancellationToken.None, true, false, 0, out bool _, out bool _);
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
                    0,
                    out bool _,
                    out bool _)
                : Capture("/bin/ps", "-ww -o command= -p " + pid.ToString(CultureInfo.InvariantCulture), null, CancellationToken.None, true, false, 0, out bool _, out bool _);
            if (output == null)
            {
                return null;
            }

            string trimmed = NormalizeProcessListing(output).Trim();
            return trimmed.Length == 0 ? null : trimmed;
        }

        /// <summary>
        /// Every process whose command line contains <paramref name="needle"/> (ordinal, ignoring case):
        /// <c>ps</c> on macOS/Linux, CIM through PowerShell on Windows. Throws
        /// <see cref="OperationCanceledException"/> when a reload or quit cancels the listing, and
        /// <see cref="ProcessListingFailedException"/> when the listing times out or exits without a
        /// table. That failure is not an empty table: an empty result would mean no compiler server.
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
                    0,
                    out cancelled,
                    out bool failed)
                : Capture("/bin/ps", "-axww -o pid= -o command=", null, CancellationToken.None, true, false, 0, out cancelled, out failed);
            if (cancelled)
            {
                throw new OperationCanceledException();
            }

            // A timeout or a child that exited without writing is not an empty process table.
            // Callers that treat empty as "the server is already gone" would record pid 0.
            if (failed || string.IsNullOrWhiteSpace(output))
            {
                throw new ProcessListingFailedException();
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
            return RunListing(fileName, arguments, cancellation, linkDomainCancellation, ignoreListingScope, 0, out bool _, out bool _);
        }

        /// <summary>
        /// Test entry with the timeout and the failure flag. <paramref name="timeoutMs"/> 0 uses
        /// <see cref="ListingTimeoutMs"/>. <paramref name="failed"/> is a timeout or a child that
        /// exited without producing output, which is not an empty process table.
        /// <paramref name="onChildExitDrainMs"/> runs on the listing thread after the post-exit
        /// drain, with how many milliseconds that drain took. Production passes null, and a null
        /// callback does not time the drain or change its result.
        /// </summary>
        internal static string? RunListing(
            string fileName,
            string arguments,
            CancellationToken cancellation,
            bool linkDomainCancellation,
            bool ignoreListingScope,
            int timeoutMs,
            out bool cancelled,
            out bool failed,
            Action<long>? onChildExitDrainMs = null)
        {
            return Capture(
                fileName,
                arguments,
                null,
                cancellation,
                linkDomainCancellation,
                ignoreListingScope,
                timeoutMs,
                out cancelled,
                out failed,
                onChildExitDrainMs);
        }

        /// <summary>
        /// Runs <paramref name="fileName"/> and returns its stdout. The calling thread waits on the
        /// child exiting or the line event, not inside a pipe read. <paramref name="cancelled"/> is
        /// true when a reload, quit, or <paramref name="cancellation"/> stopped the read.
        /// <paramref name="failed"/> is true when the child did not exit in time or exited without
        /// output. Both leave the output null so a caller cannot mistake them for an empty process table.
        /// </summary>
        private static string? Capture(
            string fileName,
            string arguments,
            Encoding? standardOutputEncoding,
            CancellationToken cancellation,
            bool linkDomainCancellation,
            bool ignoreListingScope,
            int timeoutMs,
            out bool cancelled,
            out bool failed,
            Action<long>? onChildExitDrainMs = null)
        {
            cancelled = false;
            failed = false;
            Process? process = null;
            bool registered = false;
            CancellationTokenRegistration registration = default;
            CancellationTokenSource? linked = null;
            IDisposable? listingPipes = null;
            var stdout = new StringBuilder();
            bool reserved = false;
            try
            {
                // Snapshot the domain token and take a reservation under StartGate. The reservation
                // is visible to AcquireListingScope before Process.Start returns, so a parallel test
                // cannot cancel this generation in the gap before the process is in InFlightListings.
                // A real reload still cancels it: CancelInFlightListings does not take that lock.
                if (!TryReserveListing(cancellation, ignoreListingScope, linkDomainCancellation, out linked, out CancellationToken token))
                {
                    cancelled = true;
                    return null;
                }

                reserved = true;
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

                // Held until the pipes are closed in finally, so a host or csc spawn cannot inherit them.
                listingPipes = ProcessSpawnGate.EnterListing();
                process = Process.Start(startInfo);
                if (process == null)
                {
                    failed = true;
                    return null;
                }

                // Poll bytes already sitting in the pipe. Do not wait for stdout EOF and do not use
                // the async line callback: a host started while this pipe existed can inherit the
                // write end, so EOF never arrives, and a busy thread pool can drop the callback.
                // The listing thread only reads when a peek says bytes are waiting, so it is not
                // blocked in a native read when a reload closes the pipe.
                Process child = process;
                Stream output = child.StandardOutput.BaseStream;
                Stream error = child.StandardError.BaseStream;
                Encoding encoding = standardOutputEncoding ?? Console.OutputEncoding;
                Decoder decoder = encoding.GetDecoder();
                var buffer = new byte[8192];
                var discard = new byte[8192];
                lock (StartGate)
                {
                    InFlightListings.Add(child);
                    registered = true;
                    if (reserved)
                    {
                        ListingReservations--;
                        reserved = false;
                    }

                    Monitor.PulseAll(StartGate);
                }

                if (token.IsCancellationRequested)
                {
                    cancelled = true;
                    return null;
                }

                registration = token.Register(() => StopListingProcess(child));
                int limitMs = timeoutMs > 0 ? timeoutMs : ListingTimeoutMs;
                var watch = Stopwatch.StartNew();
                while (true)
                {
                    if (token.IsCancellationRequested)
                    {
                        cancelled = true;
                        return null;
                    }

                    DrainPipe(output, buffer, decoder, stdout);
                    DrainPipe(error, discard, null, null);

                    if (HasExited(child))
                    {
                        if (token.IsCancellationRequested)
                        {
                            cancelled = true;
                            return null;
                        }

                        // The exit flush can land between the peek and HasExited. Read what is
                        // already buffered and return. No sleep and no WaitForExit: this runs on
                        // the editor main thread during reload and quit (lastShutdownMs). On
                        // Unity's Mono, WaitForExit(200) after HasExited still waited out that
                        // whole budget, so macOS shutdown sat at about 210 ms. A leaked write end
                        // never delivers EOF; a quiet drain is the end of this child's output.
                        // The callback is null in production and is not invoked, so this drain
                        // does not time itself or change its result. Tests use it to measure
                        // only this section against that 200 ms budget.
                        Stopwatch? exitDrain = onChildExitDrainMs != null ? Stopwatch.StartNew() : null;
                        try
                        {
                            while (DrainPipe(output, buffer, decoder, stdout) > 0)
                            {
                                DrainPipe(error, discard, null, null);
                            }

                            DrainPipe(error, discard, null, null);
                            if (token.IsCancellationRequested)
                            {
                                cancelled = true;
                                return null;
                            }

                            Decode(decoder, buffer, 0, stdout, flush: true);
                            return FinishExitedChild(child, stdout.ToString(), out failed);
                        }
                        finally
                        {
                            if (exitDrain != null)
                            {
                                exitDrain.Stop();
                                onChildExitDrainMs!(exitDrain.ElapsedMilliseconds);
                            }
                        }
                    }

                    if (watch.ElapsedMilliseconds >= limitMs)
                    {
                        failed = true;
                        return null;
                    }

                    // A still-running child only. One millisecond, so shutdown stays on the
                    // ps/kill runtime instead of a 20 ms poll tick.
                    Thread.Sleep(1);
                }
            }
            catch (Exception)
            {
                if (cancellation.IsCancellationRequested || (linked != null && linked.IsCancellationRequested))
                {
                    cancelled = true;
                }
                else
                {
                    failed = true;
                }

                return null;
            }
            finally
            {
                registration.Dispose();
                if (reserved)
                {
                    lock (StartGate)
                    {
                        ListingReservations--;
                        reserved = false;
                        Monitor.PulseAll(StartGate);
                    }
                }

                if (process != null)
                {
                    if (cancelled || failed || !HasExited(process))
                    {
                        StopListingProcess(process);
                    }
                    else
                    {
                        // The child has exited. A leaked stdout write end means EOF never arrives,
                        // and Dispose waits on that reader, so close the pipes first.
                        CloseListingPipes(process);
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

                // After the pipes are closed, so the next spawn cannot inherit them.
                listingPipes?.Dispose();
                linked?.Dispose();
            }
        }

        /// <summary>
        /// Reads bytes a peek says are already buffered. Returns how many were read. A peek of zero
        /// does not read, so this does not wait for EOF and does not block the caller in the pipe.
        /// </summary>
        private static int DrainPipe(Stream stream, byte[] buffer, Decoder? decoder, StringBuilder? sink)
        {
            int total = 0;
            while (TryPeekAvailable(stream, out int available) && available > 0)
            {
                int want = Math.Min(available, buffer.Length);
                int n;
                try
                {
                    n = stream.Read(buffer, 0, want);
                }
                catch (Exception)
                {
                    break;
                }

                if (n <= 0)
                {
                    break;
                }

                if (decoder != null && sink != null)
                {
                    Decode(decoder, buffer, n, sink, flush: false);
                }

                total += n;
                if (n < want)
                {
                    break;
                }
            }

            return total;
        }

        private static void Decode(Decoder decoder, byte[] buffer, int count, StringBuilder sink, bool flush)
        {
            if (count == 0 && !flush)
            {
                return;
            }

            int chars = decoder.GetCharCount(buffer, 0, count, flush);
            if (chars == 0)
            {
                return;
            }

            var text = new char[chars];
            decoder.GetChars(buffer, 0, count, text, 0, flush);
            sink.Append(text);
        }

        private static bool TryPeekAvailable(Stream stream, out int available)
        {
            available = 0;
            SafeHandle? handle = null;
            if (stream is PipeStream pipe)
            {
                handle = pipe.SafePipeHandle;
            }
            else if (stream is FileStream file)
            {
                handle = file.SafeFileHandle;
            }

            if (handle == null || handle.IsInvalid || handle.IsClosed)
            {
                return false;
            }

            bool added = false;
            try
            {
                handle.DangerousAddRef(ref added);
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    if (!PeekNamedPipe(handle.DangerousGetHandle(), IntPtr.Zero, 0, IntPtr.Zero, out uint queued, IntPtr.Zero))
                    {
                        return false;
                    }

                    available = queued > int.MaxValue ? int.MaxValue : (int)queued;
                    return true;
                }

                // poll is not variadic. ioctl(FIONREAD) is, and on macOS arm64 that P/Invoke
                // access-violates (run 40066673517). A readable pipe read returns only the
                // bytes already waiting, so the caller may ask for a full buffer.
                var polled = new PollFd
                {
                    Fd = handle.DangerousGetHandle().ToInt32(),
                    Events = PollIn,
                };
                int rc = Poll(ref polled, (UIntPtr)1, 0);
                if (rc < 0 || (polled.REvents & PollNval) != 0)
                {
                    return false;
                }

                if ((polled.REvents & (PollIn | PollHup)) == 0)
                {
                    available = 0;
                    return true;
                }

                available = int.MaxValue;
                return true;
            }
            catch (Exception)
            {
                return false;
            }
            finally
            {
                if (added)
                {
                    handle.DangerousRelease();
                }
            }
        }

        private const short PollIn = 1;
        private const short PollHup = 0x10;
        private const short PollNval = 0x20;

        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        private struct PollFd
        {
            public int Fd;
            public short Events;
            public short REvents;
        }

        // nfds_t is 32-bit on macOS and 64-bit on Linux. One fd fits either width.
        // UIntPtr, not nuint: this file also builds on the Unity 2021 editor profile.
        [DllImport("libc", EntryPoint = "poll", SetLastError = true)]
        private static extern int Poll(ref PollFd fds, UIntPtr nfds, int timeout);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool PeekNamedPipe(
            IntPtr handle,
            IntPtr buffer,
            uint bufferSize,
            IntPtr bytesRead,
            out uint totalBytesAvail,
            IntPtr bytesLeftThisMessage);

        private static string? FinishExitedChild(Process child, string text, out bool failed)
        {
            failed = false;
            int code;
            try
            {
                // HasExited is already true, so this is the cached status. Do not call
                // WaitForExit. The parameterless overload waits for stdout EOF, and a leaked
                // write handle never arrives. The timed overload still waited out its full
                // budget on Unity's Mono after the exit was already known (macOS
                // lastShutdownMs about 210 ms, on the 200 ms argument).
                code = child.ExitCode;
            }
            catch (Exception)
            {
                failed = true;
                return null;
            }

            if (code != 0 || string.IsNullOrWhiteSpace(text))
            {
                failed = true;
                return null;
            }

            return text;
        }

        private static bool TryReserveListing(
            CancellationToken cancellation,
            bool ignoreListingScope,
            bool linkDomainCancellation,
            out CancellationTokenSource? linked,
            out CancellationToken token)
        {
            linked = null;
            token = cancellation;
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

                CancellationTokenSource domain = DomainListings;
                linked = linkDomainCancellation
                    ? CancellationTokenSource.CreateLinkedTokenSource(domain.Token, cancellation)
                    : CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                token = linked.Token;
                ListingReservations++;
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

            CloseListingPipes(process);
        }

        /// <summary>
        /// Ends the async readers. Closing is what unblocks them when a leaked write end keeps EOF
        /// from arriving; <see cref="Process.Dispose"/> would otherwise wait on that read.
        /// </summary>
        private static void CloseListingPipes(Process process)
        {
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
                Capture("/bin/kill", "-KILL " + pid.ToString(CultureInfo.InvariantCulture), null, CancellationToken.None, true, false, 0, out bool _, out bool _);
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
