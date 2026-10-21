using System.Text;
using UnityCli.Cli.Services;

namespace UnityCli.Cli.Tests;

public sealed class IpcResponseLineReaderTests
{
    [Fact]
    public async Task ReadAsync_PreservesKoreanTextAndStripsCarriageReturn()
    {
        byte[] payload = Encoding.UTF8.GetBytes("{\"message\":\"한글 프로젝트\"}\r\n");
        await using var stream = new MemoryStream(payload);

        IpcResponseLineReader.Result result = await IpcResponseLineReader.ReadAsync(stream, CancellationToken.None);

        Assert.Equal("{\"message\":\"한글 프로젝트\"}", result.Line);
        Assert.True(result.WaitMilliseconds >= 0);
        Assert.True(result.ReceiveMilliseconds >= 0);
    }

    [Fact]
    public async Task ReadAsync_StopsAtTheFirstNewline()
    {
        byte[] payload = Encoding.UTF8.GetBytes("abc\nEXTRA");
        await using var stream = new MemoryStream(payload);

        IpcResponseLineReader.Result result = await IpcResponseLineReader.ReadAsync(stream, CancellationToken.None);

        Assert.Equal("abc", result.Line);
    }

    [Fact]
    public async Task ReadAsync_ReadsTheFirstByteSeparatelyFromTheRest()
    {
        var stream = new CountingStream(Encoding.UTF8.GetBytes("hello\n"));

        IpcResponseLineReader.Result result = await IpcResponseLineReader.ReadAsync(stream, CancellationToken.None);

        Assert.Equal("hello", result.Line);
        Assert.Equal(2, stream.ReadCount);
        Assert.Equal(1, stream.FirstReadCount);
    }

    private sealed class CountingStream : Stream
    {
        private readonly byte[] _data;
        private int _position;

        public CountingStream(byte[] data)
        {
            _data = data;
        }

        public int ReadCount { get; private set; }
        public int FirstReadCount { get; private set; }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _data.Length;
        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            return Read(buffer.AsSpan(offset, count));
        }

        public override int Read(Span<byte> buffer)
        {
            ReadCount++;
            if (_position >= _data.Length || buffer.Length == 0)
            {
                return 0;
            }

            int take = ReadCount == 1 ? 1 : Math.Min(buffer.Length, _data.Length - _position);
            _data.AsSpan(_position, take).CopyTo(buffer);
            _position += take;
            if (ReadCount == 1)
            {
                FirstReadCount = take;
            }

            return take;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
