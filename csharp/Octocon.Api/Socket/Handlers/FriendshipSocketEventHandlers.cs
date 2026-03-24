using Octocon.Domain.Abstractions;

namespace Octocon.Api.Socket.Handlers;

public static class FriendshipSocketEventHandlers
{
    public static async Task HandleAsync(FriendshipSocketEvent evt, SocketPushContext context)
    {
        if (!context.TryGetSystemTopic(evt.TargetSystemId, out var topic, out var joinRef, out var asArray))
        {
            return;
        }

        var payloadJson = WebSocketEvents.SerializeSocketJson(new Dictionary<string, object?>
        {
            [evt.PayloadKey] = evt.PayloadValue
        });

        await context.SendAsync(topic, joinRef, asArray, evt.EventName, payloadJson);
    }
}
