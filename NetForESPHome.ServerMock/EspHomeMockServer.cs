using Google.Protobuf;
using Microsoft.Extensions.Logging;
using NetForESPHome.DomainModel;
using NetForESPHome.Transports;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
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
public sealed partial class EspHomeMockServer : IAsyncDisposable
{
    private const int WriteChannelCapacity = 100;

    private const uint BluetoothProxyPassiveScanFeature = 1 << 0;
    private const uint BluetoothProxyRawAdvertisementsFeature = 1 << 5;
    private const uint BluetoothProxyFeatureFlags = BluetoothProxyPassiveScanFeature | BluetoothProxyRawAdvertisementsFeature;

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
        _entities = configuration.Entities.ToDictionary(entity => new EntityAddress(entity.DeviceId, entity.Key));
    }

    /// <summary>Starts the TCP listener, begins accepting one active client at a time and advertises the endpoint through mDNS when enabled.</summary>
    public ValueTask StartAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();

        var wasAlreadyRunning = Interlocked.CompareExchange(ref _running, 1, 0) != 0;
        if (wasAlreadyRunning) throw new InvalidOperationException("ESPHome mock server is already running.");

        var serverCancellation = new CancellationTokenSource();
        var listener = new TcpListener(_configuration.ListenAddress, _configuration.Port);

        try
        {
            listener.Start();
            _logger?.LogInformation("ESPHome mock server started listening on {Address}:{Port}.", _configuration.ListenAddress, _configuration.Port);
            var localEndpoint = (IPEndPoint)listener.LocalEndpoint;
            Volatile.Write(ref _listeningPort, localEndpoint.Port);

            _listener = listener;
            _serverCancellation = serverCancellation;

            if (_configuration.AdvertiseMdns)
                _mdnsAdvertiser = new EspHomeMdnsAdvertiser(_configuration, localEndpoint.Port);

            SetState(EspHomeMockServerState.Listening);
            _acceptLoopTask = AcceptClientsLoopAsync(listener, serverCancellation.Token);
            return ValueTask.CompletedTask;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "ESPHome mock server failed to start listening on {Address}:{Port}.", _configuration.ListenAddress, _configuration.Port);
            var mdnsAdvertiser = Interlocked.Exchange(ref _mdnsAdvertiser, null);
            mdnsAdvertiser?.Dispose();
            listener.Stop();
            serverCancellation.Dispose();
            Interlocked.Exchange(ref _listener, null);
            Interlocked.Exchange(ref _serverCancellation, null);
            Volatile.Write(ref _running, 0);
            Volatile.Write(ref _listeningPort, 0);
            SetState(EspHomeMockServerState.Stopped);
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
    public IMessage? GetEntityState(uint key, uint deviceId = 0)
    {
        ThrowIfDisposed();

        var address = new EntityAddress(deviceId, key);
        if (!_entities.TryGetValue(address, out var entity)) throw new KeyNotFoundException($"ESPHome entity {address} is not configured.");

        return entity.GetState();
    }

    /// <summary>Returns a typed retained state snapshot for one configured entity and verifies its protobuf state type.</summary>
    public TState? GetEntityState<TState>(uint key, uint deviceId = 0) where TState : class, IMessage<TState>
    {
        var state = GetEntityState(key, deviceId);
        if (state is null) return null;
        if (state is TState typedState) return typedState;

        throw new InvalidOperationException($"ESPHome entity {deviceId}:{key} retains {state.GetType().Name}, not {typeof(TState).Name}.");
    }

    /// <summary>Replaces one configured entity state and optionally publishes the new snapshot to the currently subscribed client.</summary>
    public async ValueTask SetEntityStateAsync(IMessage state, bool publish = true, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(state);

        if (!EspHomeMessageMapper.IsEntityStateResponseMessage(state)) throw new ArgumentException($"Message type {state.GetType().Name} is not an ESPHome entity state response.", nameof(state));

        var address = EntityAddress.FromMessage(state);
        if (!_entities.TryGetValue(address, out var entity)) throw new KeyNotFoundException($"ESPHome entity {address} is not configured.");
        if (!entity.TrySetState(state)) throw new ArgumentException($"State type {state.GetType().Name} does not match the configured state type for entity {address}.", nameof(state));
        if (!publish || Volatile.Read(ref _statesSubscribed) == 0) return;

        var currentState = entity.GetState();
        if (currentState is null) return;

        var writer = Volatile.Read(ref _activeWriter);
        if (writer is null) return;

        await QueueMessageAsync(writer, currentState, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Publishes an arbitrary ESPHome protocol message to the active client through the serialized write loop.</summary>
    public ValueTask PublishMessageAsync(IMessage message, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(message);

        var writer = Volatile.Read(ref _activeWriter);
        if (writer is null) throw new InvalidOperationException("No ESPHome client is currently connected.");

        return QueueMessageAsync(writer, message, cancellationToken);
    }


    /// <summary>Registers one Bluetooth LE device that will be advertised through the ESPHome Bluetooth proxy after Home Assistant subscribes.</summary>
    public void RegisterBluetoothDevice(EspHomeBluetoothDeviceConfiguration device)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(device);

        var isRunning = Volatile.Read(ref _running) != 0;
        if (isRunning) throw new InvalidOperationException("Bluetooth devices must be registered before the ESPHome mock server starts.");

        var wasAdded = _bluetoothDevices.TryAdd(device.Address, device);
        if (!wasAdded) throw new ArgumentException($"Bluetooth device 0x{device.Address:X12} is already registered.", nameof(device));
    }

    /// <summary>Accepts sequential client sessions so a failed or disconnected test client does not stop the mock server itself.</summary>
    private async Task AcceptClientsLoopAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                TcpClient client;

                try
                {
                    client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                    _logger?.LogInformation("Server accepted a new client connection.");
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (SocketException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                _activeClient = client;
                Volatile.Write(ref _lastClientException, null);
                SetState(EspHomeMockServerState.ClientConnected);

                try
                {
                    await HandleClientAsync(client, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
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
                    Volatile.Write(ref _statesSubscribed, 0);
                    Volatile.Write(ref _bluetoothAdvertisementsSubscribed, 0);
                    Volatile.Write(ref _bluetoothProducerLoopsStarted, 0);
                    Volatile.Write(ref _connectedClientInfo, null);

                    Interlocked.Exchange(ref _activeWriter, null);
                    Interlocked.CompareExchange(ref _activeClient, null, client);
                    client.Dispose();

                    if (Volatile.Read(ref _running) != 0) SetState(EspHomeMockServerState.Listening);
                }
            }
        }
        finally
        {
            if (Volatile.Read(ref _running) == 0 && Volatile.Read(ref _disposed) == 0)
                SetState(EspHomeMockServerState.Stopped);
        }
    }

    /// <summary>Creates the configured message transport and runs one client read loop with a single serialized writer.</summary>
    private async Task HandleClientAsync(TcpClient client, CancellationToken serverCancellationToken)
    {
        client.NoDelay = true;

        using var sessionCancellation = CancellationTokenSource.CreateLinkedTokenSource(serverCancellationToken);

        var stream = client.GetStream();
        var messageTransport = await CreateMessageTransportAsync(stream, sessionCancellation.Token).ConfigureAwait(false);

        using (messageTransport)
        {
            var writeChannel = CreateWriteChannel();

            var previousWriter = Interlocked.CompareExchange(ref _activeWriter, writeChannel.Writer, null);
            if (previousWriter is not null) throw new InvalidOperationException("Another ESPHome client writer is already active.");

            var producerTasks = new List<Task>(_configuration.Entities.Length);
            var writeLoopTask = WriteMessagesLoopAsync(messageTransport, writeChannel.Reader, sessionCancellation, sessionCancellation.Token);

            try
            {
                while (!sessionCancellation.IsCancellationRequested)
                {
                    var message = await messageTransport.ReadMessageAsync(sessionCancellation.Token).ConfigureAwait(false);

                    var shouldClose = await HandleMessageAsync(
                        message,
                        writeChannel.Writer,
                        producerTasks,
                        sessionCancellation,
                        sessionCancellation.Token).ConfigureAwait(false);

                    if (shouldClose) break;
                }
            }
            catch (OperationCanceledException) when (sessionCancellation.IsCancellationRequested)
            {
            }
            finally
            {
                writeChannel.Writer.TryComplete();

                await ObserveTaskAsync(writeLoopTask).ConfigureAwait(false);

                sessionCancellation.Cancel();

                if (producerTasks.Count > 0)
                    await ObserveTaskAsync(Task.WhenAll(producerTasks)).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Creates plaintext framing directly or completes the responder-side ESPHome Noise handshake before message processing starts.</summary>
    private async Task<IMessageTransport> CreateMessageTransportAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var noisePsk = _configuration.NoisePsk;
        if (noisePsk is null) return new PlainTextMessageTransport(stream);

        using var timeoutCancellation = new CancellationTokenSource(NoiseHandshakeTimeout, _configuration.TimeProvider);
        using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCancellation.Token);

        try
        {
            var handshakeExecutor = new NoiseServerHandshakeExecutor(noisePsk, _configuration.Name, _configuration.MacAddress);
            return await handshakeExecutor.ExecuteAsync(stream, operationCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeoutCancellation.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"ESPHome Noise client did not complete the handshake within {NoiseHandshakeTimeout}.");
        }
    }

    /// <summary>Routes core Native API requests, subscriptions and entity commands while ignoring optional protocol features not configured by the mock.</summary>
    private async Task<bool> HandleMessageAsync(IMessage message, ChannelWriter<QueuedServerMessage> writer, List<Task> producerTasks, CancellationTokenSource sessionCancellation, CancellationToken cancellationToken)
    {
        _logger?.LogTrace("Server received: {Message}", message);

        if (message is HelloRequest helloRequest)
        {
            Volatile.Write(ref _connectedClientInfo, helloRequest.ClientInfo);

            var response = new HelloResponse
            {
                ApiVersionMajor = _configuration.ApiVersionMajor,
                ApiVersionMinor = _configuration.ApiVersionMinor,
                ServerInfo = _configuration.EspHomeVersion,
                Name = _configuration.Name
            };

            await QueueMessageAsync(writer, response, cancellationToken).ConfigureAwait(false);
            return false;
        }

#pragma warning disable CS0612, CS0618
        if (message is AuthenticationRequest authenticationRequest)
        {
            var invalidPassword = _configuration.Password is not null && !StringComparer.Ordinal.Equals(authenticationRequest.Password, _configuration.Password);
            var response = new AuthenticationResponse { InvalidPassword = invalidPassword };
            await WriteMessageAndWaitAsync(writer, response, cancellationToken).ConfigureAwait(false);
            return invalidPassword;
        }
#pragma warning restore CS0612, CS0618

        if (message is PingRequest)
        {
            var response = new PingResponse();
            await QueueMessageAsync(writer, response, cancellationToken).ConfigureAwait(false);
            return false;
        }

        if (message is DisconnectRequest)
        {
            var response = new DisconnectResponse();
            await WriteMessageAndWaitAsync(writer, response, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (message is DeviceInfoRequest)
        {
            var response = _configuration.CreateDeviceInfoResponse();
            ConfigureBluetoothProxyDeviceInfo(response);

            await QueueMessageAsync(writer, response, cancellationToken).ConfigureAwait(false);
            return false;
        }

        if (message is DeviceCapabilitiesRequest)
        {
            var response = _configuration.CreateDeviceCapabilitiesResponse();
            ConfigureBluetoothProxyCapabilities(response);

            await QueueMessageAsync(writer, response, cancellationToken).ConfigureAwait(false);
            return false;
        }

        if (message is ListEntitiesRequest)
        {
            await WriteEntitiesListAsync(writer, cancellationToken).ConfigureAwait(false);
            return false;
        }

        if (message is SubscribeStatesRequest)
        {
            await StartStatesSubscriptionAsync(writer, producerTasks, sessionCancellation, cancellationToken).ConfigureAwait(false);
            return false;
        }

        if (message is SubscribeBluetoothLEAdvertisementsRequest)
        {
            await StartBluetoothAdvertisementsSubscriptionAsync(writer, producerTasks, sessionCancellation, cancellationToken).ConfigureAwait(false);
            return false;
        }

        if (message is UnsubscribeBluetoothLEAdvertisementsRequest)
        {
            Volatile.Write(ref _bluetoothAdvertisementsSubscribed, 0);
            return false;
        }


        if (!IsEntityCommandMessage(message)) return false;

        await HandleEntityCommandAsync(message, writer, cancellationToken).ConfigureAwait(false);
        return false;
    }

    /// <summary>Starts publishing registered Bluetooth advertisements after the active Home Assistant client subscribes to the proxy scanner.</summary>
    private async Task StartBluetoothAdvertisementsSubscriptionAsync(ChannelWriter<QueuedServerMessage> writer, List<Task> producerTasks, CancellationTokenSource sessionCancellation, CancellationToken cancellationToken)
    {
        var wasAlreadySubscribed = Interlocked.Exchange(ref _bluetoothAdvertisementsSubscribed, 1) != 0;
        if (wasAlreadySubscribed) return;

        foreach (var device in _bluetoothDevices.Values)
        {
            var message = device.GenerateMessage();
            await QueueMessageAsync(writer, message, cancellationToken).ConfigureAwait(false);
        }

        var producersAlreadyStarted = Interlocked.Exchange(ref _bluetoothProducerLoopsStarted, 1) != 0;
        if (producersAlreadyStarted) return;

        foreach (var device in _bluetoothDevices.Values)
        {
            var producerTask = BluetoothProducerLoopAsync(device, writer, sessionCancellation, sessionCancellation.Token);
            producerTasks.Add(producerTask);
        }
    }

    /// <summary>Produces simulated Bluetooth advertisements while the connected client keeps the Bluetooth proxy subscription active.</summary>
    private async Task BluetoothProducerLoopAsync(IMessageProducer producer, ChannelWriter<QueuedServerMessage> writer, CancellationTokenSource sessionCancellation, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await producer.WaitForDelayAsync(_configuration.TimeProvider, cancellationToken).ConfigureAwait(false);

                var isSubscribed = Volatile.Read(ref _bluetoothAdvertisementsSubscribed) != 0;
                if (!isSubscribed) continue;

                var message = producer.GenerateMessage();
                await QueueMessageAsync(writer, message, cancellationToken).ConfigureAwait(false);
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

    /// <summary>Advertises the Bluetooth proxy capability required by modern Home Assistant clients.</summary>
    private void ConfigureBluetoothProxyCapabilities(DeviceCapabilitiesResponse response)
    {
        if (_bluetoothDevices.Count == 0) return;

        response.BluetoothProxy = new BluetoothProxyCapabilities
        {
            FeatureFlags = BluetoothProxyFeatureFlags,
            MacAddress = _configuration.MacAddress
        };
    }

    /// <summary>Adds Bluetooth proxy compatibility information when at least one simulated Bluetooth device is registered.</summary>
    private void ConfigureBluetoothProxyDeviceInfo(DeviceInfoResponse response)
    {
        if (_bluetoothDevices.Count == 0) return;

        response.BluetoothProxyFeatureFlags |= BluetoothProxyFeatureFlags;

        if (string.IsNullOrEmpty(response.BluetoothMacAddress))
            response.BluetoothMacAddress = _configuration.MacAddress;
    }

    /// <summary>Writes all configured entity descriptions and user services followed by the mandatory ListEntitiesDoneResponse marker.</summary>
    private async Task WriteEntitiesListAsync(ChannelWriter<QueuedServerMessage> writer, CancellationToken cancellationToken)
    {
        foreach (var entity in _configuration.Entities)
            await QueueMessageAsync(writer, entity.EntityInfo, cancellationToken).ConfigureAwait(false);

        foreach (var service in _configuration.Services)
            await QueueMessageAsync(writer, service, cancellationToken).ConfigureAwait(false);

        var doneResponse = new ListEntitiesDoneResponse();
        await QueueMessageAsync(writer, doneResponse, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Publishes retained state snapshots once and launches one scheduled producer loop for each state-capable entity configuration.</summary>
    private async Task StartStatesSubscriptionAsync(ChannelWriter<QueuedServerMessage> writer, List<Task> producerTasks, CancellationTokenSource sessionCancellation, CancellationToken cancellationToken)
    {
        var wasAlreadySubscribed = Interlocked.Exchange(ref _statesSubscribed, 1) != 0;
        if (wasAlreadySubscribed) return;

        foreach (var entity in _configuration.Entities)
        {
            var state = entity.GetState();
            if (state is not null) await QueueMessageAsync(writer, state, cancellationToken).ConfigureAwait(false);
        }

        foreach (var entity in _configuration.Entities)
        {
            if (entity is not IMessageProducer producer) continue;

            var producerTask = ProducerLoopAsync(producer, writer, sessionCancellation, sessionCancellation.Token);
            producerTasks.Add(producerTask);
        }
    }

    /// <summary>Runs the deliberately simple producer loop requested by the mock configuration and stops the session if its generator fails.</summary>
    private async Task ProducerLoopAsync(IMessageProducer producer, ChannelWriter<QueuedServerMessage> writer, CancellationTokenSource sessionCancellation, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await producer.WaitForDelayAsync(_configuration.TimeProvider, cancellationToken).ConfigureAwait(false);
                var message = producer.GenerateMessage();
                await QueueMessageAsync(writer, message, cancellationToken).ConfigureAwait(false);
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


    [GeneratedRegex("\"key\": \\d+")]
    public static partial Regex KeyRegex();

    /// <summary>Applies a command to matching retained state, publishes the resulting state and then invokes the optional command callback.</summary>
    private async Task HandleEntityCommandAsync(IMessage command, ChannelWriter<QueuedServerMessage> writer, CancellationToken cancellationToken)
    {
        var address = EntityAddress.FromMessage(command);

        if (_entities.TryGetValue(address, out var entity))
        {
            var oldState = KeyRegex().Replace(entity.GetState()?.ToString() ?? "", "");
            var stateChanged = entity.TryApplyCommand(command, out var updatedState);
            var shouldPublishState = stateChanged && updatedState is not null && Volatile.Read(ref _statesSubscribed) != 0;
            if (shouldPublishState) await QueueMessageAsync(writer, updatedState!, cancellationToken).ConfigureAwait(false);
            var newState = KeyRegex().Replace(entity.GetState()?.ToString() ?? "", "");
            _logger?.LogTrace("Updated state: {OldState} -> {NewState}", oldState, newState);
        }

        var callback = _configuration.CommandReceived;
        if (callback is not null) await callback(command, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Determines whether protobuf metadata marks the message as an ESPHome entity command.</summary>
    private static bool IsEntityCommandMessage(IMessage message)
    {
        var options = message.Descriptor.GetOptions();
        if (options is null) return false;

        var baseClass = options.GetExtension(ApiOptionsExtensions.BaseClass);
        return baseClass == "CommandProtoMessage";
    }

    /// <summary>Creates the bounded multi-producer queue that guarantees the underlying transport receives only one write at a time.</summary>
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

    /// <summary>Serializes all protocol writes and completes queued physical-write waiters before processing the next message.</summary>
    private async Task WriteMessagesLoopAsync(IMessageTransport transport, ChannelReader<QueuedServerMessage> reader, CancellationTokenSource sessionCancellation, CancellationToken cancellationToken)
    {
        Exception? failure = null;

        var count = 0;

        if (_logger?.IsEnabled(Microsoft.Extensions.Logging.LogLevel.Debug) == true)
        {
            var timer = new Timer((_) => _logger.LogDebug("Server sent messages: {MsgRate} msg/s", count/1000), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        }

        try
        {
            while (await reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                while (reader.TryRead(out var queuedMessage))
                {
                    try
                    {
                        count++;
                        await transport.WriteMessageAsync(queuedMessage.Message, cancellationToken).ConfigureAwait(false);
                        queuedMessage.Completion?.TrySetResult(true);

                    }
                    catch (Exception exception)
                    {
                        queuedMessage.Completion?.TrySetException(exception);
                        throw;
                    }
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
            while (reader.TryRead(out var queuedMessage))
            {
                if (failure is not null)
                {
                    queuedMessage.Completion?.TrySetException(failure);
                    continue;
                }

                queuedMessage.Completion?.TrySetCanceled(cancellationToken);
            }
        }
    }

    /// <summary>Queues one protocol message without waiting for the physical network write.</summary>
    private static ValueTask QueueMessageAsync(ChannelWriter<QueuedServerMessage> writer, IMessage message, CancellationToken cancellationToken)
    {
        var queuedMessage = new QueuedServerMessage(message, null);
        return writer.WriteAsync(queuedMessage, cancellationToken);
    }

    /// <summary>Queues one protocol message and waits until the serialized writer physically writes it to the active transport.</summary>
    private static async ValueTask WriteMessageAndWaitAsync(ChannelWriter<QueuedServerMessage> writer, IMessage message, CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var queuedMessage = new QueuedServerMessage(message, completion);

        await writer.WriteAsync(queuedMessage, cancellationToken).ConfigureAwait(false);
        await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Observes a connection-owned task during shutdown without replacing the transport failure that already ended the session.</summary>
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

    /// <summary>Stops owned resources even when called from asynchronous disposal after public operations have been disabled.</summary>
    private async ValueTask StopCoreAsync(CancellationToken cancellationToken)
    {
        var wasRunning = Interlocked.Exchange(ref _running, 0) != 0;
        if (!wasRunning)
        {
            if (Volatile.Read(ref _disposed) == 0) SetState(EspHomeMockServerState.Stopped);
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
                await acceptLoopTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                mdnsAdvertiser?.Dispose();
            }
            finally
            {
                serverCancellation?.Dispose();
                Interlocked.Exchange(ref _activeWriter, null);
                Volatile.Write(ref _bluetoothAdvertisementsSubscribed, 0);
                Volatile.Write(ref _bluetoothProducerLoopsStarted, 0);
                Volatile.Write(ref _statesSubscribed, 0);
                Volatile.Write(ref _connectedClientInfo, null);
                Volatile.Write(ref _listeningPort, 0);

                if (Volatile.Read(ref _disposed) == 0) SetState(EspHomeMockServerState.Stopped);
            }
        }
    }

    /// <summary>Updates the observable lifecycle state without exposing mutable synchronization primitives.</summary>
    private void SetState(EspHomeMockServerState state) => Volatile.Write(ref _state, (int)state);

    /// <summary>Prevents operations after the server lifetime has ended.</summary>
    private void ThrowIfDisposed()
    {
        var isDisposed = Volatile.Read(ref _disposed) != 0;
        if (isDisposed) throw new ObjectDisposedException(nameof(EspHomeMockServer));
    }

    /// <summary>Stops the listener and active session, withdraws mDNS and permanently ends the server lifetime.</summary>
    public async ValueTask DisposeAsync()
    {
        var wasAlreadyDisposed = Interlocked.Exchange(ref _disposed, 1) != 0;
        if (wasAlreadyDisposed) return;

        await StopCoreAsync(CancellationToken.None).ConfigureAwait(false);
        SetState(EspHomeMockServerState.Disposed);
        GC.SuppressFinalize(this);
    }

    /// <summary>Stores one serialized protocol write and an optional completion signal for callers that require a physical write boundary.</summary>
    private readonly struct QueuedServerMessage
    {
        /// <summary>Gets the protobuf message queued for transport serialization.</summary>
        public IMessage Message { get; }

        /// <summary>Gets the optional signal completed after the physical transport write finishes.</summary>
        public TaskCompletionSource<bool>? Completion { get; }

        /// <summary>Creates one queued write without copying its protobuf payload.</summary>
        public QueuedServerMessage(IMessage message, TaskCompletionSource<bool>? completion)
        {
            Message = message;
            Completion = completion;
        }
    }
}