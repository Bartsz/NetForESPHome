using NetForESPHome.Transports;
using Noise;
using System.Buffers.Binary;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace NetForESPHome.ServerMock;

/// <summary>Performs the responder side of the ESPHome Noise NNpsk0 handshake and returns the shared message transport.</summary>
internal sealed class NoiseServerHandshakeExecutor
{
    private const byte NoiseMarker = 0x01;
    private const byte NoiseSuccess = 0x00;
    private const byte NoiseFailure = 0x01;
    private const byte NoiseProtocolId = 0x01;
    private const int FrameHeaderSize = 3;
    private const int NoisePskSize = 32;
    private const int NoiseHandshakeMessageSize = 48;

    private static readonly Protocol NoiseProtocol = new(HandshakePattern.NN, PatternModifiers.Psk0);

    private readonly string _noisePsk;
    private readonly string _nodeName;
    private readonly string _macAddress;

    /// <summary>Creates an ESPHome Noise responder for the configured node identity and base64 PSK.</summary>
    public NoiseServerHandshakeExecutor(string noisePsk, string nodeName, string macAddress)
    {
        ArgumentException.ThrowIfNullOrEmpty(noisePsk);
        ArgumentException.ThrowIfNullOrEmpty(nodeName);
        ArgumentException.ThrowIfNullOrEmpty(macAddress);

        _noisePsk = noisePsk;
        _nodeName = nodeName;
        _macAddress = macAddress;
    }

    /// <summary>Consumes ClientHello, sends ServerHello, completes NNpsk0 and returns the established encrypted transport.</summary>
    public async Task<NoiseMessageTransport> ExecuteAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);

        byte[] clientHello;

        try
        {
            clientHello = await ReadNoiseFrameAsync(stream, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidDataException)
        {
            await WriteHandshakeFailureAsync(stream, "Bad indicator byte", cancellationToken).ConfigureAwait(false);
            throw;
        }

        var prologue = CreatePrologue(clientHello);

        var serverHello = CreateServerHello();
        await WriteNoiseFrameAsync(stream, serverHello, cancellationToken).ConfigureAwait(false);

        var handshakeFrame = await ReadNoiseFrameAsync(stream, cancellationToken).ConfigureAwait(false);

        if (handshakeFrame.Length == 0)
        {
            await WriteHandshakeFailureAsync(stream, "Empty handshake message", cancellationToken).ConfigureAwait(false);
            throw new InvalidDataException("ESPHome Noise client returned an empty handshake message.");
        }

        if (handshakeFrame[0] != NoiseSuccess)
        {
            await WriteHandshakeFailureAsync(stream, "Bad handshake error byte", cancellationToken).ConfigureAwait(false);
            throw new InvalidDataException($"ESPHome Noise client returned unsupported handshake status 0x{handshakeFrame[0]:X2}.");
        }

        var psk = DecodePsk();

        try
        {
            using var handshake = NoiseProtocol.Create(initiator: false, prologue: prologue, psks: [psk]);

            try
            {
                var (_, _, prematureTransport) = handshake.ReadMessage(handshakeFrame.AsSpan(1), Span<byte>.Empty);
                if (prematureTransport is not null)
                {
                    prematureTransport.Dispose();
                    throw new InvalidOperationException("Noise NNpsk0 responder handshake completed before sending the responder message.");
                }
            }
            catch (CryptographicException)
            {
                await WriteHandshakeFailureAsync(stream, "Handshake MAC failure", cancellationToken).ConfigureAwait(false);
                throw;
            }
            catch
            {
                await WriteHandshakeFailureAsync(stream, "Handshake error", cancellationToken).ConfigureAwait(false);
                throw;
            }

            var response = new byte[1 + NoiseHandshakeMessageSize];
            response[0] = NoiseSuccess;

            var (handshakeLength, _, establishedTransport) = handshake.WriteMessage(ReadOnlySpan<byte>.Empty, response.AsSpan(1));
            if (establishedTransport is null) throw new InvalidOperationException("Noise NNpsk0 responder handshake did not produce a transport.");

            await WriteNoiseFrameAsync(stream, response.AsMemory(0, handshakeLength + 1), cancellationToken).ConfigureAwait(false);
            return new NoiseMessageTransport(stream, establishedTransport);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(psk);
            CryptographicOperations.ZeroMemory(prologue);
        }
    }

    /// <summary>Builds the exact Noise prologue ESPHome derives from its initial framed ClientHello bytes.</summary>
    private static byte[] CreatePrologue(byte[] clientHello)
    {
        ReadOnlySpan<byte> prefix = "NoiseAPIInit"u8;
        var prologue = new byte[prefix.Length + sizeof(ushort) + clientHello.Length];

        prefix.CopyTo(prologue);
        BinaryPrimitives.WriteUInt16BigEndian(prologue.AsSpan(prefix.Length), checked((ushort)clientHello.Length));
        clientHello.CopyTo(prologue.AsSpan(prefix.Length + sizeof(ushort)));

        return prologue;
    }

    /// <summary>Builds the unencrypted ESPHome ServerHello containing protocol id, node name and MAC address.</summary>
    private byte[] CreateServerHello()
    {
        var nodeNameBytes = Encoding.UTF8.GetBytes(_nodeName);
        var normalizedMacAddress = _macAddress.Replace(":", "", StringComparison.Ordinal).Replace("-", "", StringComparison.Ordinal).ToLowerInvariant();
        var macAddressBytes = Encoding.UTF8.GetBytes(normalizedMacAddress);
        var response = new byte[1 + nodeNameBytes.Length + 1 + macAddressBytes.Length + 1];

        response[0] = NoiseProtocolId;
        nodeNameBytes.CopyTo(response.AsSpan(1));
        macAddressBytes.CopyTo(response.AsSpan(1 + nodeNameBytes.Length + 1));

        return response;
    }

    /// <summary>Decodes the prevalidated ESPHome Noise PSK for one connection.</summary>
    private byte[] DecodePsk()
    {
        var psk = Convert.FromBase64String(_noisePsk);
        if (psk.Length == NoisePskSize) return psk;

        CryptographicOperations.ZeroMemory(psk);
        throw new InvalidOperationException($"ESPHome Noise PSK must decode to exactly {NoisePskSize} bytes.");
    }

    /// <summary>Sends the explicit ESPHome handshake rejection used for an invalid Noise MAC.</summary>
    private static Task WriteHandshakeFailureAsync(NetworkStream stream, string message, CancellationToken cancellationToken)
    {
        var messageBytes = Encoding.ASCII.GetBytes(message);
        var response = new byte[messageBytes.Length + 1];
        response[0] = NoiseFailure;
        messageBytes.CopyTo(response.AsSpan(1));
        return WriteNoiseFrameAsync(stream, response, cancellationToken);
    }

    /// <summary>Reads one complete unencrypted ESPHome Noise handshake frame.</summary>
    private static async Task<byte[]> ReadNoiseFrameAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var header = new byte[FrameHeaderSize];
        await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);

        if (header[0] != NoiseMarker) throw new InvalidDataException($"Invalid ESPHome Noise frame marker 0x{header[0]:X2}.");

        var frameLength = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(1));
        if (frameLength == 0) return [];

        var frame = new byte[frameLength];
        await stream.ReadExactlyAsync(frame, cancellationToken).ConfigureAwait(false);
        return frame;
    }

    /// <summary>Writes one complete unencrypted ESPHome Noise handshake frame.</summary>
    private static async Task WriteNoiseFrameAsync(NetworkStream stream, ReadOnlyMemory<byte> frame, CancellationToken cancellationToken)
    {
        if (frame.Length > ushort.MaxValue) throw new InvalidDataException($"ESPHome Noise handshake frame length {frame.Length} exceeds the 16-bit frame limit.");

        var header = new byte[FrameHeaderSize];
        header[0] = NoiseMarker;
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(1), (ushort)frame.Length);

        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        if (frame.Length > 0) await stream.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
    }
}