#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
#if UNITY_EDITOR_WIN
using System.Security.AccessControl;
using System.Security.Principal;
#endif
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using UnityCli.Protocol;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace UnityCliBridge.Bridge.Editor
{
    [InitializeOnLoad]
    internal static class BridgeBootstrap
    {
        private static readonly BridgeHost? _host;

        static BridgeBootstrap()
        {
            if (BridgeDisableSwitch.IsDisabled(
                    Environment.GetEnvironmentVariable(BridgeDisableSwitch.EnvironmentVariable),
                    Environment.GetCommandLineArgs()))
            {
                // Construct nothing: a disabled bridge must not take the session locks, publish a
                // registry entry or a token sidecar, or join the editor update loop. One line so a
                // CI job that set the switch by accident can see why the CLI cannot reach it.
                UnityEngine.Debug.Log(
                    "[unity-cli-bridge] Bridge disabled via "
                    + BridgeDisableSwitch.EnvironmentVariable
                    + " / "
                    + BridgeDisableSwitch.CommandLineFlag
                    + " — CLI commands will not reach this editor.");
                return;
            }

            _host = new BridgeHost();
            _host.Start();
        }
    }

    internal sealed class BridgeHost : IDisposable
    {
        private static readonly UTF8Encoding _utf8WithoutBomEncoding = new UTF8Encoding(false);
        private static readonly IComparer<InstanceRecord> _instanceRecordComparer = new InstanceRecordProjectNameComparer();
        private readonly ConcurrentQueue<PendingRequest> _pendingRequests = new ConcurrentQueue<PendingRequest>();
        private readonly CancellationTokenSource _cancellationTokenSource = new CancellationTokenSource();
        private readonly string[] _capabilities;
        private readonly string _projectRoot;
        private readonly string _projectName;
        private readonly string _baseProjectHash;
        private readonly string _registryFilePath;
        private readonly string _authToken;
        private readonly AssetCommandHandler _assetCommandHandler;
        private readonly SceneCommandHandler _sceneCommandHandler;
        private readonly PrefabCommandHandler _prefabCommandHandler;
        private readonly ScreenshotCommandHandler _screenshotCommandHandler;
        private readonly ExecuteCodeHandler _executeCodeHandler;
        private readonly CustomCommandHandler _customCommandHandler;
        private readonly PackageCommandHandler _packageCommandHandler;
        private readonly TestCommandHandler _testCommandHandler;
        private readonly MaterialCommandHandler _materialCommandHandler;
        private readonly QaCommandHandler _qaCommandHandler;
        private readonly RecordCommandHandler _recordCommandHandler;
        private readonly ProfileCommandHandler _profileCommandHandler;
        private NamedPipeOwnershipLock? _namedPipeOwnershipLock;
#if !UNITY_5_3_OR_NEWER || UNITY_6000_0_OR_NEWER
        private Socket? _unixListener;
#endif
        private double _lastHeartbeatTime;
        private bool _originalRunInBackground;
        private bool _isStarted;
        private bool _isDisposed;
        private bool _isInstanceRegistered;
        private volatile bool _isListenerReady;
        private volatile bool _isListenerStarting;
        private bool _isListenerAbandoned;
        private ListenerWatchdogPolicy? _listenerWatchdog;
        private string _projectHashBeforeRestart = string.Empty;
        private string _projectHash = string.Empty;
        private string _pipeName = string.Empty;
        private const string AuthTokenSessionStateKey = "UnityCliBridge.AuthToken";
        private const int AuthTokenByteLength = 32;
        private const int ListenerAcquireMaxAttempts = 16;
        private const double ListenerWatchdogIntervalSeconds = 5.0;
        private const int ListenerWatchdogMaxRecoveryAttempts = 5;
        private const int NamedPipeMaxServerInstances = 2;
        private const int NamedPipeProbeTimeoutMilliseconds = 50;

        public BridgeHost()
        {
            _projectRoot = ProtocolConstants.GetCanonicalPath(Path.Combine(Application.dataPath, ".."));
            _projectName = Path.GetFileName(_projectRoot);
            _baseProjectHash = ProtocolConstants.ComputeProjectHash(_projectRoot);
            _projectHash = _baseProjectHash;
            _registryFilePath = RegistryPathUtility.GetRegistryFilePath();
            _authToken = EnsureSessionToken();
            _capabilities = ProtocolHelpers.GetSupportedCommands();
            _assetCommandHandler = new AssetCommandHandler();
            _sceneCommandHandler = new SceneCommandHandler();
            _prefabCommandHandler = new PrefabCommandHandler();
            _screenshotCommandHandler = new ScreenshotCommandHandler();
            _executeCodeHandler = new ExecuteCodeHandler();
            _customCommandHandler = new CustomCommandHandler();
            _packageCommandHandler = new PackageCommandHandler();
            _testCommandHandler = new TestCommandHandler();
            _materialCommandHandler = new MaterialCommandHandler();
            _qaCommandHandler = new QaCommandHandler();
            _recordCommandHandler = new RecordCommandHandler();
            _profileCommandHandler = new ProfileCommandHandler();

            TestCommandHandler.RestoreLockFromSession();
            DomainReloadDisableScope.RestoreIfOrphaned();
        }

        public void Start()
        {
            if (_isStarted || IsSecondaryUnityProcess())
            {
                return;
            }

            _isStarted = true;
            ConsoleLogBuffer.Start();
            _lastHeartbeatTime = EditorApplication.timeSinceStartup;
            _listenerWatchdog = new ListenerWatchdogPolicy(
                ListenerWatchdogIntervalSeconds,
                ListenerWatchdogMaxRecoveryAttempts,
                EditorApplication.timeSinceStartup);
            StartListener();

            EditorApplication.update += OnEditorUpdate;
            EditorApplication.quitting += OnEditorQuitting;
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
        }

        // The bridge must run in the main editor process only — GUI or headless alike.
        // AssetImportWorker/MPE secondary processes share the same projectRoot and would
        // fight over the socket name and registry entry if the bridge started there.
        private static bool IsSecondaryUnityProcess()
        {
            try
            {
                if (UnityEditor.MPE.ProcessService.level != UnityEditor.MPE.ProcessLevel.Main)
                {
                    return true;
                }
            }
            catch (Exception)
            {
                // MPE unavailable: fall through to the argv check.
            }

            foreach (string arg in Environment.GetCommandLineArgs())
            {
                if (string.Equals(arg, "-adb2", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        public void Dispose()
        {
            Dispose(unregisterInstance: true);
        }

        private void Dispose(bool unregisterInstance)
        {
            if (_isDisposed)
            {
                return;
            }

            _isDisposed = true;
            EditorApplication.update -= OnEditorUpdate;
            EditorApplication.quitting -= OnEditorQuitting;
            AssemblyReloadEvents.beforeAssemblyReload -= OnBeforeAssemblyReload;

            try
            {
                _cancellationTokenSource.Cancel();
            }
            catch
            {
            }

#if !UNITY_5_3_OR_NEWER || UNITY_6000_0_OR_NEWER
            DisposeUnixListener();
#endif
            ConsoleLogBuffer.Stop();
            if (unregisterInstance)
            {
                UnregisterInstance();
            }

            CleanupSocketFile();
            DisposeNamedPipeOwnershipLock();
            _cancellationTokenSource.Dispose();
        }

        private void StartListener()
        {
            // Read by the watchdog: a bind in flight must never be mistaken for a dead listener.
            _isListenerStarting = true;
#if !UNITY_5_3_OR_NEWER || UNITY_6000_0_OR_NEWER
            // Unity 6+ / non-Unity: use raw Unix domain sockets for non-Windows.
            if (Path.DirectorySeparatorChar != '\\')
            {
                StartUnixSocketListener();
                return;
            }
#endif
            // Unity 2021-2023 (Mono): NamedPipeServerStream on non-Windows creates a Unix
            // domain socket at the exact _pipeName path, so the CLI's UnixDomainSocketEndPoint
            // connection works transparently. Verified on Unity 2021.3.45f2 + macOS (Mono).
            StartNamedPipeListener();
        }

        private void StartNamedPipeListener()
        {
            // Not an async-void method. Unity 2021.3 batchmode -quit waits until every
            // async-void operation on the editor synchronization context finishes, and this
            // accept loop does not finish until quit cancels it — which cannot happen until
            // quit is allowed to proceed. A Task is not tracked. Continuations already use
            // ConfigureAwait(false), so normal editor and headless sessions behave as before.
            TrackListener(RunNamedPipeLoopAsync(_cancellationTokenSource.Token), "named pipe listener");
        }

        private static NamedPipeServerStream CreateNamedPipeServerStream(string pipeName)
        {
#if UNITY_EDITOR_WIN
            PipeSecurity? pipeSecurity = TryCreateCurrentUserPipeSecurity();
            if (pipeSecurity != null)
            {
                return new NamedPipeServerStream(
                    pipeName,
                    PipeDirection.InOut,
                    NamedPipeMaxServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous,
                    0,
                    0,
                    pipeSecurity);
            }
#endif

            var server = new NamedPipeServerStream(
                pipeName,
                PipeDirection.InOut,
                NamedPipeMaxServerInstances,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);
            TryApplyOwnerOnlySocketMode(pipeName);
            return server;
        }

#if UNITY_EDITOR_WIN
        private static PipeSecurity? TryCreateCurrentUserPipeSecurity()
        {
            try
            {
                using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
                {
                    SecurityIdentifier? principal = identity.User ?? identity.Owner;
                    if (principal == null)
                    {
                        return null;
                    }

                    var pipeSecurity = new PipeSecurity();
                    pipeSecurity.SetAccessRuleProtection(true, false);
                    pipeSecurity.AddAccessRule(new PipeAccessRule(
                        principal,
                        PipeAccessRights.FullControl,
                        AccessControlType.Allow));
                    return pipeSecurity;
                }
            }
            catch (Exception exception)
            {
                UnityEngine.Debug.LogWarning(string.Format(
                    "Unity CLI bridge named pipe permission fix failed: {0}",
                    exception.Message));
                return null;
            }
        }
#endif

        private static void TryApplyOwnerOnlySocketMode(string socketPath)
        {
            if (Path.DirectorySeparatorChar == '\\' || string.IsNullOrWhiteSpace(socketPath))
            {
                return;
            }

            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = "chmod",
                    Arguments = "600 " + QuoteProcessArgument(socketPath),
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };

                Process? process = null;
                try
                {
                    process = Process.Start(startInfo);
                    if (process == null || !process.WaitForExit(1000))
                    {
                        UnityEngine.Debug.LogWarning("Unity CLI bridge socket permission fix failed: chmod timed out.");
                    }
                    else if (process.ExitCode != 0)
                    {
                        UnityEngine.Debug.LogWarning(string.Format(
                            "Unity CLI bridge socket permission fix failed: chmod exited with {0}.",
                            process.ExitCode));
                    }
                }
                finally
                {
                    if (process != null)
                    {
                        process.Dispose();
                    }
                }
            }
            catch (Exception exception)
            {
                UnityEngine.Debug.LogWarning(string.Format(
                    "Unity CLI bridge socket permission fix failed: {0}",
                    exception.Message));
            }
        }

        private static string QuoteProcessArgument(string value)
        {
            return "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }

#if !UNITY_5_3_OR_NEWER || UNITY_6000_0_OR_NEWER
        private void StartUnixSocketListener()
        {
            // Same scheduling as the named-pipe listener. Unity 6 does not wait the way
            // 2021.3 does, and keeping this a Task means a later editor that does will still quit.
            TrackListener(RunUnixSocketLoopAsync(_cancellationTokenSource.Token), "unix socket listener");
        }
#endif

        private async Task RunNamedPipeLoopAsync(CancellationToken cancellationToken)
        {
            NamedPipeServerStream initialServer;
            try
            {
                initialServer = await Task.Run(delegate
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return InstanceListenerKeyResolver.Acquire<NamedPipeServerStream>(
                        _baseProjectHash,
                        ListenerAcquireMaxAttempts,
                        delegate(string hash)
                        {
                            string candidatePipeName = ProtocolConstants.BuildPipeName(hash);
                            NamedPipeOwnershipLock? ownershipLock = null;

                            try
                            {
                                if (Path.DirectorySeparatorChar == '\\'
                                    && !NamedPipeOwnershipLock.TryAcquire(candidatePipeName, out ownershipLock))
                                {
                                    return null;
                                }

                                if (IsLiveNamedPipe(candidatePipeName))
                                {
                                    return null;
                                }

                                var server = CreateNamedPipeServerStream(candidatePipeName);
                                _projectHash = hash;
                                _pipeName = candidatePipeName;
                                _namedPipeOwnershipLock = ownershipLock;
                                ownershipLock = null;

                                // The hash is only settled here, and a client can connect as soon as
                                // the listener exists. Publish the token before that can happen.
                                WriteTokenSidecarSafely();
                                return server;
                            }
                            catch (IOException)
                            {
                                return null;
                            }
                            catch (UnauthorizedAccessException)
                            {
                                return null;
                            }
                            finally
                            {
                                if (ownershipLock != null)
                                {
                                    ownershipLock.Dispose();
                                }
                            }
                        },
                        out _);
                }, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _isListenerStarting = false;
                return;
            }
            catch (Exception exception)
            {
                // Leaves the listener visibly dead; the watchdog retries the acquire.
                _isListenerStarting = false;
                ReportBackgroundException("named pipe listener", exception);
                return;
            }

            _isListenerStarting = false;
            NamedPipeServerStream? server = initialServer;
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    try
                    {
                        if (server == null)
                        {
                            server = CreateNamedPipeServerStream(_pipeName);
                        }

                        _isListenerReady = true;
                        await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                        _ = HandleNamedPipeClientAsync(server, cancellationToken);
                        server = null;
                    }
                    catch (OperationCanceledException)
                    {
                        server?.Dispose();
                        server = null;
                    }
                    catch (Exception exception)
                    {
                        ReportBackgroundException("named pipe accept", exception);
                        server?.Dispose();
                        server = null;
                    }
                }
            }
            finally
            {
                _isListenerReady = false;
                server?.Dispose();
                DisposeNamedPipeOwnershipLock();
            }
        }

        private static bool IsLiveNamedPipe(string pipeName)
        {
            if (string.IsNullOrWhiteSpace(pipeName))
            {
                return false;
            }

            using (var client = new NamedPipeClientStream(
                ".",
                pipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous))
            {
                try
                {
                    client.Connect(NamedPipeProbeTimeoutMilliseconds);
                    return true;
                }
                catch (TimeoutException)
                {
                    return false;
                }
                catch (IOException)
                {
                    return false;
                }
                catch (SocketException)
                {
                    return false;
                }
                catch (UnauthorizedAccessException)
                {
                    return false;
                }
            }
        }

#if !UNITY_5_3_OR_NEWER || UNITY_6000_0_OR_NEWER
        private async Task RunUnixSocketLoopAsync(CancellationToken cancellationToken)
        {
            Socket listener;

            try
            {
                listener = await Task.Run(delegate
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return InstanceListenerKeyResolver.Acquire<Socket>(
                        _baseProjectHash,
                        ListenerAcquireMaxAttempts,
                        delegate(string hash)
                        {
                            string candidatePipeName = ProtocolConstants.BuildPipeName(hash);
                            if (!UnixSocketFileUtility.TryCleanupDeadSocketFile(candidatePipeName))
                            {
                                return null;
                            }

                            var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                            try
                            {
                                socket.Bind(new UnixDomainSocketEndPoint(candidatePipeName));
                                TryApplyOwnerOnlySocketMode(candidatePipeName);
                                socket.Listen(8);
                                _projectHash = hash;
                                _pipeName = candidatePipeName;

                                // The hash is only settled here, and a client can connect as soon as
                                // the listener exists. Publish the token before that can happen.
                                WriteTokenSidecarSafely();
                                return socket;
                            }
                            catch (SocketException)
                            {
                                socket.Dispose();
                                return null;
                            }
                            catch (IOException)
                            {
                                socket.Dispose();
                                return null;
                            }
                        },
                        out _);
                }, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _isListenerStarting = false;
                return;
            }
            catch (Exception exception)
            {
                // Leaves the listener visibly dead; the watchdog retries the acquire.
                _isListenerStarting = false;
                ReportBackgroundException("unix socket listener", exception);
                return;
            }

            _unixListener = listener;
            _isListenerReady = true;
            _isListenerStarting = false;

            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    Socket client = await listener.AcceptAsync().ConfigureAwait(false);
                    HandleSocketClient(client, cancellationToken);
                }
            }
            catch (ObjectDisposedException)
            {
            }
            catch (Exception exception)
            {
                ReportBackgroundException("unix socket accept", exception);
            }
            finally
            {
                // The accept loop is gone: an unexpected AcceptAsync failure ends it for good, so
                // the listener must stop reading as ready or the registry heartbeat would keep
                // advertising an instance nothing can connect to. The watchdog rebinds from here.
                _isListenerReady = false;
                if (ReferenceEquals(_unixListener, listener))
                {
                    _unixListener = null;
                }

                listener.Dispose();
                CleanupSocketFile();
            }
        }
#endif

        private async Task HandleNamedPipeClientAsync(NamedPipeServerStream server, CancellationToken cancellationToken)
        {
            try
            {
                await HandleStreamClientAsync(server, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                ReportBackgroundException("named pipe client", exception);
            }
        }

#if !UNITY_5_3_OR_NEWER || UNITY_6000_0_OR_NEWER
        private void HandleSocketClient(Socket client, CancellationToken cancellationToken)
        {
            TrackListener(ObserveSocketClientAsync(client, cancellationToken), "unix socket client");
        }

        private async Task ObserveSocketClientAsync(Socket client, CancellationToken cancellationToken)
        {
            try
            {
                await HandleSocketClientAsync(client, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                try
                {
                    client.Dispose();
                }
                catch
                {
                }

                ReportBackgroundException("unix socket client", exception);
            }
        }

        private Task HandleSocketClientAsync(Socket client, CancellationToken cancellationToken)
        {
            return HandleStreamClientAsync(new NetworkStream(client, true), cancellationToken);
        }
#endif

        private async Task HandleStreamClientAsync(Stream stream, CancellationToken cancellationToken)
        {
            using (stream)
            using (var writer = new StreamWriter(stream, _utf8WithoutBomEncoding, 1024, true))
            {
                // Bounded initial read: caps how much a single connection can make
                // the Editor buffer, and reclaims connections that open and never
                // write. The deadline covers this read only — command execution
                // past this point is not bounded by it.
                BoundedRequestReadResult readResult = await BoundedRequestReader
                    .ReadLineAsync(stream, cancellationToken)
                    .ConfigureAwait(false);

                if (readResult.Status == BoundedRequestReadStatus.ExceededSize)
                {
                    var error = ResponseEnvelope.Failure(
                        Guid.NewGuid().ToString("N"),
                        _projectHash,
                        ProtocolConstants.ErrorRequestTooLarge,
                        "Request exceeded the maximum size of "
                            + BoundedRequestReader.MaxRequestBytes
                            + " bytes.",
                        false,
                        0,
                        ProtocolConstants.TransportLive,
                        ProtocolErrorDetails.FromString(
                            "Read " + readResult.BytesRead + " bytes without a complete request line."));
                    await WriteResponseAsync(writer, error);
                    return;
                }

                if (readResult.Status == BoundedRequestReadStatus.TimedOut)
                {
                    var error = ResponseEnvelope.Failure(
                        Guid.NewGuid().ToString("N"),
                        _projectHash,
                        ProtocolConstants.ErrorRequestReadTimeout,
                        "Request was not fully received within "
                            + BoundedRequestReader.ReadTimeoutMs
                            + " ms.",
                        true,
                        0,
                        ProtocolConstants.TransportLive,
                        ProtocolErrorDetails.FromString(
                            "Read " + readResult.BytesRead + " bytes before the read deadline elapsed."));
                    await WriteResponseAsync(writer, error);
                    return;
                }

                string line = readResult.Line ?? string.Empty;
                if (string.IsNullOrWhiteSpace(line))
                {
                    return;
                }

                CommandEnvelope? command;
                try
                {
                    command = ProtocolJson.Deserialize<CommandEnvelope>(line);
                }
                catch (Exception deserializeException)
                {
                    var error = ResponseEnvelope.Failure(
                        Guid.NewGuid().ToString("N"),
                        _projectHash,
                        "INVALID_COMMAND",
                        "Could not parse the command payload: " + deserializeException.Message,
                        false,
                        0,
                        ProtocolConstants.TransportLive,
                        ProtocolErrorDetails.FromString(line));
                    await WriteResponseAsync(writer, error);
                    return;
                }

                if (command == null || string.IsNullOrWhiteSpace(command.command))
                {
                    string requestId = command != null && !string.IsNullOrWhiteSpace(command.requestId)
                        ? command.requestId
                        : Guid.NewGuid().ToString("N");
                    var error = ResponseEnvelope.Failure(
                        requestId,
                        _projectHash,
                        "INVALID_COMMAND",
                        "Could not parse the command payload.",
                        false,
                        0,
                        ProtocolConstants.TransportLive,
                        ProtocolErrorDetails.FromString(line));
                    await WriteResponseAsync(writer, error);
                    return;
                }

                if (string.IsNullOrWhiteSpace(command.requestId))
                {
                    command.requestId = Guid.NewGuid().ToString("N");
                }

                if (!string.Equals(command.protocolVersion, ProtocolConstants.ProtocolVersion, StringComparison.Ordinal))
                {
                    var error = ResponseEnvelope.Failure(
                        command.requestId,
                        _projectHash,
                        ProtocolConstants.ErrorProtocolMismatch,
                        "CLI version is incompatible with this Unity package. Please upgrade both CLI binary and Unity package together.",
                        false,
                        0,
                        ProtocolConstants.TransportLive,
                        ProtocolErrorDetails.FromString("Expected protocolVersion " + ProtocolConstants.ProtocolVersion + "."));
                    await WriteResponseAsync(writer, error);
                    return;
                }

                // Fixed-time compare: this is the bridge's only authentication gate, and an
                // early-exit comparison is observable to other local processes.
                if (!AuthTokenComparison.FixedTimeEquals(_authToken, command.token))
                {
                    var error = ResponseEnvelope.Failure(
                        command.requestId,
                        _projectHash,
                        ProtocolConstants.ErrorUnauthorized,
                        "IPC authentication failed.",
                        true,
                        0,
                        ProtocolConstants.TransportLive,
                        ProtocolErrorDetails.FromString("Missing or invalid IPC authentication token."));
                    await WriteResponseAsync(writer, error);
                    return;
                }

                var pending = new PendingRequest(command);
                _pendingRequests.Enqueue(pending);

                // Constructed only now, never before the bounded read: a
                // StreamReader wrapped around the stream earlier would pre-buffer
                // request bytes into itself and corrupt that read.
                using (var reader = new StreamReader(stream, Encoding.UTF8, false, 1024, true))
                using (cancellationToken.Register(CancelPendingRequest, pending))
                {
                    _ = MonitorClientDisconnectAsync(reader, pending);

                    ResponseEnvelope response;
                    try
                    {
                        response = await pending.Completion.Task.ConfigureAwait(false);
                    }
                    catch (TaskCanceledException)
                    {
                        if (pending.State.CancelReason == PendingRequestCancelReason.ClientDisconnected)
                        {
                            return;
                        }

                        response = ResponseEnvelope.Failure(
                            command.requestId,
                            _projectHash,
                            "REQUEST_CANCELLED",
                            "The bridge shut down while handling the request.",
                            true,
                            0,
                            ProtocolConstants.TransportLive,
                            null);
                    }

                    await WriteResponseAsync(writer, response).ConfigureAwait(false);
                }
            }
        }

        private static void CancelPendingRequest(object state)
        {
            PendingRequest pending = state as PendingRequest;
            if (pending == null)
            {
                return;
            }

            if (pending.State.TryCancelForHostShutdown())
            {
                pending.Completion.TrySetCanceled();
            }
        }

        private static async Task MonitorClientDisconnectAsync(TextReader reader, PendingRequest pending)
        {
            bool disconnected = await ClientDisconnectMonitor.WaitForDisconnectAsync(reader).ConfigureAwait(false);
            if (disconnected && pending.State.TryCancelForClientDisconnect())
            {
                pending.Completion.TrySetCanceled();
            }
        }

        private static async Task WriteResponseAsync(StreamWriter writer, ResponseEnvelope response)
        {
            string responseJson;
            try
            {
                responseJson = EnvelopeJsonWriter.Write(response);
            }
            catch (Exception exception)
            {
                responseJson = EnvelopeJsonWriter.Write(ResponseEnvelope.Failure(
                    response.requestId,
                    response.target,
                    ProtocolConstants.ErrorInternalInvalidPayload,
                    "Bridge generated an invalid response payload.",
                    false,
                    response.durationMs,
                    response.transport,
                    ProtocolErrorDetails.FromString(exception.ToString())));
            }

            await writer.WriteLineAsync(responseJson).ConfigureAwait(false);
            await writer.FlushAsync().ConfigureAwait(false);
        }

        private void OnEditorUpdate()
        {
            if (_isDisposed)
            {
                return;
            }

            RunListenerWatchdog();

            if (_isListenerAbandoned)
            {
                // Nothing can connect any more; heartbeating would only re-advertise a zombie.
            }
            else if (!_isInstanceRegistered && _isListenerReady)
            {
                WriteTokenSidecarSafely();
                RegisterInstance();
                _isInstanceRegistered = true;
                _lastHeartbeatTime = EditorApplication.timeSinceStartup;
            }
            else if (_isInstanceRegistered
                && EditorApplication.timeSinceStartup - _lastHeartbeatTime >= ProtocolConstants.RegistryHeartbeatSeconds)
            {
                RegisterInstance();
                WriteTokenSidecarSafely();
                _lastHeartbeatTime = EditorApplication.timeSinceStartup;
            }

            while (_pendingRequests.TryDequeue(out PendingRequest pending))
            {
                if (!pending.State.TryClaimForDispatch())
                {
                    continue;
                }

                if (_qaCommandHandler.CanHandle(pending.Command.command) && _qaCommandHandler.IsDeferred(pending.Command.command, pending.Command.argumentsJson))
                {
                    StartDeferredQaRequest(pending);
                    continue;
                }

                if (_testCommandHandler.CanHandle(pending.Command.command) && _testCommandHandler.IsDeferred(pending.Command.command, pending.Command.argumentsJson))
                {
                    StartDeferredTestRequest(pending);
                    continue;
                }

                if (_packageCommandHandler.CanHandle(pending.Command.command) && _packageCommandHandler.IsDeferred(pending.Command.command, pending.Command.argumentsJson))
                {
                    StartDeferredPackageRequest(pending);
                    continue;
                }

                if (_profileCommandHandler.CanHandle(pending.Command.command) && _profileCommandHandler.IsDeferred(pending.Command.command, pending.Command.argumentsJson))
                {
                    StartDeferredProfileRequest(pending);
                    continue;
                }

                ResponseEnvelope response = HandleCommand(pending.Command);
                pending.Completion.TrySetResult(response);
            }
        }

        // The accept loops have no retry of their own: the Unix loop ends permanently on any
        // unexpected AcceptAsync failure, and both loops give up if the very first listener
        // acquire fails. Without this the registry would keep advertising a live instance the
        // CLI cannot connect to.
        private void RunListenerWatchdog()
        {
            ListenerWatchdogPolicy? watchdog = _listenerWatchdog;
            if (watchdog == null)
            {
                return;
            }

            ListenerWatchdogDecision decision = watchdog.Evaluate(
                _isListenerReady,
                _isListenerStarting,
                EditorApplication.isCompiling || EditorApplication.isUpdating,
                EditorApplication.timeSinceStartup);

            switch (decision)
            {
                case ListenerWatchdogDecision.Restart:
                    _projectHashBeforeRestart = _projectHash;
                    UnityEngine.Debug.LogWarning(string.Format(
                        "Unity CLI bridge listener died; attempting to rebind ({0}/{1}).",
                        watchdog.RecoveryAttempts,
                        ListenerWatchdogMaxRecoveryAttempts));
                    StartListener();
                    break;
                case ListenerWatchdogDecision.Recovered:
                    OnListenerRecovered();
                    break;
                case ListenerWatchdogDecision.Abandon:
                    AbandonListener();
                    break;
            }
        }

        private void OnListenerRecovered()
        {
            // A rebind can settle on a different hash if the old socket name is still taken; the
            // sidecar is keyed by hash, so the old one would linger with a live token in it.
            if (!string.IsNullOrEmpty(_projectHashBeforeRestart)
                && !string.Equals(_projectHashBeforeRestart, _projectHash, StringComparison.Ordinal))
            {
                try
                {
                    InstanceRegistryFile.DeleteTokenSidecar(_registryFilePath, _projectHashBeforeRestart);
                }
                catch (Exception exception)
                {
                    UnityEngine.Debug.LogWarning(string.Format(
                        "Unity CLI bridge failed to remove the previous auth token: {0}",
                        exception.Message));
                }
            }

            _projectHashBeforeRestart = string.Empty;
            WriteTokenSidecarSafely();
            RegisterInstance();
            _isInstanceRegistered = true;
            _lastHeartbeatTime = EditorApplication.timeSinceStartup;
            UnityEngine.Debug.Log("Unity CLI bridge listener rebound: " + _pipeName);
        }

        private void AbandonListener()
        {
            _isListenerAbandoned = true;
            UnityEngine.Debug.LogError(string.Format(
                "Unity CLI bridge listener failed to recover after {0} rebind attempts. " +
                "Removing it from the registry so it is not advertised as reachable. Restart the Unity Editor.",
                ListenerWatchdogMaxRecoveryAttempts));

            if (_isInstanceRegistered)
            {
                UnregisterInstance();
                _isInstanceRegistered = false;
            }
        }

        private void StartDeferredTestRequest(PendingRequest pending)
        {
            CommandEnvelope command = pending.Command;
            var stopwatch = Stopwatch.StartNew();

            try
            {
                if (IsBusyEditorCommand(command.command))
                {
                    stopwatch.Stop();
                    pending.Completion.TrySetResult(BuildBusyResponse(command, stopwatch.ElapsedMilliseconds));
                    return;
                }

                _testCommandHandler.StartDeferred(command.command, command.argumentsJson, pending.Completion, _projectHash);
            }
            catch (CommandFailureException exception)
            {
                if (string.Equals(command.command, ProtocolConstants.CommandTestRun, StringComparison.Ordinal))
                {
                    TestCommandHandler.EndRun();
                }

                stopwatch.Stop();
                pending.Completion.TrySetResult(ResponseEnvelope.Failure(
                    command.requestId,
                    _projectHash,
                    exception.ErrorCode,
                    exception.Message,
                    exception.IsRetryable,
                    stopwatch.ElapsedMilliseconds,
                    ProtocolConstants.TransportLive,
                    exception.Details));
            }
            catch (Exception exception)
            {
                if (string.Equals(command.command, ProtocolConstants.CommandTestRun, StringComparison.Ordinal))
                {
                    TestCommandHandler.EndRun();
                }

                stopwatch.Stop();
                pending.Completion.TrySetResult(ResponseEnvelope.Failure(
                    command.requestId,
                    _projectHash,
                    "TEST_RUN_START_FAILED",
                    exception.Message,
                    false,
                    stopwatch.ElapsedMilliseconds,
                    ProtocolConstants.TransportLive,
                    ProtocolErrorDetails.FromString(exception.ToString())));
            }
        }

        private void StartDeferredQaRequest(PendingRequest pending)
        {
            CommandEnvelope command = pending.Command;
            var stopwatch = Stopwatch.StartNew();

            try
            {
                if (IsBusyEditorCommand(command.command))
                {
                    stopwatch.Stop();
                    pending.Completion.TrySetResult(BuildBusyResponse(command, stopwatch.ElapsedMilliseconds));
                    return;
                }

                _qaCommandHandler.StartDeferred(command.command, command.argumentsJson, pending.Completion, _projectHash);
            }
            catch (CommandFailureException exception)
            {
                stopwatch.Stop();
                pending.Completion.TrySetResult(ResponseEnvelope.Failure(
                    command.requestId,
                    _projectHash,
                    exception.ErrorCode,
                    exception.Message,
                    exception.IsRetryable,
                    stopwatch.ElapsedMilliseconds,
                    ProtocolConstants.TransportLive,
                    exception.Details));
            }
            catch (Exception exception)
            {
                stopwatch.Stop();
                pending.Completion.TrySetResult(ResponseEnvelope.Failure(
                    command.requestId,
                    _projectHash,
                    "COMMAND_FAILED",
                    exception.Message,
                    false,
                    stopwatch.ElapsedMilliseconds,
                    ProtocolConstants.TransportLive,
                    ProtocolErrorDetails.FromString(exception.ToString())));
            }
        }

        private void StartDeferredPackageRequest(PendingRequest pending)
        {
            CommandEnvelope command = pending.Command;
            var stopwatch = Stopwatch.StartNew();

            try
            {
                if (IsBusyEditorCommand(command.command))
                {
                    stopwatch.Stop();
                    pending.Completion.TrySetResult(BuildBusyResponse(command, stopwatch.ElapsedMilliseconds));
                    return;
                }

                _packageCommandHandler.StartDeferred(command.command, command.argumentsJson, pending.Completion, _projectHash);
            }
            catch (CommandFailureException exception)
            {
                stopwatch.Stop();
                pending.Completion.TrySetResult(ResponseEnvelope.Failure(
                    command.requestId,
                    _projectHash,
                    exception.ErrorCode,
                    exception.Message,
                    exception.IsRetryable,
                    stopwatch.ElapsedMilliseconds,
                    ProtocolConstants.TransportLive,
                    exception.Details));
            }
            catch (Exception exception)
            {
                stopwatch.Stop();
                pending.Completion.TrySetResult(ResponseEnvelope.Failure(
                    command.requestId,
                    _projectHash,
                    "COMMAND_FAILED",
                    exception.Message,
                    false,
                    stopwatch.ElapsedMilliseconds,
                    ProtocolConstants.TransportLive,
                    ProtocolErrorDetails.FromString(exception.ToString())));
            }
        }

        private void StartDeferredProfileRequest(PendingRequest pending)
        {
            CommandEnvelope command = pending.Command;
            var stopwatch = Stopwatch.StartNew();

            try
            {
                if (IsBusyEditorCommand(command.command))
                {
                    stopwatch.Stop();
                    pending.Completion.TrySetResult(BuildBusyResponse(command, stopwatch.ElapsedMilliseconds));
                    return;
                }

                _profileCommandHandler.StartDeferred(command.command, command.argumentsJson, pending.Completion, _projectHash);
            }
            catch (CommandFailureException exception)
            {
                stopwatch.Stop();
                pending.Completion.TrySetResult(ResponseEnvelope.Failure(
                    command.requestId,
                    _projectHash,
                    exception.ErrorCode,
                    exception.Message,
                    exception.IsRetryable,
                    stopwatch.ElapsedMilliseconds,
                    ProtocolConstants.TransportLive,
                    exception.Details));
            }
            catch (Exception exception)
            {
                stopwatch.Stop();
                pending.Completion.TrySetResult(ResponseEnvelope.Failure(
                    command.requestId,
                    _projectHash,
                    "COMMAND_FAILED",
                    exception.Message,
                    false,
                    stopwatch.ElapsedMilliseconds,
                    ProtocolConstants.TransportLive,
                    ProtocolErrorDetails.FromString(exception.ToString())));
            }
        }

        private void OnEditorQuitting()
        {
            Dispose();
        }

        private void OnBeforeAssemblyReload()
        {
            Dispose(unregisterInstance: false);
        }

        private ResponseEnvelope HandleCommand(CommandEnvelope command)
        {
            var stopwatch = Stopwatch.StartNew();

            try
            {
                if (IsBusyEditorCommand(command.command))
                {
                    stopwatch.Stop();
                    return BuildBusyResponse(command, stopwatch.ElapsedMilliseconds);
                }

                TestCommandHandler.ThrowIfEditorBusyWithTestRun(command.command);

                if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null
                    && CliCommandCatalog.RequiresGraphics(command.command))
                {
                    throw new CommandFailureException(
                        ProtocolConstants.ErrorHeadlessNoGraphics,
                        "This command is unavailable in a -nographics editor because rendering is not initialized: " + command.command,
                        "Without a GPU the capture would be a flat or broken image, so the command is rejected. " +
                        "Launch with -batchmode and without -nographics to use rendering commands with no window.");
                }

                string data;
                if (_assetCommandHandler.CanHandle(command.command))
                {
                    data = _assetCommandHandler.Handle(command.command, command.argumentsJson);
                }
                else if (_sceneCommandHandler.CanHandle(command.command))
                {
                    data = _sceneCommandHandler.Handle(command.command, command.argumentsJson);
                }
                else if (_prefabCommandHandler.CanHandle(command.command))
                {
                    data = _prefabCommandHandler.Handle(command.command, command.argumentsJson);
                }
                else if (_screenshotCommandHandler.CanHandle(command.command))
                {
                    data = _screenshotCommandHandler.Handle(command.command, command.argumentsJson);
                }
                else if (_executeCodeHandler.CanHandle(command.command))
                {
                    data = _executeCodeHandler.Handle(command.command, command.argumentsJson);
                }
                else if (_customCommandHandler.CanHandle(command.command))
                {
                    data = _customCommandHandler.Handle(command.command, command.argumentsJson);
                }
                else if (_materialCommandHandler.CanHandle(command.command))
                {
                    data = _materialCommandHandler.Handle(command.command, command.argumentsJson);
                }
                else if (_qaCommandHandler.CanHandle(command.command))
                {
                    data = _qaCommandHandler.Handle(command.command, command.argumentsJson);
                }
                else if (_testCommandHandler.CanHandle(command.command))
                {
                    data = _testCommandHandler.Handle(command.command, command.argumentsJson);
                }
                else if (_packageCommandHandler.CanHandle(command.command))
                {
                    data = _packageCommandHandler.Handle(command.command, command.argumentsJson);
                }
                else if (_recordCommandHandler.CanHandle(command.command))
                {
                    data = _recordCommandHandler.Handle(command.command, command.argumentsJson);
                }
                else if (_profileCommandHandler.CanHandle(command.command))
                {
                    data = _profileCommandHandler.Handle(command.command, command.argumentsJson);
                }
                else
                {
                    switch (command.command)
                    {
                        case ProtocolConstants.CommandPing:
                            data = ProtocolJson.Serialize(new PingPayload
                            {
                                message = "pong",
                                timestampUtc = DateTimeOffset.UtcNow.ToString("O"),
                            });
                            break;
                        case ProtocolConstants.CommandStatus:
                            data = BuildStatusJson();
                            break;
                        case ProtocolConstants.CommandRefresh:
                            AssetDatabase.Refresh();
                            data = ProtocolJson.Serialize(new MessagePayload { message = "AssetDatabase.Refresh completed" });
                            break;
                        case ProtocolConstants.CommandCompile:
                            CompilationPipeline.RequestScriptCompilation();
                            data = ProtocolJson.Serialize(new MessagePayload { message = "script compilation requested" });
                            break;
                        case ProtocolConstants.CommandPlay:
                            bool alreadyEnteringOrPlaying = EditorApplication.isPlayingOrWillChangePlaymode;
                            EditorApplication.isPaused = false;
                            EditorApplication.isPlaying = true;
                            _originalRunInBackground = UnityEngine.Application.runInBackground;
                            UnityEngine.Application.runInBackground = true;
                            data = ProtocolJson.Serialize(new PlayStatePayload
                            {
                                isPlaying = true,
                                domainId = DomainId,
                                reloadsDomain = !alreadyEnteringOrPlaying && PlayModeReloadsDomain(),
                            });
                            break;
                        case ProtocolConstants.CommandPause:
                            EditorApplication.isPaused = true;
                            data = ProtocolJson.Serialize(new PauseStatePayload { isPaused = true });
                            break;
                        case ProtocolConstants.CommandStop:
                            EditorApplication.isPlaying = false;
                            EditorApplication.isPaused = false;
                            UnityEngine.Application.runInBackground = _originalRunInBackground;
                            data = ProtocolJson.Serialize(new StopStatePayload
                            {
                                isPlaying = false,
                                isPaused = false,
                            });
                            break;
                        case ProtocolConstants.CommandExecuteMenu:
                            data = HandleExecuteMenu(command.argumentsJson);
                            break;
                        case ProtocolConstants.CommandReadConsole:
                            data = HandleReadConsole(command.argumentsJson);
                            break;
                        case ProtocolConstants.CommandEditorQuit:
                            data = HandleEditorQuit(command.argumentsJson);
                            break;
                        default:
                            throw new InvalidOperationException("Unsupported command: " + command.command);
                    }
                }

                stopwatch.Stop();
                if (string.Equals(command.command, ProtocolConstants.CommandTestResults, StringComparison.Ordinal))
                {
                    return TestCommandHandler.BuildTestRunResultEnvelope(
                        command.requestId,
                        _projectHash,
                        data,
                        stopwatch.ElapsedMilliseconds);
                }

                return ResponseEnvelope.Success(
                    command.requestId,
                    _projectHash,
                    data,
                    stopwatch.ElapsedMilliseconds,
                    ProtocolConstants.TransportLive);
            }
            catch (CommandFailureException exception)
            {
                stopwatch.Stop();
                return ResponseEnvelope.Failure(
                    command.requestId,
                    _projectHash,
                    exception.ErrorCode,
                    exception.Message,
                    exception.IsRetryable,
                    stopwatch.ElapsedMilliseconds,
                    ProtocolConstants.TransportLive,
                    exception.Details);
            }
            catch (Exception exception)
            {
                stopwatch.Stop();
                return ResponseEnvelope.Failure(
                    command.requestId,
                    _projectHash,
                    "COMMAND_FAILED",
                    exception.Message,
                    false,
                    stopwatch.ElapsedMilliseconds,
                    ProtocolConstants.TransportLive,
                    ProtocolErrorDetails.FromString(exception.ToString()));
            }
        }

        private bool IsBusyEditorCommand(string command)
        {
            if (!EditorApplication.isCompiling && !EditorApplication.isUpdating)
            {
                return false;
            }

            return !ProtocolHelpers.IsCommandAllowedWhileBusy(command);
        }

        private ResponseEnvelope BuildBusyResponse(CommandEnvelope command, long durationMs)
        {
            var busyDetails = "Try again in about 5–10 seconds. Do not launch another Unity Editor instance.\n" +
                              "Diagnostics: check the current instance with `unity-cli instances list`, " +
                              "and check compile errors with `unity-cli read-console --type error`.";

            return ResponseEnvelope.Failure(
                command.requestId,
                _projectHash,
                ProtocolConstants.BusyErrorCode,
                "Unity is compiling or updating, so this command cannot run right now.",
                true,
                durationMs,
                ProtocolConstants.TransportLive,
                ProtocolErrorDetails.FromString(busyDetails));
        }

        private string BuildStatusJson()
        {
            return ProtocolJson.Serialize(new StatusPayload
            {
                projectRoot = _projectRoot,
                projectHash = _projectHash,
                projectName = _projectName,
                unityVersion = Application.unityVersion,
                isPlaying = EditorApplication.isPlaying,
                isPaused = EditorApplication.isPaused,
                isCompiling = EditorApplication.isCompiling,
                isUpdating = EditorApplication.isUpdating,
                activeScenePath = EditorSceneManager.GetActiveScene().path,
                pipeName = _pipeName,
                isChangingPlayMode = EditorApplication.isPlaying != EditorApplication.isPlayingOrWillChangePlaymode,
                domainId = DomainId,
            });
        }

        // A static initializer runs once per domain, so this value changes on every domain reload.
        private static readonly string DomainId = Guid.NewGuid().ToString("N");

        private static bool PlayModeReloadsDomain()
        {
#pragma warning disable CS0618 // enterPlayModeOptionsEnabled is obsolete on newer Unity versions but still reports the setting.
            return !(EditorSettings.enterPlayModeOptionsEnabled
                && (EditorSettings.enterPlayModeOptions & EnterPlayModeOptions.DisableDomainReload) != 0);
#pragma warning restore CS0618
        }

        private string HandleExecuteMenu(string argumentsJson)
        {
            ExecuteMenuArgs args = ProtocolJson.Deserialize<ExecuteMenuArgs>(argumentsJson) ?? new ExecuteMenuArgs();
            if (args.list)
            {
                if (!string.IsNullOrWhiteSpace(args.path))
                {
                    throw new CommandFailureException("INVALID_ARGS", "execute-menu cannot take path and list together.");
                }

                return ListMenuItems(args.prefix);
            }

            if (string.IsNullOrWhiteSpace(args.path))
            {
                throw new CommandFailureException("INVALID_ARGS", "execute-menu requires a path.");
            }

            bool isExecuted = EditorApplication.ExecuteMenuItem(args.path);
            return ProtocolJson.Serialize(new ExecuteMenuPayload
            {
                path = args.path,
                executed = isExecuted,
                menus = Array.Empty<string>(),
            });
        }

        private static string ListMenuItems(string? prefix)
        {
            if (string.IsNullOrWhiteSpace(prefix))
            {
                throw new CommandFailureException("INVALID_ARGS", "execute-menu --list requires a prefix.");
            }

            Type? unsupportedType = typeof(EditorApplication).Assembly.GetType("UnityEditor.Unsupported");
            if (unsupportedType == null)
            {
                throw new CommandFailureException("MENU_LIST_UNAVAILABLE", "Could not find the UnityEditor.Unsupported type.");
            }

            MethodInfo? getSubmenus = unsupportedType.GetMethod(
                "GetSubmenus",
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            if (getSubmenus is null)
            {
                throw new CommandFailureException("MENU_LIST_UNAVAILABLE", "Could not find the UnityEditor.Unsupported.GetSubmenus API.");
            }

            string[] menus = getSubmenus.Invoke(null, new object[] { prefix }) as string[] ?? Array.Empty<string>();
            return ProtocolJson.Serialize(new ExecuteMenuPayload
            {
                prefix = prefix,
                menus = menus,
            });
        }

        private string HandleReadConsole(string argumentsJson)
        {
            ReadConsoleArgs args = ProtocolJson.Deserialize<ReadConsoleArgs>(argumentsJson) ?? new ReadConsoleArgs();
            int limit = args.limit <= 0 ? ProtocolConstants.DefaultConsoleLimit : args.limit;
            ConsoleLogEntry[] entries = ConsoleLogBuffer.Read(limit, args.type);
            if (ConsoleLogProjectionUtility.ShouldOmitStackTrace(args))
            {
                return JsonConvert.SerializeObject(new
                {
                    entries = ConsoleLogProjectionUtility.ApplyNoStackTrace(entries),
                }, BridgeJsonSettings.CamelCaseIgnoreNull);
            }

            return ProtocolJson.Serialize(new ReadConsolePayload { entries = entries });
        }

        private string HandleEditorQuit(string argumentsJson)
        {
            EditorQuitArgs args = ProtocolJson.Deserialize<EditorQuitArgs>(argumentsJson) ?? new EditorQuitArgs();
            List<string> dirtyTargets = CollectDirtyTargets();
            if (dirtyTargets.Count > 0 && !args.force)
            {
                throw new CommandFailureException(
                    ProtocolConstants.ErrorEditorDirty,
                    "Refusing to quit because there are unsaved changes. Pass --force to discard them and quit.",
                    string.Join("\n", dirtyTargets));
            }

            // Reply first, exit shortly after: the response is flushed on the listener
            // thread right after this method returns, so the CLI receives a normal
            // success envelope instead of LIVE_UNAVAILABLE. EditorApplication.update
            // (not delayCall) is required here — delayCall rides the inspector-update
            // cycle and starves in an unfocused GUI editor, while update keeps ticking.
            double quitAtTime = EditorApplication.timeSinceStartup + 0.5;
            EditorApplication.CallbackFunction quitWhenDue = null;
            quitWhenDue = () =>
            {
                if (EditorApplication.timeSinceStartup < quitAtTime)
                {
                    return;
                }

                EditorApplication.update -= quitWhenDue;
                EditorApplication.Exit(0);
            };
            EditorApplication.update += quitWhenDue;

            return ProtocolJson.Serialize(new EditorQuitPayload
            {
                stopping = true,
                editorProcessId = Process.GetCurrentProcess().Id,
            });
        }

        private static List<string> CollectDirtyTargets()
        {
            var dirtyTargets = new List<string>();
            for (int i = 0; i < UnityEngine.SceneManagement.SceneManager.sceneCount; i++)
            {
                UnityEngine.SceneManagement.Scene scene = UnityEngine.SceneManagement.SceneManager.GetSceneAt(i);
                if (scene.isDirty)
                {
                    dirtyTargets.Add(string.IsNullOrEmpty(scene.path) ? "(untitled scene)" : scene.path);
                }
            }

            PrefabStage prefabStage = PrefabStageUtility.GetCurrentPrefabStage();
            if (prefabStage != null && prefabStage.scene.isDirty)
            {
                dirtyTargets.Add("PrefabStage:" + prefabStage.assetPath);
            }

            return dirtyTargets;
        }

        private void RegisterInstance()
        {
            UpdateRegistrySafely(delegate(InstanceRegistry registry)
            {
                InstanceRecord[] existingRecords = registry.instances ?? Array.Empty<InstanceRecord>();
                var updatedRecords = new InstanceRecord[existingRecords.Length + 1];
                int updatedCount = 0;
                for (int i = 0; i < existingRecords.Length; i++)
                {
                    InstanceRecord record = existingRecords[i];
                    if (string.Equals(record.projectRoot, _projectRoot, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    updatedRecords[updatedCount] = record;
                    updatedCount++;
                }

                updatedRecords[updatedCount] = BuildInstanceRecord();
                updatedCount++;
                if (updatedCount != updatedRecords.Length)
                {
                    Array.Resize(ref updatedRecords, updatedCount);
                }

                Array.Sort(updatedRecords, 0, updatedRecords.Length, _instanceRecordComparer);
                registry.instances = updatedRecords;

                InstanceRecord? activeRecord = null;
                if (!string.IsNullOrWhiteSpace(registry.activeProjectRoot))
                {
                    for (int i = 0; i < registry.instances.Length; i++)
                    {
                        InstanceRecord record = registry.instances[i];
                        if (string.Equals(record.projectRoot, registry.activeProjectRoot, StringComparison.OrdinalIgnoreCase))
                        {
                            activeRecord = record;
                            break;
                        }
                    }
                }

                bool isCurrentProjectPromotionNeeded = activeRecord == null
                    || string.Equals(activeRecord.state, "offline", StringComparison.OrdinalIgnoreCase)
                    || activeRecord.editorProcessId <= 0;

                if (!registry.activeProjectRootPinned && isCurrentProjectPromotionNeeded)
                {
                    registry.activeProjectRoot = _projectRoot;
                }

                registry.activeProjectHash = null;
                return registry;
            });
        }

        private void UnregisterInstance()
        {
            UpdateRegistrySafely(delegate(InstanceRegistry registry)
            {
                InstanceRecord[] existingRecords = registry.instances ?? Array.Empty<InstanceRecord>();
                var remainingRecords = new InstanceRecord[existingRecords.Length];
                int remainingCount = 0;
                for (int i = 0; i < existingRecords.Length; i++)
                {
                    InstanceRecord record = existingRecords[i];
                    if (string.Equals(record.projectRoot, _projectRoot, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    remainingRecords[remainingCount] = record;
                    remainingCount++;
                }

                if (remainingCount != remainingRecords.Length)
                {
                    Array.Resize(ref remainingRecords, remainingCount);
                }

                registry.instances = remainingRecords;

                if (!registry.activeProjectRootPinned
                    && string.Equals(registry.activeProjectRoot, _projectRoot, StringComparison.OrdinalIgnoreCase))
                {
                    registry.activeProjectRoot = registry.instances.Length > 0 ? registry.instances[0].projectRoot : string.Empty;
                }

                registry.activeProjectHash = null;
                return registry;
            });

            InstanceRegistryFile.DeleteTokenSidecar(_registryFilePath, _projectHash);
        }

        private InstanceRecord BuildInstanceRecord()
        {
            return new InstanceRecord
            {
                projectRoot = _projectRoot,
                projectName = _projectName,
                projectHash = _projectHash,
                pipeName = _pipeName,
                editorProcessId = Process.GetCurrentProcess().Id,
                unityVersion = Application.unityVersion,
                state = BuildStateLabel(),
                editorMode = ResolveEditorModeLabel(),
                lastSeenUtc = DateTimeOffset.UtcNow.ToString("O"),
                capabilities = (string[])_capabilities.Clone(),
            };
        }

        private static string ResolveEditorModeLabel()
        {
            if (!Application.isBatchMode)
            {
                return "gui";
            }

            return SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null
                ? "headless-nographics"
                : "headless";
        }

        private static string EnsureSessionToken()
        {
            string existingToken = SessionState.GetString(AuthTokenSessionStateKey, string.Empty);
            if (IsValidToken(existingToken))
            {
                return existingToken;
            }

            string token = GenerateToken();
            SessionState.SetString(AuthTokenSessionStateKey, token);
            return token;
        }

        private static string GenerateToken()
        {
            var bytes = new byte[AuthTokenByteLength];
            using (RandomNumberGenerator generator = RandomNumberGenerator.Create())
            {
                generator.GetBytes(bytes);
            }

            var characters = new char[bytes.Length * 2];
            for (int index = 0; index < bytes.Length; index++)
            {
                byte value = bytes[index];
                characters[index * 2] = ToHexChar(value >> 4);
                characters[(index * 2) + 1] = ToHexChar(value & 0x0F);
            }

            return new string(characters);
        }

        private static bool IsValidToken(string token)
        {
            if (token.Length != AuthTokenByteLength * 2)
            {
                return false;
            }

            for (int index = 0; index < token.Length; index++)
            {
                char character = token[index];
                if (!((character >= '0' && character <= '9')
                    || (character >= 'a' && character <= 'f')
                    || (character >= 'A' && character <= 'F')))
                {
                    return false;
                }
            }

            return true;
        }

        private static char ToHexChar(int value)
        {
            return (char)(value < 10 ? '0' + value : 'a' + (value - 10));
        }

        private string BuildStateLabel()
        {
            if (EditorApplication.isCompiling)
            {
                return "compiling";
            }

            if (EditorApplication.isUpdating)
            {
                return "updating";
            }

            if (EditorApplication.isPlaying)
            {
                return "playing";
            }

            return "idle";
        }

        private void UpdateRegistrySafely(Func<InstanceRegistry, InstanceRegistry> update)
        {
            try
            {
                InstanceRegistryFile.Update(_registryFilePath, update);
            }
            catch (Exception exception)
            {
                UnityEngine.Debug.LogWarning(string.Format("Unity CLI bridge registry update failed: {0}", exception));
            }
        }

        private void WriteTokenSidecarSafely()
        {
            try
            {
                // Overwrite-if-different: a stale sidecar from a killed session would otherwise
                // make every CLI call fail UNAUTHORIZED for this whole editor session.
                InstanceRegistryFile.EnsureTokenSidecar(_registryFilePath, _projectHash, _authToken);
            }
            catch (Exception exception)
            {
                UnityEngine.Debug.LogWarning(string.Format("Unity CLI bridge auth token save failed: {0}", exception));
            }
        }

        private void CleanupSocketFile()
        {
            TryCleanupSocketFile(_pipeName);
        }

        private static void TryCleanupSocketFile(string pipeName)
        {
#if !UNITY_5_3_OR_NEWER || UNITY_6000_0_OR_NEWER
            UnixSocketFileUtility.TryCleanupDeadSocketFile(pipeName, LogLiveSocketCleanupSkipped);
#else
            if (Path.DirectorySeparatorChar != '\\' && !string.IsNullOrWhiteSpace(pipeName) && File.Exists(pipeName))
            {
                try
                {
                    File.Delete(pipeName);
                }
                catch
                {
                }
            }
#endif
        }

        private static void LogLiveSocketCleanupSkipped(string pipeName)
        {
            UnityEngine.Debug.LogWarning(
                string.Format("Unity CLI bridge skipped cleanup for live Unix socket: {0}", pipeName));
        }

        private void DisposeNamedPipeOwnershipLock()
        {
            var ownershipLock = _namedPipeOwnershipLock;
            if (ownershipLock == null)
            {
                return;
            }

            _namedPipeOwnershipLock = null;
            ownershipLock.Dispose();
        }

#if !UNITY_5_3_OR_NEWER || UNITY_6000_0_OR_NEWER
        private void DisposeUnixListener()
        {
            if (_unixListener == null)
            {
                return;
            }

            try
            {
                _unixListener.Dispose();
            }
            catch
            {
            }

            _unixListener = null;
        }
#endif

        private void TrackListener(Task listener, string operation)
        {
            listener.ContinueWith(
                task =>
                {
                    AggregateException? aggregate = task.Exception;
                    if (aggregate == null)
                    {
                        return;
                    }

                    ReportBackgroundException(operation, aggregate.GetBaseException());
                },
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        private void ReportBackgroundException(string operation, Exception exception)
        {
            if (_isDisposed || exception is OperationCanceledException)
            {
                return;
            }

            UnityEngine.Debug.LogWarning(string.Format("Unity CLI bridge {0} failed: {1}", operation, exception));
        }

        private sealed class PendingRequest
        {
            public PendingRequest(CommandEnvelope command)
            {
                Command = command;
                State = new PendingRequestState();
                Completion = new TaskCompletionSource<ResponseEnvelope>(
                    command.requestId,
                    TaskCreationOptions.RunContinuationsAsynchronously);
            }

            public CommandEnvelope Command { get; private set; }
            public PendingRequestState State { get; private set; }
            public TaskCompletionSource<ResponseEnvelope> Completion { get; private set; }
        }

        private sealed class InstanceRecordProjectNameComparer : IComparer<InstanceRecord>
        {
            public int Compare(InstanceRecord x, InstanceRecord y)
            {
                if (ReferenceEquals(x, y))
                {
                    return 0;
                }

                if (x == null)
                {
                    return -1;
                }

                if (y == null)
                {
                    return 1;
                }

                return StringComparer.OrdinalIgnoreCase.Compare(x.projectName, y.projectName);
            }
        }
    }
}
