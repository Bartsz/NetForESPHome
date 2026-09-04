using Noise;
using System.Buffers.Binary;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace NetForESPHome.Transports;

/// <summary>Performs the ESPHome Noise NNpsk0 handshake over an established TCP stream.</summary>
public sealed class NoiseHandshakeExecutor
{
    private const byte PlainTextMarker = 0x00;
    private const byte NoiseMarker = 0x01;
    private const byte NoiseSuccess = 0x00;
    private const byte NoiseProtocolId = 0x01;

    private const int FrameHeaderSize = 3;
    private const int NoiseHandshakeMessageSize = 48;
    private const int NoisePskSize = 32;
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(60);

    private static readonly Protocol NoiseProtocol = new(HandshakePattern.NN, PatternModifiers.Psk0);

    private readonly string _noisePsk;

    /// <summary>Creates an executor for the ESPHome Noise handshake using the provided base64 PSK.</summary>
    public NoiseHandshakeExecutor(string noisePsk)
    {
        ArgumentException.ThrowIfNullOrEmpty(noisePsk);

        _noisePsk = noisePsk;
    }

    /// <summary>Performs the ESPHome Noise handshake and returns the established encrypted message transport.</summary>
    public async Task<NoiseMessageTransport> ExecuteAsync(NetworkStream stream, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(stream);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(HandshakeTimeout);

        var psk = DecodePsk();

        try
        {
            using var handshake = NoiseProtocol.Create(initiator: true, prologue: "NoiseAPIInit\0\0"u8, psks: [psk]);

            var handshakeMessage = new byte[NoiseHandshakeMessageSize];
            var (handshakeLength, _, transport) = handshake.WriteMessage(ReadOnlySpan<byte>.Empty, handshakeMessage);

            if (transport is not null)
            {
                transport.Dispose();
                throw new InvalidOperationException("Noise NNpsk0 handshake completed before receiving the responder message.");
            }

            await WriteClientHelloAsync(stream, handshakeMessage.AsMemory(0, handshakeLength), timeoutCts.Token).ConfigureAwait(false);

            var serverHello = await ReadNoiseFrameAsync(stream, timeoutCts.Token).ConfigureAwait(false);
            ValidateServerHello(serverHello);

            var handshakeResponse = await ReadNoiseFrameAsync(stream, timeoutCts.Token).ConfigureAwait(false);
            ValidateHandshakeResponse(handshakeResponse);

            var (_, _, establishedTransport) = handshake.ReadMessage(handshakeResponse.AsSpan(1), Span<byte>.Empty);

            if (establishedTransport is null)
                throw new InvalidOperationException("Noise NNpsk0 handshake did not produce a transport.");

            return new NoiseMessageTransport(stream, establishedTransport);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"ESPHome Noise handshake timed out after {HandshakeTimeout.TotalSeconds:F0} seconds.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(psk);
        }
    }

    /// <summary>Decodes and validates the configured ESPHome Noise PSK.</summary>
    private byte[] DecodePsk()
    {
        var psk = Convert.FromBase64String(_noisePsk);

        if (psk.Length == NoisePskSize)
            return psk;

        CryptographicOperations.ZeroMemory(psk);

        throw new ArgumentException($"ESPHome Noise PSK must decode to exactly {NoisePskSize} bytes.", nameof(_noisePsk));
    }

    /// <summary>Writes the ESPHome encrypted hello and the first Noise handshake message.</summary>
    private static async Task WriteClientHelloAsync(NetworkStream stream, ReadOnlyMemory<byte> handshakeMessage, CancellationToken ct)
    {
        var handshakeFrameLength = handshakeMessage.Length + 1;
        var request = new byte[FrameHeaderSize + FrameHeaderSize + handshakeFrameLength];

        request[0] = NoiseMarker;

        request[3] = NoiseMarker;
        BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(4), (ushort)handshakeFrameLength);

        request[6] = NoiseSuccess;
        handshakeMessage.Span.CopyTo(request.AsSpan(7));

        await stream.WriteAsync(request, ct).ConfigureAwait(false);
    }

    /// <summary>Reads one complete ESPHome Noise frame.</summary>
    private static async Task<byte[]> ReadNoiseFrameAsync(NetworkStream stream, CancellationToken ct)
    {
        var header = new byte[FrameHeaderSize];

        await stream.ReadExactlyAsync(header, ct).ConfigureAwait(false);

        if (header[0] != NoiseMarker)
        {
            if (header[0] == PlainTextMarker) throw new InvalidTransportProtocol(ConnectionMethod.PlainText);
            throw new InvalidTransportProtocol(ConnectionMethod.Unknown);
        }

        var frameLength = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(1));
        if (frameLength == 0)
            return [];

        var frame = new byte[frameLength];

        await stream.ReadExactlyAsync(frame, ct).ConfigureAwait(false);

        return frame;
    }

    /// <summary>Validates that ESPHome selected the Noise protocol supported by this client.</summary>
    private static void ValidateServerHello(byte[] serverHello)
    {
        if (serverHello.Length == 0)
            throw new InvalidDataException("ESPHome returned an empty Noise ServerHello.");

        if (serverHello[0] != NoiseProtocolId)
            throw new InvalidDataException($"ESPHome selected unsupported Noise protocol 0x{serverHello[0]:X2}.");
    }

    /// <summary>Validates the ESPHome response preceding the Noise responder handshake message.</summary>
    private static void ValidateHandshakeResponse(byte[] response)
    {
        if (response.Length == 0)
            throw new InvalidDataException("ESPHome returned an empty Noise handshake response.");

        if (response[0] == NoiseSuccess)
            return;

        if (response.AsSpan(1).SequenceEqual("Handshake MAC failure"u8))
            throw new CryptographicException("ESPHome rejected the Noise PSK.");

        throw new InvalidDataException($"ESPHome rejected the Noise handshake with status 0x{response[0]:X2}.");
    }
}
