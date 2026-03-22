namespace Octocon.Api.Socket;

public sealed class SocketAlterChangedEvent
{
    public SocketAlterChangedEvent(string systemId, string eventName, int? alterId)
    {
        SystemId = systemId;
        EventName = eventName;
        AlterId = alterId;
    }

    public string SystemId { get; }
    public string EventName { get; }
    public int? AlterId { get; }
}

public sealed class SocketTagChangedEvent
{
    public SocketTagChangedEvent(string systemId, string eventName, string tagId)
    {
        SystemId = systemId;
        EventName = eventName;
        TagId = tagId;
    }

    public string SystemId { get; }
    public string EventName { get; }
    public string TagId { get; }
}

public sealed class SocketSettingsFieldsChangedEvent
{
    public SocketSettingsFieldsChangedEvent(string systemId)
    {
        SystemId = systemId;
    }

    public string SystemId { get; }
}

/// <summary>
/// A pre-serialized push event that can be forwarded directly to the client
/// without additional data fetching. Used by the relay-path dispatcher for
/// events whose payloads are fully known at dispatch time.
/// </summary>
public sealed class SocketRawPushEvent
{
    public SocketRawPushEvent(string systemId, string eventName, string payloadJson)
    {
        SystemId = systemId;
        EventName = eventName;
        PayloadJson = payloadJson;
    }

    public string SystemId { get; }
    public string EventName { get; }
    public string PayloadJson { get; }
}
