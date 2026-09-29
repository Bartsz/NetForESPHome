using NetForESPHome.DomainModel.Native;

namespace NetForESPHome.ServerMock;

/// <summary>Produces ESPHome protocol messages according to a configurable schedule.</summary>
public interface IMessageProducer
{
    /// <summary>Waits until the producer should generate its next message.</summary>
    ValueTask WaitForDelayAsync(TimeProvider timeProvider, CancellationToken cancellationToken);

    /// <summary>Generates the next ESPHome protocol message and updates retained producer state when applicable.</summary>
    IESPHomeMessage GenerateMessage();
}
