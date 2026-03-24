using Octocon.Domain.Abstractions;
using Octocon.Domain.Tags;

namespace Octocon.Api.Socket.Handlers;

public static class TagSocketEventHandlers
{
    public static async Task HandleAsync(TagCreatedEvent evt, SocketPushContext context, ITagRepository tagRepository)
        => await HandleUpsertAsync(evt.SystemId, evt.TagId, SocketEventNames.Tags.Created, context, tagRepository);

    public static async Task HandleAsync(TagUpdatedEvent evt, SocketPushContext context, ITagRepository tagRepository)
        => await HandleUpsertAsync(evt.SystemId, evt.TagId, SocketEventNames.Tags.Updated, context, tagRepository);

    public static async Task HandleAsync(TagDeletedEvent evt, SocketPushContext context)
    {
        if (!context.TryGetSystemTopic(evt.SystemId, out var topic, out var joinRef, out var asArray))
        {
            return;
        }

        var payloadJson = WebSocketEvents.SerializeSocketJson(new Dictionary<string, object?> { ["tag_id"] = evt.TagId });
        await context.SendAsync(topic, joinRef, asArray, SocketEventNames.Tags.Deleted, payloadJson);
    }

    private static async Task HandleUpsertAsync(string systemId, string tagId, string eventName, SocketPushContext context, ITagRepository tagRepository)
    {
        if (!context.TryGetSystemTopic(systemId, out var topic, out var joinRef, out var asArray))
        {
            return;
        }

        var tag = await tagRepository.GetAsync(systemId, tagId, context.CancellationToken).ConfigureAwait(false);
        if (tag is null)
        {
            return;
        }

        var payloadJson = WebSocketEvents.SerializeSocketJson(new { tag });
        await context.SendAsync(topic, joinRef, asArray, eventName, payloadJson);
    }
}
