using System.Diagnostics;
using System.Text;
using UnityCli.Protocol;

namespace UnityCli.Cli.Tests;

public class BoundedRequestReaderTests
{
    private const int ShortTimeoutMs = 250;

    [Fact]
    public void Constants_pin_the_documented_budgets()
    {
        Assert.Equal(32 * 1024 * 1024, BoundedRequestReader.MaxRequestBytes);
        Assert.Equal(30_000, BoundedRequestReader.ReadTimeoutMs);
    }

    [Fact]
    public async Task Reads_a_line_under_the_cap()
    {
        using var stream = StreamOf("{\"command\":\"ping\"}\n");

        var result = await BoundedRequestReader.ReadLineAsync(stream, 1024, ShortTimeoutMs, CancellationToken.None);

        Assert.Equal(BoundedRequestReadStatus.Success, result.Status);
        Assert.True(result.IsSuccess);
        Assert.Equal("{\"command\":\"ping\"}", result.Line);
        Assert.Equal(19, result.BytesRead);
    }

    [Fact]
    public async Task Reads_a_line_of_exactly_the_cap()
    {
        var line = new string('a', 64);
        using var stream = StreamOf(line + "\n");

        var result = await BoundedRequestReader.ReadLineAsync(stream, 64, ShortTimeoutMs, CancellationToken.None);

        Assert.Equal(BoundedRequestReadStatus.Success, result.Status);
        Assert.Equal(line, result.Line);
    }

    [Fact]
    public async Task Reads_a_line_of_exactly_the_cap_with_crlf()
    {
        var line = new string('a', 64);
        using var stream = StreamOf(line + "\r\n");

        var result = await BoundedRequestReader.ReadLineAsync(stream, 64, ShortTimeoutMs, CancellationToken.None);

        Assert.Equal(BoundedRequestReadStatus.Success, result.Status);
        Assert.Equal(line, result.Line);
    }

    [Fact]
    public async Task Rejects_a_line_one_byte_over_the_cap()
    {
        using var stream = StreamOf(new string('a', 65) + "\n");

        var result = await BoundedRequestReader.ReadLineAsync(stream, 64, ShortTimeoutMs, CancellationToken.None);

        Assert.Equal(BoundedRequestReadStatus.ExceededSize, result.Status);
        Assert.Null(result.Line);
    }

    [Fact]
    public async Task Rejects_a_stream_that_never_sends_a_newline()
    {
        // No terminator anywhere: the cap has to stop the accumulation, not EOF.
        using var stream = StreamOf(new string('a', 4096));

        var result = await BoundedRequestReader.ReadLineAsync(stream, 64, ShortTimeoutMs, CancellationToken.None);

        Assert.Equal(BoundedRequestReadStatus.ExceededSize, result.Status);
    }

    [Fact]
    public async Task Accepts_a_crlf_terminator_like_a_bare_lf()
    {
        using var stream = StreamOf("{\"command\":\"ping\"}\r\n");

        var result = await BoundedRequestReader.ReadLineAsync(stream, 1024, ShortTimeoutMs, CancellationToken.None);

        Assert.Equal(BoundedRequestReadStatus.Success, result.Status);
        Assert.Equal("{\"command\":\"ping\"}", result.Line);
    }

    [Fact]
    public async Task Counts_multibyte_utf8_in_bytes_not_chars()
    {
        // Two Hangul syllables are 2 chars but 6 UTF-8 bytes.
        var payload = "한글";
        Assert.Equal(2, payload.Length);
        Assert.Equal(6, Encoding.UTF8.GetByteCount(payload));

        using var underCap = StreamOf(payload + "\n");
        var accepted = await BoundedRequestReader.ReadLineAsync(underCap, 6, ShortTimeoutMs, CancellationToken.None);
        Assert.Equal(BoundedRequestReadStatus.Success, accepted.Status);
        Assert.Equal(payload, accepted.Line);

        using var overCap = StreamOf(payload + "\n");
        var rejected = await BoundedRequestReader.ReadLineAsync(overCap, 5, ShortTimeoutMs, CancellationToken.None);
        Assert.Equal(BoundedRequestReadStatus.ExceededSize, rejected.Status);
    }

    [Fact]
    public async Task Preserves_multibyte_utf8_split_across_chunks()
    {
        // Chunk boundaries must not corrupt a multi-byte sequence: the reader
        // decodes once, after the whole line has been accumulated.
        var payload = string.Concat(Enumerable.Repeat("가나다라", 4096));
        using var stream = new ChunkedStream(Encoding.UTF8.GetBytes(payload + "\n"), chunkSize: 7);

        var result = await BoundedRequestReader.ReadLineAsync(stream, 1024 * 1024, 5_000, CancellationToken.None);

        Assert.Equal(BoundedRequestReadStatus.Success, result.Status);
        Assert.Equal(payload, result.Line);
    }

    [Fact]
    public async Task Times_out_when_the_client_connects_and_stays_silent()
    {
        using var stream = new SilentStream();

        var result = await BoundedRequestReader.ReadLineAsync(stream, 1024, ShortTimeoutMs, CancellationToken.None);

        Assert.Equal(BoundedRequestReadStatus.TimedOut, result.Status);
        Assert.Equal(0, result.BytesRead);
    }

    [Fact]
    public async Task Times_out_when_the_client_stalls_partway_through_a_line()
    {
        using var stream = new ChunkedStream(Encoding.UTF8.GetBytes("{\"command\":"), chunkSize: 1024, stallAtEnd: true);

        var result = await BoundedRequestReader.ReadLineAsync(stream, 1024, ShortTimeoutMs, CancellationToken.None);

        Assert.Equal(BoundedRequestReadStatus.TimedOut, result.Status);
        Assert.Equal(11, result.BytesRead);
    }

    [Fact]
    public async Task Reports_closed_when_the_stream_ends_with_no_data()
    {
        using var stream = StreamOf(string.Empty);

        var result = await BoundedRequestReader.ReadLineAsync(stream, 1024, ShortTimeoutMs, CancellationToken.None);

        Assert.Equal(BoundedRequestReadStatus.Closed, result.Status);
        Assert.Null(result.Line);
        Assert.Equal(0, result.BytesRead);
    }

    [Fact]
    public async Task Reports_closed_when_the_stream_ends_mid_line()
    {
        using var stream = StreamOf("{\"command\":\"pi");

        var result = await BoundedRequestReader.ReadLineAsync(stream, 1024, ShortTimeoutMs, CancellationToken.None);

        Assert.Equal(BoundedRequestReadStatus.Closed, result.Status);
        Assert.Null(result.Line);
        Assert.Equal(14, result.BytesRead);
    }

    [Fact]
    public async Task Reports_closed_when_cancelled_before_any_data()
    {
        using var stream = new SilentStream();
        using var cancellation = new CancellationTokenSource();
        cancellation.CancelAfter(50);

        var result = await BoundedRequestReader.ReadLineAsync(stream, 1024, 10_000, cancellation.Token);

        Assert.Equal(BoundedRequestReadStatus.Closed, result.Status);
    }

    [Fact]
    public async Task Rejects_invalid_budgets()
    {
        using var stream = StreamOf("x\n");

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => BoundedRequestReader.ReadLineAsync(stream, 0, ShortTimeoutMs, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => BoundedRequestReader.ReadLineAsync(stream, 1024, 0, CancellationToken.None));
    }

    [Fact]
    public async Task Rejects_a_line_that_outgrows_the_cap_across_several_chunks()
    {
        // One byte per read, so the cap has to fire on the running total rather
        // than on a single oversized chunk. A single-read stream takes a
        // different branch and would not cover this one.
        using var stream = new ChunkedStream(Encoding.UTF8.GetBytes(new string('a', 200) + "\n"), chunkSize: 1);

        var result = await BoundedRequestReader.ReadLineAsync(stream, 64, 5_000, CancellationToken.None);

        Assert.Equal(BoundedRequestReadStatus.ExceededSize, result.Status);
        Assert.Equal(66, result.BytesRead);
    }

    [Fact]
    public async Task Accepts_a_crlf_terminator_split_across_chunks()
    {
        // The '\r' ends one chunk and the '\n' opens the next, so the terminator
        // is only recognisable once the carriage return is already accumulated.
        var line = new string('a', 8);
        using var stream = new ChunkedStream(Encoding.UTF8.GetBytes(line + "\r\n"), chunkSize: 9);

        var result = await BoundedRequestReader.ReadLineAsync(stream, 64, 5_000, CancellationToken.None);

        Assert.Equal(BoundedRequestReadStatus.Success, result.Status);
        Assert.Equal(line, result.Line);
        Assert.Equal(10, result.BytesRead);
    }

    [Fact]
    public async Task Waits_out_the_full_deadline_before_reporting_a_timeout()
    {
        using var stream = new SilentStream();
        var stopwatch = Stopwatch.StartNew();

        var result = await BoundedRequestReader.ReadLineAsync(stream, 1024, ShortTimeoutMs, CancellationToken.None);

        stopwatch.Stop();
        Assert.Equal(BoundedRequestReadStatus.TimedOut, result.Status);
        Assert.True(
            stopwatch.ElapsedMilliseconds >= ShortTimeoutMs - 50,
            $"timed out after {stopwatch.ElapsedMilliseconds} ms, which is too early to have waited the {ShortTimeoutMs} ms budget");
    }

    [Fact]
    public async Task Observes_the_abandoned_read_when_cancellation_ends_the_wait()
    {
        // Cancellation abandons the pending read exactly as a timeout does, so it
        // needs the same "observe the fault" handling. Without it the read faults
        // on stream disposal with nobody watching, and the Editor gets an
        // UnobservedTaskException on the finalizer thread every time it shuts
        // down with a connection mid-read.
        var unobserved = new List<Exception>();

        void OnUnobserved(object? sender, UnobservedTaskExceptionEventArgs args)
        {
            // Only count our own marker: other tests share this process-wide event.
            if (args.Exception.Flatten().InnerExceptions.Any(inner => inner is AbandonedReadMarkerException))
            {
                lock (unobserved)
                {
                    unobserved.Add(args.Exception);
                }
            }

            args.SetObserved();
        }

        TaskScheduler.UnobservedTaskException += OnUnobserved;
        try
        {
            for (var attempt = 0; attempt < 20; attempt++)
            {
                var stream = new FaultOnDisposeStream();
                using var cancellation = new CancellationTokenSource();
                cancellation.CancelAfter(20);

                var result = await BoundedRequestReader.ReadLineAsync(stream, 1024, 10_000, cancellation.Token);
                Assert.Equal(BoundedRequestReadStatus.Closed, result.Status);

                // Faults the read the reader walked away from, the way disposing a
                // NetworkStream does in BridgeHost's `using (stream)`.
                stream.Dispose();
            }

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= OnUnobserved;
        }

        lock (unobserved)
        {
            Assert.Empty(unobserved);
        }
    }

    private static MemoryStream StreamOf(string content)
    {
        return new MemoryStream(Encoding.UTF8.GetBytes(content), writable: false);
    }

    /// <summary>Hands out the payload in fixed-size slices, then either ends or stalls forever.</summary>
    private sealed class ChunkedStream : Stream
    {
        private readonly byte[] _payload;
        private readonly int _chunkSize;
        private readonly bool _stallAtEnd;
        private int _position;

        public ChunkedStream(byte[] payload, int chunkSize, bool stallAtEnd = false)
        {
            _payload = payload;
            _chunkSize = chunkSize;
            _stallAtEnd = stallAtEnd;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            int remaining = _payload.Length - _position;
            if (remaining <= 0)
            {
                return _stallAtEnd
                    ? SilentStream.NeverCompletes(cancellationToken)
                    : Task.FromResult(0);
            }

            int take = Math.Min(Math.Min(count, _chunkSize), remaining);
            Array.Copy(_payload, _position, buffer, offset, take);
            _position += take;
            return Task.FromResult(take);
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>A connected peer that never writes and never closes.</summary>
    private sealed class SilentStream : Stream
    {
        public static Task<int> NeverCompletes(CancellationToken cancellationToken)
        {
            var completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (cancellationToken.CanBeCanceled)
            {
                cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
            }

            return completion.Task;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => NeverCompletes(cancellationToken);

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>
    /// A peer whose read stays pending until the stream is disposed, then faults.
    /// Deliberately ignores the cancellation token: the runtimes this reader is
    /// compiled for do not reliably honour one mid-read, which is the whole
    /// reason the reader races a timer instead of trusting the token.
    /// </summary>
    private sealed class FaultOnDisposeStream : Stream
    {
        private readonly TaskCompletionSource<int> _pending =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => _pending.Task;

        protected override void Dispose(bool disposing)
        {
            _pending.TrySetException(new AbandonedReadMarkerException());
            base.Dispose(disposing);
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>Marks the fault raised by <see cref="FaultOnDisposeStream"/> so the
    /// process-wide unobserved-exception handler ignores every other test's noise.</summary>
    private sealed class AbandonedReadMarkerException : Exception
    {
    }
}
