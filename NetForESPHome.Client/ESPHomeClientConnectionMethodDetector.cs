using Google.Protobuf;
using NetForESPHome.Transports;
using System.Net.Sockets;

namespace NetForESPHome.Client;

/// <summary>Detects the connection method required by an ESPHome Native API server and owns the TCP client used for the probe.</summary>
public sealed class ESPHomeClientConnectionMethodDetector : IDisposable
{
    private const uint ClientApiVersionMajor = 1;
    private const uint ClientApiVersionMinor = 14;
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(60);

    private readonly TcpClient _tcpClient;
    private readonly string _address;
    private readonly int _port;
    private readonly string _clientInfo;
    private bool _disposed;

    /// <summary>Creates a one-shot detector that takes ownership of the provided TCP client.
    /// It is used to detect the required connection method in explicit way, rather then trough implicit error handling. </summary>
    public ESPHomeClientConnectionMethodDetector(TcpClient tcpClient, string address, int port, string clientInfo = "NetForESPHome")
    {
        ArgumentNullException.ThrowIfNull(tcpClient);
        ArgumentException.ThrowIfNullOrWhiteSpace(address);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientInfo);

        if (port is < 1 or > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(port), port, "TCP port must be between 1 and 65535.");

        _tcpClient = tcpClient;
        _address = address;
        _port = port;
        _clientInfo = clientInfo;
    }

    /// <summary>Connects to the ESPHome server, detects the required connection method, and always closes the probe connection afterwards.</summary>
    public async Task<ConnectionMethod> GetConnectionMethodAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        try
        {
            await ConnectAsync().ConfigureAwait(false);
            _tcpClient.NoDelay = true;

            using var probeCts = new CancellationTokenSource(ProbeTimeout);

            try
            {
                return await ProbeAsync(_tcpClient.GetStream(), probeCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException exception) when (probeCts.IsCancellationRequested)
            {
                throw new TimeoutException($"ESPHome connection method probe timed out after {ProbeTimeout.TotalSeconds:F0} seconds.", exception);
            }
        }
        finally
        {
            Dispose();
        }
    }

    /// <summary>Connects the TCP client while bounding endpoint resolution and connection establishment time.</summary>
    private async Task ConnectAsync()
    {
        using var connectCts = new CancellationTokenSource(ConnectTimeout);

        try
        {
            await _tcpClient.ConnectAsync(_address, _port, connectCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (connectCts.IsCancellationRequested)
        {
            throw new TimeoutException($"ESPHome TCP connection timed out after {ConnectTimeout.TotalSeconds:F0} seconds.", exception);
        }
    }

    /// <summary>Probes an established connection without performing password or Noise authentication.</summary>
    private async Task<ConnectionMethod> ProbeAsync(NetworkStream stream, CancellationToken ct)
    {
        using var transport = new PlainTextMessageTransport(stream);

        var helloRequest = new HelloRequest
        {
            ClientInfo = _clientInfo,
            ApiVersionMajor = ClientApiVersionMajor,
            ApiVersionMinor = ClientApiVersionMinor,
        };

        try
        {
            await transport.WriteMessageAsync(helloRequest, ct).ConfigureAwait(false);
            await ReadExpectedMessageAsync<HelloResponse>(transport, ct).ConfigureAwait(false);
        }
        catch (InvalidTransportProtocol exception) when (exception.CurrentProtocol == ConnectionMethod.Noise)
        {
            return ConnectionMethod.Noise;
        }
        catch (InvalidTransportProtocol exception) when (exception.CurrentProtocol == ConnectionMethod.Unknown)
        {
            return ConnectionMethod.Unknown;
        }

        var deviceInfoRequest = new DeviceInfoRequest();
        await transport.WriteMessageAsync(deviceInfoRequest, ct).ConfigureAwait(false);

        var deviceInfo = await ReadExpectedMessageAsync<DeviceInfoResponse>(transport, ct).ConfigureAwait(false);

        // The field is deprecated only because new ESPHome versions removed password auth; old devices still require it.
#pragma warning disable CS0612, CS0618
        var usesPassword = deviceInfo.UsesPassword;
#pragma warning restore CS0612, CS0618

        return usesPassword ? ConnectionMethod.PlainTextWithPassword : ConnectionMethod.PlainText;
    }

    /// <summary>Reads messages until the requested ESPHome response type is received.</summary>
    private static async ValueTask<TMessage> ReadExpectedMessageAsync<TMessage>(IMessageTransport transport, CancellationToken ct)
        where TMessage : class, IMessage
    {
        while (true)
        {
            var message = await transport.ReadMessageAsync(ct).ConfigureAwait(false);

            if (message is TMessage expectedMessage)
                return expectedMessage;

            if (message is DisconnectRequest)
                throw new InvalidDataException($"ESPHome server disconnected before returning {typeof(TMessage).Name}.");
        }
    }

    /// <summary>Closes the probe connection and releases the owned TCP client.</summary>
    public void Dispose()
    {
        if (_disposed) return;

        _disposed = true;
        _tcpClient.Dispose();
    }
}
