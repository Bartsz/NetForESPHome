using Microsoft.Extensions.Logging;
using NetForESPHome.Client.Model;
using NetForESPHome.DomainModel.Native;
using System.Threading.Channels;

namespace NetForESPHome.Client;

public sealed partial class EspHomeClient
{
    private readonly Channel<IESPHomeMessage> _incomingMessagesChannel;
    private readonly Channel<IESPHomeStateMessage> _incomingStateChangeMessages;
    private readonly Channel<IESPHomeMessage> _dynamicEntitiesRegistrationChannel;

    /// <summary>Waits for the next general application message received during this client lifetime; unread messages remain available across reconnects and backpressure in any public input stream pauses the shared read loop.</summary>
    public ValueTask<IESPHomeMessage> GetIncomingMessageAsync(CancellationToken ct = default)
    {
        return _incomingMessagesChannel.Reader.ReadAsync(ct);
    }

    /// <summary>Waits for the next entity state message without mixing high-frequency state traffic with the general application message stream; unread states remain available across reconnects and backpressure in any public input stream pauses the shared read loop.</summary>
    public ValueTask<IESPHomeStateMessage> GetIncomingStateChangeMessageAsync(CancellationToken ct = default)
    {
        return _incomingStateChangeMessages.Reader.ReadAsync(ct);
    }


    /// <summary>Waits for an entity or user-service description received outside an active entity discovery request; unread registrations remain available across reconnects and backpressure in any public input stream pauses the shared read loop.</summary>
    public ValueTask<IESPHomeMessage> GetDynamicEntityRegistrationMessageAsync(CancellationToken ct = default)
    {
        return _dynamicEntitiesRegistrationChannel.Reader.ReadAsync(ct);
    }

    /// <summary>Reads one session until cancellation or transport failure, routing FIFO response waiters before normal application handling.</summary>
    private async Task ReadLoopAsync(ConnectionSession session)
    {
        var ct = session.Cancellation.Token;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var message = await session.Transport.ReadMessageAsync(ct).ConfigureAwait(false);
                _logger?.LogTrace("Received message: {MessageType}", message.GetType().Name);


                var callbacks = session.ResponseCallbacks[message.MessageId];

                if (callbacks.TryTakeFirstMatching(message, out var callback))
                {
                    if (!callback.Completion.Task.IsCompleted)
                    {
                        var responseResult = EspHomeRpcResult<IESPHomeMessage>.Completed(message);
                        callback.Completion.TrySetResult(responseResult);
                    }

                    if (!callback.PublishResponse) continue;
                }

                Volatile.Write(ref session.ApplicationDispatchInProgress, 1);

                try
                {
                    await HandleIncomingMessageAsync(session, message, ct).ConfigureAwait(false);
                }
                finally
                {
                    Volatile.Write(ref session.ApplicationDispatchInProgress, 0);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                session.RequestClose(EspHomeConnectionCloseReason.TransportFailure, exception);
                return;
            }
        }
    }

    /// <summary>Handles protocol-internal and semantically separated messages locally, publishing every remaining application message through the general input channel.</summary>
    private async Task HandleIncomingMessageAsync(ConnectionSession session, IESPHomeMessage message, CancellationToken ct)
    {
        if (message is IESPHomeProtocolMessage protocolMessage)
        {
            await HandleProtocolMessage(session, protocolMessage, ct).ConfigureAwait(false);
            return;
        }

        if (message is CameraImageResponse cameraImageResponse)
        {
            await HandleCameraImageResponseAsync(session, cameraImageResponse, ct).ConfigureAwait(false);
            return;
        }

        if (message is IESPHomeStateMessage stateMessage)
        {
            await PublishIncomingStateMessageAsync(stateMessage, ct).ConfigureAwait(false);
            return;
        }

        if (message is ListEntitiesDoneResponse)
        {
            HandleListEntitiesDoneResponse(session);
            return;
        }

        if (message is ListEntitiesServicesResponse servicesResponse)
        {
            var serviceCaptured = TryCaptureListEntitiesService(session, servicesResponse);
            if (serviceCaptured) return;

            await PublishDynamicEntityRegistrationMessageAsync(servicesResponse, ct).ConfigureAwait(false);
            return;
        }

        if (message is IESPHomeEntityInfoMessage entityInfoMessage)
        {
            var entityCaptured = TryCaptureListEntity(session, entityInfoMessage);
            if (entityCaptured) return;

            await PublishDynamicEntityRegistrationMessageAsync(entityInfoMessage, ct).ConfigureAwait(false);
            return;
        }


        await PublishIncomingMessageAsync(message, ct).ConfigureAwait(false);
    }

    /// <summary>Handles protocol control messages inside the owning session without exposing them to application channels.</summary>
    private async ValueTask HandleProtocolMessage(ConnectionSession session, IESPHomeProtocolMessage message, CancellationToken ct)
    {

#pragma warning disable CS0612 // Type or member is obsolete
        switch (message.ProtocolMessageType)
        {
            case ESPHomeProtocolMessageType.PingRequest:
                await TryQueueMessageAsync(session, new PingResponse(), ct).ConfigureAwait(false);
                break;
            case ESPHomeProtocolMessageType.HelloResponse when message is HelloResponse helloResponse:
                session.HelloResponse.TrySetResult(helloResponse);
                break;
            case ESPHomeProtocolMessageType.AuthenticationResponse when message is AuthenticationResponse authenticationResponse:
                session.AuthenticationResponse.TrySetResult(authenticationResponse);
                break;
            case ESPHomeProtocolMessageType.DisconnectRequest when message is DisconnectRequest disconnectRequest:
                await HandleDisconnectRequestAsync(session, disconnectRequest, ct).ConfigureAwait(false);
                break;
            case ESPHomeProtocolMessageType.DisconnectResponse:
                session.DisconnectResponse.TrySetResult(true);
                break;
            case ESPHomeProtocolMessageType.GetTimeRequest:
                await HandleGetTimeRequestAsync(session, ct).ConfigureAwait(false);
                break;
            case ESPHomeProtocolMessageType.PingResponse:
                Volatile.Read(ref session.PendingPingResponse)?.TrySetResult(true);
                break;
            case ESPHomeProtocolMessageType.AuthenticationRequest:
            case ESPHomeProtocolMessageType.GetTimeResponse:
            case ESPHomeProtocolMessageType.HelloRequest:
                break;
        }
#pragma warning restore CS0612 // Type or member is obsolete
    }

    /// <summary>Publishes one application message through the lifetime-scoped bounded channel so slow consumers apply backpressure to the session read loop.</summary>
    private ValueTask PublishIncomingMessageAsync(IESPHomeMessage message, CancellationToken ct)
    {
        return _incomingMessagesChannel.Writer.WriteAsync(message, ct);
    }

    /// <summary>Publishes one entity state through its bounded stream so slow consumers naturally stop further TCP reads.</summary>
    private ValueTask PublishIncomingStateMessageAsync(IESPHomeStateMessage message, CancellationToken ct)
    {
        return _incomingStateChangeMessages.Writer.WriteAsync(message, ct);
    }


    /// <summary>Publishes entity and user-service descriptions that arrive outside an active finite discovery request.</summary>
    private ValueTask PublishDynamicEntityRegistrationMessageAsync(IESPHomeMessage message, CancellationToken ct)
    {
        return _dynamicEntitiesRegistrationChannel.Writer.WriteAsync(message, ct);
    }

    /// <summary>Captures an entity description when it belongs to the current finite discovery operation.</summary>
    private static bool TryCaptureListEntity(ConnectionSession session, IESPHomeEntityInfoMessage message)
    {
        var assembly = Volatile.Read(ref session.CurrentEntityListAssembly);
        if (assembly is null) return false;

        if (!assembly.Completion.Task.IsCompleted)
            assembly.Entities.Add(message);

        return true;
    }

    /// <summary>Captures a user-service description when it belongs to the current finite discovery operation.</summary>
    private static bool TryCaptureListEntitiesService(ConnectionSession session, ListEntitiesServicesResponse message)
    {
        var assembly = Volatile.Read(ref session.CurrentEntityListAssembly);
        if (assembly is null) return false;

        if (!assembly.Completion.Task.IsCompleted)
            assembly.Services.Add(message);

        return true;
    }

    /// <summary>Completes and detaches the current entity discovery operation when ESPHome terminates its response sequence.</summary>
    private static void HandleListEntitiesDoneResponse(ConnectionSession session)
    {
        var assembly = Interlocked.Exchange(ref session.CurrentEntityListAssembly, value: null);
        if (assembly is null) return;

        if (assembly.Completion.Task.IsCompleted) return;

        var entities = assembly.Entities.ToArray();
        var services = assembly.Services.ToArray();
        var discovery = new EspHomeEntityDiscovery(entities, services);
        var result = EspHomeRpcResult<EspHomeEntityDiscovery>.Completed(discovery);

        assembly.Completion.TrySetResult(result);
    }

    /// <summary>Appends one camera chunk to session-local assembly state and publishes only a complete image through normal backpressure.</summary>
    private async ValueTask HandleCameraImageResponseAsync(ConnectionSession session, CameraImageResponse response, CancellationToken ct)
    {
        var frameKey = (response.DeviceId, response.Key);
        var assemblies = session.CameraAssemblies;
        var assemblyExists = assemblies.TryGetValue(frameKey, out var assembly);

        if (!assemblyExists)
        {
            var tooManyFrames = assemblies.Count >= MaxInflightCameraFrames;
            if (tooManyFrames) return;

            assembly = new CameraFrameAssembly();
            assemblies.Add(frameKey, assembly);
        }

        if (assembly is null)
        {
            if (response.Done) assemblies.Remove(frameKey);
            return;
        }

        var frameTooLarge = response.Data.Length > MaxCameraFrameBytes - assembly.Length;
        if (frameTooLarge)
        {
            assemblies.Remove(frameKey);
            assembly.Dispose();

            if (!response.Done) assemblies.Add(frameKey, value: null);
            return;
        }

        try
        {
            assembly.Append(response.Data.Span);
        }
        catch
        {
            assemblies.Remove(frameKey);
            assembly.Dispose();
            throw;
        }

        if (!response.Done) return;

        assemblies.Remove(frameKey);

        try
        {
            var data = assembly.Complete();
            var image = new EspHomeCameraImage(response.Key, response.DeviceId, data);
            await PublishIncomingMessageAsync(image, ct).ConfigureAwait(false);
        }
        finally
        {
            assembly.Dispose();
        }
    }

}
