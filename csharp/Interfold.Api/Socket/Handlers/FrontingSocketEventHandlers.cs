using Interfold.Contracts;
using Interfold.Contracts.Events;
using Interfold.Domain.Abstractions.Repository;

namespace Interfold.Api.Socket.Handlers;

public static class FrontingSocketEventHandlers
{
    public static async Task HandleAsync(FrontingStartedEvent evt, SocketPushContext context, IFrontingRepository frontingRepository)
    {
        if (!context.TryGetSystemTopic(evt.SystemId, out var topic, out var joinRef, out var asArray))
        {
            return;
        }

        var front = await frontingRepository.GetActiveByFrontIdAsync(evt.SystemId, evt.FrontId, context.CancellationToken).ConfigureAwait(false);
        if (front is null)
        {
            return;
        }

        var payloadJson = WebSocketEvents.SerializeSocketJson(new { front });
        await context.SendAsync(topic, joinRef, asArray, SocketEventNames.Fronting.Started, payloadJson);
    }

    public static async Task HandleAsync(FrontingEndedEvent evt, SocketPushContext context)
    {
        if (!context.TryGetSystemTopic(evt.SystemId, out var topic, out var joinRef, out var asArray))
        {
            return;
        }

        var payloadJson = WebSocketEvents.SerializeSocketJson(new { alter_id = evt.AlterId });
        await context.SendAsync(topic, joinRef, asArray, SocketEventNames.Fronting.Ended, payloadJson);
    }

    public static async Task HandleAsync(FrontingSetEvent evt, SocketPushContext context, IFrontingRepository frontingRepository)
    {
        if (!context.TryGetSystemTopic(evt.SystemId, out var topic, out var joinRef, out var asArray))
        {
            return;
        }

        var front = await frontingRepository.GetActiveByFrontIdAsync(evt.SystemId, evt.FrontId, context.CancellationToken).ConfigureAwait(false);
        if (front is null)
        {
            return;
        }

        var payloadJson = WebSocketEvents.SerializeSocketJson(new { front });
        await context.SendAsync(topic, joinRef, asArray, SocketEventNames.Fronting.Set, payloadJson);
    }

    public static async Task HandleAsync(FrontingBulkUpdatedEvent evt, SocketPushContext context, IFrontingRepository frontingRepository)
    {
        if (!context.TryGetSystemTopic(evt.SystemId, out var topic, out var joinRef, out var asArray))
        {
            return;
        }

        var fronts = await frontingRepository.ListActiveAsync(evt.SystemId, context.CancellationToken).ConfigureAwait(false);
        var payloadJson = WebSocketEvents.SerializeSocketJson(new { fronts });
    await context.SendAsync(topic, joinRef, asArray, SocketEventNames.Fronting.BulkUpdated, payloadJson);
    }

    public static async Task HandleAsync(FrontCommentUpdatedEvent evt, SocketPushContext context, IFrontingRepository frontingRepository)
    {
        if (!context.TryGetSystemTopic(evt.SystemId, out var topic, out var joinRef, out var asArray))
        {
            return;
        }

        var front = await frontingRepository.GetActiveByFrontIdAsync(evt.SystemId, evt.FrontId, context.CancellationToken).ConfigureAwait(false);
        if (front is null)
        {
            return;
        }

        var payloadJson = WebSocketEvents.SerializeSocketJson(new { front });
        await context.SendAsync(topic, joinRef, asArray, SocketEventNames.Fronting.CommentUpdated, payloadJson);
    }

    public static async Task HandleAsync(FrontingPrimaryChangedEvent evt, SocketPushContext context)
    {
        if (!context.TryGetSystemTopic(evt.SystemId, out var topic, out var joinRef, out var asArray))
        {
            return;
        }

        var payloadJson = WebSocketEvents.SerializeSocketJson(new { alter_id = evt.AlterId });
        await context.SendAsync(topic, joinRef, asArray, SocketEventNames.Fronting.PrimaryChanged, payloadJson);
    }

    public static async Task HandleAsync(FrontDeletedEvent evt, SocketPushContext context)
    {
        if (!context.TryGetSystemTopic(evt.SystemId, out var topic, out var joinRef, out var asArray))
        {
            return;
        }

        var payloadJson = WebSocketEvents.SerializeSocketJson(new { front_id = evt.FrontId });
        await context.SendAsync(topic, joinRef, asArray, SocketEventNames.Fronting.Deleted, payloadJson);
    }
}
