using System.IO.Pipes;
using System.Net.Sockets;
using UnityCli.Protocol;

namespace UnityCli.Cli.Services;

/// <summary>
/// Whether a registry row is a running Editor for multi-project disambiguation.
/// A non-offline <c>state</c> is not enough: after <c>kill -9</c> the row stays fresh until the
/// heartbeat goes stale, while the process and the pipe or socket are already gone.
/// </summary>
internal static class EditorReachability
{
    private const int ConnectTimeoutMilliseconds = 50;

    public static bool IsReachable(InstanceRecord instance)
    {
        if (string.Equals(instance.state, "offline", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (instance.editorProcessId > 0 && EditorLauncher.IsProcessAlive(instance.editorProcessId))
        {
            return true;
        }

        return IsEndpointReachable(instance.pipeName);
    }

    private static bool IsEndpointReachable(string? endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            return false;
        }

        return OperatingSystem.IsWindows()
            ? IsNamedPipeReachable(endpoint)
            : IsUnixSocketReachable(endpoint);
    }

    private static bool IsNamedPipeReachable(string pipeName)
    {
        try
        {
            using var stream = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            stream.Connect(ConnectTimeoutMilliseconds);
            return stream.IsConnected;
        }
        catch (Exception exception) when (exception is TimeoutException
            or IOException
            or UnauthorizedAccessException
            or InvalidOperationException
            or PlatformNotSupportedException)
        {
            return false;
        }
    }

    private static bool IsUnixSocketReachable(string path)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            socket.Blocking = false;
            socket.Connect(new UnixDomainSocketEndPoint(path));
            return true;
        }
        catch (SocketException exception) when (IsConnectionInProgress(exception))
        {
            if (!socket.Poll(ConnectTimeoutMilliseconds * 1000, SelectMode.SelectWrite))
            {
                return false;
            }

            object? errorValue = socket.GetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Error);
            return errorValue is int error && error == 0;
        }
        catch (Exception exception) when (exception is SocketException or IOException)
        {
            return false;
        }
    }

    private static bool IsConnectionInProgress(SocketException exception)
    {
        return exception.SocketErrorCode == SocketError.WouldBlock
            || exception.SocketErrorCode == SocketError.InProgress
            || exception.SocketErrorCode == SocketError.AlreadyInProgress;
    }
}
