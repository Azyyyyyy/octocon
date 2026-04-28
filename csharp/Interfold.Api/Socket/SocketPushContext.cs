using System.Collections.Concurrent;
using System.Net.WebSockets;

namespace Interfold.Api.Socket;

public sealed class SocketPushContext
{
    public SocketPushContext(
        WebSocket socket,
        ConcurrentDictionary<string, byte> joinedTopics,
        ConcurrentDictionary<string, string?> topicJoinReference,
        ConcurrentDictionary<string, bool> topicReplyAsArrayFrame,
        SemaphoreSlim sendGate,
        CancellationToken cancellationToken,
        string? requestOrigin = null)
    {
        Socket = socket;
        JoinedTopics = joinedTopics;
        TopicJoinReference = topicJoinReference;
        TopicReplyAsArrayFrame = topicReplyAsArrayFrame;
        SendGate = sendGate;
        CancellationToken = cancellationToken;
        RequestOrigin = requestOrigin;
    }

    public WebSocket Socket { get; }
    public ConcurrentDictionary<string, byte> JoinedTopics { get; }
    public ConcurrentDictionary<string, string?> TopicJoinReference { get; }
    public ConcurrentDictionary<string, bool> TopicReplyAsArrayFrame { get; }
    public SemaphoreSlim SendGate { get; }
    public CancellationToken CancellationToken { get; }
    /// <summary>
    /// The origin of the HTTP request that upgraded to this WebSocket
    /// (e.g. <c>https://api.example.com</c>). Used to qualify relative avatar URLs.
    /// </summary>
    public string? RequestOrigin { get; }

    public bool TryGetSystemTopic(string systemId, out string topic, out string? joinRef, out bool asArray)
    {
        topic = $"system:{systemId}";
        if (!JoinedTopics.ContainsKey(topic))
        {
            var targetComparableId = ComparableSystemId(systemId);
            var matchedTopic = JoinedTopics.Keys.FirstOrDefault(t =>
            {
                if (!t.StartsWith("system:", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                var topicSystemId = t["system:".Length..];
                return string.Equals(
                    ComparableSystemId(topicSystemId),
                    targetComparableId,
                    StringComparison.Ordinal);
            });

            if (matchedTopic is null)
            {
                joinRef = null;
                asArray = false;
                return false;
            }

            topic = matchedTopic;
        }

        TopicJoinReference.TryGetValue(topic, out joinRef);
        TopicReplyAsArrayFrame.TryGetValue(topic, out asArray);
        return true;
    }

    private static string ComparableSystemId(string systemId)
    {
        if (string.IsNullOrWhiteSpace(systemId))
        {
            return systemId;
        }

        var separator = systemId.IndexOf(':', StringComparison.Ordinal);
        if (separator <= 0 || separator >= systemId.Length - 1)
        {
            return systemId;
        }

        return systemId[(separator + 1)..];
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
