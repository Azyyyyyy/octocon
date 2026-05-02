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

        await context.SendAsync(topic, joinRef, asArray, SocketEventNames.Fronting.Started, new FrontSocketPayload(front));
    }

    public static async Task HandleAsync(FrontingEndedEvent evt, SocketPushContext context)
    {
        if (!context.TryGetSystemTopic(evt.SystemId, out var topic, out var joinRef, out var asArray))
        {
            return;
        }

        await context.SendAsync(topic, joinRef, asArray, SocketEventNames.Fronting.Ended, new AlterIdSocketPayload(evt.AlterId));
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

        await context.SendAsync(topic, joinRef, asArray, SocketEventNames.Fronting.Set, new FrontSocketPayload(front));
    }

    public static async Task HandleAsync(FrontingBulkUpdatedEvent evt, SocketPushContext context, IFrontingRepository frontingRepository)
    {
        if (!context.TryGetSystemTopic(evt.SystemId, out var topic, out var joinRef, out var asArray))
        {
            return;
        }

        var fronts = await frontingRepository.ListActiveAsync(evt.SystemId, context.CancellationToken).ConfigureAwait(false);
        await context.SendAsync(topic, joinRef, asArray, SocketEventNames.Fronting.BulkUpdated, new FrontsSocketPayload(fronts));
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

        await context.SendAsync(topic, joinRef, asArray, SocketEventNames.Fronting.CommentUpdated, new FrontSocketPayload(front));
    }

    public static async Task HandleAsync(FrontingPrimaryChangedEvent evt, SocketPushContext context)
    {
        if (!context.TryGetSystemTopic(evt.SystemId, out var topic, out var joinRef, out var asArray))
        {
            return;
        }

        await context.SendAsync(topic, joinRef, asArray, SocketEventNames.Fronting.PrimaryChanged, new AlterIdSocketPayload(evt.AlterId));
    }

    public static async Task HandleAsync(FrontDeletedEvent evt, SocketPushContext context)
    {
        if (!context.TryGetSystemTopic(evt.SystemId, out var topic, out var joinRef, out var asArray))
        {
            return;
        }

        await context.SendAsync(topic, joinRef, asArray, SocketEventNames.Fronting.Deleted, new FrontIdSocketPayload(evt.FrontId));
    }
}
