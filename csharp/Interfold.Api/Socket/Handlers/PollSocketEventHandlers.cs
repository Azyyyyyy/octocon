using Interfold.Contracts;
using Interfold.Contracts.Events;
using Interfold.Domain.Abstractions.Repository;

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

        await context.SendAsync(topic, joinRef, asArray, SocketEventNames.Polls.Deleted, new PollDeletedSocketPayload(evt.PollId));
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

        await context.SendAsync(topic, joinRef, asArray, eventName, new PollSocketPayload(poll));
    }
}
