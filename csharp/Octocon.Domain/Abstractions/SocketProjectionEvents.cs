namespace Octocon.Domain.Abstractions;

public sealed class AlterChangedEvent
{
    public AlterChangedEvent(string systemId, string eventName, int? alterId)
    {
        SystemId = systemId;
        EventName = eventName;
        AlterId = alterId;
    }

    public string SystemId { get; }
    public string EventName { get; }
    public int? AlterId { get; }
}

public sealed class TagChangedEvent
{
    public TagChangedEvent(string systemId, string eventName, string tagId)
    {
        SystemId = systemId;
        EventName = eventName;
        TagId = tagId;
    }

    public string SystemId { get; }
    public string EventName { get; }
    public string TagId { get; }
}

public sealed class SettingsFieldsChangedEvent
{
    public SettingsFieldsChangedEvent(string systemId)
    {
        SystemId = systemId;
    }

    public string SystemId { get; }
}

public sealed class SettingsProfileUpdatedEvent
{
    public SettingsProfileUpdatedEvent(string systemId, bool emitUsernameUpdated)
    {
        SystemId = systemId;
        EmitUsernameUpdated = emitUsernameUpdated;
    }

    public string SystemId { get; }
    public bool EmitUsernameUpdated { get; }
}

public sealed class SettingsSocketSignalEvent
{
    public SettingsSocketSignalEvent(string systemId, string eventName)
    {
        SystemId = systemId;
        EventName = eventName;
    }

    public string SystemId { get; }
    public string EventName { get; }
}

public sealed class PollChangedEvent
{
    public PollChangedEvent(string systemId, string eventName, string pollId)
    {
        SystemId = systemId;
        EventName = eventName;
        PollId = pollId;
    }

    public string SystemId { get; }
    public string EventName { get; }
    public string PollId { get; }
}

public sealed class GlobalJournalChangedEvent
{
    public GlobalJournalChangedEvent(string systemId, string eventName, string entryId)
    {
        SystemId = systemId;
        EventName = eventName;
        EntryId = entryId;
    }

    public string SystemId { get; }
    public string EventName { get; }
    public string EntryId { get; }
}

public sealed class AlterJournalChangedEvent
{
    public AlterJournalChangedEvent(string systemId, string eventName, string entryId)
    {
        SystemId = systemId;
        EventName = eventName;
        EntryId = entryId;
    }

    public string SystemId { get; }
    public string EventName { get; }
    public string EntryId { get; }
}

public sealed class FriendshipSocketEvent
{
    public FriendshipSocketEvent(string targetSystemId, string eventName, string payloadKey, string payloadValue)
    {
        TargetSystemId = targetSystemId;
        EventName = eventName;
        PayloadKey = payloadKey;
        PayloadValue = payloadValue;
    }

    public string TargetSystemId { get; }
    public string EventName { get; }
    public string PayloadKey { get; }
    public string PayloadValue { get; }
}

public sealed class SettingsAccountLinkedEvent
{
    public SettingsAccountLinkedEvent(string systemId, string providerKey, string identity)
    {
        SystemId = systemId;
        ProviderKey = providerKey;
        Identity = identity;
    }

    public string SystemId { get; }
    public string ProviderKey { get; }
    public string Identity { get; }
}
