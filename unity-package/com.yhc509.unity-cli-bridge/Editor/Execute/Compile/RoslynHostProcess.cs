#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using UnityCli.Protocol;

namespace UnityCliBridge.Bridge.Editor
{
    /// <summary>A started host the supervisor can talk to. <see cref="RoslynHostProcess"/> in production, fakes in tests.</summary>
    internal interface IRoslynHostInstance : IDisposable
    {
        int Pid { get; }

        RoslynHostHello Hello { get; }

        DateTime StartedUtc { get; }

        /// <summary>Spawn to hello, in milliseconds.</summary>
        double StartMs { get; }

        /// <summary>False once the process exited or the pipe closed.</summary>
        bool IsAlive { get; }

        RoslynHostMessage Request(RoslynHostMessage request, string expectedType, TimeSpan timeout);

        /// <summary>
        /// Sends <c>quit</c>, waits up to <paramref name="quitWait"/> for the exit, then kills. A zero
        /// wait kills at once and does not wait for the exit (the host has nothing to flush).
        /// </summary>
        void Stop(TimeSpan quitWait);

        void Kill();

        IReadOnlyList<string> StderrTail();
    }

    internal sealed class RoslynHostLaunchSpec
    {
        public RoslynHostLaunchSpec(
            string dotnetPath,
            string hostAssemblyPath,
            string bundledRuntimeDirectory,
            RoslynHostArguments arguments,
            TimeSpan helloTimeout)
        {
            DotnetPath = dotnetPath;
            HostAssemblyPath = hostAssemblyPath;
            BundledRuntimeDirectory = bundledRuntimeDirectory;
            Arguments = arguments;
            HelloTimeout = helloTimeout;
        }

        public string DotnetPath { get; }

        public string HostAssemblyPath { get; }

        /// <summary>The located NetCoreRuntime folder; hello must report a runtime under it (plan §3-5).</summary>
        public string BundledRuntimeDirectory { get; }

        public RoslynHostArguments Arguments { get; }

        public TimeSpan HelloTimeout { get; }

        /// <summary><c>exec &lt;host.dll&gt; --ucb-roslyn-host &lt;hash&gt; ...</c>; the marker is the first host argument.</summary>
        public List<string> BuildCommandLine()
        {
            var args = new List<string> { "exec", HostAssemblyPath };
            args.AddRange(Arguments.ToArgumentList());
            return args;
        }
    }

    /// <summary>
    /// The host child process: started on the bundled muxer only (<see cref="BundledDotnetProcess"/>:
    /// absolute path, no shell, scrubbed environment), framed over its raw stdin/stdout, stderr kept
    /// in a 64-line ring for doctor. <see cref="Start"/> returns only after a valid <c>hello</c> whose
    /// runtime sits under the bundled runtime; anything else kills the child and throws.
    /// Unity-free (plain <see cref="Process"/>), so it works the same under Mono and .NET.
    /// </summary>
    internal sealed class RoslynHostProcess : IRoslynHostInstance
    {
        public const int StderrTailLines = 64;

        private readonly Process _process;
        private readonly RoslynHostConnection _connection;
        private readonly Queue<string> _stderr = new Queue<string>();
        private RoslynHostHello? _hello;
        private int _stopped;

        private RoslynHostProcess(Process process, DateTime startedUtc)
        {
            _process = process;
            StartedUtc = startedUtc;
            Pid = process.Id;
            _connection = new RoslynHostConnection(process.StandardInput.BaseStream, process.StandardOutput.BaseStream);
        }

        public int Pid { get; }

        public RoslynHostHello Hello
        {
            get { return _hello ?? new RoslynHostHello(); }
        }

        public DateTime StartedUtc { get; }

        public double StartMs { get; private set; }

        public bool IsAlive
        {
            get
            {
                if (_connection.IsClosed)
                {
                    return false;
                }

                try
                {
                    return !_process.HasExited;
                }
                catch (Exception)
                {
                    return false;
                }
            }
        }

        /// <param name="onSpawned">Called right after the child exists (before hello), so a caller can record or kill it.</param>
        public static RoslynHostProcess Start(RoslynHostLaunchSpec spec, Action<RoslynHostProcess>? onSpawned = null)
        {
            if (spec == null)
            {
                throw new ArgumentNullException(nameof(spec));
            }

            ProcessStartInfo startInfo;
            try
            {
                startInfo = BundledDotnetProcess.CreateStartInfo(
                    spec.DotnetPath,
                    spec.BuildCommandLine(),
                    Path.GetDirectoryName(spec.HostAssemblyPath));
            }
            catch (Exception ex)
            {
                throw new RoslynHostException(RoslynHostFailureCodes.StartFailed, ex.Message);
            }

            var process = new Process { StartInfo = startInfo };
            Stopwatch spawn = Stopwatch.StartNew();
            try
            {
                if (!process.Start())
                {
                    throw new InvalidOperationException("Process.Start returned false");
                }
            }
            catch (Exception ex)
            {
                process.Dispose();
                throw new RoslynHostException(RoslynHostFailureCodes.StartFailed, "cannot start " + spec.DotnetPath + ": " + ex.Message);
            }

            var host = new RoslynHostProcess(process, DateTime.UtcNow);
            host.StartStderrPump();
            host._connection.Start("ucb-roslyn-host-reader-" + host.Pid);
            onSpawned?.Invoke(host);
            try
            {
                RoslynHostMessage first = host._connection.Receive(spec.HelloTimeout);
                if (!string.Equals(first.Type, RoslynHostMessageTypes.Hello, StringComparison.Ordinal))
                {
                    throw new RoslynHostException(RoslynHostFailureCodes.FrameError, "the host's first message was '" + first.Type + "', not hello");
                }

                RoslynHostHello hello = RoslynHostHello.FromMessage(first);
                if (hello.Protocol != RoslynHostHello.CurrentProtocol)
                {
                    throw new RoslynHostException(
                        RoslynHostFailureCodes.StartFailed,
                        "host pipe protocol " + hello.Protocol + ", expected " + RoslynHostHello.CurrentProtocol);
                }

                if (!hello.IsRuntimeUnder(spec.BundledRuntimeDirectory))
                {
                    throw new RoslynHostException(
                        RoslynHostFailureCodes.ForeignRuntime,
                        "the host runs on " + hello.RuntimeDirectory + " (" + hello.CoreLibraryPath + "), not under the bundled "
                        + spec.BundledRuntimeDirectory);
                }

                host._hello = hello;
                host.StartMs = spawn.Elapsed.TotalMilliseconds;
                return host;
            }
            catch (RoslynHostException ex)
            {
                host.Kill();
                string tail = host.LastStderrLine();
                throw new RoslynHostException(ex.Code, tail.Length == 0 ? ex.Message : ex.Message + " (stderr: " + tail + ")");
            }
            catch (Exception ex)
            {
                host.Kill();
                throw new RoslynHostException(RoslynHostFailureCodes.StartFailed, ex.GetType().Name + ": " + ex.Message);
            }
        }

        public RoslynHostMessage Request(RoslynHostMessage request, string expectedType, TimeSpan timeout)
        {
            try
            {
                return _connection.Request(request, expectedType, timeout);
            }
            catch (RoslynHostException ex) when (ex.Code == RoslynHostFailureCodes.Crashed)
            {
                throw new RoslynHostException(ex.Code, ex.Message + DescribeExit());
            }
        }

        public void Stop(TimeSpan quitWait)
        {
            if (Interlocked.Exchange(ref _stopped, 1) != 0)
            {
                return;
            }

            if (quitWait <= TimeSpan.Zero)
            {
                KillCore(waitForExit: false);
                Dispose();
                return;
            }

            try
            {
                if (IsAlive)
                {
                    _connection.Send(new RoslynHostMessage(RoslynHostMessageTypes.Quit));
                    if (_process.WaitForExit((int)Math.Max(0, Math.Min(int.MaxValue, quitWait.TotalMilliseconds))))
                    {
                        Dispose();
                        return;
                    }
                }
            }
            catch (Exception)
            {
                // Fall through to the kill.
            }

            KillCore();
            Dispose();
        }

        public void Kill()
        {
            Interlocked.Exchange(ref _stopped, 1);
            KillCore();
            Dispose();
        }

        public IReadOnlyList<string> StderrTail()
        {
            lock (_stderr)
            {
                return _stderr.ToArray();
            }
        }

        public void Dispose()
        {
            _connection.Dispose();
            try
            {
                _process.Dispose();
            }
            catch (Exception)
            {
                // Best effort.
            }
        }

        private void KillCore(bool waitForExit = true)
        {
            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill();
                }
            }
            catch (Exception)
            {
                // Already gone.
            }

            if (!waitForExit)
            {
                return;
            }

            try
            {
                _process.WaitForExit(2000);
            }
            catch (Exception)
            {
                // Best effort.
            }
        }

        private string DescribeExit()
        {
            try
            {
                if (_process.WaitForExit(200))
                {
                    return " (exit code " + _process.ExitCode + ")";
                }
            }
            catch (Exception)
            {
                // Unknown exit state.
            }

            return string.Empty;
        }

        private string LastStderrLine()
        {
            // Give the stderr pump a moment to drain what the dying host wrote.
            Thread.Sleep(50);
            lock (_stderr)
            {
                string last = string.Empty;
                foreach (string line in _stderr)
                {
                    last = line;
                }

                return last;
            }
        }

        private void StartStderrPump()
        {
            StreamReader reader = _process.StandardError;
            var thread = new Thread(() =>
            {
                try
                {
                    string? line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        lock (_stderr)
                        {
                            _stderr.Enqueue(line);
                            while (_stderr.Count > StderrTailLines)
                            {
                                _stderr.Dequeue();
                            }
                        }
                    }
                }
                catch (Exception)
                {
                    // The pipe closed with the process.
                }
            })
            {
                IsBackground = true,
                Name = "ucb-roslyn-host-stderr-" + Pid,
            };
            thread.Start();
        }
    }
}
