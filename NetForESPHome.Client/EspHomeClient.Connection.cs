using Microsoft.Extensions.Logging;
using NetForESPHome.Client.Model;
using NetForESPHome.Transports;
using System.Net.Sockets;
using System.Threading.Channels;

namespace NetForESPHome.Client;

/// <summary>Represents the lifecycle state of an ESPHome Native API connection.</summary>
public enum ConnectionState
{
    SocketOpened,
    HandshakeCompleted,
    Connected,
    Disconnected
}

public sealed partial class EspHomeClient
{
    private static readonly TimeSpan TcpConnectTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan NoiseHandshakeTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan HelloTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DisconnectTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan EntityListResponseTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan KeepAliveResponseTimeout = TimeSpan.FromSeconds(90);

    private readonly string? _noisePsk;
    private readonly string? _password;
    private readonly string _clientInfo;
    public readonly string Host;
    public readonly int Port;

    private TcpClient? _tcpClient;
    private NetworkStream? _networkStream;
    private IMessageTransport? _messageTransport;
    private CancellationTokenSource? _connectionCancellation;

    private const int ConnectionLifecycleStarting = 0;
    private const int ConnectionLifecycleEstablished = 1;
    private const int ConnectionLifecycleClosed = 2;

    private long _lastMessageReceivedTimestamp;
    private long _connectionGeneration;
    private int _state = (int)ConnectionState.Disconnected;
    private int _connectInProgress;
    private int _connectionLifecycle = ConnectionLifecycleClosed;
    private int _closeIntent;
    private int _disposed;

    /// <summary>Establishes TCP, optional Noise transport and the ESPHome Native API session.</summary>
    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();

        var isAnotherConnectInProgress = Interlocked.CompareExchange(ref _connectInProgress, 1, 0) != 0;
        if (isAnotherConnectInProgress) throw new InvalidOperationException("Client is already connecting.");

        try
        {
            await WaitForDisconnectOperationAsync(cancellationToken).ConfigureAwait(false);

            var isConnectionActive = State != ConnectionState.Disconnected;
            if (isConnectionActive) throw new InvalidOperationException($"Client is already connected or connecting. State: {State}");

            await WaitForConnectionLoopsAsync().ConfigureAwait(false);

            var connectionCancellation = new CancellationTokenSource();
            var existingConnectionCancellation = Interlocked.CompareExchange(ref _connectionCancellation, connectionCancellation, null);

            if (existingConnectionCancellation is not null)
            {
                connectionCancellation.Dispose();
                throw new InvalidOperationException("The previous connection has not finished shutting down.");
            }

            var connectionGeneration = Interlocked.Increment(ref _connectionGeneration);
            var outputGeneration = Volatile.Read(ref _outputGeneration);

            Volatile.Write(ref _connectionLifecycle, ConnectionLifecycleStarting);
            Volatile.Write(ref _closeIntent, 0);
            Volatile.Write(ref _connectionFailure, null);
            ApiVersion = null;
            LastDisconnectReason = null;

            try
            {
                ThrowIfDisposed();
                ThrowIfConnectionFailed();

                var tcpClient = new TcpClient();
                _tcpClient = tcpClient;

                using var tcpTimeoutCancellation = new CancellationTokenSource(TcpConnectTimeout, _timeProvider);
                using var tcpOperationCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, connectionCancellation.Token, tcpTimeoutCancellation.Token);

                try
                {
                    await tcpClient.ConnectAsync(Host, Port, tcpOperationCancellation.Token).ConfigureAwait(false);
                    _logger?.LogInformation("Client established TCP connection to {Host}:{Port}.", Host, Port);
                }
                catch (OperationCanceledException) when (tcpTimeoutCancellation.IsCancellationRequested && !cancellationToken.IsCancellationRequested && !connectionCancellation.IsCancellationRequested)
                {
                    throw new TimeoutException($"ESPHome TCP connection did not complete within {TcpConnectTimeout}.");
                }

                ThrowIfConnectionFailed();

                ConfigureTcpClient(tcpClient);

                var networkStream = tcpClient.GetStream();
                _networkStream = networkStream;
                SetState(ConnectionState.SocketOpened);
                _logger?.LogInformation("Client socket opened to {Host}:{Port}.", Host, Port);

                IMessageTransport messageTransport;

                if (_noisePsk is null)
                {
                    messageTransport = new PlainTextMessageTransport(networkStream);
                }
                else
                {
                    using var handshakeTimeoutCancellation = new CancellationTokenSource(NoiseHandshakeTimeout, _timeProvider);
                    using var handshakeOperationCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, connectionCancellation.Token, handshakeTimeoutCancellation.Token);

                    try
                    {
                        messageTransport = await CreateMessageTransportAsync(networkStream, handshakeOperationCancellation.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (handshakeTimeoutCancellation.IsCancellationRequested && !cancellationToken.IsCancellationRequested && !connectionCancellation.IsCancellationRequested)
                    {
                        throw new TimeoutException($"ESPHome Noise handshake did not complete within {NoiseHandshakeTimeout}.");
                    }
                }

                _messageTransport = messageTransport;

                ThrowIfConnectionFailed();
                SetState(ConnectionState.HandshakeCompleted);
                _logger?.LogInformation("Client handshake completed to {Host}:{Port}.", Host, Port);

                var messageWriteChannel = CreateMessageWriteChannel();
                _messageWriteChannel = messageWriteChannel;

                var helloResponseSource = new TaskCompletionSource<HelloResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
                _helloResponseSource = helloResponseSource;

                ThrowIfConnectionFailed();

                Volatile.Write(ref _lastMessageReceivedTimestamp, _timeProvider.GetTimestamp());

                _writeLoopTask = WriteMessagesLoopAsync(messageTransport, messageWriteChannel.Reader, connectionGeneration, connectionCancellation.Token);
                _readLoopTask = ReadLoopAsync(messageTransport, messageWriteChannel.Writer, connectionGeneration, outputGeneration, connectionCancellation.Token);

                var helloRequest = new HelloRequest { ClientInfo = _clientInfo, ApiVersionMajor = 1, ApiVersionMinor = 16 };
                var helloWritten = await TryWriteMessageAndWaitAsync(messageWriteChannel.Writer, helloRequest, cancellationToken).ConfigureAwait(false);

                if (!helloWritten)
                {
                    ThrowIfConnectionFailed();
                    _logger?.LogError("ESPHome connection closed before the hello request could be written to {Host}:{Port}.", Host, Port);
                    throw new IOException("ESPHome connection closed before the hello request could be written.");
                }

                if (_password is not null)
                {
                    var authenticationRequest = new AuthenticationRequest { Password = _password };
                    var authenticationWritten = await TryWriteMessageAndWaitAsync(messageWriteChannel.Writer, authenticationRequest, cancellationToken).ConfigureAwait(false);

                    if (!authenticationWritten)
                    {
                        ThrowIfConnectionFailed();
                        _logger?.LogError("ESPHome connection closed before the authentication request could be written to {Host}:{Port}.", Host, Port);
                        throw new IOException("ESPHome connection closed before the authentication request could be written.");
                    }
                }

                HelloResponse helloResponse;

                try
                {
                    helloResponse = await helloResponseSource.Task.WaitAsync(HelloTimeout, _timeProvider, cancellationToken).ConfigureAwait(false);
                }
                catch (TimeoutException exception) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new TimeoutException($"ESPHome did not answer the hello request within {HelloTimeout}.", exception);
                }

                var isApiVersionUnsupported = helloResponse.ApiVersionMajor > 2;
                if (isApiVersionUnsupported) throw new NotSupportedException($"ESPHome API major version {helloResponse.ApiVersionMajor} is not supported.");

                ApiVersion = new ApiVersion(helloResponse.ApiVersionMajor, helloResponse.ApiVersionMinor);
                Interlocked.CompareExchange(ref _helloResponseSource, null, helloResponseSource);

                ThrowIfConnectionFailed();

                var lifecycleBeforeEstablish = Interlocked.CompareExchange(ref _connectionLifecycle, ConnectionLifecycleEstablished, ConnectionLifecycleStarting);
                if (lifecycleBeforeEstablish != ConnectionLifecycleStarting)
                {
                    ThrowIfConnectionFailed();
                    _logger?.LogError("ESPHome connection closed while the session was being established to {Host}:{Port}.", Host, Port);
                    throw new IOException("ESPHome connection closed while the session was being established.");
                }

                SetState(ConnectionState.Connected);

                if (Volatile.Read(ref _connectionLifecycle) != ConnectionLifecycleEstablished)
                {
                    SetState(ConnectionState.Disconnected);
                    ThrowIfConnectionFailed();
                    throw new IOException("ESPHome connection closed while the session was being established.");
                }

                _keepAliveLoopTask = KeepAliveLoopAsync(messageWriteChannel.Writer, connectionGeneration, connectionCancellation.Token);
                ThrowIfConnectionFailed();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                MarkCloseIntent(EspHomeConnectionCloseReason.LocalDisconnect);
                CloseConnection(connectionGeneration, EspHomeConnectionCloseReason.LocalDisconnect, publishConnectionClosed: false);
                await WaitForConnectionLoopsAsync().ConfigureAwait(false);
                _logger?.LogError("ESPHome connection closed due to cancellation while the session was being established to {Host}:{Port}.", Host, Port);
                throw;
            }
            catch (OperationCanceledException exception)
            {
                var connectionFailure = Volatile.Read(ref _connectionFailure);
                var setupFailure = connectionFailure ?? new IOException("ESPHome connection closed while the session was being established.", exception);
                var closeReason = ResolveCloseReason(EspHomeConnectionCloseReason.TransportFailure);

                CloseConnection(connectionGeneration, closeReason, setupFailure);
                await WaitForConnectionLoopsAsync().ConfigureAwait(false);
                _logger?.LogError("ESPHome connection closed due to cancellation while the session was being established to {Host}:{Port}.", Host, Port);
                throw setupFailure;
            }
            catch (Exception exception)
            {
                var closeReason = ResolveCloseReason(EspHomeConnectionCloseReason.TransportFailure);
                CloseConnection(connectionGeneration, closeReason, exception);
                await WaitForConnectionLoopsAsync().ConfigureAwait(false);
                _logger?.LogError("ESPHome connection closed due to transport failure while the session was being established to {Host}:{Port}.", Host, Port);
                throw;
            }
        }
        finally
        {
            Volatile.Write(ref _connectInProgress, 0);
        }
    }

    /// <summary>Sends keep-alive pings after inactivity and closes the connection when no valid inbound message arrives within the response timeout.</summary>
    private async Task KeepAliveLoopAsync(ChannelWriter<QueuedMessage> writer, long connectionGeneration, CancellationToken cancellationToken)
    {
        long? pendingPingStartedTimestamp = null;
        long lastMessageTimestampWhenPingStarted = 0;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var lastMessageTimestamp = Volatile.Read(ref _lastMessageReceivedTimestamp);
                var receivedMessageAfterPing = pendingPingStartedTimestamp.HasValue && lastMessageTimestamp != lastMessageTimestampWhenPingStarted;

                if (receivedMessageAfterPing)
                {
                    pendingPingStartedTimestamp = null;
                    continue;
                }

                if (!pendingPingStartedTimestamp.HasValue)
                {
                    var inactivity = _timeProvider.GetElapsedTime(lastMessageTimestamp);
                    var timeUntilPing = KeepAliveInterval - inactivity;

                    if (timeUntilPing > TimeSpan.Zero)
                    {
                        await Task.Delay(timeUntilPing, _timeProvider, cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    var request = new PingRequest();
                    var queued = await TryQueueMessageAsync(writer, request, cancellationToken).ConfigureAwait(false);
                    if (!queued) return;

                    pendingPingStartedTimestamp = _timeProvider.GetTimestamp();
                    lastMessageTimestampWhenPingStarted = lastMessageTimestamp;
                    continue;
                }

                var responseWaitTime = _timeProvider.GetElapsedTime(pendingPingStartedTimestamp.Value);
                var responseTimedOut = responseWaitTime >= KeepAliveResponseTimeout;

                if (responseTimedOut)
                {
                    var exception = new TimeoutException($"ESPHome did not produce a valid message within {KeepAliveResponseTimeout} after the keep-alive ping.");
                    var closeReason = ResolveCloseReason(EspHomeConnectionCloseReason.KeepAliveTimeout);
                    CloseConnection(connectionGeneration, closeReason, exception);
                    return;
                }

                var remainingResponseTime = KeepAliveResponseTimeout - responseWaitTime;
                var delay = remainingResponseTime < KeepAliveInterval ? remainingResponseTime : KeepAliveInterval;

                await Task.Delay(delay, _timeProvider, cancellationToken).ConfigureAwait(false);

                lastMessageTimestamp = Volatile.Read(ref _lastMessageReceivedTimestamp);
                receivedMessageAfterPing = lastMessageTimestamp != lastMessageTimestampWhenPingStarted;

                if (receivedMessageAfterPing)
                {
                    pendingPingStartedTimestamp = null;
                    continue;
                }

                responseWaitTime = _timeProvider.GetElapsedTime(pendingPingStartedTimestamp.Value);
                responseTimedOut = responseWaitTime >= KeepAliveResponseTimeout;

                if (responseTimedOut)
                {
                    var exception = new TimeoutException($"ESPHome did not produce a valid message within {KeepAliveResponseTimeout} after the keep-alive ping.");
                    var closeReason = ResolveCloseReason(EspHomeConnectionCloseReason.KeepAliveTimeout);
                    CloseConnection(connectionGeneration, closeReason, exception);
                    return;
                }

                var repeatedRequest = new PingRequest();
                var repeatedQueued = await TryQueueMessageAsync(writer, repeatedRequest, cancellationToken).ConfigureAwait(false);
                if (!repeatedQueued) return;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            HandleConnectionFailure(connectionGeneration, exception);
        }
    }

    /// <summary>Gracefully closes the active ESPHome session while sharing one physical disconnect operation between concurrent callers.</summary>
    public Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();

        var operationSource = Volatile.Read(ref _disconnectOperationSource);

        if (operationSource is null)
        {
            var newOperationSource = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var existingOperationSource = Interlocked.CompareExchange(ref _disconnectOperationSource, newOperationSource, null);

            if (existingOperationSource is null)
            {
                operationSource = newOperationSource;
                _ = RunDisconnectAsync(operationSource);
            }
            else
            {
                operationSource = existingOperationSource;
            }
        }

        return operationSource.Task.WaitAsync(cancellationToken);
    }

    /// <summary>Runs the shared disconnect operation independently from cancellation requested by individual callers.</summary>
    private async Task RunDisconnectAsync(TaskCompletionSource<bool> operationSource)
    {
        try
        {
            await DisconnectCoreAsync().ConfigureAwait(false);
            operationSource.TrySetResult(true);
        }
        catch (Exception exception)
        {
            operationSource.TrySetException(exception);
        }
        finally
        {
            Interlocked.CompareExchange(ref _disconnectOperationSource, null, operationSource);
        }
    }

    /// <summary>Performs a best-effort ESPHome disconnect exchange and always closes the local session as a local lifecycle event.</summary>
    private async Task DisconnectCoreAsync()
    {
        _logger?.LogInformation("ESPHome connection is being closed by local request to {Host}:{Port}.", Host, Port);

        var connectionGeneration = Volatile.Read(ref _connectionGeneration);
        var connectionCancellation = Volatile.Read(ref _connectionCancellation);

        if (State == ConnectionState.Disconnected)
        {
            if (connectionCancellation is not null) CloseConnection(connectionGeneration, EspHomeConnectionCloseReason.LocalDisconnect);
            await WaitForConnectionLoopsAsync().ConfigureAwait(false);
            return;
        }

        MarkCloseIntent(EspHomeConnectionCloseReason.LocalDisconnect);

        if (State != ConnectionState.Connected)
        {
            CloseConnection(connectionGeneration, EspHomeConnectionCloseReason.LocalDisconnect);
            await WaitForConnectionLoopsAsync().ConfigureAwait(false);
            return;
        }

        var messageWriteChannel = Volatile.Read(ref _messageWriteChannel);

        if (messageWriteChannel is null)
        {
            CloseConnection(connectionGeneration, EspHomeConnectionCloseReason.LocalDisconnect);
            await WaitForConnectionLoopsAsync().ConfigureAwait(false);
            return;
        }

        var responseSource = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var existingResponseSource = Interlocked.CompareExchange(ref _disconnectResponseSource, responseSource, null);
        if (existingResponseSource is not null) throw new InvalidOperationException("A disconnect response waiter is already active.");

        connectionCancellation = Volatile.Read(ref _connectionCancellation);
        var connectionToken = connectionCancellation?.Token ?? CancellationToken.None;

        using var timeoutCancellation = new CancellationTokenSource(DisconnectTimeout, _timeProvider);
        using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(connectionToken, timeoutCancellation.Token);

        Exception? diagnosticFailure = null;

        try
        {
            var request = new DisconnectRequest();
            var requestWritten = await TryWriteMessageAndWaitAsync(messageWriteChannel.Writer, request, operationCancellation.Token).ConfigureAwait(false);

            if (!requestWritten)
            {
                diagnosticFailure = Volatile.Read(ref _connectionFailure);
                return;
            }

            await responseSource.Task.WaitAsync(operationCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeoutCancellation.IsCancellationRequested && !connectionToken.IsCancellationRequested)
        {
            diagnosticFailure = new TimeoutException($"ESPHome did not complete the disconnect exchange within {DisconnectTimeout}.");
        }
        catch (OperationCanceledException) when (connectionToken.IsCancellationRequested)
        {
            diagnosticFailure ??= Volatile.Read(ref _connectionFailure);
        }
        finally
        {
            Interlocked.CompareExchange(ref _disconnectResponseSource, null, responseSource);

            CloseConnection(connectionGeneration, EspHomeConnectionCloseReason.LocalDisconnect, diagnosticFailure);
            await WaitForConnectionLoopsAsync().ConfigureAwait(false);
        }


    }

    /// <summary>Marks local or remote disconnect intent before any write that can race with a transport failure.</summary>
    private void MarkCloseIntent(EspHomeConnectionCloseReason reason)
    {
        var isExpectedDisconnect = reason is EspHomeConnectionCloseReason.LocalDisconnect or EspHomeConnectionCloseReason.RemoteDisconnect;
        if (!isExpectedDisconnect) throw new ArgumentOutOfRangeException(nameof(reason), reason, "Only local and remote disconnect can be stored as close intent.");

        if (reason == EspHomeConnectionCloseReason.RemoteDisconnect)
        {
            Interlocked.Exchange(ref _closeIntent, (int)EspHomeConnectionCloseReason.RemoteDisconnect);
            return;
        }

        Interlocked.CompareExchange(ref _closeIntent, (int)EspHomeConnectionCloseReason.LocalDisconnect, 0);
    }

    /// <summary>Preserves an already-declared local or remote disconnect classification over later transport failures.</summary>
    private EspHomeConnectionCloseReason ResolveCloseReason(EspHomeConnectionCloseReason fallbackReason)
    {
        var closeIntent = (EspHomeConnectionCloseReason)Volatile.Read(ref _closeIntent);
        var hasExpectedIntent = closeIntent is EspHomeConnectionCloseReason.LocalDisconnect or EspHomeConnectionCloseReason.RemoteDisconnect;
        return hasExpectedIntent ? closeIntent : fallbackReason;
    }

    /// <summary>Claims lifecycle closure exactly once, invalidates prior output, completes pending operations and releases all current transport resources.</summary>
    private void CloseConnection(long connectionGeneration, EspHomeConnectionCloseReason reason, Exception? failure = null, bool publishConnectionClosed = true)
    {
        var activeConnectionGeneration = Volatile.Read(ref _connectionGeneration);
        if (connectionGeneration != activeConnectionGeneration) return;

        var previousLifecycle = Interlocked.Exchange(ref _connectionLifecycle, ConnectionLifecycleClosed);
        if (previousLifecycle == ConnectionLifecycleClosed) return;

        var effectiveReason = reason == EspHomeConnectionCloseReason.OutputConsumerStalled ? reason : ResolveCloseReason(reason);
        var wasEstablished = previousLifecycle == ConnectionLifecycleEstablished;
        var newOutputGeneration = wasEstablished ? Interlocked.Increment(ref _outputGeneration) : Volatile.Read(ref _outputGeneration);

        SetState(ConnectionState.Disconnected);

        var shouldPublishConnectionClosed = publishConnectionClosed && wasEstablished && effectiveReason != EspHomeConnectionCloseReason.OutputConsumerStalled;
        if (shouldPublishConnectionClosed)
        {
            var remoteReason = effectiveReason == EspHomeConnectionCloseReason.RemoteDisconnect ? LastDisconnectReason : null;
            var payload = new EspHomeConnectionClosed(effectiveReason, remoteReason, failure);
            var message = new EspHomeMessage(EspHomeMessageType.ConnectionClosed, payload);
            var envelope = new OutputMessageEnvelope(newOutputGeneration, message);

            _outputChannel.Writer.TryWrite(envelope);
        }

        FailPendingOperations();

        var closeFailure = failure;
        if (closeFailure is not null) Interlocked.CompareExchange(ref _connectionFailure, closeFailure, null);

        var helloResponseSource = Interlocked.Exchange(ref _helloResponseSource, null);
        var disconnectResponseSource = Interlocked.Exchange(ref _disconnectResponseSource, null);

        if (helloResponseSource is not null)
        {
            var setupFailure = closeFailure ?? new IOException("ESPHome connection closed during setup.");
            helloResponseSource.TrySetException(setupFailure);
        }

        disconnectResponseSource?.TrySetResult(false);

        var connectionCancellation = Volatile.Read(ref _connectionCancellation);
        closeFailure = CancelConnection(connectionCancellation, closeFailure);

        var messageWriteChannel = Interlocked.Exchange(ref _messageWriteChannel, null);
        messageWriteChannel?.Writer.TryComplete();

        var messageTransport = Interlocked.Exchange(ref _messageTransport, null);
        var networkStream = Interlocked.Exchange(ref _networkStream, null);
        var tcpClient = Interlocked.Exchange(ref _tcpClient, null);

        closeFailure = DisposeConnectionResource(messageTransport, closeFailure);
        closeFailure = DisposeConnectionResource(networkStream, closeFailure);
        closeFailure = DisposeConnectionResource(tcpClient, closeFailure);

        if (closeFailure is not null) Interlocked.CompareExchange(ref _connectionFailure, closeFailure, null);

        ApiVersion = null;
    }

    /// <summary>Cancels the current connection token while preserving the first diagnostic failure if cancellation callbacks fail.</summary>
    private static Exception? CancelConnection(CancellationTokenSource? cancellation, Exception? failure)
    {
        if (cancellation is null || cancellation.IsCancellationRequested) return failure;

        try
        {
            cancellation.Cancel();
        }
        catch (Exception exception)
        {
            failure ??= exception;
        }

        return failure;
    }

    /// <summary>Disposes one owned connection resource while preserving the first diagnostic failure.</summary>
    private static Exception? DisposeConnectionResource(IDisposable? resource, Exception? failure)
    {
        if (resource is null) return failure;

        try
        {
            resource.Dispose();
        }
        catch (Exception exception)
        {
            failure ??= exception;
        }

        return failure;
    }

    /// <summary>Waits for an already-running shared disconnect operation before a new connection generation is allowed to start.</summary>
    private async Task WaitForDisconnectOperationAsync(CancellationToken cancellationToken)
    {
        var operationSource = Volatile.Read(ref _disconnectOperationSource);
        if (operationSource is null) return;

        await operationSource.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        Interlocked.CompareExchange(ref _disconnectOperationSource, null, operationSource);
    }

    /// <summary>Waits for the captured connection loops and releases their cancellation source without affecting a newer connection.</summary>
    private async Task WaitForConnectionLoopsAsync()
    {
        var readLoopTask = Volatile.Read(ref _readLoopTask);
        var writeLoopTask = Volatile.Read(ref _writeLoopTask);
        var keepAliveLoopTask = Volatile.Read(ref _keepAliveLoopTask);
        var connectionCancellation = Volatile.Read(ref _connectionCancellation);

        if (readLoopTask is not null) await readLoopTask.ConfigureAwait(false);
        if (writeLoopTask is not null) await writeLoopTask.ConfigureAwait(false);
        if (keepAliveLoopTask is not null) await keepAliveLoopTask.ConfigureAwait(false);

        if (readLoopTask is not null) Interlocked.CompareExchange(ref _readLoopTask, null, readLoopTask);
        if (writeLoopTask is not null) Interlocked.CompareExchange(ref _writeLoopTask, null, writeLoopTask);
        if (keepAliveLoopTask is not null) Interlocked.CompareExchange(ref _keepAliveLoopTask, null, keepAliveLoopTask);

        if (connectionCancellation is null) return;

        var ownedCancellation = Interlocked.CompareExchange(ref _connectionCancellation, null, connectionCancellation);
        var ownsCancellation = ReferenceEquals(ownedCancellation, connectionCancellation);

        if (ownsCancellation) connectionCancellation.Dispose();
    }

    /// <summary>Prevents new operations after the client lifetime has ended.</summary>
    private void ThrowIfDisposed()
    {
        var isDisposed = Volatile.Read(ref _disposed) != 0;
        if (isDisposed) throw new ObjectDisposedException(nameof(EspHomeClient));
    }

    /// <summary>Stops the active connection, waits for protocol processing to finish and permanently completes the application output stream.</summary>
    public async ValueTask DisposeAsync()
    {
        var wasAlreadyDisposed = Interlocked.Exchange(ref _disposed, 1) != 0;
        if (wasAlreadyDisposed) return;

        var connectionGeneration = Volatile.Read(ref _connectionGeneration);
        var hasActiveConnection = State != ConnectionState.Disconnected || Volatile.Read(ref _connectionCancellation) is not null;

        if (hasActiveConnection)
        {
            MarkCloseIntent(EspHomeConnectionCloseReason.LocalDisconnect);
            CloseConnection(connectionGeneration, EspHomeConnectionCloseReason.LocalDisconnect);
        }

        await WaitForConnectionLoopsAsync().ConfigureAwait(false);

        try
        {
            var disconnectOperationSource = Volatile.Read(ref _disconnectOperationSource);
            if (disconnectOperationSource is not null)
            {
                await disconnectOperationSource.Task.ConfigureAwait(false);
                Interlocked.CompareExchange(ref _disconnectOperationSource, null, disconnectOperationSource);
            }
        }
        finally
        {
            _outputChannel.Writer.TryComplete();
            GC.SuppressFinalize(this);
        }
    }
}