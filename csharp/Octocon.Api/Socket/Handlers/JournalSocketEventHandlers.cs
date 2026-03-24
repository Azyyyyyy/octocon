using Octocon.Domain.Abstractions;
using Octocon.Domain.Journals;

namespace Octocon.Api.Socket.Handlers;

public static class JournalSocketEventHandlers
{
    public static async Task HandleAsync(GlobalJournalChangedEvent evt, SocketPushContext context, IJournalRepository journalRepository)
    {
        if (!context.TryGetSystemTopic(evt.SystemId, out var topic, out var joinRef, out var asArray))
        {
            return;
        }

        string payloadJson;
        if (string.Equals(evt.EventName, "global_journal_entry_deleted", StringComparison.Ordinal))
        {
            payloadJson = WebSocketEvents.SerializeSocketJson(new { entry_id = evt.EntryId });
        }
        else
        {
            var entry = await journalRepository.GetGlobalAsync(evt.SystemId, evt.EntryId, context.CancellationToken).ConfigureAwait(false);
            if (entry is null)
            {
                return;
            }

            payloadJson = WebSocketEvents.SerializeSocketJson(new { entry });
        }

        await context.SendAsync(topic, joinRef, asArray, evt.EventName, payloadJson);
    }

    public static async Task HandleAsync(AlterJournalChangedEvent evt, SocketPushContext context, IJournalRepository journalRepository)
    {
        if (!context.TryGetSystemTopic(evt.SystemId, out var topic, out var joinRef, out var asArray))
        {
            return;
        }

        string payloadJson;
        if (string.Equals(evt.EventName, "alter_journal_entry_deleted", StringComparison.Ordinal))
        {
            payloadJson = WebSocketEvents.SerializeSocketJson(new { entry_id = evt.EntryId });
        }
        else
        {
            var entry = await journalRepository.GetAlterAsync(evt.SystemId, evt.EntryId, context.CancellationToken).ConfigureAwait(false);
            if (entry is null)
            {
                return;
            }

            payloadJson = WebSocketEvents.SerializeSocketJson(new { entry });
        }

        await context.SendAsync(topic, joinRef, asArray, evt.EventName, payloadJson);
    }
}
