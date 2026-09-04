using Google.Protobuf;

namespace NetForESPHome.Transports;

/// <summary>Reads and writes ESPHome messages. One read and one write may execute concurrently; 
/// multiple concurrent reads or writes are not supported.</summary>
public interface IMessageTransport : IDisposable
{
    ValueTask<IMessage> ReadMessageAsync(CancellationToken ct);
    ValueTask WriteMessageAsync(IMessage message, CancellationToken ct);
}
 

/// <summary>Describes the connection method required by an ESPHome Native API server.</summary>
public enum ConnectionMethod
{
    PlainText = 0,
    PlainTextWithPassword = 1,
    Noise = 2,
    Unknown = 3,
}
