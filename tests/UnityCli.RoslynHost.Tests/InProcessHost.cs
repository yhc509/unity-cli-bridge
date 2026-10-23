using System.Collections.Concurrent;
using UnityCli.Protocol;
using UnityCliBridge.Bridge.Editor;
using UnityCliBridge.RoslynHost;

namespace UnityCli.RoslynHost.Tests;

/// <summary>A one-way blocking byte pipe: writes on one side, blocking reads on the other, EOF after <see cref="CloseWriter"/>.</summary>
internal sealed class BlockingPipe
{
    private readonly BlockingCollection<byte[]> _chunks = new();
    private byte[] _current = Array.Empty<byte>();
    private int _offset;

    public BlockingPipe()
    {
        Writer = new WriterStream(this);
        Reader = new ReaderStream(this);
    }

    public Stream Writer { get; }

    public Stream Reader { get; }

    public void CloseWriter()
    {
        if (!_chunks.IsAddingCompleted)
        {
            _chunks.CompleteAdding();
        }
    }

    private void Write(byte[] buffer, int offset, int count)
    {
        if (_chunks.IsAddingCompleted)
        {
            throw new IOException("pipe closed");
        }

        var copy = new byte[count];
        Buffer.BlockCopy(buffer, offset, copy, 0, count);
        try
        {
            _chunks.Add(copy);
        }
        catch (InvalidOperationException)
        {
            throw new IOException("pipe closed");
        }
    }

    private int Read(byte[] buffer, int offset, int count)
    {
        while (_offset >= _current.Length)
        {
            if (!_chunks.TryTake(out byte[]? next, Timeout.Infinite))
            {
                return 0;
            }

            _current = next;
            _offset = 0;
        }

        int n = Math.Min(count, _current.Length - _offset);
        Buffer.BlockCopy(_current, _offset, buffer, offset, n);
        _offset += n;
        return n;
    }

    private sealed class WriterStream : Stream
    {
        private readonly BlockingPipe _pipe;

        public WriterStream(BlockingPipe pipe) => _pipe = pipe;

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => _pipe.Write(buffer, offset, count);

        protected override void Dispose(bool disposing)
        {
            _pipe.CloseWriter();
            base.Dispose(disposing);
        }
    }

    private sealed class ReaderStream : Stream
    {
        private readonly BlockingPipe _pipe;

        public ReaderStream(BlockingPipe pipe) => _pipe = pipe;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => _pipe.Read(buffer, offset, count);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

/// <summary>
/// The real host loop (<see cref="RoslynHostServer"/>, real Roslyn) on a thread, wired to a
/// <see cref="RoslynHostConnection"/> through two blocking pipes: the editor half end to end without a process.
/// </summary>
internal sealed class InProcessHostInstance : IRoslynHostInstance
{
    private static int _nextPid = 70000;
    private readonly BlockingPipe _toHost = new();
    private readonly BlockingPipe _fromHost = new();
    private readonly RoslynHostConnection _connection;
    private readonly Thread _server;
    private int _killed;

    public InProcessHostInstance(bool prewarm = false, string marker = "abc123def456")
    {
        Pid = Interlocked.Increment(ref _nextPid);
        StartedUtc = DateTime.UtcNow;
        var log = new StringWriter();
        _server = new Thread(() =>
        {
            var server = new RoslynHostServer(_toHost.Reader, _fromHost.Writer, log, marker, prewarm);
            try
            {
                ExitReason = server.Run();
            }
            finally
            {
                _fromHost.CloseWriter();
            }
        })
        { IsBackground = true };
        _server.Start();
        _connection = new RoslynHostConnection(_toHost.Writer, _fromHost.Reader);
        _connection.Start("test-reader");
        Hello = RoslynHostHello.FromMessage(_connection.Receive(TimeSpan.FromSeconds(30)));
    }

    public RoslynHostExitReason? ExitReason { get; private set; }

    public int Pid { get; }

    public RoslynHostHello Hello { get; }

    public DateTime StartedUtc { get; }

    public double StartMs => 1;

    public bool IsAlive => Volatile.Read(ref _killed) == 0 && !_connection.IsClosed;

    public int Requests { get; private set; }

    public List<string> RequestTypes { get; } = new();

    public RoslynHostMessage Request(RoslynHostMessage request, string expectedType, TimeSpan timeout)
    {
        Requests++;
        RequestTypes.Add(request.Type);
        return _connection.Request(request, expectedType, timeout);
    }

    public bool WaitForServerExit(TimeSpan timeout) => _server.Join(timeout);

    public void Stop(TimeSpan quitWait)
    {
        try
        {
            _connection.Send(new RoslynHostMessage(RoslynHostMessageTypes.Quit));
            _server.Join(quitWait);
        }
        catch (RoslynHostException)
        {
        }

        Kill();
    }

    public void Kill()
    {
        Interlocked.Exchange(ref _killed, 1);
        _toHost.CloseWriter();
        _connection.Dispose();
    }

    public IReadOnlyList<string> StderrTail() => new[] { "in-process host " + Pid };

    public void Dispose() => Kill();
}

/// <summary>Scriptable stand-in for a host, for failure paths the real loop never takes.</summary>
internal sealed class ScriptedHostInstance : IRoslynHostInstance
{
    private static int _nextPid = 80000;
    private readonly Func<RoslynHostMessage, RoslynHostMessage> _reply;

    public ScriptedHostInstance(Func<RoslynHostMessage, RoslynHostMessage> reply)
    {
        _reply = reply;
        Pid = Interlocked.Increment(ref _nextPid);
    }

    public int Pid { get; }

    public RoslynHostHello Hello { get; } = new() { PrewarmMs = 10, RuntimeVersion = "6.0.21", RoslynVersion = "4.3.1" };

    public DateTime StartedUtc { get; } = DateTime.UtcNow;

    public double StartMs => 2;

    public bool Alive { get; set; } = true;

    public bool IsAlive => Alive;

    public int Kills { get; private set; }

    public int Stops { get; private set; }

    public List<string> RequestTypes { get; } = new();

    public RoslynHostMessage Request(RoslynHostMessage request, string expectedType, TimeSpan timeout)
    {
        RequestTypes.Add(request.Type);
        return _reply(request);
    }

    public List<TimeSpan> QuitWaits { get; } = new();

    public void Stop(TimeSpan quitWait)
    {
        Stops++;
        QuitWaits.Add(quitWait);
        Alive = false;
    }

    public void Kill()
    {
        Kills++;
        Alive = false;
    }

    public IReadOnlyList<string> StderrTail() => new[] { "scripted " + Pid };

    public void Dispose()
    {
    }

    /// <summary>Answers setRefs with an ack and compile with whatever <paramref name="compile"/> returns.</summary>
    public static Func<RoslynHostMessage, RoslynHostMessage> Replies(Func<RoslynHostCompileRequest, RoslynHostMessage> compile)
    {
        return message =>
        {
            if (message.Type == RoslynHostMessageTypes.SetRefs)
            {
                RoslynHostSetRefs set = RoslynHostSetRefs.FromMessage(message);
                return new RoslynHostRefsSet { RefsHash = set.RefsHash, Count = set.Paths.Count, PrewarmMs = 5 }.ToMessage();
            }

            return compile(RoslynHostCompileRequest.FromMessage(message));
        };
    }
}

internal sealed class FakeLauncher : IRoslynHostLauncher
{
    private readonly Func<int, IRoslynHostInstance> _create;

    public FakeLauncher(Func<int, IRoslynHostInstance> create) => _create = create;

    public int Launches { get; private set; }

    public List<bool> PrewarmFlags { get; } = new();

    public ManualResetEventSlim? Gate { get; set; }

    /// <summary>Held before the process is spawned, so the supervisor stays in <c>building</c>.</summary>
    public ManualResetEventSlim? BeforeSpawn { get; set; }

    private RoslynHostImageDisposition _imageDisposition = RoslynHostImageDisposition.Ready;

    /// <summary>Held inside the image probe, like a slow disk read of the host cache.</summary>
    public ManualResetEventSlim? ProbeGate { get; set; }

    public RoslynHostImageDisposition ImageDisposition
    {
        get
        {
            ProbeGate?.Wait(TimeSpan.FromSeconds(30));
            return _imageDisposition;
        }
        set => _imageDisposition = value;
    }

    public RoslynHostLaunch Launch(bool prewarm, Action<IRoslynHostInstance> onSpawned)
    {
        BeforeSpawn?.Wait(TimeSpan.FromSeconds(30));
        int attempt = ++Launches;
        PrewarmFlags.Add(prewarm);
        IRoslynHostInstance instance = _create(attempt);
        onSpawned(instance);
        Gate?.Wait(TimeSpan.FromSeconds(30));
        return new RoslynHostLaunch(instance, "test-key", attempt > 1, attempt > 1 ? 0 : 123);
    }
}

internal sealed class MutableClock
{
    public DateTime Now { get; set; } = new(2026, 10, 22, 0, 0, 0, DateTimeKind.Utc);
}
