using Microsoft.Extensions.Logging;
using NetForESPHome.Client.Model;
using NetForESPHome.DomainModel.Native;
using NetForESPHome.Transports;
using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Net.Sockets;
using System.Threading.Channels;

#pragma warning disable MA0018 // Do not declare static members on generic types (deprecated; use CA1000 instead)
#pragma warning disable CS0612 // Type or member is obsolete

namespace NetForESPHome.Client;

/// <summary>Identifies why an established ESPHome connection was closed.</summary>
public enum EspHomeConnectionCloseReason
{
    /// <summary>The local application requested the connection to close.</summary>
    LocalDisconnect = 1,

    /// <summary>The remote ESPHome device requested the connection to close.</summary>
    RemoteDisconnect = 2,

    /// <summary>The transport or protocol failed while the connection was active.</summary>
    TransportFailure = 3,
}

/// <summary>Identifies the terminal status of an operation waiting for an ESPHome response or matching state observation.</summary>
public enum EspHomeRpcStatus
{
    /// <summary>The request was written and its expected response or matching state observation was received.</summary>
    Completed,

    /// <summary>The connection closed before the request could complete.</summary>
    ConnectionClosed,

    /// <summary>The operation did not complete within its response timeout.</summary>
    TimedOut,

    /// <summary>A mutually exclusive operation of the same kind is already active or still draining late responses.</summary>
    AlreadyInProgress,
}

/// <summary>Represents the terminal result of an ESPHome response-waiting operation without using lifecycle exceptions.</summary>
public readonly struct EspHomeRpcResult<T>
{
    /// <summary>Gets the terminal operation status.</summary>
    public EspHomeRpcStatus Status { get; }

    /// <summary>Gets the completed value when <see cref="Status"/> is <see cref="EspHomeRpcStatus.Completed"/>.</summary>
    public T? Value { get; }

    private EspHomeRpcResult(EspHomeRpcStatus status, T? value)
    {
        Status = status;
        Value = value;
    }

    /// <summary>Creates a completed RPC result with its response value.</summary>
    public static EspHomeRpcResult<T> Completed(T value) => new(EspHomeRpcStatus.Completed, value);

    /// <summary>Creates an RPC result interrupted by connection closure.</summary>
    public static EspHomeRpcResult<T> ConnectionClosed() => new(EspHomeRpcStatus.ConnectionClosed, default);

    /// <summary>Creates an RPC result that reached its response timeout.</summary>
    public static EspHomeRpcResult<T> TimedOut() => new(EspHomeRpcStatus.TimedOut, default);

    /// <summary>Creates an RPC result rejected because an equivalent mutually exclusive operation is already active.</summary>
    public static EspHomeRpcResult<T> AlreadyInProgress() => new(EspHomeRpcStatus.AlreadyInProgress, default);
}

/// <summary>Describes one established ESPHome connection that has closed.</summary>
public sealed class EspHomeConnectionClosed(EspHomeConnectionCloseReason reason, int? remoteReason = null, Exception? failure = null)
{
    /// <summary>Gets the semantic reason why the connection closed.</summary>
    public EspHomeConnectionCloseReason Reason { get; } = reason;

    /// <summary>Gets the raw disconnect reason reported by ESPHome when the remote device requested the close.</summary>
    public int? RemoteReason { get; } = remoteReason;

    /// <summary>Gets an optional diagnostic failure associated with the close; callers must not use it for control flow.</summary>
    public Exception? Failure { get; } = failure;
}

/// <summary>Represents one fully assembled camera image received from ESPHome.</summary>
public sealed class EspHomeCameraImage(uint key, uint deviceId, byte[] data) : IESPHomeMessage
{
    /// <summary>Gets the ESPHome entity key associated with the camera image.</summary>
    public uint Key { get; } = key;

    /// <summary>Gets the ESPHome device identifier associated with the camera image.</summary>
    public uint DeviceId { get; } = deviceId;

    /// <summary>Gets the complete image bytes owned by this message.</summary>
    public byte[] Data { get; } = data;
    public int MessageId { get; } = CameraImageResponse.ID;
}


/// <summary>Maintains an ESPHome Native API connection and exposes application messages to exactly one logical consumer.</summary>
public sealed partial class EspHomeClient : IAsyncDisposable, IEspHomeClient
{
    private const int WriteChannelCapacity = 100;
    private const int PreferredReceiveBufferSize = 2 * 1024 * 1024;
    private const int MinimumReceiveBufferSize = 128 * 1024;
    private const int CameraSegmentSize = 256 * 1024;
    private const int MaxCameraFrameBytes = 8 * 1024 * 1024;
    private const int MaxInflightCameraFrames = 4;
    private const int OutputChannelCapacity = 1024;

    private readonly TimeProvider _timeProvider;
    private readonly ILogger<EspHomeClient>? _logger;
    private int _statesSubscriptionRequested;

    /// <summary>Gets the current observable stage of connection maintenance.</summary>
    public ConnectionState State => (ConnectionState)Volatile.Read(ref _state);

    /// <summary>Gets the API version reported by the currently active ESPHome session.</summary>
    public ApiVersion? ApiVersion => Volatile.Read(ref _activeSession)?.ApiVersion;

    /// <summary>Gets the raw reason reported by the most recent server-requested disconnect, or null when no remote disconnect has been observed.</summary>
    public int? LastDisconnectReason
    {
        get
        {
            var remoteReason = Volatile.Read(ref _lastDisconnectReason);
            return remoteReason >= 0 ? remoteReason : null;
        }
    }

    /// <summary>Creates a reusable ESPHome Native API client for the specified endpoint.</summary>
    public EspHomeClient(string host, int port, string? noisePsk = null, string clientInfo = "EspClient.NET", string? password = null, TimeProvider? timeProvider = null, ILogger<EspHomeClient>? logger = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(host);
        ArgumentException.ThrowIfNullOrEmpty(clientInfo);

        var isPortInvalid = port is < 1 or > ushort.MaxValue;
        if (isPortInvalid) throw new ArgumentOutOfRangeException(nameof(port), port, "Port must be between 1 and 65535.");

        Host = host;
        Port = port;
        _noisePsk = string.IsNullOrEmpty(noisePsk?.Trim()) ? null : noisePsk;
        _password = password;
        _clientInfo = clientInfo;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger;

        var incomingMessagesChannel = new BoundedChannelOptions(OutputChannelCapacity)
        {
            AllowSynchronousContinuations = false,
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = true
        };

        var incomingStateChangesChannel = new BoundedChannelOptions(OutputChannelCapacity)
        {
            AllowSynchronousContinuations = false,
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = true
        };

        var dynamicEntitiesRegistrationChannel = new BoundedChannelOptions(OutputChannelCapacity)
        {
            AllowSynchronousContinuations = false,
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = true
        };

        _incomingMessagesChannel = Channel.CreateBounded<IESPHomeMessage>(incomingMessagesChannel);
        _incomingStateChangeMessages = Channel.CreateBounded<IESPHomeStateMessage>(incomingStateChangesChannel);
        _dynamicEntitiesRegistrationChannel = Channel.CreateBounded<IESPHomeMessage>(dynamicEntitiesRegistrationChannel);
    }

    /// <summary>Sends the persistent state subscription at most once for the supplied session.</summary>
    private async ValueTask EnsureStatesSubscriptionAsync(ConnectionSession session, CancellationToken ct)
    {
        if (Volatile.Read(ref _statesSubscriptionRequested) == 0) return;

        var wasAlreadySent = Interlocked.CompareExchange(ref session.StatesSubscriptionSent, 1, 0) != 0;
        if (wasAlreadySent) return;

        var request = new SubscribeStatesRequest();
        var accepted = false;

        try
        {
            accepted = await TryQueueMessageAsync(session, request, ct).ConfigureAwait(false);
        }
        finally
        {
            if (!accepted) Volatile.Write(ref session.StatesSubscriptionSent, 0);
        }
    }


    /// <summary>Creates the Noise transport required by the configured encrypted connection.</summary>
    private async Task<IMessageTransport> CreateNoiseMessageTransportAsync(NetworkStream stream, CancellationToken ct)
    {
        var noisePsk = _noisePsk ?? throw new InvalidOperationException("Noise transport cannot be created without a PSK.");
        var handshakeExecutor = new NoiseHandshakeExecutor(noisePsk);
        return await handshakeExecutor.ExecuteAsync(stream, ct).ConfigureAwait(false);
    }

    /// <summary>Configures latency and receive buffering for sustained ESPHome traffic without failing when a larger receive buffer is unavailable.</summary>
    private static void ConfigureTcpClient(TcpClient tcpClient)
    {
        tcpClient.NoDelay = true;

        var receiveBufferSize = PreferredReceiveBufferSize;

        while (receiveBufferSize >= MinimumReceiveBufferSize)
        {
            try
            {
                tcpClient.ReceiveBufferSize = receiveBufferSize;
                return;
            }
            catch (SocketException)
            {
                receiveBufferSize /= 2;
            }
        }
    }

    /// <summary>Creates the bounded multi-producer queue used to serialize all protocol writes through one consumer.</summary>
    private static Channel<QueuedMessage> CreateMessageWriteChannel()
    {
        var options = new BoundedChannelOptions(WriteChannelCapacity)
        {
            AllowSynchronousContinuations = true,
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        };

        return Channel.CreateBounded<QueuedMessage>(options);
    }



    /// <summary>Fails connection setup or the active session when ESPHome rejects the legacy API password.</summary>
    private static void HandleAuthenticationResponse(AuthenticationResponse response)
    {
        if (!response.InvalidPassword) return;

        throw new UnauthorizedAccessException("ESPHome rejected the legacy API password.");
    }

    /// <summary>Returns the current time and timezone representation expected by ESPHome without exposing the protocol request to the application consumer.</summary>
    private async Task HandleGetTimeRequestAsync(ConnectionSession session, CancellationToken ct)
    {
        var response = EspHomeTimeResponseFactory.Create(_timeProvider);
        await TryQueueMessageAsync(session, response, ct).ConfigureAwait(false);
    }

    /// <summary>Acknowledges a server-requested disconnect and requests session closure while preserving the remote close classification.</summary>
    private async Task HandleDisconnectRequestAsync(ConnectionSession session, DisconnectRequest request, CancellationToken ct)
    {
        session.MarkCloseIntent(EspHomeConnectionCloseReason.RemoteDisconnect, (int)request.Reason);
        Exception? acknowledgementFailure = null;

        using var timeoutCancellation = new CancellationTokenSource(DisconnectTimeout, _timeProvider);
        using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCancellation.Token);

        try
        {
            var response = new DisconnectResponse();
            var acknowledged = await TryWriteMessageAndWaitAsync(session.OutgoingMessages.Writer, response, operationCancellation.Token).ConfigureAwait(false);

            if (!acknowledged && !session.CloseRequested.Task.IsCompleted)
            {
                acknowledgementFailure = new IOException("ESPHome connection closed before the disconnect acknowledgement could be written.");
            }
        }
        catch (OperationCanceledException) when (timeoutCancellation.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            acknowledgementFailure = new TimeoutException($"ESPHome disconnect acknowledgement could not be written within {DisconnectTimeout}.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            if (session.CloseRequested.Task.IsCompleted) return;
        }

        session.RequestClose(EspHomeConnectionCloseReason.RemoteDisconnect, acknowledgementFailure);
    }

    /// <summary>Queues one request for the captured session, starts its response timeout after the physical write succeeds, and returns the first FIFO response accepted by the supplied matcher.</summary>
    private async ValueTask<EspHomeRpcResult<TResponse>> WriteMessageAwaitResponseAsync<TResponse>(IESPHomeCommandMessage request, int responseMessageId, Func<IESPHomeMessage, bool>? isMatchingMessage, bool publishResponse, TimeSpan timeout, CancellationToken ct) where TResponse : class, IESPHomeMessage
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(request);
        ct.ThrowIfCancellationRequested();

        var isTimeoutInvalid = timeout <= TimeSpan.Zero;
        if (isTimeoutInvalid) throw new ArgumentOutOfRangeException(nameof(timeout), timeout, "Response timeout must be greater than zero.");

        var session = Volatile.Read(ref _activeSession);
        if (session is null) return EspHomeRpcResult<TResponse>.ConnectionClosed();

        var pendingResponse = new PendingResponse(responseMessageId, isMatchingMessage, publishResponse);
        var writeCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var queuedMessage = new QueuedMessage(request, writeCompletion, pendingResponse);

        try
        {
            await session.OutgoingMessages.Writer.WriteAsync(queuedMessage, ct).ConfigureAwait(false);
        }
        catch (ChannelClosedException)
        {
            return EspHomeRpcResult<TResponse>.ConnectionClosed();
        }

        bool written;

        try
        {
            written = await writeCompletion.Task.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            pendingResponse.Completion.TrySetCanceled(ct);
            throw;
        }

        if (!written) return EspHomeRpcResult<TResponse>.ConnectionClosed();

        EspHomeRpcResult<IESPHomeMessage> responseResult;

        try
        {
            responseResult = await pendingResponse.Completion.Task.WaitAsync(timeout, _timeProvider, ct).ConfigureAwait(false);
        }
        catch (TimeoutException) when (!ct.IsCancellationRequested)
        {
            var timeoutResult = EspHomeRpcResult<IESPHomeMessage>.TimedOut();
            pendingResponse.Completion.TrySetResult(timeoutResult);
            responseResult = await pendingResponse.Completion.Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            pendingResponse.Completion.TrySetCanceled(ct);
            throw;
        }

        if (responseResult.Status == EspHomeRpcStatus.ConnectionClosed) return EspHomeRpcResult<TResponse>.ConnectionClosed();
        if (responseResult.Status == EspHomeRpcStatus.TimedOut) return EspHomeRpcResult<TResponse>.TimedOut();

        if (responseResult.Value is not TResponse response) 
            throw new InvalidOperationException($"Response message {responseMessageId} was not a {typeof(TResponse).Name}.");

        return EspHomeRpcResult<TResponse>.Completed(response);
    }

    /// <summary>Completes every response waiter and entity discovery operation that cannot survive the end of the supplied session.</summary>
    private static void FailPendingOperations(ConnectionSession session)
    {
        foreach (var callbacks in session.ResponseCallbacks) callbacks.FailAll();

        var entityListAssembly = Interlocked.Exchange(ref session.CurrentEntityListAssembly, value: null);

        if (entityListAssembly is not null)
        {
            var connectionClosedResult = EspHomeRpcResult<EspHomeEntityDiscovery>.ConnectionClosed();
            entityListAssembly.Completion.TrySetResult(connectionClosedResult);
        }
    }

    /// <summary>Bounds how long a canceled entity discovery may keep draining its uncorrelated response sequence before the session is reset.</summary>
    private async Task WatchAbandonedEntityListAssemblyAsync(ConnectionSession session, EntityListAssembly assembly)
    {
        try
        {
            await Task.Delay(EntityListResponseTimeout, _timeProvider, session.Cancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (session.Cancellation.IsCancellationRequested)
        {
            return;
        }

        var currentAssembly = Volatile.Read(ref session.CurrentEntityListAssembly);
        if (!ReferenceEquals(currentAssembly, assembly)) return;

        var failure = new TimeoutException($"ESPHome did not finish the canceled entity discovery sequence within {EntityListResponseTimeout}.");
        session.RequestClose(EspHomeConnectionCloseReason.TransportFailure, failure);
    }

    /// <summary>Returns all pooled camera segments still owned by incomplete frames in the supplied session.</summary>
    private static void ClearCameraAssemblies(ConnectionSession session)
    {
        foreach (var assembly in session.CameraAssemblies.Values) assembly?.Dispose();
        session.CameraAssemblies.Clear();
    }

    /// <summary>Attempts to queue a protocol message and waits until its serialized transport write physically completes.</summary>
    private static async Task<bool> TryWriteMessageAndWaitAsync(ChannelWriter<QueuedMessage> writer, IESPHomeMessage message, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var queuedMessage = new QueuedMessage(message, completion, pendingResponse: null);

        try
        {
            await writer.WriteAsync(queuedMessage, ct).ConfigureAwait(false);
        }
        catch (ChannelClosedException)
        {
            return false;
        }

        return await completion.Task.WaitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Collects one finite ListEntities response sequence until ESPHome sends ListEntitiesDoneResponse.</summary>
    private sealed class EntityListAssembly
    {
        /// <summary>Gets entity descriptions accumulated for this discovery operation.</summary>
        public List<IESPHomeEntityInfoMessage> Entities { get; } = new(128);

        /// <summary>Gets user-defined service descriptions accumulated for this discovery operation.</summary>
        public List<ListEntitiesServicesResponse> Services { get; } = new(8);

        /// <summary>Gets the terminal discovery result completed by Done, timeout, cancellation, or session teardown.</summary>
        public TaskCompletionSource<EspHomeRpcResult<EspHomeEntityDiscovery>> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>Stores pooled fixed-size camera segments until one complete image can be materialized exactly once.</summary>
    private sealed class CameraFrameAssembly : IDisposable
    {
        private readonly List<byte[]> _segments = new(4);
        private int _currentSegmentLength;

        /// <summary>Gets the number of camera payload bytes accumulated so far.</summary>
        public int Length { get; private set; }

        /// <summary>Copies one transport chunk into fixed-size pooled segments without reallocating existing data.</summary>
        public void Append(ReadOnlySpan<byte> data)
        {
            while (!data.IsEmpty)
            {
                if (_segments.Count == 0 || _currentSegmentLength == CameraSegmentSize)
                {
                    var segment = ArrayPool<byte>.Shared.Rent(CameraSegmentSize);
                    _segments.Add(segment);
                    _currentSegmentLength = 0;
                }

                var currentSegment = _segments[^1];
                var availableBytes = CameraSegmentSize - _currentSegmentLength;
                var bytesToCopy = Math.Min(availableBytes, data.Length);

                data[..bytesToCopy].CopyTo(currentSegment.AsSpan(_currentSegmentLength, bytesToCopy));

                _currentSegmentLength += bytesToCopy;
                Length += bytesToCopy;
                data = data[bytesToCopy..];
            }
        }

        /// <summary>Allocates the exact final image buffer and merges all pooled segments into it once.</summary>
        public byte[] Complete()
        {
            if (Length == 0) return [];

            var image = new byte[Length];
            var targetOffset = 0;
            var remainingBytes = Length;

            foreach (var segment in _segments)
            {
                var bytesToCopy = Math.Min(CameraSegmentSize, remainingBytes);
                segment.AsSpan(0, bytesToCopy).CopyTo(image.AsSpan(targetOffset, bytesToCopy));

                targetOffset += bytesToCopy;
                remainingBytes -= bytesToCopy;
            }

            return image;
        }

        /// <summary>Returns every rented camera segment to the shared array pool.</summary>
        public void Dispose()
        {
            foreach (var segment in _segments)
            {
                ArrayPool<byte>.Shared.Return(segment);
            }

            _segments.Clear();
            _currentSegmentLength = 0;
            Length = 0;
        }
    }

    /// <summary>Stores response waiters in registration order and removes only the first waiter whose matching predicate accepts an incoming message.</summary>
    private sealed class PendingResponseQueue
    {
        private readonly Lock _lock = new();
        private readonly List<PendingResponse> _responses = [];

        /// <summary>Adds one waiter at the end of the registration order.</summary>
        public void Enqueue(PendingResponse response)
        {
            lock (_lock) _responses.Add(response);
        }

        /// <summary>Removes and returns the earliest registered waiter that accepts the supplied message while preserving every non-matching waiter in place.</summary>
        public bool TryTakeFirstMatching(IESPHomeMessage message, [NotNullWhen(true)] out PendingResponse? response)
        {
            lock (_lock)
            {
                for (var index = 0; index < _responses.Count; index++)
                {
                    var candidate = _responses[index];
                    var messageMatches = candidate.IsMatchingMessage is null || candidate.IsMatchingMessage(message);
                    if (!messageMatches) continue;

                    _responses.RemoveAt(index);
                    response = candidate;
                    return true;
                }
            }

            response = null;
            return false;
        }

        /// <summary>Completes and removes every waiter that cannot survive the end of its owning session.</summary>
        public void FailAll()
        {
            lock (_lock)
            {
                var connectionClosedResult = EspHomeRpcResult<IESPHomeMessage>.ConnectionClosed();

                foreach (var response in _responses) response.Completion.TrySetResult(connectionClosedResult);
                _responses.Clear();
            }
        }
    }

    /// <summary>Represents one response waiter owned by a physical session and optionally restricts which message may complete it.</summary>
    private sealed class PendingResponse
    {
        /// <summary>Gets the protocol message identifier routed to this waiter.</summary>
        public int MessageId { get; }

        /// <summary>Gets the optional matching predicate evaluated only for messages with the expected identifier.</summary>
        public Func<IESPHomeMessage, bool>? IsMatchingMessage { get; }

        /// <summary>Gets whether a matched response must still be published through the normal application dispatch path.</summary>
        public bool PublishResponse { get; }

        /// <summary>Gets the terminal result signal completed by the read loop, timeout, cancellation, or session teardown.</summary>
        public TaskCompletionSource<EspHomeRpcResult<IESPHomeMessage>> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Creates one response waiter with an optional matching predicate and response publication policy supplied by the sending operation.</summary>
        public PendingResponse(int messageId, Func<IESPHomeMessage, bool>? isMatchingMessage, bool publishResponse)
        {
            var isMessageIdInvalid = messageId <= 0 || messageId > IESPHomeMessage.MaxMsgId;
            if (isMessageIdInvalid) throw new ArgumentOutOfRangeException(nameof(messageId), messageId, "Response message id must identify a supported ESPHome message.");

            MessageId = messageId;
            IsMatchingMessage = isMatchingMessage;
            PublishResponse = publishResponse;
        }
    }

    /// <summary>Stores one serialized protocol message together with optional physical-write and response completion state.</summary>
    private readonly struct QueuedMessage
    {
        /// <summary>Gets the protocol message queued for transmission.</summary>
        public IESPHomeMessage Message { get; }

        /// <summary>Gets the optional signal completed with true only after the transport write succeeds.</summary>
        public TaskCompletionSource<bool>? WriteCompletion { get; }

        /// <summary>Gets the optional response waiter registered immediately before this message is written to the transport.</summary>
        public PendingResponse? PendingResponse { get; }

        /// <summary>Creates one transport queue item without copying the protocol payload.</summary>
        public QueuedMessage(IESPHomeMessage message, TaskCompletionSource<bool>? writeCompletion, PendingResponse? pendingResponse)
        {
            Message = message;
            WriteCompletion = writeCompletion;
            PendingResponse = pendingResponse;
        }
    }
}