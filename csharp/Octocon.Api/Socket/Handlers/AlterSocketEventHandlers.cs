using Octocon.Domain.Abstractions;
using Octocon.Domain.Alters;

namespace Octocon.Api.Socket.Handlers;

public static class AlterSocketEventHandlers
{
    public static async Task HandleAsync(AlterChangedEvent evt, SocketPushContext context, IAlterRepository alterRepository)
    {
        if (!context.TryGetSystemTopic(evt.SystemId, out var topic, out var joinRef, out var asArray))
        {
            return;
        }

        string payloadJson;
        if (string.Equals(evt.EventName, "alter_deleted", StringComparison.Ordinal) && evt.AlterId.HasValue)
        {
            payloadJson = WebSocketEvents.SerializeSocketJson(new Dictionary<string, object?> { ["alter_id"] = evt.AlterId.Value });
        }
        else
        {
            if (!evt.AlterId.HasValue)
            {
                return;
            }

            var alter = await alterRepository.GetAsync(evt.SystemId, evt.AlterId.Value, context.CancellationToken).ConfigureAwait(false);
            if (alter is null)
            {
                return;
            }

            payloadJson = WebSocketEvents.SerializeSocketJson(new { alter });
        }

        await context.SendAsync(topic, joinRef, asArray, evt.EventName, payloadJson);
    }
}
