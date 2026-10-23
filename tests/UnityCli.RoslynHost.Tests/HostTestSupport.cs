using System.Diagnostics;
using System.Runtime.InteropServices;
using UnityCli.Protocol;
using UnityCliBridge.RoslynHost;

namespace UnityCli.RoslynHost.Tests;

internal static class HostTestSupport
{
    private static readonly string[] FrameworkNames =
    {
        "System.Private.CoreLib.dll",
        "System.Runtime.dll",
        "System.Collections.dll",
        "System.Linq.dll",
        "System.Console.dll",
        "netstandard.dll",
    };

    /// <summary>A small reference set from the test's own runtime, standing in for the editor's Mono references.</summary>
    public static List<string> FrameworkReferences()
    {
        string directory = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        return FrameworkNames.Select(name => Path.Combine(directory, name)).Where(File.Exists).ToList();
    }

    public static string PackageRoot()
    {
        string? directory = AppContext.BaseDirectory;
        while (directory != null)
        {
            string candidate = Path.Combine(directory, "unity-package", "com.yhc509.unity-cli-bridge");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            directory = Path.GetDirectoryName(directory);
        }

        throw new InvalidOperationException("Package root not found above " + AppContext.BaseDirectory);
    }

    public static byte[] Encode(params RoslynHostMessage[] messages)
    {
        using var stream = new MemoryStream();
        foreach (RoslynHostMessage message in messages)
        {
            RoslynHostFrameCodec.WriteMessage(stream, message);
        }

        return stream.ToArray();
    }

    public static List<RoslynHostMessage> DecodeAll(byte[] bytes)
    {
        var result = new List<RoslynHostMessage>();
        using var stream = new MemoryStream(bytes);
        while (RoslynHostFrameCodec.ReadMessage(stream) is { } message)
        {
            result.Add(message);
        }

        return result;
    }

    public static ServerRun RunServer(byte[] input, bool prewarm = false)
    {
        using var inputStream = new MemoryStream(input);
        using var output = new MemoryStream();
        using var log = new StringWriter();
        var server = new RoslynHostServer(inputStream, output, log, "abc123def456", prewarm);
        RoslynHostExitReason reason = server.Run();
        return new ServerRun(reason, DecodeAll(output.ToArray()), log.ToString(), server);
    }

    public static RoslynHostMessage SetRefs(string hash, IEnumerable<string> paths)
    {
        return new RoslynHostSetRefs { RefsHash = hash, Paths = paths.ToList() }.ToMessage();
    }

    public static RoslynHostMessage Compile(string id, string source, string refsHash = "", Action<RoslynHostCompileRequest>? configure = null)
    {
        var request = new RoslynHostCompileRequest { Id = id, Source = source, RefsHash = refsHash, AssemblyName = "t_" + id };
        configure?.Invoke(request);
        return request.ToMessage();
    }

    public static RoslynHostMessage Simple(string type)
    {
        return new RoslynHostMessage(type);
    }

    /// <summary>A process that stays alive until killed; stands in for the editor in parent-watch tests.</summary>
    public static Process StartSleeper()
    {
        ProcessStartInfo startInfo = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? new ProcessStartInfo("ping", "-n 120 127.0.0.1")
            : new ProcessStartInfo("/bin/sleep", "120");
        startInfo.UseShellExecute = false;
        startInfo.CreateNoWindow = true;
        startInfo.RedirectStandardOutput = true;
        return Process.Start(startInfo)!;
    }

    public static void KillAndReap(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill();
            }
        }
        catch (InvalidOperationException)
        {
        }

        process.WaitForExit(10000);
    }

    internal sealed record ServerRun(RoslynHostExitReason Reason, List<RoslynHostMessage> Messages, string Log, RoslynHostServer Server);
}

/// <summary>Returns at most one byte per Read call, to exercise partial reads.</summary>
internal sealed class TrickleStream : Stream
{
    private readonly MemoryStream _inner;

    public TrickleStream(byte[] bytes)
    {
        _inner = new MemoryStream(bytes);
    }

    public int ReadCalls { get; private set; }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ReadCalls++;
        return _inner.Read(buffer, offset, Math.Min(1, count));
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
