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
