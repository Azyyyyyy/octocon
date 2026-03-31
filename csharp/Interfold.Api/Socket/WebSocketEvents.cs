using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace Interfold.Api.Socket;

public static class WebSocketEvents
{

public static async Task SendPhoenixPushAsync(
    WebSocket socket,
    string topic,
    string? joinReference,
    string eventName,
    string payloadJson,
    bool replyAsArrayFrame,
    CancellationToken cancellationToken,
    SemaphoreSlim? sendGate = null)
{
    var escapedTopic = JsonSerializer.Serialize(topic);
    var escapedJoinRef = joinReference is null ? "null" : JsonSerializer.Serialize(joinReference);
    var escapedEvent = JsonSerializer.Serialize(eventName);

    var frame = replyAsArrayFrame
        ?
        "[" +
        escapedJoinRef + "," +
        "null," +
        escapedTopic + "," +
        escapedEvent + "," +
        payloadJson +
        "]"
        :
        "{" +
        "\"topic\":" + escapedTopic + "," +
        "\"event\":" + escapedEvent + "," +
        "\"payload\":" + payloadJson + "," +
        "\"ref\":null," +
        "\"join_ref\":" + escapedJoinRef +
        "}";

    var bytes = Encoding.UTF8.GetBytes(frame);
    if (sendGate is not null)
    {
        await sendGate.WaitAsync(cancellationToken);
        try
        {
            await socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, cancellationToken);
        }
        finally
        {
            sendGate.Release();
        }
    }
    else
    {
        await socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, cancellationToken);
    }
}

public static string SerializeSocketJson(this object value)
{
    var opts = new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };
    return JsonSerializer.Serialize(value, opts);
}

}