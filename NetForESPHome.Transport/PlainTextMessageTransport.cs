using Google.Protobuf;
using NetForESPHome.DomainModel;
using System.Net.Sockets;

namespace NetForESPHome.Transports;

/// <summary>Reads and writes ESPHome plaintext Native API messages over a network stream.</summary>
public sealed class PlainTextMessageTransport : IMessageTransport
{
    private const int InitialBufferSize = 1024;
    private const int MaxMessageSize = ushort.MaxValue;
    private const int MaxVarUIntBytes = 4;
    private const int MaxVarUIntValue = (1 << (MaxVarUIntBytes * 7)) - 1;
    private const int MaxHeaderSize = 1 + MaxVarUIntBytes + MaxVarUIntBytes;

    private readonly NetworkStream _stream;
    private readonly byte[] _readByteBuffer = new byte[1];

    private byte[] _readBuffer = new byte[InitialBufferSize];
    private byte[] _writeBuffer = new byte[InitialBufferSize];
    private bool _disposed;

    /// <summary>Creates a plaintext ESPHome message transport over the provided network stream.</summary>
    public PlainTextMessageTransport(NetworkStream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        _stream = stream;
    }

    /// <summary>Reads and deserializes the next plaintext ESPHome message.</summary>
    public async ValueTask<IMessage> ReadMessageAsync(CancellationToken ct)
    {
        ThrowIfDisposed();

        var preamble = await ReadVarUIntAsync(ct).ConfigureAwait(false);
        if (preamble != 0)
        {
            if (preamble == 1) throw new InvalidTransportProtocol(ConnectionMethod.Noise);
            throw new InvalidTransportProtocol(ConnectionMethod.Unknown);
        }

        var messageLength = await ReadVarUIntAsync(ct).ConfigureAwait(false);
        if (messageLength > MaxMessageSize)
            throw new InvalidDataException($"ESPHome message length {messageLength} exceeds the {MaxMessageSize}-byte limit.");

        var messageId = (int)await ReadVarUIntAsync(ct).ConfigureAwait(false);
        var length = (int)messageLength;
        EnsureReadBufferCapacity(length);

        if (length > 0)
            await _stream.ReadExactlyAsync(_readBuffer.AsMemory(0, length), ct).ConfigureAwait(false);

        return EspHomeMessageMapper.ParseMessage(messageId, _readBuffer.AsSpan(0, length));
    }

    /// <summary>Serializes and writes one plaintext ESPHome message.</summary>
    public async ValueTask WriteMessageAsync(IMessage message, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);
        ThrowIfDisposed();

        var messageSize = message.CalculateSize();
        if (messageSize > MaxMessageSize)
            throw new InvalidDataException($"ESPHome message length {messageSize} exceeds the {MaxMessageSize}-byte limit.");

        var messageId = EspHomeMessageMapper.GetMessageId(message);
        if (messageId < 0 || messageId > MaxVarUIntValue)
            throw new InvalidDataException($"ESPHome message ID {messageId} exceeds the supported range.");

        EnsureWriteBufferCapacity(messageSize + MaxHeaderSize);

        var position = 0;
        _writeBuffer[position++] = 0;
        position += WriteVarUInt(_writeBuffer.AsSpan(position), (uint)messageSize);
        position += WriteVarUInt(_writeBuffer.AsSpan(position), (uint)messageId);

        message.WriteTo(_writeBuffer.AsSpan(position, messageSize));
        position += messageSize;

        await _stream.WriteAsync(_writeBuffer.AsMemory(0, position), ct).ConfigureAwait(false);
    }

    /// <summary>Reads one ESPHome varuint value from the network stream.</summary>
    private async ValueTask<uint> ReadVarUIntAsync(CancellationToken ct)
    {
        uint result = 0;

        for (var index = 0; index < MaxVarUIntBytes; index++)
        {
            await _stream.ReadExactlyAsync(_readByteBuffer, ct).ConfigureAwait(false);

            var value = _readByteBuffer[0];
            result |= (uint)(value & 0x7F) << (index * 7);

            if ((value & 0x80) == 0) return result;
        }

        throw new InvalidDataException($"ESPHome varuint exceeds the {MaxVarUIntBytes}-byte limit.");
    }

    /// <summary>Writes one ESPHome varuint value and returns its encoded length.</summary>
    private static int WriteVarUInt(Span<byte> buffer, uint value)
    {
        var position = 0;

        do
        {
            var current = (byte)(value & 0x7F);
            value >>= 7;

            if (value != 0) current |= 0x80;

            buffer[position++] = current;
        }
        while (value != 0);

        return position;
    }

    /// <summary>Ensures the reusable read buffer can contain the requested message.</summary>
    private void EnsureReadBufferCapacity(int requiredSize)
    {
        if (requiredSize <= _readBuffer.Length) return;

        var newSize = Math.Min(MaxMessageSize, Math.Max(requiredSize, _readBuffer.Length * 2));
        Array.Resize(ref _readBuffer, newSize);
    }

    /// <summary>Ensures the reusable write buffer can contain the requested frame.</summary>
    private void EnsureWriteBufferCapacity(int requiredSize)
    {
        if (requiredSize <= _writeBuffer.Length) return;

        var maxSize = MaxMessageSize + MaxHeaderSize;
        var newSize = Math.Min(maxSize, Math.Max(requiredSize, _writeBuffer.Length * 2));
        Array.Resize(ref _writeBuffer, newSize);
    }

    /// <summary>Throws when the transport has already been disposed.</summary>
    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    /// <summary>Releases resources owned by this message transport without closing the underlying connection.</summary>
    public void Dispose()
    {
        if (_disposed) return;

        _disposed = true;
    }
}
