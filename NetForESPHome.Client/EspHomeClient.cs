using Google.Protobuf;
using Microsoft.Extensions.Logging;
using NetForESPHome.Client.Model;
using NetForESPHome.DomainModel;
using NetForESPHome.Transports;
using System.Buffers;
using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Threading.Channels;
using Zeroconf;

namespace NetForESPHome.Client;

/// <summary>Identifies an application-level message produced by the ESPHome client.</summary>
public enum EspHomeMessageType
{
    /// <summary>Receives legacy Bluetooth LE advertisements from the ESPHome device.</summary>
    BluetoothAdvertisementReceived,

    /// <summary>Receives raw batched Bluetooth LE advertisements from the ESPHome device.</summary>
    BluetoothRawAdvertisementsReceived,

    /// <summary>Receives Bluetooth connection state changes from the ESPHome device.</summary>
    BluetoothConnectionChanged,

    /// <summary>Receives Bluetooth GATT notifications from the ESPHome device.</summary>
    BluetoothGattNotification,

    /// <summary>Receives entity state changes from the ESPHome device.</summary>
    EspHomeEntityStateChanged,

    /// <summary>Receives a completed entity list returned by the ESPHome device.</summary>
    EspHomeEntitiesListReceived,

    /// <summary>Receives a completed user-defined services list returned by the ESPHome device.</summary>
    EspHomeEntitiesServicesListReceived,

    /// <summary>Receives device information returned by the ESPHome device.</summary>
    EspHomeDeviceInfoReceived,

    /// <summary>Receives device capabilities returned by the ESPHome device.</summary>
    EspHomeDeviceCapabilitiesReceived,

    /// <summary>Receives a fully assembled camera image from the ESPHome device.</summary>
    CameraImageReceived,

    /// <summary>Receives ESPHome log records from an active log subscription.</summary>
    LogReceived,

    /// <summary>Receives a Home Assistant action requested by the ESPHome device.</summary>
    HomeAssistantActionRequested,

    /// <summary>ESPHome requests the current value of an entity or attribute from the Automation System.</summary>
    EspHomeRequestedState,

    /// <summary>ESPHome requests continuous updates for an entity or attribute from the Automation System.</summary>
    EspHomeRequestedStateSubscription,

    /// <summary>Receives Bluetooth connection capacity changes from the ESPHome device.</summary>
    BluetoothConnectionsFreeChanged,

    /// <summary>Receives Bluetooth scanner state changes from the ESPHome device.</summary>
    BluetoothScannerStateChanged,

    /// <summary>Receives Z-Wave frames from the ESPHome proxy.</summary>
    ZWaveProxyFrameReceived,

    /// <summary>Receives Z-Wave proxy requests from the ESPHome device.</summary>
    ZWaveProxyRequestReceived,

    /// <summary>Receives infrared or radio-frequency input events from the ESPHome device.</summary>
    InfraredRFReceived,

    /// <summary>Receives data from an ESPHome serial proxy.</summary>
    SerialProxyDataReceived,

    /// <summary>Requests starting a voice assistant session; the consumer must respond with a VoiceAssistantResponse.</summary>
    VoiceAssistantStartRequested,

    /// <summary>Requests stopping the active voice assistant session.</summary>
    VoiceAssistantStopRequested,

    /// <summary>Receives a voice assistant audio chunk from the ESPHome device.</summary>
    VoiceAssistantAudioReceived,

    /// <summary>Signals the normal end of the voice assistant audio stream.</summary>
    VoiceAssistantAudioStreamEnded,

    /// <summary>Receives completion of a voice assistant announcement.</summary>
    VoiceAssistantAnnouncementFinished,

    /// <summary>Signals that an established ESPHome connection has closed.</summary>
    ConnectionClosed,

    /// <summary>Signals that an application-requested outgoing protobuf message could not be accepted.</summary>
    OutgoingMessageRejected,
}

/// <summary>Identifies why an established ESPHome connection was closed.</summary>
public enum EspHomeConnectionCloseReason
{
    /// <summary>The local application requested the connection to close.</summary>
    LocalDisconnect = 1,

    /// <summary>The remote ESPHome device requested the connection to close.</summary>
    RemoteDisconnect = 2,

    /// <summary>The transport or protocol failed while the connection was active.</summary>
    TransportFailure = 3,

    /// <summary>No valid inbound traffic was observed within the configured keep-alive response window.</summary>
    KeepAliveTimeout = 4,

    /// <summary>The bounded output channel overflowed because the application consumer did not keep up.</summary>
    OutputConsumerStalled = 5,
}

/// <summary>Identifies why an application-requested outgoing protobuf message was rejected.</summary>
public enum EspHomeOutgoingMessageRejectedReason
{
    /// <summary>The message could not be accepted because its connection was already closed or closing.</summary>
    ConnectionClosed = 1,
}

/// <summary>Identifies the terminal status of an RPC-style ESPHome operation.</summary>
public enum EspHomeRpcStatus
{
    /// <summary>The request was accepted and its expected response sequence completed.</summary>
    Completed,

    /// <summary>The connection closed before the request could complete.</summary>
    ConnectionClosed,

    /// <summary>The operation did not complete within its response timeout.</summary>
    TimedOut,
}

/// <summary>Represents the terminal result of an RPC-style ESPHome operation without using lifecycle exceptions.</summary>
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
}

/// <summary>Represents one application-level message produced by the ESPHome client.</summary>
public readonly struct EspHomeMessage(EspHomeMessageType messageType, object payload)
{
    /// <summary>Gets the semantic type used by the consumer to dispatch the message.</summary>
    public EspHomeMessageType MessageType { get; } = messageType;

    /// <summary>Gets the protocol or aggregated payload associated with the application message.</summary>
    public object Payload { get; } = payload;
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

/// <summary>Describes an outgoing protobuf message that could not be accepted by the current connection.</summary>
public sealed class EspHomeOutgoingMessageRejected(Type protobufMessageType, EspHomeOutgoingMessageRejectedReason reason)
{
    /// <summary>Gets the generated protobuf CLR type that was rejected.</summary>
    public Type ProtobufMessageType { get; } = protobufMessageType;

    /// <summary>Gets the semantic reason why the outgoing message was rejected.</summary>
    public EspHomeOutgoingMessageRejectedReason Reason { get; } = reason;
}

/// <summary>Represents one fully assembled camera image received from ESPHome.</summary>
public sealed class EspHomeCameraImage(uint key, uint deviceId, byte[] data)
{
    /// <summary>Gets the ESPHome entity key associated with the camera image.</summary>
    public uint Key { get; } = key;

    /// <summary>Gets the ESPHome device identifier associated with the camera image.</summary>
    public uint DeviceId { get; } = deviceId;

    /// <summary>Gets the complete image bytes owned by this message.</summary>
    public byte[] Data { get; } = data;
}

/// <summary>Maintains an ESPHome Native API connection and exposes application messages to exactly one logical consumer.</summary>
public sealed partial class EspHomeClient : IAsyncDisposable
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
    private readonly Channel<OutputMessageEnvelope> _outputChannel;
    private readonly ConcurrentDictionary<PendingResponseKey, PendingResponseOperation> _pendingResponseOperations = new();
    private readonly Dictionary<(uint DeviceId, uint Key), CameraFrameAssembly?> _cameraAssemblies = [];
    private Channel<QueuedMessage>? _messageWriteChannel;
    private Task? _readLoopTask;
    private Task? _writeLoopTask;
    private Task? _keepAliveLoopTask;
    private TaskCompletionSource<HelloResponse>? _helloResponseSource;
    private TaskCompletionSource<bool>? _disconnectResponseSource;
    private TaskCompletionSource<bool>? _disconnectOperationSource;
    private EntityListAssembly? _currentEntityListAssembly;
    private Exception? _connectionFailure;
    private long _outputGeneration;

    /// <summary>Gets the current stage of the ESPHome connection lifecycle.</summary>
    public ConnectionState State => (ConnectionState)Volatile.Read(ref _state);

    /// <summary>Gets the API version reported by the currently connected ESPHome device.</summary>
    public ApiVersion? ApiVersion { get; private set; }

    /// <summary>Gets the raw reason reported by the most recent server-requested disconnect, or null when none was reported.</summary>
    public int? LastDisconnectReason { get; private set; }

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

        var outputChannelOptions = new BoundedChannelOptions(OutputChannelCapacity)
        {
            AllowSynchronousContinuations = false,
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        };

        _outputChannel = Channel.CreateBounded<OutputMessageEnvelope>(outputChannelOptions);
    }

    /// <summary>Discovers the first matching ESPHome Native API service and creates a client for its endpoint.</summary>
    public static async Task<IZeroconfHost> DiscoverAsync(string serviceName = "_esphomelib._tcp.local.", string? noisePsk = null, string clientInfo = "EspClient.NET", string? password = null, TimeProvider? timeProvider = null)
    {
        while (true)
        {
            Console.WriteLine($"Scanning network for services {serviceName}");
            var response = await ZeroconfResolver.ResolveAsync(serviceName).ConfigureAwait(false);
            var service = response.FirstOrDefault();

            if (service is null) continue;
            var serviceInfo = service.Services.FirstOrDefault(x => x.Value.Name == serviceName).Value;
            if (serviceInfo is null) continue;
            return service;
        }
    }
     
    /// <summary>Waits for the next current-generation application message; exactly one logical consumer must call this method for the client lifetime.</summary>
    public async ValueTask<EspHomeMessage> GetOutputMessageAsync(CancellationToken cancellationToken = default)
    {
        float? z= 23f;
        var x = new LightCommandRequest() { Brightness = z ?? 0, HasBrightness = z.HasValue };
        while (true)
        {
            var envelope = await _outputChannel.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            var currentGeneration = Volatile.Read(ref _outputGeneration);

            if (envelope.Generation != currentGeneration) continue;
            return envelope.Message;
        }
    }

    /// <summary>Requests entity descriptions and user-defined services, publishes both snapshots and returns the terminal operation status.</summary>
    public async ValueTask<EspHomeRpcStatus> RequestEntitiesListRefresh(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();

        var assembly = new EntityListAssembly();
        var existingAssembly = Interlocked.CompareExchange(ref _currentEntityListAssembly, assembly, null);
        if (existingAssembly is not null) throw new InvalidOperationException("An entity list request is already in progress.");

        try
        {
            var request = new ListEntitiesRequest();
            var requestAccepted = await TryWriteMessageAsync(request, cancellationToken).ConfigureAwait(false);
            if (!requestAccepted) return EspHomeRpcStatus.ConnectionClosed;

            try
            {
                return await assembly.Completion.Task.WaitAsync(EntityListResponseTimeout, _timeProvider, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException) when (!cancellationToken.IsCancellationRequested)
            {
                return EspHomeRpcStatus.TimedOut;
            }
        }
        finally
        {
            Interlocked.CompareExchange(ref _currentEntityListAssembly, null, assembly);
        }
    }

    /// <summary>Requests current device information and publishes the response through the output stream.</summary>
    public ValueTask RequestDeviceInfoRefresh(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        var request = new DeviceInfoRequest();
        return WriteMessageAsync(request, cancellationToken);
    }

    /// <summary>Requests current device capabilities and publishes the response through the output stream.</summary>
    public ValueTask RequestDeviceCapabilitiesRefresh(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        var request = new DeviceCapabilitiesRequest();
        return WriteMessageAsync(request, cancellationToken);
    }

    /// <summary>Starts receiving entity state changes and publishes them through the output stream.</summary>
    public ValueTask StartStatesSubscriptionAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        var request = new SubscribeStatesRequest();
        return WriteMessageAsync(request, cancellationToken);
    }

    /// <summary>Attempts to queue an application-requested protocol message and reports rejection through the output stream instead of throwing lifecycle exceptions.</summary>
    public async ValueTask WriteMessageAsync(IMessage message, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(message);
        cancellationToken.ThrowIfCancellationRequested();

        var accepted = await TryWriteMessageAsync(message, cancellationToken).ConfigureAwait(false);
        if (accepted) return;

        PublishOutgoingMessageRejected(message);
    }

    /// <summary>Creates the Noise transport required by the configured encrypted connection.</summary>
    private async Task<IMessageTransport> CreateMessageTransportAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var noisePsk = _noisePsk;
        if (noisePsk is null) throw new InvalidOperationException("Noise transport cannot be created without a PSK.");

        var handshakeExecutor = new NoiseHandshakeExecutor(noisePsk);
        return await handshakeExecutor.ExecuteAsync(stream, cancellationToken).ConfigureAwait(false);
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

    /// <summary>Attempts to queue a public or RPC request only when the same connection generation remains active throughout the acceptance check.</summary>
    private async ValueTask<bool> TryWriteMessageAsync(IMessage message, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var connectionGeneration = Volatile.Read(ref _connectionGeneration);
        if (Volatile.Read(ref _connectionLifecycle) != ConnectionLifecycleEstablished || State != ConnectionState.Connected) return false;

        var messageWriteChannel = Volatile.Read(ref _messageWriteChannel);
        if (messageWriteChannel is null) return false;

        var connectionChanged = connectionGeneration != Volatile.Read(ref _connectionGeneration);
        var lifecycleChanged = Volatile.Read(ref _connectionLifecycle) != ConnectionLifecycleEstablished;
        if (connectionChanged || lifecycleChanged || State != ConnectionState.Connected) return false;

        return await TryQueueMessageAsync(messageWriteChannel.Writer, message, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Serializes queued protocol messages so the underlying transport is never written concurrently.</summary>
    private async Task WriteMessagesLoopAsync(IMessageTransport messageTransport, ChannelReader<QueuedMessage> reader, long connectionGeneration, CancellationToken cancellationToken)
    {
        try
        {
            while (await reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                while (reader.TryRead(out var queuedMessage))
                {
                    try
                    {
                        await messageTransport.WriteMessageAsync(queuedMessage.Message, cancellationToken).ConfigureAwait(false);
                        queuedMessage.Completion?.TrySetResult(true);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        queuedMessage.Completion?.TrySetResult(false);
                        return;
                    }
                    catch (Exception exception)
                    {
                        HandleConnectionFailure(connectionGeneration, exception);
                        queuedMessage.Completion?.TrySetResult(false);
                        return;
                    }
                }
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
        finally
        {
            while (reader.TryRead(out var queuedMessage))
            {
                queuedMessage.Completion?.TrySetResult(false);
            }
        }
    }

    /// <summary>Reads protocol messages continuously, records connection activity and dispatches protocol handling.</summary>
    private async Task ReadLoopAsync(IMessageTransport messageTransport, ChannelWriter<QueuedMessage> writer, long connectionGeneration, long outputGeneration, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var message = await messageTransport.ReadMessageAsync(cancellationToken).ConfigureAwait(false);

                Volatile.Write(ref _lastMessageReceivedTimestamp, _timeProvider.GetTimestamp());

                await HandleMessageAsync(message, writer, connectionGeneration, outputGeneration, cancellationToken).ConfigureAwait(false);
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
        finally
        {
            ClearCameraAssemblies();
        }
    }

    /// <summary>Handles protocol-internal messages locally and publishes only application-level messages to the output stream.</summary>
    private async Task HandleMessageAsync(IMessage message, ChannelWriter<QueuedMessage> writer, long connectionGeneration, long outputGeneration, CancellationToken cancellationToken)
    {
        if (EspHomeMessageMapper.IsEntityStateResponseMessage(message))
        {
            PublishOutputMessage(EspHomeMessageType.EspHomeEntityStateChanged, message, connectionGeneration, outputGeneration);
            return;
        }

        if (message is PingRequest)
        {
            var response = new PingResponse();
            await TryQueueMessageAsync(writer, response, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (message is CameraImageResponse cameraImageResponse)
        {
            HandleCameraImageResponse(cameraImageResponse, connectionGeneration, outputGeneration);
            return;
        }

        if (message is PingResponse) return;

        if (message is HelloResponse helloResponse)
        {
            _helloResponseSource?.TrySetResult(helloResponse);
            return;
        }

        if (message is AuthenticationResponse authenticationResponse)
        {
            HandleAuthenticationResponse(authenticationResponse);
            return;
        }

        if (message is DisconnectResponse)
        {
            _disconnectResponseSource?.TrySetResult(true);
            return;
        }

        if (message is DisconnectRequest disconnectRequest)
        {
            await HandleDisconnectRequestAsync(disconnectRequest, writer, connectionGeneration, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (message is GetTimeRequest)
        {
            await HandleGetTimeRequestAsync(writer, cancellationToken).ConfigureAwait(false);
            return;
        }

        HandlePendingResponse(message);

        if (message is ListEntitiesDoneResponse)
        {
            HandleListEntitiesDoneResponse(connectionGeneration, outputGeneration);
            return;
        }

        if (message is ListEntitiesServicesResponse servicesResponse)
        {
            HandleListEntitiesServicesResponse(servicesResponse);
            return;
        }

        if (EspHomeMessageMapper.IsEntityInfoResponseMessage(message))
        {
            HandleListResponseMessage(message);
            return;
        }

        if (message is SubscribeLogsResponse logResponse)
        {
            PublishOutputMessage(EspHomeMessageType.LogReceived, logResponse, connectionGeneration, outputGeneration);
            return;
        }

        if (message is HomeassistantActionRequest actionRequest)
        {
            PublishOutputMessage(EspHomeMessageType.HomeAssistantActionRequested, actionRequest, connectionGeneration, outputGeneration);
            return;
        }

        if (message is SubscribeHomeAssistantStateResponse homeAssistantStateResponse)
        {
            HandleSubscribeHomeAssistantStateResponse(homeAssistantStateResponse, connectionGeneration, outputGeneration);
            return;
        }

        if (message is BluetoothLEAdvertisementResponse advertisementResponse)
        {
            PublishOutputMessage(EspHomeMessageType.BluetoothAdvertisementReceived, advertisementResponse, connectionGeneration, outputGeneration);
            return;
        }

        if (message is BluetoothLERawAdvertisementsResponse rawAdvertisementsResponse)
        {
            PublishOutputMessage(EspHomeMessageType.BluetoothRawAdvertisementsReceived, rawAdvertisementsResponse, connectionGeneration, outputGeneration);
            return;
        }

        if (message is BluetoothConnectionsFreeResponse connectionsFreeResponse)
        {
            PublishOutputMessage(EspHomeMessageType.BluetoothConnectionsFreeChanged, connectionsFreeResponse, connectionGeneration, outputGeneration);
            return;
        }

        if (message is BluetoothScannerStateResponse scannerStateResponse)
        {
            PublishOutputMessage(EspHomeMessageType.BluetoothScannerStateChanged, scannerStateResponse, connectionGeneration, outputGeneration);
            return;
        }

        if (message is BluetoothDeviceConnectionResponse connectionResponse)
        {
            PublishOutputMessage(EspHomeMessageType.BluetoothConnectionChanged, connectionResponse, connectionGeneration, outputGeneration);
            return;
        }

        if (message is BluetoothGATTNotifyDataResponse notificationResponse)
        {
            PublishOutputMessage(EspHomeMessageType.BluetoothGattNotification, notificationResponse, connectionGeneration, outputGeneration);
            return;
        }

        if (message is ZWaveProxyFrame zWaveProxyFrame)
        {
            PublishOutputMessage(EspHomeMessageType.ZWaveProxyFrameReceived, zWaveProxyFrame, connectionGeneration, outputGeneration);
            return;
        }

        if (message is ZWaveProxyRequest zWaveProxyRequest)
        {
            PublishOutputMessage(EspHomeMessageType.ZWaveProxyRequestReceived, zWaveProxyRequest, connectionGeneration, outputGeneration);
            return;
        }

        if (message is InfraredRFReceiveEvent infraredRFReceiveEvent)
        {
            PublishOutputMessage(EspHomeMessageType.InfraredRFReceived, infraredRFReceiveEvent, connectionGeneration, outputGeneration);
            return;
        }

        if (message is SerialProxyDataReceived serialProxyDataReceived)
        {
            PublishOutputMessage(EspHomeMessageType.SerialProxyDataReceived, serialProxyDataReceived, connectionGeneration, outputGeneration);
            return;
        }

        if (message is VoiceAssistantRequest voiceAssistantRequest)
        {
            HandleVoiceAssistantRequest(voiceAssistantRequest, connectionGeneration, outputGeneration);
            return;
        }

        if (message is VoiceAssistantAudio voiceAssistantAudio)
        {
            HandleVoiceAssistantAudio(voiceAssistantAudio, connectionGeneration, outputGeneration);
            return;
        }

        if (message is VoiceAssistantAnnounceFinished announcementFinished)
        {
            PublishOutputMessage(EspHomeMessageType.VoiceAssistantAnnouncementFinished, announcementFinished, connectionGeneration, outputGeneration);
            return;
        }

        if (message is DeviceInfoResponse deviceInfoResponse)
        {
            PublishOutputMessage(EspHomeMessageType.EspHomeDeviceInfoReceived, deviceInfoResponse, connectionGeneration, outputGeneration);
            return;
        }

        if (message is DeviceCapabilitiesResponse deviceCapabilitiesResponse)
        {
            PublishOutputMessage(EspHomeMessageType.EspHomeDeviceCapabilitiesReceived, deviceCapabilitiesResponse, connectionGeneration, outputGeneration);
        }
    }

    /// <summary>Fails connection setup or the active session when ESPHome rejects the legacy API password.</summary>
    private static void HandleAuthenticationResponse(AuthenticationResponse response)
    {
        if (!response.InvalidPassword) return;

        throw new UnauthorizedAccessException("ESPHome rejected the legacy API password.");
    }

    /// <summary>Returns the current time and timezone representation expected by ESPHome without exposing the protocol request to the application consumer.</summary>
    private async Task HandleGetTimeRequestAsync(ChannelWriter<QueuedMessage> writer, CancellationToken cancellationToken)
    {
        var response = EspHomeTimeResponseFactory.Create(_timeProvider);
        await TryQueueMessageAsync(writer, response, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Marks a server-requested disconnect before its best-effort acknowledgement so later transport failures retain remote-disconnect classification.</summary>
    private async Task HandleDisconnectRequestAsync(DisconnectRequest request, ChannelWriter<QueuedMessage> writer, long connectionGeneration, CancellationToken cancellationToken)
    {
        LastDisconnectReason = (int)request.Reason;
        MarkCloseIntent(EspHomeConnectionCloseReason.RemoteDisconnect);

        Exception? acknowledgementFailure = null;

        using var timeoutCancellation = new CancellationTokenSource(DisconnectTimeout, _timeProvider);
        using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCancellation.Token);

        try
        {
            var response = new DisconnectResponse();
            var acknowledged = await TryWriteMessageAndWaitAsync(writer, response, operationCancellation.Token).ConfigureAwait(false);

            if (!acknowledged) acknowledgementFailure = Volatile.Read(ref _connectionFailure);
        }
        catch (OperationCanceledException) when (timeoutCancellation.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            acknowledgementFailure = new TimeoutException($"ESPHome disconnect acknowledgement could not be written within {DisconnectTimeout}.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }

        CloseConnection(connectionGeneration, EspHomeConnectionCloseReason.RemoteDisconnect, acknowledgementFailure);
    }

    /// <summary>Adds an entity description to the currently active entity-list request.</summary>
    private void HandleListResponseMessage(IMessage message)
    {
        var assembly = Volatile.Read(ref _currentEntityListAssembly);
        if (assembly is null) return;

        assembly.Entities.Add(message);
    }

    /// <summary>Adds a user-defined service description to the currently active entity-list request.</summary>
    private void HandleListEntitiesServicesResponse(ListEntitiesServicesResponse response)
    {
        var assembly = Volatile.Read(ref _currentEntityListAssembly);
        if (assembly is null) return;

        assembly.Services.Add(response);
    }

    /// <summary>Completes entity discovery, publishes both snapshots and reports output overflow as connection closure.</summary>
    private void HandleListEntitiesDoneResponse(long connectionGeneration, long outputGeneration)
    {
        var assembly = Interlocked.Exchange(ref _currentEntityListAssembly, null);
        if (assembly is null) return;

        var entities = assembly.Entities.ToArray();
        var services = assembly.Services.ToArray();

        var entitiesPublished = PublishOutputMessage(EspHomeMessageType.EspHomeEntitiesListReceived, entities, connectionGeneration, outputGeneration);
        var servicesPublished = PublishOutputMessage(EspHomeMessageType.EspHomeEntitiesServicesListReceived, services, connectionGeneration, outputGeneration);

        var status = entitiesPublished && servicesPublished ? EspHomeRpcStatus.Completed : EspHomeRpcStatus.ConnectionClosed;
        assembly.Completion.TrySetResult(status);
    }

    /// <summary>Appends one camera frame chunk and publishes only the final fully assembled image.</summary>
    private void HandleCameraImageResponse(CameraImageResponse response, long connectionGeneration, long outputGeneration)
    {
        var frameKey = (response.DeviceId, response.Key);
        var assemblyExists = _cameraAssemblies.TryGetValue(frameKey, out var assembly);

        if (!assemblyExists)
        {
            var tooManyFrames = _cameraAssemblies.Count >= MaxInflightCameraFrames;
            if (tooManyFrames) return;

            assembly = new CameraFrameAssembly();
            _cameraAssemblies.Add(frameKey, assembly);
        }

        if (assembly is null)
        {
            if (response.Done) _cameraAssemblies.Remove(frameKey);
            return;
        }

        var frameTooLarge = response.Data.Length > MaxCameraFrameBytes - assembly.Length;
        if (frameTooLarge)
        {
            _cameraAssemblies.Remove(frameKey);
            assembly.Dispose();

            if (!response.Done) _cameraAssemblies.Add(frameKey, null);
            return;
        }

        try
        {
            assembly.Append(response.Data.Span);
        }
        catch
        {
            _cameraAssemblies.Remove(frameKey);
            assembly.Dispose();
            throw;
        }

        if (!response.Done) return;

        _cameraAssemblies.Remove(frameKey);

        try
        {
            var data = assembly.Complete();
            var image = new EspHomeCameraImage(response.Key, response.DeviceId, data);
            PublishOutputMessage(EspHomeMessageType.CameraImageReceived, image, connectionGeneration, outputGeneration);
        }
        finally
        {
            assembly.Dispose();
        }
    }

    /// <summary>Routes a Home Assistant state request according to whether the device requests one value or a continuing subscription.</summary>
    private void HandleSubscribeHomeAssistantStateResponse(SubscribeHomeAssistantStateResponse response, long connectionGeneration, long outputGeneration)
    {
        var messageType = response.Once ? EspHomeMessageType.EspHomeRequestedState : EspHomeMessageType.EspHomeRequestedStateSubscription;
        PublishOutputMessage(messageType, response, connectionGeneration, outputGeneration);
    }

    /// <summary>Routes a voice assistant control request to either session start or session stop processing.</summary>
    private void HandleVoiceAssistantRequest(VoiceAssistantRequest request, long connectionGeneration, long outputGeneration)
    {
        var messageType = request.Start ? EspHomeMessageType.VoiceAssistantStartRequested : EspHomeMessageType.VoiceAssistantStopRequested;
        PublishOutputMessage(messageType, request, connectionGeneration, outputGeneration);
    }

    /// <summary>Routes voice assistant audio either as stream data or as the normal end of the audio stream.</summary>
    private void HandleVoiceAssistantAudio(VoiceAssistantAudio audio, long connectionGeneration, long outputGeneration)
    {
        var messageType = audio.End ? EspHomeMessageType.VoiceAssistantAudioStreamEnded : EspHomeMessageType.VoiceAssistantAudioReceived;
        PublishOutputMessage(messageType, audio, connectionGeneration, outputGeneration);
    }

    /// <summary>Registers an RPC response before sending its request and returns an explicit terminal status instead of lifecycle exceptions.</summary>
    private async Task<EspHomeRpcResult<IMessage[]>> WriteMessageAwaitResponsesAsync(IMessage request, PendingResponseKey completionKey, TimeSpan timeout, CancellationToken cancellationToken, PendingResponseKey[]? accumulationKeys = null, PendingResponseKey[]? interruptKeys = null)
    {
        var operation = new PendingResponseOperation(completionKey, accumulationKeys ?? [], interruptKeys ?? []);
        var operationRegistered = _pendingResponseOperations.TryAdd(completionKey, operation);
        if (!operationRegistered) throw new InvalidOperationException("An RPC response waiter with the same correlation key is already active.");

        try
        {
            var requestAccepted = await TryWriteMessageAsync(request, cancellationToken).ConfigureAwait(false);
            if (!requestAccepted) return EspHomeRpcResult<IMessage[]>.ConnectionClosed();

            try
            {
                return await operation.Completion.Task.WaitAsync(timeout, _timeProvider, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException) when (!cancellationToken.IsCancellationRequested)
            {
                return EspHomeRpcResult<IMessage[]>.TimedOut();
            }
        }
        finally
        {
            RemovePendingResponseOperation(completionKey, operation);
        }
    }

    /// <summary>Registers and waits for one uncorrelated response message of the requested protobuf type.</summary>
    private async Task<EspHomeRpcResult<TResponse>> WriteMessageAwaitResponseAsync<TResponse>(IMessage request, TimeSpan timeout, CancellationToken cancellationToken) where TResponse : class, IMessage
    {
        var completionKey = CreatePendingResponseKey<TResponse>();
        var result = await WriteMessageAwaitResponsesAsync(request, completionKey, timeout, cancellationToken).ConfigureAwait(false);

        if (result.Status == EspHomeRpcStatus.ConnectionClosed) return EspHomeRpcResult<TResponse>.ConnectionClosed();
        if (result.Status == EspHomeRpcStatus.TimedOut) return EspHomeRpcResult<TResponse>.TimedOut();

        var responses = result.Value;
        if (responses is null || responses.Length == 0) throw new InvalidOperationException("A completed RPC operation did not contain its terminal response.");

        return EspHomeRpcResult<TResponse>.Completed((TResponse)responses[0]);
    }

    /// <summary>Routes one incoming protobuf message to every matching pending RPC operation.</summary>
    private void HandlePendingResponse(IMessage message)
    {
        if (_pendingResponseOperations.IsEmpty) return;

        var responseKey = CreatePendingResponseKey(message);

        foreach (var operation in _pendingResponseOperations.Values)
        {
            operation.TryHandleResponse(responseKey, message);
        }
    }

    /// <summary>Completes and removes all active RPC and entity-discovery waiters with an explicit connection-closed result.</summary>
    private void FailPendingOperations()
    {
        foreach (var pair in _pendingResponseOperations)
        {
            if (!RemovePendingResponseOperation(pair.Key, pair.Value)) continue;
            pair.Value.Completion.TrySetResult(EspHomeRpcResult<IMessage[]>.ConnectionClosed());
        }

        var entityListAssembly = Interlocked.Exchange(ref _currentEntityListAssembly, null);
        entityListAssembly?.Completion.TrySetResult(EspHomeRpcStatus.ConnectionClosed);
    }

    /// <summary>Removes a pending RPC only when the dictionary still contains the exact operation instance owned by the caller.</summary>
    private bool RemovePendingResponseOperation(PendingResponseKey key, PendingResponseOperation operation)
    {
        var pair = new KeyValuePair<PendingResponseKey, PendingResponseOperation>(key, operation);
        return _pendingResponseOperations.TryRemove(pair);
    }

    /// <summary>Returns all pooled camera segments still owned by incomplete camera frames.</summary>
    private void ClearCameraAssemblies()
    {
        foreach (var assembly in _cameraAssemblies.Values)
        {
            assembly?.Dispose();
        }

        _cameraAssemblies.Clear();
    }

    /// <summary>Creates the correlation key represented by an incoming protobuf response.</summary>
    private static PendingResponseKey CreatePendingResponseKey(IMessage message)
    {
        return message switch
        {
            ExecuteServiceResponse response => CreatePendingResponseKey<ExecuteServiceResponse>(response.CallId),
            BluetoothDeviceConnectionResponse response => CreatePendingResponseKey<BluetoothDeviceConnectionResponse>(response.Address, response.Connected ? 1UL : 0UL),
            BluetoothDevicePairingResponse response => CreatePendingResponseKey<BluetoothDevicePairingResponse>(response.Address),
            BluetoothDeviceUnpairingResponse response => CreatePendingResponseKey<BluetoothDeviceUnpairingResponse>(response.Address),
            BluetoothDeviceClearCacheResponse response => CreatePendingResponseKey<BluetoothDeviceClearCacheResponse>(response.Address),
            BluetoothGATTGetServicesResponse response => CreatePendingResponseKey<BluetoothGATTGetServicesResponse>(response.Address),
            BluetoothGATTGetServicesDoneResponse response => CreatePendingResponseKey<BluetoothGATTGetServicesDoneResponse>(response.Address),
            BluetoothGATTReadResponse response => CreatePendingResponseKey<BluetoothGATTReadResponse>(response.Address, response.Handle),
            BluetoothGATTWriteResponse response => CreatePendingResponseKey<BluetoothGATTWriteResponse>(response.Address, response.Handle),
            BluetoothGATTNotifyResponse response => CreatePendingResponseKey<BluetoothGATTNotifyResponse>(response.Address, response.Handle),
            BluetoothGATTErrorResponse response => CreatePendingResponseKey<BluetoothGATTErrorResponse>(response.Address, response.Handle),
            BluetoothSetConnectionParamsResponse response => CreatePendingResponseKey<BluetoothSetConnectionParamsResponse>(response.Address),
            SerialProxyGetModemPinsResponse response => CreatePendingResponseKey<SerialProxyGetModemPinsResponse>(response.Instance),
            SerialProxyRequestResponse response => CreatePendingResponseKey<SerialProxyRequestResponse>(response.Instance, (ulong)response.Type),
            ZWaveProxyRequestResponse response => CreatePendingResponseKey<ZWaveProxyRequestResponse>((ulong)response.Type),
            _ => new PendingResponseKey(message.GetType())
        };
    }

    /// <summary>Creates an uncorrelated response key for one protobuf message type.</summary>
    private static PendingResponseKey CreatePendingResponseKey<TMessage>() where TMessage : IMessage
    {
        return new PendingResponseKey(typeof(TMessage));
    }

    /// <summary>Creates a response key correlated by one unsigned protocol value.</summary>
    private static PendingResponseKey CreatePendingResponseKey<TMessage>(ulong correlation) where TMessage : IMessage
    {
        return new PendingResponseKey(typeof(TMessage), correlation);
    }

    /// <summary>Creates a response key correlated by two unsigned protocol values.</summary>
    private static PendingResponseKey CreatePendingResponseKey<TMessage>(ulong correlationA, ulong correlationB) where TMessage : IMessage
    {
        return new PendingResponseKey(typeof(TMessage), correlationA, correlationB);
    }

    /// <summary>Transfers a connection-owned application payload to the bounded output stream without blocking protocol processing.</summary>
    private bool PublishOutputMessage(EspHomeMessageType messageType, object payload, long connectionGeneration, long outputGeneration)
    {
        var ownsActiveConnection = connectionGeneration == Volatile.Read(ref _connectionGeneration);
        var ownsCurrentOutput = outputGeneration == Volatile.Read(ref _outputGeneration);
        var connectionIsEstablished = Volatile.Read(ref _connectionLifecycle) == ConnectionLifecycleEstablished && State == ConnectionState.Connected;

        if (!ownsActiveConnection || !ownsCurrentOutput || !connectionIsEstablished) return false;

        var message = new EspHomeMessage(messageType, payload);
        var envelope = new OutputMessageEnvelope(outputGeneration, message);
        var messagePublished = _outputChannel.Writer.TryWrite(envelope);

        if (messagePublished) return true;

        CloseConnection(connectionGeneration, EspHomeConnectionCloseReason.OutputConsumerStalled, publishConnectionClosed: false);
        return false;
    }

    /// <summary>Publishes rejection of an application-requested outgoing message in the current output generation.</summary>
    private void PublishOutgoingMessageRejected(IMessage message)
    {
        var payload = new EspHomeOutgoingMessageRejected(message.GetType(), EspHomeOutgoingMessageRejectedReason.ConnectionClosed);
        var outputMessage = new EspHomeMessage(EspHomeMessageType.OutgoingMessageRejected, payload);
        var outputGeneration = Volatile.Read(ref _outputGeneration);
        var envelope = new OutputMessageEnvelope(outputGeneration, outputMessage);

        if (_outputChannel.Writer.TryWrite(envelope)) return;
        if (State != ConnectionState.Connected) return;

        var connectionGeneration = Volatile.Read(ref _connectionGeneration);
        CloseConnection(connectionGeneration, EspHomeConnectionCloseReason.OutputConsumerStalled, publishConnectionClosed: false);
    }

    /// <summary>Attempts to queue an internal protocol message and returns false when the writer closed during the enqueue race.</summary>
    private static async ValueTask<bool> TryQueueMessageAsync(ChannelWriter<QueuedMessage> writer, IMessage message, CancellationToken cancellationToken)
    {
        var queuedMessage = new QueuedMessage(message, null);

        try
        {
            await writer.WriteAsync(queuedMessage, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (ChannelClosedException)
        {
            return false;
        }
    }

    /// <summary>Attempts to queue a protocol message and waits until its serialized transport write physically completes.</summary>
    private static async Task<bool> TryWriteMessageAndWaitAsync(ChannelWriter<QueuedMessage> writer, IMessage message, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var queuedMessage = new QueuedMessage(message, completion);

        try
        {
            await writer.WriteAsync(queuedMessage, cancellationToken).ConfigureAwait(false);
        }
        catch (ChannelClosedException)
        {
            return false;
        }

        return await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Propagates a connection failure detected concurrently with connection setup.</summary>
    private void ThrowIfConnectionFailed()
    {
        var connectionCancellation = Volatile.Read(ref _connectionCancellation);
        var connectionClosed = connectionCancellation is null || connectionCancellation.IsCancellationRequested;

        if (!connectionClosed) return;

        var failure = Volatile.Read(ref _connectionFailure);
        if (failure is not null) throw failure;

        throw new IOException("ESPHome connection closed during setup.");
    }

    /// <summary>Records a transport or protocol failure and delegates all lifecycle closure to the central close owner.</summary>
    private void HandleConnectionFailure(long connectionGeneration, Exception exception)
    {
        var closeReason = ResolveCloseReason(EspHomeConnectionCloseReason.TransportFailure);
        CloseConnection(connectionGeneration, closeReason, exception);
    }

    /// <summary>Updates the externally visible connection state with cross-thread memory visibility.</summary>
    private void SetState(ConnectionState state)
    {
        Volatile.Write(ref _state, (int)state);
    }

    /// <summary>Stores entity and user-service descriptions until ListEntitiesDoneResponse completes discovery.</summary>
    private sealed class EntityListAssembly
    {
        /// <summary>Gets the entity descriptions accumulated for the current discovery request.</summary>
        public List<IMessage> Entities { get; } = new(128);

        /// <summary>Gets the user-defined services accumulated for the current discovery request.</summary>
        public List<ListEntitiesServicesResponse> Services { get; } = new(8);

        /// <summary>Gets the terminal status signal for the current discovery operation.</summary>
        public TaskCompletionSource<EspHomeRpcStatus> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
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

    /// <summary>Identifies one pending response by protobuf type and up to two protocol correlation values.</summary>
    private readonly struct PendingResponseKey : IEquatable<PendingResponseKey>
    {
        private readonly Type _messageType;
        private readonly ulong _correlationA;
        private readonly ulong _correlationB;
        private readonly byte _correlationCount;

        /// <summary>Creates a response key correlated only by protobuf message type.</summary>
        public PendingResponseKey(Type messageType)
        {
            _messageType = messageType;
            _correlationA = 0;
            _correlationB = 0;
            _correlationCount = 0;
        }

        /// <summary>Creates a response key correlated by protobuf message type and one protocol value.</summary>
        public PendingResponseKey(Type messageType, ulong correlationA)
        {
            _messageType = messageType;
            _correlationA = correlationA;
            _correlationB = 0;
            _correlationCount = 1;
        }

        /// <summary>Creates a response key correlated by protobuf message type and two protocol values.</summary>
        public PendingResponseKey(Type messageType, ulong correlationA, ulong correlationB)
        {
            _messageType = messageType;
            _correlationA = correlationA;
            _correlationB = correlationB;
            _correlationCount = 2;
        }

        /// <summary>Checks whether this key is a complete or prefix correlation match for an incoming response key.</summary>
        public bool Matches(PendingResponseKey responseKey)
        {
            if (_messageType != responseKey._messageType) return false;
            if (_correlationCount > responseKey._correlationCount) return false;
            if (_correlationCount >= 1 && _correlationA != responseKey._correlationA) return false;
            if (_correlationCount >= 2 && _correlationB != responseKey._correlationB) return false;

            return true;
        }

        /// <summary>Checks exact response-key equality for dictionary lookup.</summary>
        public bool Equals(PendingResponseKey other)
        {
            return _messageType == other._messageType
                && _correlationA == other._correlationA
                && _correlationB == other._correlationB
                && _correlationCount == other._correlationCount;
        }

        /// <summary>Checks exact response-key equality against an arbitrary object.</summary>
        public override bool Equals(object? obj)
        {
            return obj is PendingResponseKey other && Equals(other);
        }

        /// <summary>Returns the hash code used for exact pending-response dictionary lookup.</summary>
        public override int GetHashCode()
        {
            return HashCode.Combine(_messageType, _correlationA, _correlationB, _correlationCount);
        }
    }

    /// <summary>Collects one correlated RPC response sequence and completes with an explicit result when its terminal, interrupt or connection-close condition occurs.</summary>
    private sealed class PendingResponseOperation
    {
        private readonly PendingResponseKey[] _accumulationKeys;
        private readonly PendingResponseKey[] _interruptKeys;
        private List<IMessage>? _responses;

        /// <summary>Gets the terminal response key that uniquely identifies this pending operation.</summary>
        public PendingResponseKey CompletionKey { get; }

        /// <summary>Gets the task completed with the explicit terminal RPC result.</summary>
        public TaskCompletionSource<EspHomeRpcResult<IMessage[]>> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Creates one pending RPC operation with optional nonterminal and interrupt response keys.</summary>
        public PendingResponseOperation(PendingResponseKey completionKey, PendingResponseKey[] accumulationKeys, PendingResponseKey[] interruptKeys)
        {
            CompletionKey = completionKey;
            _accumulationKeys = accumulationKeys;
            _interruptKeys = interruptKeys;

            if (accumulationKeys.Length > 0) _responses = new List<IMessage>(4);
        }

        /// <summary>Routes one correlated response into accumulation, normal completion or interrupt completion.</summary>
        public bool TryHandleResponse(PendingResponseKey responseKey, IMessage message)
        {
            if (Completion.Task.IsCompleted) return false;

            foreach (var accumulationKey in _accumulationKeys)
            {
                if (!accumulationKey.Matches(responseKey)) continue;

                _responses ??= new List<IMessage>(4);
                _responses.Add(message);
                return true;
            }

            if (CompletionKey.Matches(responseKey))
            {
                Complete(message);
                return true;
            }

            foreach (var interruptKey in _interruptKeys)
            {
                if (!interruptKey.Matches(responseKey)) continue;

                Complete(message);
                return true;
            }

            return false;
        }

        /// <summary>Completes the operation with the accumulated responses and the terminal response.</summary>
        private void Complete(IMessage message)
        {
            var responses = _responses;
            if (responses is null)
            {
                Completion.TrySetResult(EspHomeRpcResult<IMessage[]>.Completed([message]));
                return;
            }

            responses.Add(message);
            Completion.TrySetResult(EspHomeRpcResult<IMessage[]>.Completed(responses.ToArray()));
        }
    }

    /// <summary>Stores one serialized protocol message and an optional completion signal for callers that must wait for the physical write.</summary>
    private readonly struct QueuedMessage
    {
        /// <summary>Gets the protocol message queued for transmission.</summary>
        public IMessage Message { get; }

        /// <summary>Gets the optional signal completed with true only after the transport write succeeds.</summary>
        public TaskCompletionSource<bool>? Completion { get; }

        /// <summary>Creates one transport write queue item without copying the protocol payload.</summary>
        public QueuedMessage(IMessage message, TaskCompletionSource<bool>? completion)
        {
            Message = message;
            Completion = completion;
        }
    }

    /// <summary>Stores one bounded output item together with the generation that owns it.</summary>
    private readonly struct OutputMessageEnvelope(long generation, EspHomeMessage message)
    {
        /// <summary>Gets the output generation that owns this item.</summary>
        public long Generation { get; } = generation;

        /// <summary>Gets the application message carried by this output item.</summary>
        public EspHomeMessage Message { get; } = message;
    }
}