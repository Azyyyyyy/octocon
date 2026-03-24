using Octocon.Domain.Abstractions;
using Octocon.Domain.Tags;

namespace Octocon.Api.Socket.Handlers;

public static class TagSocketEventHandlers
{
    public static async Task HandleAsync(TagChangedEvent evt, SocketPushContext context, ITagRepository tagRepository)
    {
        if (!context.TryGetSystemTopic(evt.SystemId, out var topic, out var joinRef, out var asArray))
        {
            return;
        }

        string payloadJson;
        if (string.Equals(evt.EventName, "tag_deleted", StringComparison.Ordinal))
        {
            payloadJson = WebSocketEvents.SerializeSocketJson(new Dictionary<string, object?> { ["tag_id"] = evt.TagId });
        }
        else
        {
            var tag = await tagRepository.GetAsync(evt.SystemId, evt.TagId, context.CancellationToken).ConfigureAwait(false);
            if (tag is null)
            {
                return;
            }

            payloadJson = WebSocketEvents.SerializeSocketJson(new { tag });
        }

        await context.SendAsync(topic, joinRef, asArray, evt.EventName, payloadJson);
    }
}
