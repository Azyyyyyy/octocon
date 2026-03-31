using Interfold.Domain.Abstractions;
using Interfold.Domain.Polls;

namespace Interfold.Api.Socket.Handlers;

public static class PollSocketEventHandlers
{
    public static async Task HandleAsync(PollCreatedEvent evt, SocketPushContext context, IPollRepository pollRepository)
        => await HandleUpsertAsync(evt.SystemId, evt.PollId, SocketEventNames.Polls.Created, context, pollRepository);

    public static async Task HandleAsync(PollUpdatedEvent evt, SocketPushContext context, IPollRepository pollRepository)
        => await HandleUpsertAsync(evt.SystemId, evt.PollId, SocketEventNames.Polls.Updated, context, pollRepository);

    public static async Task HandleAsync(PollDeletedEvent evt, SocketPushContext context)
    {
        if (!context.TryGetSystemTopic(evt.SystemId, out var topic, out var joinRef, out var asArray))
        {
            return;
        }

        var payloadJson = WebSocketEvents.SerializeSocketJson(new Dictionary<string, object?> { ["poll_id"] = evt.PollId });
        await context.SendAsync(topic, joinRef, asArray, SocketEventNames.Polls.Deleted, payloadJson);
    }

    private static async Task HandleUpsertAsync(string systemId, string pollId, string eventName, SocketPushContext context, IPollRepository pollRepository)
    {
        if (!context.TryGetSystemTopic(systemId, out var topic, out var joinRef, out var asArray))
        {
            return;
        }

        var poll = await pollRepository.GetAsync(systemId, pollId, context.CancellationToken).ConfigureAwait(false);
        if (poll is null)
        {
            return;
        }

        var payloadJson = WebSocketEvents.SerializeSocketJson(new { poll });
        await context.SendAsync(topic, joinRef, asArray, eventName, payloadJson);
    }
}
