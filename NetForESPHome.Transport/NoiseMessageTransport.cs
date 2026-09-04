using Google.Protobuf;
using NetForESPHome.DomainModel;
using Noise;
using System.Buffers.Binary;
using System.Net.Sockets;

namespace NetForESPHome.Transports;

/// <summary>Reads and writes ESPHome Native API messages over an established Noise transport.</summary>
public sealed class NoiseMessageTransport : IMessageTransport
{
    private const byte NoiseMarker = 0x01;
    private const byte PlainTextMarker = 0x00;
    private const int NoiseTagSize = 16;
    private const int FrameHeaderSize = 3;
    private const int MessageHeaderSize = 4;
    private const int InitialBufferSize = 1024;
    private const int MaxEncryptedFrameSize = ushort.MaxValue;
    private const int MaxMessageSize = MaxEncryptedFrameSize - NoiseTagSize - MessageHeaderSize;

    private readonly NetworkStream _stream;
    private readonly Transport _transport;
    private readonly byte[] _readHeader = new byte[FrameHeaderSize];

    private byte[] _readEncryptedBuffer = new byte[InitialBufferSize];
    private byte[] _readPlaintextBuffer = new byte[InitialBufferSize];
    private byte[] _writePlaintextBuffer = new byte[InitialBufferSize];
    private byte[] _writeFrameBuffer = new byte[InitialBufferSize];
    private bool _disposed;

    /// <summary>Creates an ESPHome message transport using an established Noise transport.</summary>
    public NoiseMessageTransport(NetworkStream stream, Transport transport)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(transport);

        _stream = stream;
        _transport = transport;
    }

    /// <summary>Reads, authenticates and deserializes the next encrypted ESPHome message.</summary>
    public async ValueTask<IMessage> ReadMessageAsync(CancellationToken ct)
    {
        ThrowIfDisposed();

        await _stream.ReadExactlyAsync(_readHeader, ct).ConfigureAwait(false);

        if (_readHeader[0] != NoiseMarker)
        {
            if (_readHeader[0] == PlainTextMarker) throw new InvalidTransportProtocol(ConnectionMethod.PlainText);
            throw new InvalidTransportProtocol(ConnectionMethod.Unknown);
        }

        var encryptedLength = BinaryPrimitives.ReadUInt16BigEndian(_readHeader.AsSpan(1));
        if (encryptedLength < MessageHeaderSize + NoiseTagSize)
            throw new InvalidDataException($"ESPHome Noise frame is too short: {encryptedLength} bytes.");

        EnsureReadEncryptedBufferCapacity(encryptedLength);

        await _stream.ReadExactlyAsync(_readEncryptedBuffer.AsMemory(0, encryptedLength), ct).ConfigureAwait(false);

        var maxPlaintextLength = encryptedLength - NoiseTagSize;
        EnsureReadPlaintextBufferCapacity(maxPlaintextLength);

        var plaintextLength = _transport.ReadMessage(
            _readEncryptedBuffer.AsSpan(0, encryptedLength),
            _readPlaintextBuffer.AsSpan(0, maxPlaintextLength));

        if (plaintextLength < MessageHeaderSize)
            throw new InvalidDataException($"Decrypted ESPHome Noise message is too short: {plaintextLength} bytes.");

        var messageId = BinaryPrimitives.ReadUInt16BigEndian(_readPlaintextBuffer);
        var payload = _readPlaintextBuffer.AsSpan(MessageHeaderSize, plaintextLength - MessageHeaderSize);

        return EspHomeMessageMapper.ParseMessage(messageId, payload);
    }

    /// <summary>Serializes, encrypts and writes one ESPHome message.</summary>
    public async ValueTask WriteMessageAsync(IMessage message, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);
        ThrowIfDisposed();

        var messageSize = message.CalculateSize();
        if (messageSize > MaxMessageSize)
            throw new InvalidDataException($"ESPHome Noise message length {messageSize} exceeds the {MaxMessageSize}-byte limit.");

        var messageId = EspHomeMessageMapper.GetMessageId(message);
        if (messageId < 0 || messageId > ushort.MaxValue)
            throw new InvalidDataException($"ESPHome message ID {messageId} exceeds the 16-bit Noise message ID range.");

        var plaintextLength = MessageHeaderSize + messageSize;
        var encryptedLength = plaintextLength + NoiseTagSize;

        EnsureWritePlaintextBufferCapacity(plaintextLength);
        EnsureWriteFrameBufferCapacity(FrameHeaderSize + encryptedLength);

        BinaryPrimitives.WriteUInt16BigEndian(_writePlaintextBuffer, (ushort)messageId);
        BinaryPrimitives.WriteUInt16BigEndian(_writePlaintextBuffer.AsSpan(2), (ushort)messageSize);

        message.WriteTo(_writePlaintextBuffer.AsSpan(MessageHeaderSize, messageSize));

        var bytesWritten = _transport.WriteMessage(
            _writePlaintextBuffer.AsSpan(0, plaintextLength),
            _writeFrameBuffer.AsSpan(FrameHeaderSize, encryptedLength));

        if (bytesWritten != encryptedLength)
            throw new InvalidOperationException($"Noise transport produced {bytesWritten} bytes instead of the expected {encryptedLength}.");

        _writeFrameBuffer[0] = NoiseMarker;
        BinaryPrimitives.WriteUInt16BigEndian(_writeFrameBuffer.AsSpan(1), (ushort)bytesWritten);

        await _stream.WriteAsync(_writeFrameBuffer.AsMemory(0, FrameHeaderSize + bytesWritten), ct).ConfigureAwait(false);
    }

    /// <summary>Ensures the encrypted read buffer can contain the requested frame.</summary>
    private void EnsureReadEncryptedBufferCapacity(int requiredSize)
    {
        if (requiredSize <= _readEncryptedBuffer.Length) return;

        var newSize = Math.Min(MaxEncryptedFrameSize, Math.Max(requiredSize, _readEncryptedBuffer.Length * 2));
        Array.Resize(ref _readEncryptedBuffer, newSize);
    }

    /// <summary>Ensures the plaintext read buffer can contain the decrypted frame.</summary>
    private void EnsureReadPlaintextBufferCapacity(int requiredSize)
    {
        if (requiredSize <= _readPlaintextBuffer.Length) return;

        var maxSize = MaxEncryptedFrameSize - NoiseTagSize;
        var newSize = Math.Min(maxSize, Math.Max(requiredSize, _readPlaintextBuffer.Length * 2));
        Array.Resize(ref _readPlaintextBuffer, newSize);
    }

    /// <summary>Ensures the plaintext write buffer can contain the message and its header.</summary>
    private void EnsureWritePlaintextBufferCapacity(int requiredSize)
    {
        if (requiredSize <= _writePlaintextBuffer.Length) return;

        var maxSize = MaxEncryptedFrameSize - NoiseTagSize;
        var newSize = Math.Min(maxSize, Math.Max(requiredSize, _writePlaintextBuffer.Length * 2));
        Array.Resize(ref _writePlaintextBuffer, newSize);
    }

    /// <summary>Ensures the encrypted write buffer can contain the frame and its transport header.</summary>
    private void EnsureWriteFrameBufferCapacity(int requiredSize)
    {
        if (requiredSize <= _writeFrameBuffer.Length) return;

        var maxSize = FrameHeaderSize + MaxEncryptedFrameSize;
        var newSize = Math.Min(maxSize, Math.Max(requiredSize, _writeFrameBuffer.Length * 2));
        Array.Resize(ref _writeFrameBuffer, newSize);
    }

    /// <summary>Throws when the transport has already been disposed.</summary>
    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    /// <summary>Releases Noise cryptographic state without closing the underlying connection.</summary>
    public void Dispose()
    {
        if (_disposed) return;

        _disposed = true;
        _transport.Dispose();
    }
}