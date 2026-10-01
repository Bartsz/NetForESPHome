using Microsoft.Extensions.Logging;
using NetForESPHome.DomainModel.Native;
using NetForESPHome.Transports;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;

namespace NetForESPHome.ServerMock;

/// <summary>Represents the externally observable lifecycle state of an ESPHome Native API mock server.</summary>
public enum EspHomeMockServerState
{
    /// <summary>The server does not own a TCP listener or active client session.</summary>
    Stopped,

    /// <summary>The server is accepting TCP connections and currently has no active client.</summary>
    Listening,

    /// <summary>The server owns one active client session, including any transport handshake currently in progress.</summary>
    ClientConnected,

    /// <summary>The server lifetime has ended and it cannot be started again.</summary>
    Disposed
}

/// <summary>Hosts one ESPHome Native API endpoint with plaintext or Noise transport, retained entity state and scheduled producers.</summary>
public sealed class EspHomeMockServer : IAsyncDisposable
{
    private const int WriteChannelCapacity = 100;
    private const uint BluetoothProxyPassiveScanFeature = 1 << 0;
    private const uint BluetoothProxyRawAdvertisementsFeature = 1 << 5;
    private const uint BluetoothProxyFeatureFlags =
        BluetoothProxyPassiveScanFeature | BluetoothProxyRawAdvertisementsFeature;

    private static readonly TimeSpan NoiseHandshakeTimeout = TimeSpan.FromSeconds(60);

    private readonly EspHomeMockServerConfiguration _configuration;
    private readonly ILogger<EspHomeMockServer>? _logger;
    private readonly Dictionary<EntityAddress, EspHomeEntityConfiguration> _entities;
    private readonly Dictionary<ulong, EspHomeBluetoothDeviceConfiguration> _bluetoothDevices = [];

    private TcpListener? _listener;
    private CancellationTokenSource? _serverCancellation;
    private Task? _acceptLoopTask;
    private TcpClient? _activeClient;
    private ChannelWriter<QueuedServerMessage>? _activeWriter;
    private EspHomeMdnsAdvertiser? _mdnsAdvertiser;
    private string? _connectedClientInfo;
    private Exception? _lastClientException;

    private int _state = (int)EspHomeMockServerState.Stopped;
    private int _running;
    private int _statesSubscribed;
    private int _disposed;
    private int _listeningPort;
    private int _bluetoothAdvertisementsSubscribed;
    private int _bluetoothProducerLoopsStarted;

    /// <summary>Gets the current lifecycle state of the mock server.</summary>
    public EspHomeMockServerState State => (EspHomeMockServerState)Volatile.Read(ref _state);

    /// <summary>Gets the actual TCP port while listening, including an operating-system assigned port when configuration requested zero.</summary>
    public int Port => Volatile.Read(ref _listeningPort);

    /// <summary>Gets the client_info string from the active client's HelloRequest, or null when no client has completed Hello.</summary>
    public string? ConnectedClientInfo => Volatile.Read(ref _connectedClientInfo);

    /// <summary>Gets the most recent client-session exception that caused a session to end unexpectedly.</summary>
    public Exception? LastClientException => Volatile.Read(ref _lastClientException);

    /// <summary>Creates a reusable mock server from a validated ESPHome endpoint configuration.</summary>
    public EspHomeMockServer(EspHomeMockServerConfiguration configuration, ILogger<EspHomeMockServer>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        _configuration = configuration;
        _logger = logger;
        _entities = configuration.Entities.ToDictionary(entity => entity.Address);
    }

    /// <summary>Starts the TCP listener, begins accepting one active client at a time and advertises the endpoint through mDNS when enabled.</summary>
    public ValueTask StartAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();

        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
        {
            throw new InvalidOperationException("ESPHome mock server is already running.");
        }

        var serverCancellation = new CancellationTokenSource();
        var listener = new TcpListener(_configuration.ListenAddress, _configuration.Port);

        try
        {
            listener.Start();
            var localEndpoint = (IPEndPoint)listener.LocalEndpoint;

            _listener = listener;
            _serverCancellation = serverCancellation;
            Volatile.Write(ref _listeningPort, localEndpoint.Port);

            if (_configuration.AdvertiseMdns)
            {
                _mdnsAdvertiser = new EspHomeMdnsAdvertiser(_configuration, localEndpoint.Port);
            }

            SetState(EspHomeMockServerState.Listening);
            _acceptLoopTask = AcceptClientsLoopAsync(listener, serverCancellation.Token);

            _logger?.LogInformation(
                "ESPHome mock server started listening on {Address}:{Port}.",
                _configuration.ListenAddress,
                localEndpoint.Port);

            return ValueTask.CompletedTask;
        }
        catch (Exception exception)
        {
            _logger?.LogError(
                exception,
                "ESPHome mock server failed to start listening on {Address}:{Port}.",
                _configuration.ListenAddress,
                _configuration.Port);

            CleanupFailedStart(listener, serverCancellation);
            throw;
        }
    }

    /// <summary>Stops accepting clients, closes the active session, withdraws mDNS and waits for all producer and transport loops to finish.</summary>
    public async ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await StopCoreAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Returns a cloned retained state snapshot for one configured entity, or null when that entity intentionally retains no state.</summary>
    public IESPHomeStateMessage? GetEntityState(uint key, uint deviceId = 0)
    {
        ThrowIfDisposed();
        return GetEntity(new EntityAddress(deviceId, key)).GetState();
    }

    /// <summary>Returns a typed retained state snapshot for one configured entity and verifies its protobuf state type.</summary>
    public TState? GetEntityState<TState>(uint key, uint deviceId = 0) where TState : class, IESPHomeStateMessage
    {
        var state = GetEntityState(key, deviceId);
        if (state is null)
        {
            return null;
        }

        if (state is TState typedState)
        {
            return typedState;
        }

        throw new InvalidOperationException(
            $"ESPHome entity {deviceId}:{key} retains {state.GetType().Name}, not {typeof(TState).Name}.");
    }

    /// <summary>Replaces one configured entity state and optionally publishes the new snapshot to the currently subscribed client.</summary>
    public async ValueTask SetEntityStateAsync(IESPHomeMessage state, bool publish = true, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(state);

        if (state is not IESPHomeStateMessage entityState)
        {
            throw new ArgumentException(
                $"Message type {state.GetType().Name} is not an ESPHome entity state response.",
                nameof(state));
        }

        var address = EntityAddress.From(entityState);
        var entity = GetEntity(address);
        if (!entity.TrySetState(entityState))
        {
            throw new ArgumentException(
                $"State type {state.GetType().Name} does not match the configured state type for entity {address}.",
                nameof(state));
        }

        if (!publish || !AreStatesSubscribed())
        {
            return;
        }

        var currentState = entity.GetState();
        var writer = Volatile.Read(ref _activeWriter);
        if (currentState is null || writer is null)
        {
            return;
        }

        await QueueMessageAsync(writer, currentState, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Publishes an arbitrary ESPHome protocol message to the active client through the serialized write loop.</summary>
    public ValueTask PublishMessageAsync(IESPHomeMessage message, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(message);

        var writer = Volatile.Read(ref _activeWriter)
            ?? throw new InvalidOperationException("No ESPHome client is currently connected.");

        return QueueMessageAsync(writer, message, cancellationToken);
    }

    /// <summary>Registers one Bluetooth LE device that will be advertised through the ESPHome Bluetooth proxy after Home Assistant subscribes.</summary>
    public void RegisterBluetoothDevice(EspHomeBluetoothDeviceConfiguration device)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(device);

        if (Volatile.Read(ref _running) != 0)
        {
            throw new InvalidOperationException(
                "Bluetooth devices must be registered before the ESPHome mock server starts.");
        }

        if (!_bluetoothDevices.TryAdd(device.Address, device))
        {
            throw new ArgumentException(
                $"Bluetooth device 0x{device.Address:X12} is already registered.",
                nameof(device));
        }
    }

    private EspHomeEntityConfiguration GetEntity(EntityAddress address)
    {
        if (_entities.TryGetValue(address, out var entity))
        {
            return entity;
        }

        throw new KeyNotFoundException($"ESPHome entity {address} is not configured.");
    }

    private async Task AcceptClientsLoopAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var client = await AcceptClientAsync(listener, cancellationToken).ConfigureAwait(false);
                if (client is null)
                {
                    return;
                }

                await RunClientSessionAsync(client, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            if (Volatile.Read(ref _running) == 0 && Volatile.Read(ref _disposed) == 0)
            {
                SetState(EspHomeMockServerState.Stopped);
            }
        }
    }

    private async Task RunClientSessionAsync(TcpClient client, CancellationToken cancellationToken)
    {
        _activeClient = client;
        Volatile.Write(ref _lastClientException, null);
        SetState(EspHomeMockServerState.ClientConnected);

        try
        {
            await HandleClientAsync(client, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (IOException)
        {
        }
        catch (SocketException)
        {
        }
        catch (Exception exception)
        {
            Volatile.Write(ref _lastClientException, exception);
        }
        finally
        {
            ResetClientSession(client);
        }
    }

    private async Task<TcpClient?> AcceptClientAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        try
        {
            var client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            _logger?.LogInformation("Server accepted a new client connection.");
            return client;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (SocketException) when (cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    private void ResetClientSession(TcpClient client)
    {
        Volatile.Write(ref _statesSubscribed, 0);
        Volatile.Write(ref _bluetoothAdvertisementsSubscribed, 0);
        Volatile.Write(ref _bluetoothProducerLoopsStarted, 0);
        Volatile.Write(ref _connectedClientInfo, null);

        Interlocked.Exchange(ref _activeWriter, null);
        Interlocked.CompareExchange(ref _activeClient, null, client);
        client.Dispose();

        if (Volatile.Read(ref _running) != 0)
        {
            SetState(EspHomeMockServerState.Listening);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken serverCancellationToken)
    {
        client.NoDelay = true;

        using var sessionCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(serverCancellationToken);
        using var producerCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(sessionCancellation.Token);
        using var messageTransport = await CreateMessageTransportAsync(client.GetStream(), sessionCancellation.Token).ConfigureAwait(false);

        var writeChannel = CreateWriteChannel();
        var previousWriter = Interlocked.CompareExchange(ref _activeWriter, writeChannel.Writer, null);

        if (previousWriter is not null)
        {
            throw new InvalidOperationException("Another ESPHome client writer is already active.");
        }

        var producerTasks = new List<Task>(
            _configuration.Entities.Length + _bluetoothDevices.Count);
        var writeLoopTask = WriteMessagesLoopAsync(messageTransport, writeChannel.Reader, sessionCancellation, sessionCancellation.Token);

        try
        {
            await ReadClientMessagesAsync(messageTransport, writeChannel.Writer, producerTasks, sessionCancellation, producerCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (sessionCancellation.IsCancellationRequested)
        {
        }
        finally
        {
            await StopClientWorkersAsync(producerCancellation, producerTasks, writeChannel.Writer, writeLoopTask).ConfigureAwait(false);
        }
    }

    private async Task ReadClientMessagesAsync(IMessageTransport messageTransport, ChannelWriter<QueuedServerMessage> writer, List<Task> producerTasks, CancellationTokenSource sessionCancellation, CancellationToken cancellationToken)
    {
        while (!sessionCancellation.IsCancellationRequested)
        {
            var message = await messageTransport
                .ReadMessageAsync(sessionCancellation.Token)
                .ConfigureAwait(false);

            if (await HandleMessageAsync(message, writer, producerTasks, sessionCancellation, cancellationToken).ConfigureAwait(false))
            {
                return;
            }
        }
    }

    private static async Task StopClientWorkersAsync(CancellationTokenSource producerCancellation, List<Task> producerTasks, ChannelWriter<QueuedServerMessage> writer, Task writeLoopTask)
    {
        producerCancellation.Cancel();

        if (producerTasks.Count > 0)
        {
            await ObserveTaskAsync(Task.WhenAll(producerTasks)).ConfigureAwait(false);
        }

        writer.TryComplete();
        await ObserveTaskAsync(writeLoopTask).ConfigureAwait(false);
    }

    private async Task<IMessageTransport> CreateMessageTransportAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var noisePsk = _configuration.NoisePsk;
        if (noisePsk is null)
        {
            return new PlainTextMessageTransport(stream);
        }

        using var timeoutCancellation =
            new CancellationTokenSource(NoiseHandshakeTimeout, _configuration.TimeProvider);
        using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutCancellation.Token);

        try
        {
            var handshakeExecutor = new NoiseServerHandshakeExecutor(
                noisePsk,
                _configuration.Name,
                _configuration.MacAddress);

            return await handshakeExecutor
                .ExecuteAsync(stream, operationCancellation.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (
            timeoutCancellation.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"ESPHome Noise client did not complete the handshake within {NoiseHandshakeTimeout}.");
        }
    }

    private async Task<bool> HandleMessageAsync(IESPHomeMessage message, ChannelWriter<QueuedServerMessage> writer, List<Task> producerTasks, CancellationTokenSource sessionCancellation, CancellationToken producerCancellationToken)
    {
        _logger?.LogTrace("Server received: {Message}", message);

        switch (message)
        {
            case HelloRequest request:
                Volatile.Write(ref _connectedClientInfo, request.ClientInfo);
                await QueueMessageAsync(writer, CreateHelloResponse(), producerCancellationToken)
                    .ConfigureAwait(false);
                return false;

#pragma warning disable CS0612, CS0618
            case AuthenticationRequest request:
                var invalidPassword = _configuration.Password is not null &&
                    !StringComparer.Ordinal.Equals(request.Password, _configuration.Password);

                await WriteMessageAndWaitAsync(writer, new AuthenticationResponse { InvalidPassword = invalidPassword }, producerCancellationToken).ConfigureAwait(false);
                return invalidPassword;
#pragma warning restore CS0612, CS0618

            case PingRequest:
                await QueueMessageAsync(writer, new PingResponse(), producerCancellationToken)
                    .ConfigureAwait(false);
                return false;

            case DisconnectRequest:
                await WriteMessageAndWaitAsync(writer, new DisconnectResponse(), producerCancellationToken).ConfigureAwait(false);
                return true;

            case DeviceInfoRequest:
                await QueueMessageAsync(writer, CreateDeviceInfoResponse(), producerCancellationToken)
                    .ConfigureAwait(false);
                return false;

            case DeviceCapabilitiesRequest:
                await QueueMessageAsync(writer, CreateDeviceCapabilitiesResponse(), producerCancellationToken).ConfigureAwait(false);
                return false;

            case ListEntitiesRequest:
                await WriteEntitiesListAsync(writer, producerCancellationToken).ConfigureAwait(false);
                return false;

            case SubscribeStatesRequest:
                await StartStatesSubscriptionAsync(writer, producerTasks, sessionCancellation, producerCancellationToken).ConfigureAwait(false);
                return false;

            case SubscribeBluetoothLEAdvertisementsRequest:
                await StartBluetoothAdvertisementsSubscriptionAsync(writer, producerTasks, sessionCancellation, producerCancellationToken).ConfigureAwait(false);
                return false;

            case UnsubscribeBluetoothLEAdvertisementsRequest:
                Volatile.Write(ref _bluetoothAdvertisementsSubscribed, 0);
                return false;

            case IESPHomeCommandMessage command:
                await HandleEntityCommandAsync(command, writer, producerCancellationToken)
                    .ConfigureAwait(false);
                return false;

            default:
                return false;
        }
    }

    private HelloResponse CreateHelloResponse() => new()
    {
        ApiVersionMajor = _configuration.ApiVersionMajor,
        ApiVersionMinor = _configuration.ApiVersionMinor,
        ServerInfo = _configuration.EspHomeVersion,
        Name = _configuration.Name
    };

    private DeviceInfoResponse CreateDeviceInfoResponse()
    {
        var response = _configuration.CreateDeviceInfoResponse();
        ConfigureBluetoothProxyDeviceInfo(response);
        return response;
    }

    private DeviceCapabilitiesResponse CreateDeviceCapabilitiesResponse()
    {
        var response = _configuration.CreateDeviceCapabilitiesResponse();
        ConfigureBluetoothProxyCapabilities(response);
        return response;
    }

    private async Task StartBluetoothAdvertisementsSubscriptionAsync(ChannelWriter<QueuedServerMessage> writer, List<Task> producerTasks, CancellationTokenSource sessionCancellation, CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _bluetoothAdvertisementsSubscribed, 1) != 0)
        {
            return;
        }

        foreach (var device in _bluetoothDevices.Values)
        {
            await QueueMessageAsync(writer, device.GenerateMessage(), cancellationToken)
                .ConfigureAwait(false);
        }

        if (Interlocked.Exchange(ref _bluetoothProducerLoopsStarted, 1) != 0)
        {
            return;
        }

        foreach (var device in _bluetoothDevices.Values)
        {
            producerTasks.Add(RunProducerLoopAsync(
                device,
                writer,
                sessionCancellation,
                cancellationToken,
                AreBluetoothAdvertisementsSubscribed));
        }
    }

    private void ConfigureBluetoothProxyCapabilities(DeviceCapabilitiesResponse response)
    {
        if (_bluetoothDevices.Count == 0)
        {
            return;
        }

        response.BluetoothProxy = new BluetoothProxyCapabilities
        {
            FeatureFlags = BluetoothProxyFeatureFlags,
            MacAddress = _configuration.MacAddress
        };
    }

    private void ConfigureBluetoothProxyDeviceInfo(DeviceInfoResponse response)
    {
        if (_bluetoothDevices.Count == 0)
        {
            return;
        }

        response.BluetoothProxyFeatureFlags |= BluetoothProxyFeatureFlags;
        if (string.IsNullOrEmpty(response.BluetoothMacAddress))
        {
            response.BluetoothMacAddress = _configuration.MacAddress;
        }
    }

    private async Task WriteEntitiesListAsync(ChannelWriter<QueuedServerMessage> writer, CancellationToken cancellationToken)
    {
        foreach (var entity in _configuration.Entities)
        {
            await QueueMessageAsync(writer, entity.EntityInfo, cancellationToken).ConfigureAwait(false);
        }

        foreach (var service in _configuration.Services)
        {
            await QueueMessageAsync(writer, service, cancellationToken).ConfigureAwait(false);
        }

        await QueueMessageAsync(writer, new ListEntitiesDoneResponse(), cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task StartStatesSubscriptionAsync(ChannelWriter<QueuedServerMessage> writer, List<Task> producerTasks, CancellationTokenSource sessionCancellation, CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _statesSubscribed, 1) != 0)
        {
            return;
        }

        foreach (var entity in _configuration.Entities)
        {
            var state = entity.GetState();
            if (state is not null)
            {
                await QueueMessageAsync(writer, state, cancellationToken).ConfigureAwait(false);
            }
        }

        foreach (var entity in _configuration.Entities)
        {
            if (entity is IMessageProducer producer)
            {
                producerTasks.Add(RunProducerLoopAsync(
                    producer,
                    writer,
                    sessionCancellation,
                    cancellationToken));
            }
        }
    }

    private async Task RunProducerLoopAsync(IMessageProducer producer, ChannelWriter<QueuedServerMessage> writer, CancellationTokenSource sessionCancellation, CancellationToken cancellationToken, Func<bool>? shouldPublish = null)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await producer.WaitForDelayAsync(_configuration.TimeProvider, cancellationToken)
                    .ConfigureAwait(false);

                if (shouldPublish is not null && !shouldPublish())
                {
                    continue;
                }

                await QueueMessageAsync(writer, producer.GenerateMessage(), cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Volatile.Write(ref _lastClientException, exception);
            sessionCancellation.Cancel();
            throw;
        }
    }

    private async Task HandleEntityCommandAsync(IESPHomeCommandMessage command, ChannelWriter<QueuedServerMessage> writer, CancellationToken cancellationToken)
    {
        await ApplyEntityCommandAsync(command, writer, cancellationToken).ConfigureAwait(false);

        var callback = _configuration.CommandReceived;
        if (callback is not null)
        {
            await callback(command, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ApplyEntityCommandAsync(IESPHomeCommandMessage command, ChannelWriter<QueuedServerMessage> writer, CancellationToken cancellationToken)
    {
        var address = EntityAddress.From(command);
        if (!_entities.TryGetValue(address, out var entity))
        {
            return;
        }

        var traceStateChanges = _logger?.IsEnabled(Microsoft.Extensions.Logging.LogLevel.Trace) == true;
        var previousState = traceStateChanges ? entity.GetState() : null;

        if (!entity.TryApplyCommand(command, out var updatedState) || updatedState is null)
        {
            return;
        }

        if (AreStatesSubscribed())
        {
            await QueueMessageAsync(writer, updatedState, cancellationToken).ConfigureAwait(false);
        }

        if (traceStateChanges)
        {
            _logger!.LogTrace(
                "Updated state for {EntityAddress}: {OldState} -> {NewState}",
                address,
                previousState,
                updatedState);
        }
    }

    private bool AreStatesSubscribed() => Volatile.Read(ref _statesSubscribed) != 0;

    private bool AreBluetoothAdvertisementsSubscribed() =>
        Volatile.Read(ref _bluetoothAdvertisementsSubscribed) != 0;

    private static Channel<QueuedServerMessage> CreateWriteChannel()
    {
        var options = new BoundedChannelOptions(WriteChannelCapacity)
        {
            AllowSynchronousContinuations = true,
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        };

        return Channel.CreateBounded<QueuedServerMessage>(options);
    }

    private async Task WriteMessagesLoopAsync(IMessageTransport transport, ChannelReader<QueuedServerMessage> reader, CancellationTokenSource sessionCancellation, CancellationToken cancellationToken)
    {
        Exception? failure = null;
        var messageCount = 0;

        using var messageRateTimer = CreateMessageRateTimer(
            () => Interlocked.Exchange(ref messageCount, 0));

        try
        {
            while (await reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                while (reader.TryRead(out var queuedMessage))
                {
                    Interlocked.Increment(ref messageCount);
                    await WriteQueuedMessageAsync(transport, queuedMessage, cancellationToken)
                        .ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            failure = exception;
            Volatile.Write(ref _lastClientException, exception);
            sessionCancellation.Cancel();
        }
        finally
        {
            CompletePendingWrites(reader, failure, cancellationToken);
        }
    }

    private ITimer? CreateMessageRateTimer(Func<int> resetMessageCount)
    {
        if (_logger?.IsEnabled(Microsoft.Extensions.Logging.LogLevel.Debug) != true)
        {
            return null;
        }

        return _configuration.TimeProvider.CreateTimer(
            _ => _logger.LogDebug("Server sent messages: {MsgRate} msg/s", resetMessageCount()),
            null,
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(1));
    }

    private static async ValueTask WriteQueuedMessageAsync(
        IMessageTransport transport,
        QueuedServerMessage queuedMessage,
        CancellationToken cancellationToken)
    {
        try
        {
            await transport.WriteMessageAsync(queuedMessage.Message, cancellationToken)
                .ConfigureAwait(false);
            queuedMessage.Completion?.TrySetResult(true);
        }
        catch (Exception exception)
        {
            queuedMessage.Completion?.TrySetException(exception);
            throw;
        }
    }

    private static void CompletePendingWrites(
        ChannelReader<QueuedServerMessage> reader,
        Exception? failure,
        CancellationToken cancellationToken)
    {
        while (reader.TryRead(out var queuedMessage))
        {
            if (failure is not null)
            {
                queuedMessage.Completion?.TrySetException(failure);
            }
            else
            {
                queuedMessage.Completion?.TrySetCanceled(cancellationToken);
            }
        }
    }

    private static ValueTask QueueMessageAsync(
        ChannelWriter<QueuedServerMessage> writer,
        IESPHomeMessage message,
        CancellationToken cancellationToken) =>
        writer.WriteAsync(new QueuedServerMessage(message, null), cancellationToken);

    private static async ValueTask WriteMessageAndWaitAsync(
        ChannelWriter<QueuedServerMessage> writer,
        IESPHomeMessage message,
        CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        await writer.WriteAsync(
            new QueuedServerMessage(message, completion),
            cancellationToken).ConfigureAwait(false);
        await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task ObserveTaskAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
        }
    }

    private async ValueTask StopCoreAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _running, 0) == 0)
        {
            if (Volatile.Read(ref _disposed) == 0)
            {
                SetState(EspHomeMockServerState.Stopped);
            }

            return;
        }

        var serverCancellation = Interlocked.Exchange(ref _serverCancellation, null);
        var listener = Interlocked.Exchange(ref _listener, null);
        var activeClient = Interlocked.Exchange(ref _activeClient, null);
        var acceptLoopTask = Interlocked.Exchange(ref _acceptLoopTask, null);
        var mdnsAdvertiser = Interlocked.Exchange(ref _mdnsAdvertiser, null);

        serverCancellation?.Cancel();
        listener?.Stop();
        activeClient?.Dispose();

        try
        {
            if (acceptLoopTask is not null)
            {
                await acceptLoopTask.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            DisposeStoppedResources(mdnsAdvertiser, serverCancellation);
        }
    }

    private void CleanupFailedStart(
        TcpListener listener,
        CancellationTokenSource serverCancellation)
    {
        var mdnsAdvertiser = Interlocked.Exchange(ref _mdnsAdvertiser, null);

        try
        {
            mdnsAdvertiser?.Dispose();
        }
        finally
        {
            listener.Stop();
            serverCancellation.Dispose();
            Interlocked.Exchange(ref _listener, null);
            Interlocked.Exchange(ref _serverCancellation, null);
            Volatile.Write(ref _running, 0);
            Volatile.Write(ref _listeningPort, 0);
            SetState(EspHomeMockServerState.Stopped);
        }
    }

    private void DisposeStoppedResources(
        EspHomeMdnsAdvertiser? mdnsAdvertiser,
        CancellationTokenSource? serverCancellation)
    {
        try
        {
            mdnsAdvertiser?.Dispose();
        }
        finally
        {
            serverCancellation?.Dispose();
            ResetStoppedState();
        }
    }

    private void ResetStoppedState()
    {
        Interlocked.Exchange(ref _activeWriter, null);
        Volatile.Write(ref _bluetoothAdvertisementsSubscribed, 0);
        Volatile.Write(ref _bluetoothProducerLoopsStarted, 0);
        Volatile.Write(ref _statesSubscribed, 0);
        Volatile.Write(ref _connectedClientInfo, null);
        Volatile.Write(ref _listeningPort, 0);

        if (Volatile.Read(ref _disposed) == 0)
        {
            SetState(EspHomeMockServerState.Stopped);
        }
    }

    private void SetState(EspHomeMockServerState state) =>
        Volatile.Write(ref _state, (int)state);

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            throw new ObjectDisposedException(nameof(EspHomeMockServer));
        }
    }

    /// <summary>Stops the listener and active session, withdraws mDNS and permanently ends the server lifetime.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await StopCoreAsync(CancellationToken.None).ConfigureAwait(false);
        SetState(EspHomeMockServerState.Disposed);
        GC.SuppressFinalize(this);
    }

    private readonly record struct QueuedServerMessage(
        IESPHomeMessage Message,
        TaskCompletionSource<bool>? Completion);
}
