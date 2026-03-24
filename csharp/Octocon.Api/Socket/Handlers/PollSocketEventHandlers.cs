using Octocon.Domain.Abstractions;
using Octocon.Domain.Polls;

namespace Octocon.Api.Socket.Handlers;

public static class PollSocketEventHandlers
{
    public static async Task HandleAsync(PollChangedEvent evt, SocketPushContext context, IPollRepository pollRepository)
    {
        if (!context.TryGetSystemTopic(evt.SystemId, out var topic, out var joinRef, out var asArray))
        {
            return;
        }

        string payloadJson;
        if (string.Equals(evt.EventName, "poll_deleted", StringComparison.Ordinal))
        {
            payloadJson = WebSocketEvents.SerializeSocketJson(new Dictionary<string, object?> { ["poll_id"] = evt.PollId });
        }
        else
        {
            var poll = await pollRepository.GetAsync(evt.SystemId, evt.PollId, context.CancellationToken).ConfigureAwait(false);
            if (poll is null)
            {
                return;
            }

            payloadJson = WebSocketEvents.SerializeSocketJson(new { poll });
        }

        await context.SendAsync(topic, joinRef, asArray, evt.EventName, payloadJson);
    }
}
