using System.Collections.Concurrent;
using System.Net.WebSockets;

namespace Octocon.Api.Socket;

public sealed class SocketPushContext
{
    public SocketPushContext(
        WebSocket socket,
        ConcurrentDictionary<string, byte> joinedTopics,
        ConcurrentDictionary<string, string?> topicJoinReference,
        ConcurrentDictionary<string, bool> topicReplyAsArrayFrame,
        SemaphoreSlim sendGate,
        CancellationToken cancellationToken)
    {
        Socket = socket;
        JoinedTopics = joinedTopics;
        TopicJoinReference = topicJoinReference;
        TopicReplyAsArrayFrame = topicReplyAsArrayFrame;
        SendGate = sendGate;
        CancellationToken = cancellationToken;
    }

    public WebSocket Socket { get; }
    public ConcurrentDictionary<string, byte> JoinedTopics { get; }
    public ConcurrentDictionary<string, string?> TopicJoinReference { get; }
    public ConcurrentDictionary<string, bool> TopicReplyAsArrayFrame { get; }
    public SemaphoreSlim SendGate { get; }
    public CancellationToken CancellationToken { get; }

    public bool TryGetSystemTopic(string systemId, out string topic, out string? joinRef, out bool asArray)
    {
        topic = $"system:{systemId}";
        if (!JoinedTopics.ContainsKey(topic))
        {
            joinRef = null;
            asArray = false;
            return false;
        }

        TopicJoinReference.TryGetValue(topic, out joinRef);
        TopicReplyAsArrayFrame.TryGetValue(topic, out asArray);
        return true;
    }

    public Task SendAsync(string topic, string? joinRef, bool asArray, string eventName, string payloadJson)
        => WebSocketEvents.SendPhoenixPushAsync(
            Socket,
            topic,
            joinRef,
            eventName,
            payloadJson,
            asArray,
            CancellationToken,
            SendGate);
}
