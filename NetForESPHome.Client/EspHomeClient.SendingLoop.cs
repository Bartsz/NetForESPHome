using NetForESPHome.DomainModel.Native;
using System.Threading.Channels;

namespace NetForESPHome.Client;

public sealed partial class EspHomeClient
{
    /// <summary>Serializes session writes, registers response waiters immediately before transport writes, and fails unsent work when the session ends.</summary>
    private async Task WriteMessagesLoopAsync(ConnectionSession session)
    {
        var reader = session.OutgoingMessages.Reader;
        var ct = session.Cancellation.Token;

        try
        {
            while (await reader.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                while (reader.TryRead(out var queuedMessage))
                {
                    var pendingResponse = queuedMessage.PendingResponse;
                    var operationCanceledBeforeWrite = pendingResponse?.Completion.Task.IsCanceled == true;
                    if (operationCanceledBeforeWrite)
                    {
                        queuedMessage.WriteCompletion?.TrySetResult(false);
                        continue;
                    }

                    if (pendingResponse is not null) session.ResponseCallbacks[pendingResponse.MessageId].Enqueue(pendingResponse);

                    try
                    {
                        await session.Transport.WriteMessageAsync(queuedMessage.Message, ct).ConfigureAwait(false);
                        queuedMessage.WriteCompletion?.TrySetResult(true);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        queuedMessage.WriteCompletion?.TrySetResult(false);
                        pendingResponse?.Completion.TrySetResult(EspHomeRpcResult<IESPHomeMessage>.ConnectionClosed());
                        return;
                    }
                    catch (Exception exception)
                    {
                        queuedMessage.WriteCompletion?.TrySetResult(false);
                        pendingResponse?.Completion.TrySetResult(EspHomeRpcResult<IESPHomeMessage>.ConnectionClosed());
                        session.RequestClose(EspHomeConnectionCloseReason.TransportFailure, exception);
                        return;
                    }
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            session.RequestClose(EspHomeConnectionCloseReason.TransportFailure, exception);
        }
        finally
        {
            session.OutgoingMessages.Writer.TryComplete();

            while (reader.TryRead(out var queuedMessage))
            {
                queuedMessage.WriteCompletion?.TrySetResult(false);
                queuedMessage.PendingResponse?.Completion.TrySetResult(EspHomeRpcResult<IESPHomeMessage>.ConnectionClosed());
            }
        }
    }

    /// <summary>Queues an application-requested command message for the session captured at the start of this operation.</summary>
    public async ValueTask<bool> WriteMessageAsync(IESPHomeMessage message, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(message);
        ct.ThrowIfCancellationRequested();

        var session = Volatile.Read(ref _activeSession);
        if (session is not null)
        {
            var accepted = await TryQueueMessageAsync(session, message, ct).ConfigureAwait(false);
            if (accepted) return true;
        }

        return false;
    }

    /// <summary>Attempts to queue one protocol message to the captured session and reports when teardown wins the enqueue race.</summary>
    private static async ValueTask<bool> TryQueueMessageAsync(ConnectionSession session, IESPHomeMessage message, CancellationToken ct)
    {
        var queuedMessage = new QueuedMessage(message, writeCompletion: null, pendingResponse: null);

        try
        {
            await session.OutgoingMessages.Writer.WriteAsync(queuedMessage, ct).ConfigureAwait(false);
            return true;
        }
        catch (ChannelClosedException)
        {
            return false;
        }
    }
}
