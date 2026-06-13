namespace Interfold.Contracts.Events;

public sealed record AlterCreatedEvent(string TargetSystemId, int AlterId) : ITargetedClusterEvent;

public sealed record AlterUpdatedEvent(string TargetSystemId, int AlterId) : ITargetedClusterEvent;

public sealed record AlterDeletedEvent(string TargetSystemId, int AlterId) : ITargetedClusterEvent;

public sealed record TagCreatedEvent(string TargetSystemId, string TagId) : ITargetedClusterEvent;

public sealed record TagUpdatedEvent(string TargetSystemId, string TagId) : ITargetedClusterEvent;

public sealed record TagDeletedEvent(string TargetSystemId, string TagId) : ITargetedClusterEvent;

public sealed record SettingsFieldsChangedEvent(string TargetSystemId) : ITargetedClusterEvent;

public sealed record SettingsProfileUpdatedEvent(string TargetSystemId, bool EmitUsernameUpdated) : ITargetedClusterEvent;

public sealed record SettingsAccountDeletedSignalEvent(string TargetSystemId) : ITargetedClusterEvent;

public sealed record SettingsAltersWipedSignalEvent(string TargetSystemId) : ITargetedClusterEvent;

public sealed record SettingsEncryptedDataWipedSignalEvent(string TargetSystemId) : ITargetedClusterEvent;

public sealed record SettingsDiscordAccountUnlinkedSignalEvent(string TargetSystemId) : ITargetedClusterEvent;

public sealed record SettingsAppleAccountUnlinkedSignalEvent(string TargetSystemId) : ITargetedClusterEvent;

public sealed record PollCreatedEvent(string TargetSystemId, string PollId) : ITargetedClusterEvent;

public sealed record PollUpdatedEvent(string TargetSystemId, string PollId) : ITargetedClusterEvent;

public sealed record PollDeletedEvent(string TargetSystemId, string PollId) : ITargetedClusterEvent;

public sealed record GlobalJournalEntryCreatedEvent(string TargetSystemId, string EntryId) : ITargetedClusterEvent;

public sealed record GlobalJournalEntryUpdatedEvent(string TargetSystemId, string EntryId) : ITargetedClusterEvent;

public sealed record GlobalJournalEntryDeletedEvent(string TargetSystemId, string EntryId) : ITargetedClusterEvent;

public sealed record AlterJournalEntryCreatedEvent(string TargetSystemId, string EntryId) : ITargetedClusterEvent;

public sealed record AlterJournalEntryUpdatedEvent(string TargetSystemId, string EntryId) : ITargetedClusterEvent;

public sealed record AlterJournalEntryDeletedEvent(string TargetSystemId, string EntryId) : ITargetedClusterEvent;

public sealed record FriendshipAddedEvent(string TargetSystemId, string SystemId) : ITargetedClusterEvent;

public sealed record FriendshipRemovedEvent(string TargetSystemId, string SystemId) : ITargetedClusterEvent;

public sealed record FriendshipTrustedEvent(string TargetSystemId, string SystemId) : ITargetedClusterEvent;

public sealed record FriendshipUntrustedEvent(string TargetSystemId, string SystemId) : ITargetedClusterEvent;

public sealed record FriendRequestSentEvent(string TargetSystemId, string ToSystemId) : ITargetedClusterEvent;

public sealed record FriendRequestReceivedEvent(string TargetSystemId, string FromSystemId) : ITargetedClusterEvent;

public sealed record FriendRequestRemovedFromEvent(string TargetSystemId, string FromSystemId) : ITargetedClusterEvent;

public sealed record FriendRequestRemovedToEvent(string TargetSystemId, string ToSystemId) : ITargetedClusterEvent;

public sealed record SettingsAccountLinkedEvent(string TargetSystemId, string ProviderKey, string Identity) : ITargetedClusterEvent;
