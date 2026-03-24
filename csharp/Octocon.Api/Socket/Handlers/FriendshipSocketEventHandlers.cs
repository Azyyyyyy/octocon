using Octocon.Domain.Abstractions;

namespace Octocon.Api.Socket.Handlers;

public static class FriendshipSocketEventHandlers
{
    public static Task HandleAsync(FriendshipAddedEvent evt, SocketPushContext context)
        => SendAsync(evt.TargetSystemId, SocketEventNames.Friendships.Added, "system_id", evt.SystemId, context);

    public static Task HandleAsync(FriendshipRemovedEvent evt, SocketPushContext context)
        => SendAsync(evt.TargetSystemId, SocketEventNames.Friendships.Removed, "system_id", evt.SystemId, context);

    public static Task HandleAsync(FriendshipTrustedEvent evt, SocketPushContext context)
        => SendAsync(evt.TargetSystemId, SocketEventNames.Friendships.Trusted, "system_id", evt.SystemId, context);

    public static Task HandleAsync(FriendshipUntrustedEvent evt, SocketPushContext context)
        => SendAsync(evt.TargetSystemId, SocketEventNames.Friendships.Untrusted, "system_id", evt.SystemId, context);

    public static Task HandleAsync(FriendRequestSentEvent evt, SocketPushContext context)
        => SendAsync(evt.TargetSystemId, SocketEventNames.Friendships.RequestSent, "to", evt.ToSystemId, context);

    public static Task HandleAsync(FriendRequestReceivedEvent evt, SocketPushContext context)
        => SendAsync(evt.TargetSystemId, SocketEventNames.Friendships.RequestReceived, "from", evt.FromSystemId, context);

    public static Task HandleAsync(FriendRequestRemovedFromEvent evt, SocketPushContext context)
        => SendAsync(evt.TargetSystemId, SocketEventNames.Friendships.RequestRemoved, "from", evt.FromSystemId, context);

    public static Task HandleAsync(FriendRequestRemovedToEvent evt, SocketPushContext context)
        => SendAsync(evt.TargetSystemId, SocketEventNames.Friendships.RequestRemoved, "to", evt.ToSystemId, context);

    private static async Task SendAsync(string targetSystemId, string eventName, string payloadKey, string payloadValue, SocketPushContext context)
    {
        if (!context.TryGetSystemTopic(targetSystemId, out var topic, out var joinRef, out var asArray))
        {
            return;
        }

        var payloadJson = WebSocketEvents.SerializeSocketJson(new Dictionary<string, object?>
        {
            [payloadKey] = payloadValue
        });

        await context.SendAsync(topic, joinRef, asArray, eventName, payloadJson);
    }
}
