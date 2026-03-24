using Octocon.Domain.Abstractions;
using Octocon.Domain.Journals;

namespace Octocon.Api.Socket.Handlers;

public static class JournalSocketEventHandlers
{
    public static async Task HandleAsync(GlobalJournalEntryCreatedEvent evt, SocketPushContext context, IJournalRepository journalRepository)
        => await HandleGlobalUpsertAsync(evt.SystemId, evt.EntryId, SocketEventNames.Journals.GlobalCreated, context, journalRepository);

    public static async Task HandleAsync(GlobalJournalEntryUpdatedEvent evt, SocketPushContext context, IJournalRepository journalRepository)
        => await HandleGlobalUpsertAsync(evt.SystemId, evt.EntryId, SocketEventNames.Journals.GlobalUpdated, context, journalRepository);

    public static async Task HandleAsync(GlobalJournalEntryDeletedEvent evt, SocketPushContext context)
    {
        if (!context.TryGetSystemTopic(evt.SystemId, out var topic, out var joinRef, out var asArray))
        {
            return;
        }

        var payloadJson = WebSocketEvents.SerializeSocketJson(new { entry_id = evt.EntryId });
        await context.SendAsync(topic, joinRef, asArray, SocketEventNames.Journals.GlobalDeleted, payloadJson);
    }

    public static async Task HandleAsync(AlterJournalEntryCreatedEvent evt, SocketPushContext context, IJournalRepository journalRepository)
        => await HandleAlterUpsertAsync(evt.SystemId, evt.EntryId, SocketEventNames.Journals.AlterCreated, context, journalRepository);

    public static async Task HandleAsync(AlterJournalEntryUpdatedEvent evt, SocketPushContext context, IJournalRepository journalRepository)
        => await HandleAlterUpsertAsync(evt.SystemId, evt.EntryId, SocketEventNames.Journals.AlterUpdated, context, journalRepository);

    public static async Task HandleAsync(AlterJournalEntryDeletedEvent evt, SocketPushContext context)
    {
        if (!context.TryGetSystemTopic(evt.SystemId, out var topic, out var joinRef, out var asArray))
        {
            return;
        }

        var payloadJson = WebSocketEvents.SerializeSocketJson(new { entry_id = evt.EntryId });
        await context.SendAsync(topic, joinRef, asArray, SocketEventNames.Journals.AlterDeleted, payloadJson);
    }

    private static async Task HandleGlobalUpsertAsync(string systemId, string entryId, string eventName, SocketPushContext context, IJournalRepository journalRepository)
    {
        if (!context.TryGetSystemTopic(systemId, out var topic, out var joinRef, out var asArray))
        {
            return;
        }

        var entry = await journalRepository.GetGlobalAsync(systemId, entryId, context.CancellationToken).ConfigureAwait(false);
        if (entry is null)
        {
            return;
        }

        var payloadJson = WebSocketEvents.SerializeSocketJson(new { entry });
        await context.SendAsync(topic, joinRef, asArray, eventName, payloadJson);
    }

    private static async Task HandleAlterUpsertAsync(string systemId, string entryId, string eventName, SocketPushContext context, IJournalRepository journalRepository)
    {
        if (!context.TryGetSystemTopic(systemId, out var topic, out var joinRef, out var asArray))
        {
            return;
        }

        var entry = await journalRepository.GetAlterAsync(systemId, entryId, context.CancellationToken).ConfigureAwait(false);
        if (entry is null)
        {
            return;
        }

        var payloadJson = WebSocketEvents.SerializeSocketJson(new { entry });
        await context.SendAsync(topic, joinRef, asArray, eventName, payloadJson);
    }
}
