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
        IJournalRepository journalRepository,
        IEncryptionStateRepository encryptionStateRepository)
    {
        return Task.WhenAll(
            SubscribeAsync<FrontingStartedEvent>(eventBus, context, evt => FrontingSocketEventHandlers.HandleAsync(evt, context, frontingRepository)),
            SubscribeAsync<FrontingEndedEvent>(eventBus, context, evt => FrontingSocketEventHandlers.HandleAsync(evt, context)),
            SubscribeAsync<FrontingSetEvent>(eventBus, context, evt => FrontingSocketEventHandlers.HandleAsync(evt, context, frontingRepository)),
            SubscribeAsync<FrontingBulkUpdatedEvent>(eventBus, context, evt => FrontingSocketEventHandlers.HandleAsync(evt, context, frontingRepository)),
            SubscribeAsync<FrontCommentUpdatedEvent>(eventBus, context, evt => FrontingSocketEventHandlers.HandleAsync(evt, context, frontingRepository)),
            SubscribeAsync<FrontingPrimaryChangedEvent>(eventBus, context, evt => FrontingSocketEventHandlers.HandleAsync(evt, context)),
            SubscribeAsync<FrontDeletedEvent>(eventBus, context, evt => FrontingSocketEventHandlers.HandleAsync(evt, context)),
            SubscribeAsync<AlterCreatedEvent>(eventBus, context, evt => AlterSocketEventHandlers.HandleAsync(evt, context, alterRepository)),
            SubscribeAsync<AlterUpdatedEvent>(eventBus, context, evt => AlterSocketEventHandlers.HandleAsync(evt, context, alterRepository)),
            SubscribeAsync<AlterDeletedEvent>(eventBus, context, evt => AlterSocketEventHandlers.HandleAsync(evt, context)),
            SubscribeAsync<TagCreatedEvent>(eventBus, context, evt => TagSocketEventHandlers.HandleAsync(evt, context, tagRepository)),
            SubscribeAsync<TagUpdatedEvent>(eventBus, context, evt => TagSocketEventHandlers.HandleAsync(evt, context, tagRepository)),
            SubscribeAsync<TagDeletedEvent>(eventBus, context, evt => TagSocketEventHandlers.HandleAsync(evt, context)),
            SubscribeAsync<SettingsFieldsChangedEvent>(eventBus, context, evt => SettingsSocketEventHandlers.HandleAsync(evt, context, settingsFieldRepository)),
            SubscribeAsync<SettingsProfileUpdatedEvent>(eventBus, context, evt => SettingsSocketEventHandlers.HandleAsync(evt, context, accountRepository, alterRepository, frontingRepository, settingsFieldRepository, encryptionStateRepository)),
            SubscribeAsync<SettingsAccountDeletedSignalEvent>(eventBus, context, evt => SettingsSocketEventHandlers.HandleAsync(evt, context)),
            SubscribeAsync<SettingsAltersWipedSignalEvent>(eventBus, context, evt => SettingsSocketEventHandlers.HandleAsync(evt, context)),
            SubscribeAsync<SettingsEncryptedDataWipedSignalEvent>(eventBus, context, evt => SettingsSocketEventHandlers.HandleAsync(evt, context)),
            SubscribeAsync<SettingsDiscordAccountUnlinkedSignalEvent>(eventBus, context, evt => SettingsSocketEventHandlers.HandleAsync(evt, context)),
            SubscribeAsync<SettingsAppleAccountUnlinkedSignalEvent>(eventBus, context, evt => SettingsSocketEventHandlers.HandleAsync(evt, context)),
            SubscribeAsync<SettingsAccountLinkedEvent>(eventBus, context, evt => SettingsSocketEventHandlers.HandleAsync(evt, context)),
            SubscribeAsync<FriendshipAddedEvent>(eventBus, context, evt => FriendshipSocketEventHandlers.HandleAsync(evt, context)),
            SubscribeAsync<FriendshipRemovedEvent>(eventBus, context, evt => FriendshipSocketEventHandlers.HandleAsync(evt, context)),
            SubscribeAsync<FriendshipTrustedEvent>(eventBus, context, evt => FriendshipSocketEventHandlers.HandleAsync(evt, context)),
            SubscribeAsync<FriendshipUntrustedEvent>(eventBus, context, evt => FriendshipSocketEventHandlers.HandleAsync(evt, context)),
            SubscribeAsync<FriendRequestSentEvent>(eventBus, context, evt => FriendshipSocketEventHandlers.HandleAsync(evt, context)),
            SubscribeAsync<FriendRequestReceivedEvent>(eventBus, context, evt => FriendshipSocketEventHandlers.HandleAsync(evt, context)),
            SubscribeAsync<FriendRequestRemovedFromEvent>(eventBus, context, evt => FriendshipSocketEventHandlers.HandleAsync(evt, context)),
            SubscribeAsync<FriendRequestRemovedToEvent>(eventBus, context, evt => FriendshipSocketEventHandlers.HandleAsync(evt, context)),
            SubscribeAsync<PollCreatedEvent>(eventBus, context, evt => PollSocketEventHandlers.HandleAsync(evt, context, pollRepository)),
            SubscribeAsync<PollUpdatedEvent>(eventBus, context, evt => PollSocketEventHandlers.HandleAsync(evt, context, pollRepository)),
            SubscribeAsync<PollDeletedEvent>(eventBus, context, evt => PollSocketEventHandlers.HandleAsync(evt, context)),
            SubscribeAsync<GlobalJournalEntryCreatedEvent>(eventBus, context, evt => JournalSocketEventHandlers.HandleAsync(evt, context, journalRepository)),
            SubscribeAsync<GlobalJournalEntryUpdatedEvent>(eventBus, context, evt => JournalSocketEventHandlers.HandleAsync(evt, context, journalRepository)),
            SubscribeAsync<GlobalJournalEntryDeletedEvent>(eventBus, context, evt => JournalSocketEventHandlers.HandleAsync(evt, context)),
            SubscribeAsync<AlterJournalEntryCreatedEvent>(eventBus, context, evt => JournalSocketEventHandlers.HandleAsync(evt, context, journalRepository)),
            SubscribeAsync<AlterJournalEntryUpdatedEvent>(eventBus, context, evt => JournalSocketEventHandlers.HandleAsync(evt, context, journalRepository)),
            SubscribeAsync<AlterJournalEntryDeletedEvent>(eventBus, context, evt => JournalSocketEventHandlers.HandleAsync(evt, context)));
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
