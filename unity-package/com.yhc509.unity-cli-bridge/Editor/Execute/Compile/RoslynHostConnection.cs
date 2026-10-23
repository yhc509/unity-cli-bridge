#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using UnityCli.Protocol;

namespace UnityCliBridge.Bridge.Editor
{
    /// <summary>Fallback reason codes (the prefix of <c>timings.compilerFallbackReason</c>).</summary>
    internal static class RoslynHostFailureCodes
    {
        public const string ToolchainMissing = "toolchain-missing";
        public const string BuildFailed = "build-failed";
        public const string StartFailed = "start-failed";
        public const string ForeignRuntime = "foreign-runtime";
        public const string NotReady = "host-not-ready";
        public const string Building = "host-building";
        public const string Crashed = "host-crashed";
        public const string Timeout = "timeout";
        public const string FrameError = "frame-error";
        public const string HostError = "host-error";
        public const string LoadFailed = "load-failed";
        public const string SessionDisabled = "session-disabled";
        public const string Stopped = "host-stopped";
    }

    /// <summary>An infrastructure failure of the host pipe or process. <see cref="Code"/> is a <see cref="RoslynHostFailureCodes"/> value.</summary>
    internal sealed class RoslynHostException : Exception
    {
        public RoslynHostException(string code, string message)
            : base(message)
        {
            Code = code;
        }

        public string Code { get; }

        /// <summary><c>code: message</c>, the form used for fallback reasons and doctor.</summary>
        public string Reason
        {
            get { return Code + ": " + Message; }
        }
    }

    /// <summary>
    /// Editor side of the host pipe: writes request frames to the host's stdin and reads reply frames
    /// from its stdout on a background thread, so a wait can time out instead of blocking forever.
    /// Requests are answered in order, one at a time (the bridge compiles on the main thread, so at
    /// most one is in flight). Unity-free; the tests run it against the real host loop in-process.
    /// </summary>
    internal sealed class RoslynHostConnection : IDisposable
    {
        private readonly Stream _toHost;
        private readonly Stream _fromHost;
        private readonly RoslynHostFrameLimits _limits;
        private readonly object _gate = new object();
        private readonly Queue<RoslynHostMessage> _inbox = new Queue<RoslynHostMessage>();
        private readonly object _writeGate = new object();
        private Thread? _reader;
        private string? _closedCode;
        private string? _closedReason;
        private int _disposed;

        public RoslynHostConnection(Stream toHost, Stream fromHost, RoslynHostFrameLimits? limits = null)
        {
            _toHost = toHost ?? throw new ArgumentNullException(nameof(toHost));
            _fromHost = fromHost ?? throw new ArgumentNullException(nameof(fromHost));
            _limits = limits ?? RoslynHostFrameLimits.Default;
        }

        public bool IsClosed
        {
            get
            {
                lock (_gate)
                {
                    return _closedReason != null;
                }
            }
        }

        /// <summary>Why the read side ended (EOF, frame error); null while open.</summary>
        public string? ClosedReason
        {
            get
            {
                lock (_gate)
                {
                    return _closedReason;
                }
            }
        }

        public void Start(string threadName)
        {
            if (_reader != null)
            {
                return;
            }

            _reader = new Thread(ReadLoop) { IsBackground = true, Name = threadName };
            _reader.Start();
        }

        public void Send(RoslynHostMessage message)
        {
            try
            {
                lock (_writeGate)
                {
                    RoslynHostFrameCodec.WriteMessage(_toHost, message, _limits);
                }
            }
            catch (RoslynHostFrameException ex)
            {
                throw new RoslynHostException(RoslynHostFailureCodes.FrameError, ex.Message);
            }
            catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException || ex is NotSupportedException)
            {
                throw new RoslynHostException(RoslynHostFailureCodes.Crashed, "cannot write to the host: " + ex.Message);
            }
        }

        /// <summary>Next message, or a <see cref="RoslynHostException"/> on timeout or a closed pipe.</summary>
        public RoslynHostMessage Receive(TimeSpan timeout)
        {
            long timeoutMs = (long)Math.Max(0, Math.Min(int.MaxValue, timeout.TotalMilliseconds));
            System.Diagnostics.Stopwatch waited = System.Diagnostics.Stopwatch.StartNew();
            lock (_gate)
            {
                while (true)
                {
                    if (_inbox.Count > 0)
                    {
                        return _inbox.Dequeue();
                    }

                    if (_closedReason != null)
                    {
                        throw new RoslynHostException(_closedCode ?? RoslynHostFailureCodes.Crashed, _closedReason);
                    }

                    long remaining = timeoutMs - waited.ElapsedMilliseconds;
                    if (remaining <= 0)
                    {
                        throw new RoslynHostException(
                            RoslynHostFailureCodes.Timeout,
                            "no reply from the host within " + (long)timeout.TotalMilliseconds + " ms");
                    }

                    Monitor.Wait(_gate, (int)Math.Min(int.MaxValue, remaining));
                }
            }
        }

        /// <summary>
        /// Sends <paramref name="request"/> and returns the reply of <paramref name="expectedType"/>.
        /// A host <c>error</c> reply becomes a <see cref="RoslynHostFailureCodes.HostError"/> exception.
        /// </summary>
        public RoslynHostMessage Request(RoslynHostMessage request, string expectedType, TimeSpan timeout)
        {
            Send(request);
            RoslynHostMessage reply = Receive(timeout);
            if (string.Equals(reply.Type, expectedType, StringComparison.Ordinal))
            {
                return reply;
            }

            if (string.Equals(reply.Type, RoslynHostMessageTypes.Error, StringComparison.Ordinal))
            {
                RoslynHostError error = RoslynHostError.FromMessage(reply);
                throw new RoslynHostException(
                    error.Fatal ? RoslynHostFailureCodes.FrameError : RoslynHostFailureCodes.HostError,
                    error.Message);
            }

            throw new RoslynHostException(
                RoslynHostFailureCodes.FrameError,
                "expected '" + expectedType + "' from the host, got '" + reply.Type + "'");
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            Close(RoslynHostFailureCodes.Stopped, "connection closed");
            TryDispose(_toHost);
            TryDispose(_fromHost);
        }

        private void ReadLoop()
        {
            try
            {
                while (true)
                {
                    RoslynHostMessage? message = RoslynHostFrameCodec.ReadMessage(_fromHost, _limits);
                    if (message == null)
                    {
                        Close(RoslynHostFailureCodes.Crashed, "the host closed its output (process exited)");
                        return;
                    }

                    lock (_gate)
                    {
                        _inbox.Enqueue(message);
                        Monitor.PulseAll(_gate);
                    }
                }
            }
            catch (RoslynHostFrameException ex)
            {
                Close(RoslynHostFailureCodes.FrameError, ex.Message);
            }
            catch (Exception ex)
            {
                Close(RoslynHostFailureCodes.Crashed, "reading from the host failed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private void Close(string code, string reason)
        {
            lock (_gate)
            {
                if (_closedReason == null)
                {
                    _closedCode = code;
                    _closedReason = reason;
                }

                Monitor.PulseAll(_gate);
            }
        }

        private static void TryDispose(Stream stream)
        {
            try
            {
                stream.Dispose();
            }
            catch (Exception)
            {
                // Best effort; the process is going away.
            }
        }
    }
}
