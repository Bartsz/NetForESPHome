using Microsoft.Extensions.Logging;
using NetForESPHome.Client.Model;
using NetForESPHome.DomainModel.Native;
using NetForESPHome.Transports;
using System.Net.Sockets;
using System.Threading.Channels;

#pragma warning disable CS0612 // Type or member is obsolete

namespace NetForESPHome.Client;

/// <summary>Represents the observable stage of the ESPHome Native API connection.</summary>
public enum ConnectionState
{
    SocketOpened,
    HandshakeCompleted,
    Connected,
    Disconnected
}

/// <summary>Handles a connection-close notification without participating in connection lifecycle control.</summary>
public delegate Task EspHomeConnectionClosedHandler(object sender, EspHomeConnectionClosed e);

public sealed partial class EspHomeClient
{
    private static readonly TimeSpan TcpConnectTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan NoiseHandshakeTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan HelloTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DisconnectTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan EntityListResponseTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan KeepAliveResponseTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(5);

    private readonly string? _noisePsk;
    private readonly string? _password;
    private readonly string _clientInfo;
    public readonly string Host;
    public readonly int Port;

    private ConnectionSupervisor? _connectionSupervisor;
    private ConnectionSession? _activeSession;
    private int _state = (int)ConnectionState.Disconnected;
    private int _lastDisconnectReason = -1;
    private int _disposed;

    /// <summary>Notifies consumers after an established session closes without allowing consumer work to block reconnection.</summary>
    public event EspHomeConnectionClosedHandler? ConnectionClosed;

    /// <summary>Starts connection maintenance for the configured endpoint and waits until the first session is established.</summary>
    public async Task ConnectAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ct.ThrowIfCancellationRequested();

        while (true)
        {
            var existingSupervisor = Volatile.Read(ref _connectionSupervisor);
            if (existingSupervisor is not null)
            {
                if (existingSupervisor.IsStopRequested)
                {
                    await existingSupervisor.Stopped.Task.WaitAsync(ct).ConfigureAwait(false);
                    continue;
                }

                await existingSupervisor.FirstConnected.Task.WaitAsync(ct).ConfigureAwait(false);
                return;
            }

            var supervisor = new ConnectionSupervisor();
            existingSupervisor = Interlocked.CompareExchange(ref _connectionSupervisor, supervisor, comparand: null);

            if (existingSupervisor is not null)
            {
                supervisor.Dispose();
                continue;
            }

            if (Volatile.Read(ref _disposed) != 0)
            {
                Interlocked.CompareExchange(ref _connectionSupervisor, value: null, supervisor);
                supervisor.RequestStop();
                supervisor.Stopped.TrySetResult(true);
                supervisor.Dispose();
                ThrowIfDisposed();
            }

            _ = RunConnectionSupervisorAsync(supervisor);

            await supervisor.FirstConnected.Task.WaitAsync(ct).ConfigureAwait(false);
            return;
        }
    }

    /// <summary>Stops connection maintenance, gracefully closes the active ESPHome session when possible, and waits for all session loops to finish.</summary>
    public async Task DisconnectAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ct.ThrowIfCancellationRequested();

        var supervisor = Volatile.Read(ref _connectionSupervisor);
        if (supervisor is null) return;

        var activeSession = Volatile.Read(ref _activeSession);
        activeSession?.MarkCloseIntent(EspHomeConnectionCloseReason.LocalDisconnect);
        supervisor.RequestStop();

        await supervisor.Stopped.Task.WaitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Owns all connection attempts so only one sequential session can exist for this client endpoint.</summary>
    private async Task RunConnectionSupervisorAsync(ConnectionSupervisor supervisor)
    {
        try
        {
            while (!supervisor.IsStopRequested)
            {
                try
                {
                    var closeInfo = await RunConnectionSessionAsync(supervisor).ConfigureAwait(false);
                    if (closeInfo.HasValue) PublishConnectionClosed(closeInfo.Value);
                }
                catch (OperationCanceledException) when (supervisor.IsStopRequested)
                {
                    break;
                }
                catch (UnauthorizedAccessException exception)
                {
                    supervisor.FirstConnected.TrySetException(exception);
                    _logger?.LogError(exception, "ESPHome rejected authentication for {Host}:{Port}.", Host, Port);
                    break;
                }
                catch (NotSupportedException exception)
                {
                    supervisor.FirstConnected.TrySetException(exception);
                    _logger?.LogError(exception, "ESPHome protocol version is not supported for {Host}:{Port}.", Host, Port);
                    break;
                }
                catch (Exception exception)
                {
                    _logger?.LogWarning(exception, "ESPHome connection attempt to {Host}:{Port} failed; the client will reconnect.", Host, Port);
                }

                if (supervisor.IsStopRequested) break;

                try
                {
                    await Task.Delay(ReconnectDelay, _timeProvider, supervisor.StopCancellation.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (supervisor.IsStopRequested)
                {
                    break;
                }
            }
        }
        finally
        {
            if (!supervisor.IsStopRequested) supervisor.RequestStop();

            SetState(ConnectionState.Disconnected);
            Interlocked.CompareExchange(ref _connectionSupervisor, value: null, supervisor);

            if (!supervisor.FirstConnected.Task.IsCompleted)
            {
                var ct = supervisor.StopCancellation.Token;
                supervisor.FirstConnected.TrySetCanceled(ct);
            }

            supervisor.Stopped.TrySetResult(true);
            supervisor.Dispose();
        }
    }

    /// <summary>Creates, establishes, runs, and fully tears down one ESPHome session before another session can start.</summary>
    private async Task<SessionCloseInfo?> RunConnectionSessionAsync(ConnectionSupervisor supervisor)
    {
        var session = await OpenConnectionSessionAsync(supervisor.StopCancellation.Token).ConfigureAwait(false);
        var sessionPublished = false;

        try
        {
            session.WriteLoopTask = WriteMessagesLoopAsync(session);
            session.ReadLoopTask = ReadLoopAsync(session);

            await CompleteApiHandshakeAsync(session, supervisor.StopCancellation.Token).ConfigureAwait(false);
            supervisor.StopCancellation.Token.ThrowIfCancellationRequested();

            var existingSession = Interlocked.CompareExchange(ref _activeSession, session, comparand: null);
            if (existingSession is not null) throw new InvalidOperationException("An ESPHome session is already active.");

            sessionPublished = true;
            supervisor.StopCancellation.Token.ThrowIfCancellationRequested();

            Volatile.Write(ref _lastDisconnectReason, -1);
            SetState(ConnectionState.Connected);
            supervisor.FirstConnected.TrySetResult(true);
            session.KeepAliveLoopTask = KeepAliveLoopAsync(session);
            await EnsureStatesSubscriptionAsync(session, session.Cancellation.Token).ConfigureAwait(false);

            return await WaitForSessionCloseAsync(session, supervisor).ConfigureAwait(false);
        }
        finally
        {
            if (sessionPublished) Interlocked.CompareExchange(ref _activeSession, value: null, session);

            await StopSessionAsync(session).ConfigureAwait(false);
            SetState(ConnectionState.Disconnected);
        }
    }

    /// <summary>Opens TCP and the configured ESPHome transport while keeping partially created resources local until ownership can be transferred safely.</summary>
    private async Task<ConnectionSession> OpenConnectionSessionAsync(CancellationToken ct)
    {
        var tcpClient = new TcpClient();
        NetworkStream? networkStream = null;
        IMessageTransport? messageTransport = null;

        try
        {
            using var tcpTimeoutCancellation = new CancellationTokenSource(TcpConnectTimeout, _timeProvider);
            using var tcpOperationCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct, tcpTimeoutCancellation.Token);

            try
            {
                await tcpClient.ConnectAsync(Host, Port, tcpOperationCancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (tcpTimeoutCancellation.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                throw new TimeoutException($"ESPHome TCP connection did not complete within {TcpConnectTimeout}.");
            }

            ConfigureTcpClient(tcpClient);
            networkStream = tcpClient.GetStream();
            SetState(ConnectionState.SocketOpened);
            _logger?.LogInformation("Client established TCP connection to {Host}:{Port}.", Host, Port);

            if (_noisePsk is null)
            {
                messageTransport = new PlainTextMessageTransport(networkStream);
            }
            else
            {
                using var handshakeTimeoutCancellation = new CancellationTokenSource(NoiseHandshakeTimeout, _timeProvider);
                using var handshakeOperationCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct, handshakeTimeoutCancellation.Token);

                try
                {
                    messageTransport = await CreateNoiseMessageTransportAsync(networkStream, handshakeOperationCancellation.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (handshakeTimeoutCancellation.IsCancellationRequested && !ct.IsCancellationRequested)
                {
                    throw new TimeoutException($"ESPHome Noise handshake did not complete within {NoiseHandshakeTimeout}.");
                }
            }

            SetState(ConnectionState.HandshakeCompleted);
            _logger?.LogInformation("Client transport handshake completed to {Host}:{Port}.", Host, Port);

            var outgoingMessages = CreateMessageWriteChannel();
            var session = new ConnectionSession(tcpClient, networkStream, messageTransport, outgoingMessages);
            return session;
        }
        catch
        {
            DisposeConnectionResource(messageTransport);
            DisposeConnectionResource(networkStream);
            DisposeConnectionResource(tcpClient);
            SetState(ConnectionState.Disconnected);
            throw;
        }
    }

    /// <summary>Completes ESPHome hello and optional legacy password authentication before the session becomes visible to public send operations.</summary>
    private async Task CompleteApiHandshakeAsync(ConnectionSession session, CancellationToken ct)
    {
        var helloRequest = new HelloRequest { ClientInfo = _clientInfo, ApiVersionMajor = 1, ApiVersionMinor = 16 };

        var helloWritten = await TryWriteMessageAndWaitAsync(session.OutgoingMessages.Writer, helloRequest, ct).ConfigureAwait(false);
        if (!helloWritten) throw CreateSetupClosedException(session);

        var helloResponse = await WaitForSetupResponseAsync(session.HelloResponse.Task, session, HelloTimeout, "ESPHome did not answer the hello request within the configured timeout.", ct).ConfigureAwait(false);
        var isApiVersionUnsupported = helloResponse.ApiVersionMajor > 2;
        if (isApiVersionUnsupported) throw new NotSupportedException($"ESPHome API major version {helloResponse.ApiVersionMajor} is not supported.");

        if (_password is not null)
        {
            var authenticationRequest = new AuthenticationRequest { Password = _password };
            var authenticationWritten = await TryWriteMessageAndWaitAsync(session.OutgoingMessages.Writer, authenticationRequest, ct).ConfigureAwait(false);
            if (!authenticationWritten) throw CreateSetupClosedException(session);

            var authenticationResponse = await WaitForSetupResponseAsync(session.AuthenticationResponse.Task, session, HelloTimeout, "ESPHome did not answer the authentication request within the configured timeout.", ct).ConfigureAwait(false);
            HandleAuthenticationResponse(authenticationResponse);
        }

        session.ApiVersion = new ApiVersion(helloResponse.ApiVersionMajor, helloResponse.ApiVersionMinor);
    }

    /// <summary>Waits for one setup response while also terminating promptly when the session fails or the setup timeout expires.</summary>
    private async Task<TResponse> WaitForSetupResponseAsync<TResponse>(Task<TResponse> responseTask, ConnectionSession session, TimeSpan timeout, string timeoutMessage, CancellationToken ct)
    {
        var timeoutTask = Task.Delay(timeout, _timeProvider, ct);
        var completedTask = await Task.WhenAny(responseTask, session.CloseRequested.Task, timeoutTask).ConfigureAwait(false);

        if (ReferenceEquals(completedTask, responseTask)) return await responseTask.ConfigureAwait(false);

        if (ReferenceEquals(completedTask, session.CloseRequested.Task))
        {
            var closeInfo = await session.CloseRequested.Task.ConfigureAwait(false);
            throw closeInfo.Failure ?? new IOException("ESPHome connection closed while the API session was being established.");
        }

        await timeoutTask.ConfigureAwait(false);
        throw new TimeoutException(timeoutMessage);
    }

    /// <summary>Waits until the active session requests closure or the client requests connection maintenance to stop.</summary>
    private async Task<SessionCloseInfo> WaitForSessionCloseAsync(ConnectionSession session, ConnectionSupervisor supervisor)
    {
        if (supervisor.IsStopRequested)
        {
            session.MarkCloseIntent(EspHomeConnectionCloseReason.LocalDisconnect);
            await RequestLocalDisconnectAsync(session).ConfigureAwait(false);
            return await session.CloseRequested.Task.ConfigureAwait(false);
        }

        var completedTask = await Task.WhenAny(session.CloseRequested.Task, supervisor.StopRequested.Task).ConfigureAwait(false);
        if (ReferenceEquals(completedTask, session.CloseRequested.Task)) return await session.CloseRequested.Task.ConfigureAwait(false);

        session.MarkCloseIntent(EspHomeConnectionCloseReason.LocalDisconnect);
        await RequestLocalDisconnectAsync(session).ConfigureAwait(false);
        return await session.CloseRequested.Task.ConfigureAwait(false);
    }

    /// <summary>Performs the ESPHome disconnect exchange when the transport is still usable and always produces a local close request.</summary>
    private async Task RequestLocalDisconnectAsync(ConnectionSession session)
    {
        if (session.CloseRequested.Task.IsCompleted) return;

        session.MarkCloseIntent(EspHomeConnectionCloseReason.LocalDisconnect);
        Exception? diagnosticFailure = null;

        using var timeoutCancellation = new CancellationTokenSource(DisconnectTimeout, _timeProvider);
        using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(session.Cancellation.Token, timeoutCancellation.Token);

        try
        {
            var request = new DisconnectRequest();
            var requestWritten = await TryWriteMessageAndWaitAsync(session.OutgoingMessages.Writer, request, operationCancellation.Token).ConfigureAwait(false);

            if (!requestWritten)
            {
                if (session.CloseRequested.Task.IsCompleted) return;
                diagnosticFailure = new IOException("ESPHome connection closed before the disconnect request could be written.");
            }
            else
            {
                var cancellationTask = Task.Delay(Timeout.InfiniteTimeSpan, _timeProvider, operationCancellation.Token);
                var completedTask = await Task.WhenAny(session.DisconnectResponse.Task, session.CloseRequested.Task, cancellationTask).ConfigureAwait(false);

                if (ReferenceEquals(completedTask, session.CloseRequested.Task)) return;

                if (ReferenceEquals(completedTask, cancellationTask))
                {
                    if (timeoutCancellation.IsCancellationRequested && !session.Cancellation.IsCancellationRequested)
                    {
                        diagnosticFailure = new TimeoutException($"ESPHome did not complete the disconnect exchange within {DisconnectTimeout}.");
                    }
                }
            }
        }
        catch (OperationCanceledException) when (timeoutCancellation.IsCancellationRequested && !session.Cancellation.IsCancellationRequested)
        {
            diagnosticFailure = new TimeoutException($"ESPHome did not complete the disconnect exchange within {DisconnectTimeout}.");
        }
        catch (OperationCanceledException) when (session.Cancellation.IsCancellationRequested)
        {
            if (session.CloseRequested.Task.IsCompleted) return;
        }

        session.RequestClose(EspHomeConnectionCloseReason.LocalDisconnect, diagnosticFailure);
    }

    /// <summary>Stops all loops owned by one session, fails session-bound operations, and releases its transport resources before reconnecting.</summary>
    private async Task StopSessionAsync(ConnectionSession session)
    {
        if (!session.Cancellation.IsCancellationRequested) session.Cancellation.Cancel();
        session.OutgoingMessages.Writer.TryComplete();

        DisposeConnectionResource(session.Transport);
        DisposeConnectionResource(session.NetworkStream);
        DisposeConnectionResource(session.TcpClient);

        await session.ReadLoopTask.ConfigureAwait(false);
        await session.WriteLoopTask.ConfigureAwait(false);
        await session.KeepAliveLoopTask.ConfigureAwait(false);

        FailPendingOperations(session);
        ClearCameraAssemblies(session);
        session.Cancellation.Dispose();
    }

    /// <summary>Sends periodic keep-alive pings and closes the session when ESPHome does not answer within the configured response timeout.</summary>
    private async Task KeepAliveLoopAsync(ConnectionSession session)
    {
        var ct = session.Cancellation.Token;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(KeepAliveInterval, _timeProvider, ct).ConfigureAwait(false);

                if (Volatile.Read(ref session.ApplicationDispatchInProgress) != 0) continue;

                var responseCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var existingResponseCompletion = Interlocked.CompareExchange(ref session.PendingPingResponse, responseCompletion, comparand: null);
                if (existingResponseCompletion is not null) throw new InvalidOperationException("A keep-alive ping is already awaiting its response.");

                try
                {
                    var request = new PingRequest();
                    var queued = await TryQueueMessageAsync(session, request, ct).ConfigureAwait(false);
                    if (!queued) return;

                    try
                    {
                        await responseCompletion.Task.WaitAsync(KeepAliveResponseTimeout, _timeProvider, ct).ConfigureAwait(false);
                    }
                    catch (TimeoutException) when (!ct.IsCancellationRequested)
                    {
                        if (Volatile.Read(ref session.ApplicationDispatchInProgress) != 0) continue;

                        var failure = new TimeoutException($"ESPHome did not answer the keep-alive ping within {KeepAliveResponseTimeout}.");
                        session.RequestClose(EspHomeConnectionCloseReason.TransportFailure, failure);
                        return;
                    }
                }
                finally
                {
                    Interlocked.CompareExchange(ref session.PendingPingResponse, value: null, responseCompletion);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }

    /// <summary>Schedules every connection-close handler independently so consumer latency and failures never delay reconnection.</summary>
    private void PublishConnectionClosed(SessionCloseInfo closeInfo)
    {
        if (closeInfo.RemoteReason.HasValue) Volatile.Write(ref _lastDisconnectReason, closeInfo.RemoteReason.Value);

        var handlers = ConnectionClosed?.GetInvocationList();
        if (handlers is null) return;

        var eventArgs = new EspHomeConnectionClosed(closeInfo.Reason, closeInfo.RemoteReason, closeInfo.Failure);

        foreach (var handlerDelegate in handlers)
        {
            if (handlerDelegate is not EspHomeConnectionClosedHandler handler) continue;
            _ = InvokeConnectionClosedHandlerAsync(handler, eventArgs);
        }
    }

    /// <summary>Runs one consumer handler asynchronously and isolates its failures from the client connection lifecycle.</summary>
    private async Task InvokeConnectionClosedHandlerAsync(EspHomeConnectionClosedHandler handler, EspHomeConnectionClosed eventArgs)
    {
        await Task.Yield();

        try
        {
            await handler(this, eventArgs).ConfigureAwait(false);
        }
        catch (Exception)
        {
        }
    }

    /// <summary>Creates a setup exception from the first session failure when a queued setup write cannot complete.</summary>
    private static Exception CreateSetupClosedException(ConnectionSession session)
    {
        if (!session.CloseRequested.Task.IsCompleted)
        {
            var exception = new IOException("ESPHome connection closed while the API session was being established.");
            return exception;
        }

        var closeInfo = session.CloseRequested.Task.GetAwaiter().GetResult();
        if (closeInfo.Failure is not null) return closeInfo.Failure;

        var setupException = new IOException("ESPHome connection closed while the API session was being established.");
        return setupException;
    }

    /// <summary>Releases one connection resource without allowing cleanup failures to prevent the remaining session resources from being released.</summary>
    private void DisposeConnectionResource(IDisposable? resource)
    {
        if (resource is null) return;

        try
        {
            resource.Dispose();
        }
        catch (Exception exception)
        {
            _logger?.LogWarning(exception, "Failed to dispose an ESPHome connection resource for {Host}:{Port}.", Host, Port);
        }
    }

    /// <summary>Prevents new operations after the client lifetime has ended.</summary>
    private void ThrowIfDisposed()
    {
        var isDisposed = Volatile.Read(ref _disposed) != 0;
        if (isDisposed) throw new ObjectDisposedException(nameof(EspHomeClient));
    }

    /// <summary>Updates the observable connection state without using it as an ownership or synchronization mechanism.</summary>
    private void SetState(ConnectionState state)
    {
        Volatile.Write(ref _state, (int)state);
    }

    /// <summary>Stops connection maintenance and permanently completes all application-facing input channels.</summary>
    public async ValueTask DisposeAsync()
    {
        var wasAlreadyDisposed = Interlocked.Exchange(ref _disposed, 1) != 0;
        if (wasAlreadyDisposed) return;

        var activeSession = Volatile.Read(ref _activeSession);
        activeSession?.MarkCloseIntent(EspHomeConnectionCloseReason.LocalDisconnect);

        var supervisor = Volatile.Read(ref _connectionSupervisor);
        if (supervisor is not null)
        {
            supervisor.RequestStop();
            await supervisor.Stopped.Task.ConfigureAwait(false);
        }

        _incomingMessagesChannel.Writer.TryComplete();
        _incomingStateChangeMessages.Writer.TryComplete();
        _dynamicEntitiesRegistrationChannel.Writer.TryComplete();
        GC.SuppressFinalize(this);
    }

    /// <summary>Coordinates one client-level period in which the client continuously maintains a session to its fixed endpoint.</summary>
    private sealed class ConnectionSupervisor : IDisposable
    {
        private int _stopRequested;

        /// <summary>Cancels setup and reconnect delays when connection maintenance is requested to stop.</summary>
        public CancellationTokenSource StopCancellation { get; } = new();

        /// <summary>Signals the first successfully established session to callers waiting in <see cref="ConnectAsync"/>.</summary>
        public TaskCompletionSource<bool> FirstConnected { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Signals the supervisor loop to gracefully close an already established session.</summary>
        public TaskCompletionSource<bool> StopRequested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Signals that no setup attempt or active session remains owned by this supervisor.</summary>
        public TaskCompletionSource<bool> Stopped { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Gets whether connection maintenance has been requested to stop.</summary>
        public bool IsStopRequested => Volatile.Read(ref _stopRequested) != 0;

        /// <summary>Requests shutdown exactly once and wakes both setup waits and an active session wait.</summary>
        public void RequestStop()
        {
            var wasAlreadyRequested = Interlocked.Exchange(ref _stopRequested, 1) != 0;
            if (wasAlreadyRequested) return;

            StopRequested.TrySetResult(true);
            StopCancellation.Cancel();
        }

        /// <summary>Releases the cancellation source after the supervisor no longer owns any asynchronous operation.</summary>
        public void Dispose()
        {
            StopCancellation.Dispose();
        }
    }

    /// <summary>Owns every resource and pending protocol operation that must never survive an ESPHome reconnect.</summary>
    private sealed class ConnectionSession
    {
        private int _closeIntent;
        private int _remoteDisconnectReason = -1;

        /// <summary>Creates one fully transport-initialized session with fresh outgoing and response queues that cannot survive reconnect.</summary>
        public ConnectionSession(TcpClient tcpClient, NetworkStream networkStream, IMessageTransport transport, Channel<QueuedMessage> outgoingMessages)
        {
            TcpClient = tcpClient;
            NetworkStream = networkStream;
            Transport = transport;
            OutgoingMessages = outgoingMessages;

            ResponseCallbacks = Enumerable.Range(0, IESPHomeMessage.MaxMsgId +1).Select(x=> new PendingResponseQueue()).ToArray();
        }

        /// <summary>Gets the socket owned exclusively by this session.</summary>
        public TcpClient TcpClient { get; }

        /// <summary>Gets the network stream owned exclusively by this session.</summary>
        public NetworkStream NetworkStream { get; }

        /// <summary>Gets the plaintext or Noise transport used by all protocol loops in this session.</summary>
        public IMessageTransport Transport { get; }

        /// <summary>Gets the session-local queue that serializes every protocol write.</summary>
        public Channel<QueuedMessage> OutgoingMessages { get; }

        /// <summary>Gets the token source that terminates all loops belonging to this session.</summary>
        public CancellationTokenSource Cancellation { get; } = new();

        /// <summary>Gets the setup waiter completed by the ESPHome hello response.</summary>
        public TaskCompletionSource<HelloResponse> HelloResponse { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Gets the setup waiter completed by the legacy password authentication response.</summary>
        public TaskCompletionSource<AuthenticationResponse> AuthenticationResponse { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Gets the graceful-disconnect waiter completed by the ESPHome disconnect response.</summary>
        public TaskCompletionSource<bool> DisconnectResponse { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Gets the single terminal close request that causes the supervisor to tear this session down.</summary>
        public TaskCompletionSource<SessionCloseInfo> CloseRequested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Gets response waiters in registration order, indexed by the protocol message identifier they expect from this session.</summary>
        public PendingResponseQueue[] ResponseCallbacks { get; }

        /// <summary>Gets or sets the response signal for the single keep-alive ping currently in flight.</summary>
        public TaskCompletionSource<bool>? PendingPingResponse;

        /// <summary>Indicates that the read loop is dispatching an application message and may currently be blocked by public-channel backpressure.</summary>
        public int ApplicationDispatchInProgress;

        /// <summary>Gets partial camera frames whose chunks must all originate from this session.</summary>
        public Dictionary<(uint DeviceId, uint Key), CameraFrameAssembly?> CameraAssemblies { get; } = [];

        /// <summary>Gets or sets the active entity discovery assembly for this session.</summary>
        public EntityListAssembly? CurrentEntityListAssembly;

        /// <summary>Gets or sets the API version reported during this session setup.</summary>
        public ApiVersion? ApiVersion { get; set; }

        /// <summary>Gets or sets the read loop task after protocol processing starts.</summary>
        public Task ReadLoopTask { get; set; } = Task.CompletedTask;

        /// <summary>Gets or sets the serialized write loop task after protocol processing starts.</summary>
        public Task WriteLoopTask { get; set; } = Task.CompletedTask;

        /// <summary>Gets or sets the keep-alive loop task after the session becomes connected.</summary>
        public Task KeepAliveLoopTask { get; set; } = Task.CompletedTask;

        /// <summary>Ensures the persistent state subscription is sent at most once in this session.</summary>
        public int StatesSubscriptionSent;

        /// <summary>Records a local or remote disconnect intent so secondary transport failures keep the initiating close classification.</summary>
        public void MarkCloseIntent(EspHomeConnectionCloseReason reason, int? remoteReason = null)
        {
            var isExpectedDisconnect = reason is EspHomeConnectionCloseReason.LocalDisconnect or EspHomeConnectionCloseReason.RemoteDisconnect;
            if (!isExpectedDisconnect) throw new ArgumentOutOfRangeException(nameof(reason), reason, "Only local and remote disconnect can be stored as close intent.");

            if (reason == EspHomeConnectionCloseReason.RemoteDisconnect)
            {
                if (remoteReason.HasValue) Volatile.Write(ref _remoteDisconnectReason, remoteReason.Value);
                Interlocked.Exchange(ref _closeIntent, (int)EspHomeConnectionCloseReason.RemoteDisconnect);
                return;
            }

            Interlocked.CompareExchange(ref _closeIntent, (int)EspHomeConnectionCloseReason.LocalDisconnect, 0);
        }

        /// <summary>Requests session termination exactly once while preserving an already declared local or remote disconnect intent.</summary>
        public void RequestClose(EspHomeConnectionCloseReason fallbackReason, Exception? failure = null)
        {
            var effectiveReason = ResolveCloseReason(fallbackReason);
            var remoteReasonValue = Volatile.Read(ref _remoteDisconnectReason);
            int? remoteReason = effectiveReason == EspHomeConnectionCloseReason.RemoteDisconnect && remoteReasonValue >= 0 ? remoteReasonValue : null;
            var closeInfo = new SessionCloseInfo(effectiveReason, remoteReason, failure);
            CloseRequested.TrySetResult(closeInfo);
        }

        /// <summary>Returns the declared disconnect intent when one exists, otherwise the supplied failure classification.</summary>
        private EspHomeConnectionCloseReason ResolveCloseReason(EspHomeConnectionCloseReason fallbackReason)
        {
            var closeIntent = (EspHomeConnectionCloseReason)Volatile.Read(ref _closeIntent);
            var hasExpectedIntent = closeIntent is EspHomeConnectionCloseReason.LocalDisconnect or EspHomeConnectionCloseReason.RemoteDisconnect;
            return hasExpectedIntent ? closeIntent : fallbackReason;
        }
    }

    /// <summary>Describes why one established session ended without coupling session loops to global client state.</summary>
    private readonly struct SessionCloseInfo(EspHomeConnectionCloseReason reason, int? remoteReason, Exception? failure)
    {
        /// <summary>Gets the semantic reason that caused the session to end.</summary>
        public EspHomeConnectionCloseReason Reason { get; } = reason;

        /// <summary>Gets the raw reason supplied by ESPHome for a remote disconnect.</summary>
        public int? RemoteReason { get; } = remoteReason;

        /// <summary>Gets the first diagnostic failure associated with the session end when one exists.</summary>
        public Exception? Failure { get; } = failure;
    }
}
