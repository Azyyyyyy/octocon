using Octocon.Api.Socket.Handlers;
using Octocon.Domain.Abstractions;
using Octocon.Domain.Accounts;
using Octocon.Domain.Alters;
using Octocon.Domain.Fronting;
using Octocon.Domain.Journals;
using Octocon.Domain.Polls;
using Octocon.Domain.Settings;
using Octocon.Domain.Tags;

namespace Octocon.Api.Socket;

public static class SocketEventPumpRunner
{
    public static Task RunAllAsync(
        IClusterEventBus eventBus,
        SocketPushContext context,
        IFrontingRepository frontingRepository,
        IAlterRepository alterRepository,
        ITagRepository tagRepository,
        ISettingsFieldRepository settingsFieldRepository,
        IAccountRepository accountRepository,
        IPollRepository pollRepository,
        IJournalRepository journalRepository)
    {
        return Task.WhenAll(
            SubscribeAsync<FrontingStateChangedEvent>(eventBus, context, evt => FrontingSocketEventHandlers.HandleAsync(evt, context, frontingRepository)),
            SubscribeAsync<FrontingStartedEvent>(eventBus, context, evt => FrontingSocketEventHandlers.HandleAsync(evt, context, frontingRepository)),
            SubscribeAsync<FrontingEndedEvent>(eventBus, context, evt => FrontingSocketEventHandlers.HandleAsync(evt, context)),
            SubscribeAsync<FrontingSetEvent>(eventBus, context, evt => FrontingSocketEventHandlers.HandleAsync(evt, context, frontingRepository)),
            SubscribeAsync<FrontingBulkUpdatedEvent>(eventBus, context, evt => FrontingSocketEventHandlers.HandleAsync(evt, context, frontingRepository)),
            SubscribeAsync<FrontCommentUpdatedEvent>(eventBus, context, evt => FrontingSocketEventHandlers.HandleAsync(evt, context, frontingRepository)),
            SubscribeAsync<FrontingPrimaryChangedEvent>(eventBus, context, evt => FrontingSocketEventHandlers.HandleAsync(evt, context)),
            SubscribeAsync<FrontDeletedEvent>(eventBus, context, evt => FrontingSocketEventHandlers.HandleAsync(evt, context)),
            SubscribeAsync<AlterChangedEvent>(eventBus, context, evt => AlterSocketEventHandlers.HandleAsync(evt, context, alterRepository)),
            SubscribeAsync<TagChangedEvent>(eventBus, context, evt => TagSocketEventHandlers.HandleAsync(evt, context, tagRepository)),
            SubscribeAsync<SettingsFieldsChangedEvent>(eventBus, context, evt => SettingsSocketEventHandlers.HandleAsync(evt, context, settingsFieldRepository)),
            SubscribeAsync<SettingsProfileUpdatedEvent>(eventBus, context, evt => SettingsSocketEventHandlers.HandleAsync(evt, context, accountRepository)),
            SubscribeAsync<SettingsSocketSignalEvent>(eventBus, context, evt => SettingsSocketEventHandlers.HandleAsync(evt, context)),
            SubscribeAsync<SettingsAccountLinkedEvent>(eventBus, context, evt => SettingsSocketEventHandlers.HandleAsync(evt, context)),
            SubscribeAsync<FriendshipSocketEvent>(eventBus, context, evt => FriendshipSocketEventHandlers.HandleAsync(evt, context)),
            SubscribeAsync<PollChangedEvent>(eventBus, context, evt => PollSocketEventHandlers.HandleAsync(evt, context, pollRepository)),
            SubscribeAsync<GlobalJournalChangedEvent>(eventBus, context, evt => JournalSocketEventHandlers.HandleAsync(evt, context, journalRepository)),
            SubscribeAsync<AlterJournalChangedEvent>(eventBus, context, evt => JournalSocketEventHandlers.HandleAsync(evt, context, journalRepository)));
    }

    private static async Task SubscribeAsync<TEvent>(
        IClusterEventBus eventBus,
        SocketPushContext context,
        Func<TEvent, Task> handler)
        where TEvent : class
    {
        await foreach (var evt in eventBus.SubscribeAsync<TEvent>(context.CancellationToken).ConfigureAwait(false))
        {
            await handler(evt).ConfigureAwait(false);
        }
    }
}
