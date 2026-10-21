using System.Diagnostics;
using System.Text;

namespace UnityCli.Cli.Services;

/// <summary>
/// Reads one UTF-8 IPC response line and splits the time until the first byte from the time to
/// finish the line. Used only when <see cref="CliTiming"/> is on; the idle path keeps
/// <see cref="StreamReader.ReadLineAsync(CancellationToken)"/>.
/// </summary>
internal static class IpcResponseLineReader
{
    internal readonly struct Result
    {
        public Result(string line, double waitMilliseconds, double receiveMilliseconds)
        {
            Line = line;
            WaitMilliseconds = waitMilliseconds;
            ReceiveMilliseconds = receiveMilliseconds;
        }

        public string Line { get; }
        public double WaitMilliseconds { get; }
        public double ReceiveMilliseconds { get; }
    }

    public static async Task<Result> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        var first = new byte[1];
        var wait = Stopwatch.StartNew();
        int read = await stream.ReadAsync(first.AsMemory(0, 1), cancellationToken).ConfigureAwait(false);
        wait.Stop();
        double waitMs = wait.Elapsed.TotalMilliseconds;
        if (read == 0)
        {
            return new Result(string.Empty, waitMs, 0);
        }

        var receive = Stopwatch.StartNew();
        using var buffer = new MemoryStream();
        if (first[0] != (byte)'\n')
        {
            buffer.WriteByte(first[0]);
            var chunk = new byte[4096];
            while (true)
            {
                int count = await stream.ReadAsync(chunk.AsMemory(), cancellationToken).ConfigureAwait(false);
                if (count == 0)
                {
                    break;
                }

                int newline = Array.IndexOf(chunk, (byte)'\n', 0, count);
                if (newline >= 0)
                {
                    if (newline > 0)
                    {
                        buffer.Write(chunk, 0, newline);
                    }

                    break;
                }

                buffer.Write(chunk, 0, count);
            }

            if (buffer.Length > 0 && buffer.GetBuffer()[buffer.Length - 1] == (byte)'\r')
            {
                buffer.SetLength(buffer.Length - 1);
            }
        }

        receive.Stop();
        string line = Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
        return new Result(line, waitMs, receive.Elapsed.TotalMilliseconds);
    }
}
