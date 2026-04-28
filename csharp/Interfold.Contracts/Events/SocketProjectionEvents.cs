namespace Interfold.Contracts.Events;

public sealed record AlterCreatedEvent(string SystemId, int AlterId);

public sealed record AlterUpdatedEvent(string SystemId, int AlterId);

public sealed record AlterDeletedEvent(string SystemId, int AlterId);

public sealed record TagCreatedEvent(string SystemId, string TagId);

public sealed record TagUpdatedEvent(string SystemId, string TagId);

public sealed record TagDeletedEvent(string SystemId, string TagId);

public sealed record SettingsFieldsChangedEvent(string SystemId);

public sealed record SettingsProfileUpdatedEvent(string SystemId, bool EmitUsernameUpdated);

public sealed record SettingsAccountDeletedSignalEvent(string SystemId);

public sealed record SettingsAltersWipedSignalEvent(string SystemId);

public sealed record SettingsEncryptedDataWipedSignalEvent(string SystemId);

public sealed record SettingsDiscordAccountUnlinkedSignalEvent(string SystemId);

public sealed record SettingsAppleAccountUnlinkedSignalEvent(string SystemId);

public sealed record PollCreatedEvent(string SystemId, string PollId);

public sealed record PollUpdatedEvent(string SystemId, string PollId);

public sealed record PollDeletedEvent(string SystemId, string PollId);

public sealed record GlobalJournalEntryCreatedEvent(string SystemId, string EntryId);

public sealed record GlobalJournalEntryUpdatedEvent(string SystemId, string EntryId);

public sealed record GlobalJournalEntryDeletedEvent(string SystemId, string EntryId);

public sealed record AlterJournalEntryCreatedEvent(string SystemId, string EntryId);

public sealed record AlterJournalEntryUpdatedEvent(string SystemId, string EntryId);

public sealed record AlterJournalEntryDeletedEvent(string SystemId, string EntryId);

public sealed record FriendshipAddedEvent(string TargetSystemId, string SystemId);

public sealed record FriendshipRemovedEvent(string TargetSystemId, string SystemId);

public sealed record FriendshipTrustedEvent(string TargetSystemId, string SystemId);

public sealed record FriendshipUntrustedEvent(string TargetSystemId, string SystemId);

public sealed record FriendRequestSentEvent(string TargetSystemId, string ToSystemId);

public sealed record FriendRequestReceivedEvent(string TargetSystemId, string FromSystemId);

public sealed record FriendRequestRemovedFromEvent(string TargetSystemId, string FromSystemId);

public sealed record FriendRequestRemovedToEvent(string TargetSystemId, string ToSystemId);

public sealed record SettingsAccountLinkedEvent(string SystemId, string ProviderKey, string Identity);