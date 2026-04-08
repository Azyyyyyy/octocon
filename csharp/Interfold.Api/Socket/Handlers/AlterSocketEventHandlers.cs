using Interfold.Domain.Abstractions;
using Interfold.Domain.Alters;

namespace Interfold.Api.Socket.Handlers;

public static class AlterSocketEventHandlers
{
    public static async Task HandleAsync(AlterCreatedEvent evt, SocketPushContext context, IAlterRepository alterRepository)
        => await HandleUpsertAsync(evt.SystemId, evt.AlterId, SocketEventNames.Alters.Created, context, alterRepository);

    public static async Task HandleAsync(AlterUpdatedEvent evt, SocketPushContext context, IAlterRepository alterRepository)
        => await HandleUpsertAsync(evt.SystemId, evt.AlterId, SocketEventNames.Alters.Updated, context, alterRepository);

    public static async Task HandleAsync(AlterDeletedEvent evt, SocketPushContext context)
    {
        if (!context.TryGetSystemTopic(evt.SystemId, out var topic, out var joinRef, out var asArray))
        {
            return;
        }

        var payloadJson = WebSocketEvents.SerializeSocketJson(new Dictionary<string, object?> { ["alter_id"] = evt.AlterId });
        await context.SendAsync(topic, joinRef, asArray, SocketEventNames.Alters.Deleted, payloadJson);
    }

    private static async Task HandleUpsertAsync(string systemId, int alterId, string eventName, SocketPushContext context, IAlterRepository alterRepository)
    {
        if (!context.TryGetSystemTopic(systemId, out var topic, out var joinRef, out var asArray))
        {
            return;
        }

        var alter = await alterRepository.GetAsync(systemId, alterId, context.CancellationToken).ConfigureAwait(false);
        if (alter is null)
        {
            return;
        }

        alter.AvatarUrl = AvatarUrlQualifier.Qualify(alter.AvatarUrl, context.RequestOrigin);
        var payloadJson = WebSocketEvents.SerializeSocketJson(new { alter });
        await context.SendAsync(topic, joinRef, asArray, eventName, payloadJson);
    }
}
