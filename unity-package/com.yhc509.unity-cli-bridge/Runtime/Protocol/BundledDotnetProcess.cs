#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace UnityCli.Protocol
{
    /// <summary>
    /// Starts child processes on the editor's bundled <c>dotnet</c> muxer only (csc builds, the
    /// Roslyn host). The executable must be an absolute path to an existing file, the shell is never
    /// used (so there is no PATH lookup), and the environment is cleaned of every variable that could
    /// steer the muxer or runtime toward a system .NET or change how it runs.
    /// </summary>
    public static class BundledDotnetProcess
    {
        /// <summary>Removed from the child environment (exact names, compared ignoring case).</summary>
        public static readonly string[] RemovedVariables =
        {
            "DOTNET_ROOT",
            "DOTNET_ROOT(x86)",
            "DOTNET_ROOT_X64",
            "DOTNET_ROOT_ARM64",
            "DOTNET_HOST_PATH",
            "DOTNET_ROLL_FORWARD",
            "DOTNET_ROLL_FORWARD_TO_PRERELEASE",
            "DOTNET_ADDITIONAL_DEPS",
            "DOTNET_SHARED_STORE",
            "DOTNET_STARTUP_HOOKS",
        };

        /// <summary>
        /// Removed from the child environment (name prefixes, compared ignoring case so that
        /// <c>COMPLUS_</c> and <c>DOTNET_GCgen0size</c> are caught as well; <c>MSBuild</c> covers <c>MSBUILD</c>).
        /// </summary>
        public static readonly string[] RemovedPrefixes =
        {
            "COMPlus_",
            "DOTNET_gc",
            "MSBuild",
        };

        /// <summary>Set on the child environment after removal.</summary>
        public static readonly KeyValuePair<string, string>[] ForcedVariables =
        {
            new KeyValuePair<string, string>("DOTNET_MULTILEVEL_LOOKUP", "0"),
            new KeyValuePair<string, string>("DOTNET_CLI_TELEMETRY_OPTOUT", "1"),
            new KeyValuePair<string, string>("DOTNET_NOLOGO", "1"),
            new KeyValuePair<string, string>("DOTNET_TieredPGO", "0"),
        };

        public static bool ShouldRemove(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return false;
            }

            foreach (string removed in RemovedVariables)
            {
                if (string.Equals(name, removed, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            foreach (string prefix in RemovedPrefixes)
            {
                if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>Applies the removal and forced values to a child environment in place.</summary>
        public static void SanitizeEnvironment(IDictionary<string, string?> environment)
        {
            if (environment == null)
            {
                throw new ArgumentNullException(nameof(environment));
            }

            var doomed = new List<string>();
            foreach (string name in environment.Keys)
            {
                if (ShouldRemove(name))
                {
                    doomed.Add(name);
                }
            }

            foreach (string name in doomed)
            {
                environment.Remove(name);
            }

            foreach (KeyValuePair<string, string> forced in ForcedVariables)
            {
                // Drop any differently-cased copy first so exactly one entry remains on case-sensitive platforms.
                var sameName = new List<string>();
                foreach (string name in environment.Keys)
                {
                    if (string.Equals(name, forced.Key, StringComparison.OrdinalIgnoreCase))
                    {
                        sameName.Add(name);
                    }
                }

                foreach (string name in sameName)
                {
                    environment.Remove(name);
                }

                environment[forced.Key] = forced.Value;
            }
        }

        /// <summary>
        /// A start info for <paramref name="dotnetPath"/> with redirected stdio, no window, and a
        /// sanitized environment. Throws when the muxer path is relative or missing.
        /// </summary>
        public static ProcessStartInfo CreateStartInfo(string dotnetPath, IEnumerable<string> arguments, string? workingDirectory)
        {
            if (string.IsNullOrEmpty(dotnetPath) || !Path.IsPathRooted(dotnetPath))
            {
                throw new ArgumentException("The bundled dotnet path must be absolute: " + dotnetPath, nameof(dotnetPath));
            }

            if (!File.Exists(dotnetPath))
            {
                throw new FileNotFoundException("The bundled dotnet muxer does not exist.", dotnetPath);
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = dotnetPath,
                Arguments = JoinArguments(arguments),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            if (!string.IsNullOrEmpty(workingDirectory))
            {
                startInfo.WorkingDirectory = workingDirectory;
            }

            SanitizeEnvironment(startInfo.Environment!);
            return startInfo;
        }

        /// <summary>Joins arguments with the quoting rules both the Windows CRT and .NET's Unix argument parser use.</summary>
        public static string JoinArguments(IEnumerable<string> arguments)
        {
            var builder = new StringBuilder();
            foreach (string argument in arguments)
            {
                if (builder.Length > 0)
                {
                    builder.Append(' ');
                }

                builder.Append(QuoteArgument(argument));
            }

            return builder.ToString();
        }

        public static string QuoteArgument(string argument)
        {
            if (argument == null)
            {
                throw new ArgumentNullException(nameof(argument));
            }

            if (argument.Length > 0 && argument.IndexOfAny(new[] { ' ', '\t', '\n', '\v', '"' }) < 0)
            {
                return argument;
            }

            var builder = new StringBuilder();
            builder.Append('"');
            int backslashes = 0;
            foreach (char c in argument)
            {
                if (c == '\\')
                {
                    backslashes++;
                    continue;
                }

                if (c == '"')
                {
                    builder.Append('\\', backslashes * 2 + 1);
                    builder.Append('"');
                }
                else
                {
                    builder.Append('\\', backslashes);
                    builder.Append(c);
                }

                backslashes = 0;
            }

            builder.Append('\\', backslashes * 2);
            builder.Append('"');
            return builder.ToString();
        }

        /// <summary>
        /// Runs a short-lived child (stdin closed immediately) and captures its output. A child that
        /// outlives <paramref name="timeout"/> is killed and reported with a null exit code.
        /// </summary>
        public static BundledDotnetRunResult Run(ProcessStartInfo startInfo, TimeSpan timeout)
        {
            var stdout = new StringBuilder();
            var stderr = new StringBuilder();
            using (var process = new Process { StartInfo = startInfo })
            {
                process.OutputDataReceived += (_, e) => Append(stdout, e.Data);
                process.ErrorDataReceived += (_, e) => Append(stderr, e.Data);
                // Wait out any process-listing pipes. Starting here while those handles are still
                // inheritable copies them into this child (Mono does not pass a handle list), and the
                // listing then never sees stdout EOF.
                ProcessSpawnGate.Run(() => process.Start());
                if (startInfo.RedirectStandardInput)
                {
                    process.StandardInput.Close();
                }

                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                if (!process.WaitForExit((int)Math.Min(int.MaxValue, Math.Max(0, timeout.TotalMilliseconds))))
                {
                    try
                    {
                        process.Kill();
                    }
                    catch (Exception)
                    {
                        // Already gone.
                    }

                    process.WaitForExit(5000);
                    return new BundledDotnetRunResult(null, Snapshot(stdout), Snapshot(stderr));
                }

                // The parameterless wait drains the asynchronous output readers.
                process.WaitForExit();
                return new BundledDotnetRunResult(process.ExitCode, Snapshot(stdout), Snapshot(stderr));
            }
        }

        private static void Append(StringBuilder builder, string? line)
        {
            if (line == null)
            {
                return;
            }

            lock (builder)
            {
                builder.AppendLine(line);
            }
        }

        private static string Snapshot(StringBuilder builder)
        {
            lock (builder)
            {
                return builder.ToString();
            }
        }
    }

    public sealed class BundledDotnetRunResult
    {
        public BundledDotnetRunResult(int? exitCode, string standardOutput, string standardError)
        {
            ExitCode = exitCode;
            StandardOutput = standardOutput;
            StandardError = standardError;
        }

        /// <summary>Null when the process was killed for exceeding its timeout.</summary>
        public int? ExitCode { get; }

        public string StandardOutput { get; }

        public string StandardError { get; }

        public bool TimedOut => !ExitCode.HasValue;
    }

    /// <summary>
    /// Stops a host or csc child from inheriting a process listing's redirected pipes.
    /// <see cref="Process.Start"/> with redirection creates inheritable handles. On Mono the next
    /// <c>CreateProcess</c> copies every inheritable handle, not only the new child's stdio, so the
    /// listing's stdout write end stays open after powershell exits and the read never sees EOF.
    /// A listing holds <see cref="EnterListing"/> until it has closed those pipes. Every other child
    /// start goes through <see cref="Run"/> and waits.
    /// </summary>
    internal static class ProcessSpawnGate
    {
        private static readonly object Gate = new object();
        private static int _listingPipes;

        /// <summary>Runs <paramref name="start"/> only while no listing pipes are open.</summary>
        public static void Run(Action start)
        {
            if (start == null)
            {
                throw new ArgumentNullException(nameof(start));
            }

            lock (Gate)
            {
                while (_listingPipes > 0)
                {
                    Monitor.Wait(Gate, 20);
                }

                start();
            }
        }

        /// <summary>
        /// Marks listing pipes as open so <see cref="Run"/> waits. Dispose only after those pipes are closed.
        /// </summary>
        public static IDisposable EnterListing()
        {
            lock (Gate)
            {
                while (_listingPipes > 0)
                {
                    Monitor.Wait(Gate, 20);
                }

                _listingPipes++;
            }

            return new Release();
        }

        private sealed class Release : IDisposable
        {
            private int _disposed;

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0)
                {
                    return;
                }

                lock (Gate)
                {
                    if (_listingPipes > 0)
                    {
                        _listingPipes--;
                    }

                    Monitor.PulseAll(Gate);
                }
            }
        }
    }
}
