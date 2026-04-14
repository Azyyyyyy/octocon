using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Interfold.IntegrationTests.Attributes;
using Interfold.IntegrationTests.TestServices;
using Microsoft.AspNetCore.TestHost;

namespace Interfold.IntegrationTests.Endpoints;

public class WebSocketTests : BaseEndpointTest
{
    [Test, ApiIntegration]
    public async Task Api_UserSocketEndpoint_AllowsWebSocketUpgrade()
    {
        await using var factory = new InterfoldWebApplicationFactory()
            .WithConfiguration("OCTOCON_PERSISTENCE", "inmemory");
        
        using var server = factory.Server;
        var client = server.CreateWebSocketClient();
        
        const string socketToken = "integration-test-token";
        var uri = new Uri(WebSocketBasePath(server), "/api/socket/websocket?token=" + socketToken);
        
        using var ws = await client.ConnectAsync(uri, CancellationToken.None);

        await Assert.That(ws.State == WebSocketState.Open).IsTrue().Because($"Expected websocket to be open after connecting to /api/socket/weboscket, got {ws.State}.");

        var arrayJoinFrame =
            "[" +
            "\"51\"," +
            "\"51\"," +
            "\"system:sys-phx-join\"," +
            "\"phx_join\"," +
            "{" +
            "\"token\":\"" + socketToken + "\"," +
            "\"protocolVersion\":\"2.0.0\"," +
            "\"platform\":\"wasm\"," +
            "\"isReconnect\":true" +
            "}" +
            "]";

        var arrayJoinBytes = Encoding.UTF8.GetBytes(arrayJoinFrame);
        await ws.SendAsync(arrayJoinBytes, WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);

        var arrayJoinReply = await ReceiveWebSocketTextAsync(ws);
        using var arrayJoinDoc = JsonDocument.Parse(arrayJoinReply);
        var arrayRoot = arrayJoinDoc.RootElement;

        await Assert.That(arrayRoot.ValueKind == JsonValueKind.Array).IsTrue().Because($"Expected array-frame reply for array-frame request. Payload: {arrayJoinReply}");
        await Assert.That(arrayRoot.GetArrayLength() >= 5).IsTrue().Because($"Expected 5-element Phoenix array reply. Payload: {arrayJoinReply}");
        await Assert.That(string.Equals(arrayRoot[0].GetString(), "51", StringComparison.Ordinal)).IsTrue().Because($"Expected join_ref=51 in array reply. Payload: {arrayJoinReply}");
        await Assert.That(string.Equals(arrayRoot[1].GetString(), "51", StringComparison.Ordinal)).IsTrue().Because($"Expected ref=51 in array reply. Payload: {arrayJoinReply}");
        await Assert.That(string.Equals(arrayRoot[2].GetString(), "system:sys-phx-join", StringComparison.Ordinal)).IsTrue().Because($"Expected topic to match array join topic. Payload: {arrayJoinReply}");
        await Assert.That(string.Equals(arrayRoot[3].GetString(), "phx_reply", StringComparison.Ordinal)).IsTrue().Because($"Expected array reply event phx_reply. Payload: {arrayJoinReply}");
    }
    
        
    [Test, ApiIntegration]
    public async Task Api_UserSocketEndpoint_AllowsWebSocketUpgrade_WithToken()
    {
        await using var factory = new InterfoldWebApplicationFactory()
            .WithConfiguration("OCTOCON_PERSISTENCE", "inmemory")
            .WithConfiguration("OCTOCON_AUTH_CHALLENGE_ENABLED", "false");

        var wsClient = factory.Server.CreateWebSocketClient();
        const string socketToken = "integration-test-token";
        var uri = new Uri(WebSocketBasePath(factory.Server), $"/api/socket/websocket?token={socketToken}");
        using var ws = await wsClient.ConnectAsync(uri, CancellationToken.None);

        await Assert.That(ws.State == WebSocketState.Open).IsTrue().Because($"Expected websocket to be open after connecting to /api/socket, got {ws.State}.");

        var arrayJoinFrame =
            "[" +
            "\"51\"," +
            "\"51\"," +
            "\"system:sys-phx-join\"," +
            "\"phx_join\"," +
            "{" +
            "\"token\":\"" + socketToken + "\"," +
            "\"protocolVersion\":\"2.0.0\"," +
            "\"platform\":\"wasm\"," +
            "\"isReconnect\":true" +
            "}" +
            "]";

        var arrayJoinBytes = Encoding.UTF8.GetBytes(arrayJoinFrame);
        await ws.SendAsync(arrayJoinBytes, WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);

        var arrayJoinReply = await ReceiveWebSocketTextAsync(ws);
        using var arrayJoinDoc = JsonDocument.Parse(arrayJoinReply);
        var arrayRoot = arrayJoinDoc.RootElement;

        await Assert.That(arrayRoot.ValueKind == JsonValueKind.Array).IsTrue().Because($"Expected array-frame reply for array-frame request. Payload: {arrayJoinReply}");
        await Assert.That(arrayRoot.GetArrayLength() >= 5).IsTrue().Because($"Expected 5-element Phoenix array reply. Payload: {arrayJoinReply}");
        await Assert.That(string.Equals(arrayRoot[0].GetString(), "51", StringComparison.Ordinal)).IsTrue().Because($"Expected join_ref=51 in array reply. Payload: {arrayJoinReply}");
        await Assert.That(string.Equals(arrayRoot[1].GetString(), "51", StringComparison.Ordinal)).IsTrue().Because($"Expected ref=51 in array reply. Payload: {arrayJoinReply}");
        await Assert.That(string.Equals(arrayRoot[2].GetString(), "system:sys-phx-join", StringComparison.Ordinal)).IsTrue().Because($"Expected topic to match array join topic. Payload: {arrayJoinReply}");
        await Assert.That(string.Equals(arrayRoot[3].GetString(), "phx_reply", StringComparison.Ordinal)).IsTrue().Because($"Expected array reply event phx_reply. Payload: {arrayJoinReply}");

        var arrayPayload = arrayRoot[4];
        await Assert.That(string.Equals(arrayPayload.GetProperty("status").GetString(), "ok", StringComparison.Ordinal)).IsTrue().Because($"Expected array join status=ok. Payload: {arrayJoinReply}");
        await Assert.That(arrayPayload.GetProperty("response").ValueKind == JsonValueKind.Object).IsTrue().Because($"Expected reconnect join response object in array reply. Payload: {arrayJoinReply}");

        var joinFrame =
            "{" +
            "\"topic\":\"system:sys-phx-join\"," +
            "\"event\":\"phx_join\"," +
            "\"payload\":{\"token\":\"" + socketToken + "\",\"forceBatch\":true}," +
            "\"ref\":\"1\"," +
            "\"join_ref\":\"1\"" +
            "}";

        var joinBytes = Encoding.UTF8.GetBytes(joinFrame);
        await ws.SendAsync(joinBytes, WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);

        var joinReply = await ReceiveWebSocketTextAsync(ws);
        using var joinDoc = JsonDocument.Parse(joinReply);
        var root = joinDoc.RootElement;

        await Assert.That(string.Equals(root.GetProperty("event").GetString(), "phx_reply", StringComparison.Ordinal)).IsTrue().Because($"Expected Phoenix phx_reply event, got payload: {joinReply}");
        await Assert.That(string.Equals(root.GetProperty("topic").GetString(), "system:sys-phx-join", StringComparison.Ordinal)).IsTrue().Because($"Expected join reply topic to match requested topic. Payload: {joinReply}");

        var payload = root.GetProperty("payload");
        await Assert.That(string.Equals(payload.GetProperty("status").GetString(), "ok", StringComparison.Ordinal)).IsTrue().Because($"Expected phx_join status=ok, got payload: {joinReply}");

        var response = payload.GetProperty("response");
        await Assert.That(response.TryGetProperty("system", out _)).IsTrue().Because($"Expected phx_join response to include 'system' key. Payload: {joinReply}");
        await Assert.That(response.TryGetProperty("batched", out var batchedEl) && batchedEl.ValueKind == JsonValueKind.True).IsTrue().Because($"Expected phx_join response batched=true when forceBatch=true. Payload: {joinReply}");
        await Assert.That(response.TryGetProperty("alters", out var altersEl) && altersEl.ValueKind == JsonValueKind.Null).IsTrue().Because($"Expected phx_join response alters=null for batched init. Payload: {joinReply}");
        await Assert.That(response.TryGetProperty("fronts", out var frontsEl) && frontsEl.ValueKind == JsonValueKind.Null).IsTrue().Because($"Expected phx_join response fronts=null for batched init. Payload: {joinReply}");
        await Assert.That(response.TryGetProperty("tags", out var tagsEl) && tagsEl.ValueKind == JsonValueKind.Null).IsTrue().Because($"Expected phx_join response tags=null for batched init. Payload: {joinReply}");

        var batchedComplete = await ReceiveWebSocketTextAsync(ws);
        using var batchedCompleteDoc = JsonDocument.Parse(batchedComplete);
        var batchedCompleteRoot = batchedCompleteDoc.RootElement;
        await Assert.That(string.Equals(batchedCompleteRoot.GetProperty("event").GetString(), "batched_init_complete", StringComparison.Ordinal)).IsTrue().Because($"Expected batched_init_complete server push after forceBatch join. Payload: {batchedComplete}");

        var endpointFrame =
            "{" +
            "\"topic\":\"system:sys-phx-join\"," +
            "\"event\":\"endpoint\"," +
            "\"payload\":{" +
            "\"method\":\"GET\"," +
            "\"path\":\"/api/heartbeat\"," +
            "\"body\":{}" +
            "}," +
            "\"ref\":\"2\"," +
            "\"join_ref\":\"1\"" +
            "}";

        var endpointBytes = Encoding.UTF8.GetBytes(endpointFrame);
        await ws.SendAsync(endpointBytes, WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);

        var endpointReply = await ReceiveWebSocketTextAsync(ws);
        using var endpointDoc = JsonDocument.Parse(endpointReply);
        var endpointRoot = endpointDoc.RootElement;

        await Assert.That(string.Equals(endpointRoot.GetProperty("event").GetString(), "phx_reply", StringComparison.Ordinal)).IsTrue().Because($"Expected endpoint reply to use phx_reply event. Payload: {endpointReply}");

        var endpointPayload = endpointRoot.GetProperty("payload");
        await Assert.That(string.Equals(endpointPayload.GetProperty("status").GetString(), "ok", StringComparison.Ordinal)).IsTrue().Because($"Expected endpoint event status=ok envelope. Payload: {endpointReply}");

        var endpointResponse = endpointPayload.GetProperty("response");
        await Assert.That(endpointResponse.GetProperty("status").GetInt32() == 200).IsTrue().Because($"Expected proxied /api/heartbeat status=200. Payload: {endpointReply}");

        var proxiedBody = endpointResponse.GetProperty("body").GetString() ?? string.Empty;
        await Assert.That(proxiedBody.Contains("ACK", StringComparison.Ordinal)).IsTrue().Because($"Expected proxied heartbeat body to include ACK. Body: {proxiedBody}");

        var createAlterFrame =
            "{" +
            "\"topic\":\"system:sys-phx-join\"," +
            "\"event\":\"endpoint\"," +
            "\"payload\":{" +
            "\"method\":\"POST\"," +
            "\"path\":\"/api/systems/me/alters\"," +
            "\"body\":{\"name\":\"SocketCreate\"}" +
            "}," +
            "\"ref\":\"3\"," +
            "\"join_ref\":\"1\"" +
            "}";

        var createAlterBytes = Encoding.UTF8.GetBytes(createAlterFrame);
        await ws.SendAsync(createAlterBytes, WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);

        var createAlterReply = await ReceiveWebSocketTextAsync(ws);
        using var createAlterDoc = JsonDocument.Parse(createAlterReply);
        var createAlterRoot = createAlterDoc.RootElement;
        var createAlterPayload = createAlterRoot.GetProperty("payload");
        var createAlterResponse = createAlterPayload.GetProperty("response");

        await Assert.That(createAlterResponse.GetProperty("status").GetInt32() == 201).IsTrue().Because($"Expected proxied alter-create status=201. Payload: {createAlterReply}");

        var createAlterBody = createAlterResponse.GetProperty("body").GetString() ?? string.Empty;
        await Assert.That(ReadReplay(createAlterBody) == false).IsTrue().Because($"Expected first socket-proxied alter-create replay=false. Body: {createAlterBody}");

        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "test done", CancellationToken.None);
    }
    
        [Test, ApiIntegration]
    public async Task Api_UserSocketEndpoint_RejectsUnsupportedProtocolVersion()
    {
        await using var factory = new InterfoldWebApplicationFactory()
            .WithConfiguration("OCTOCON_PERSISTENCE", "inmemory")
            .WithConfiguration("OCTOCON_DEEPLINK_ADDRESS", "octocon://app")
            .WithConfiguration("OCTOCON_REGION", "nam")
            .WithConfiguration("OCTOCON_AUTH_CHALLENGE_ENABLED", "false");

        var wsClient = factory.Server.CreateWebSocketClient();
        const string socketToken = "integration-test-token";
        var uri = new Uri(WebSocketBasePath(factory.Server), $"api/socket/websocket?token={socketToken}");
        using var ws = await wsClient.ConnectAsync(uri, CancellationToken.None);

        var joinFrame =
            "{" +
            "\"topic\":\"system:sys-phx-unsupported\"," +
            "\"event\":\"phx_join\"," +
            "\"payload\":{" +
            "\"token\":\"" + socketToken + "\"," +
            "\"protocolVersion\":\"not-a-version\"" +
            "}," +
            "\"ref\":\"1\"," +
            "\"join_ref\":\"1\"" +
            "}";

        await ws.SendAsync(Encoding.UTF8.GetBytes(joinFrame), WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);
        var joinReply = await ReceiveWebSocketTextAsync(ws);
        using var joinDoc = JsonDocument.Parse(joinReply);
        var payload = joinDoc.RootElement.GetProperty("payload");

        await Assert.That(string.Equals(payload.GetProperty("status").GetString(), "error", StringComparison.Ordinal)).IsTrue().Because($"Expected status=error for unsupported protocol version. Payload: {joinReply}");
        await Assert.That(string.Equals(payload.GetProperty("response").GetProperty("reason").GetString(), "unsupported_protocol_version", StringComparison.Ordinal)).IsTrue().Because($"Expected reason=unsupported_protocol_version. Payload: {joinReply}");

        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "test done", CancellationToken.None);
    }

    [Test, ApiIntegration]
    public async Task Api_UserSocketEndpoint_BatchesForIos_WhenThresholdExceeded()
    {
        await using var factory = new InterfoldWebApplicationFactory()
            .WithConfiguration("OCTOCON_PERSISTENCE", "inmemory")
            .WithConfiguration("OCTOCON_DEEPLINK_ADDRESS", "octocon://app")
            .WithConfiguration("OCTOCON_REGION", "nam")
            .WithConfiguration("OCTOCON_AUTH_CHALLENGE_ENABLED", "false");

        var wsClient = factory.Server.CreateWebSocketClient();
        const string socketToken = "integration-test-token";
        var uri = new Uri(WebSocketBasePath(factory.Server), $"api/socket/websocket?token={socketToken}");
        using var ws = await wsClient.ConnectAsync(uri, CancellationToken.None);

        var joinFrame =
            "{" +
            "\"topic\":\"system:sys-phx-ios-batch\"," +
            "\"event\":\"phx_join\"," +
            "\"payload\":{" +
            "\"token\":\"" + socketToken + "\"," +
            "\"platform\":\"ios\"," +
            "\"protocolVersion\":\"2.0.0\"" +
            "}," +
            "\"ref\":\"1\"," +
            "\"join_ref\":\"1\"" +
            "}";

        await ws.SendAsync(Encoding.UTF8.GetBytes(joinFrame), WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);
        var joinReply = await ReceiveWebSocketTextAsync(ws);
        using var joinDoc = JsonDocument.Parse(joinReply);
        var payload = joinDoc.RootElement.GetProperty("payload");
        var response = payload.GetProperty("response");

        await Assert.That(string.Equals(payload.GetProperty("status").GetString(), "ok", StringComparison.Ordinal)).IsTrue().Because($"Expected status=ok for iOS batched join. Payload: {joinReply}");
        await Assert.That(response.TryGetProperty("batched", out var batchedEl) && batchedEl.ValueKind == JsonValueKind.True).IsTrue().Because($"Expected batched=true for iOS join above threshold. Payload: {joinReply}");

        var batchedComplete = await ReceiveWebSocketTextAsync(ws);
        using var batchedCompleteDoc = JsonDocument.Parse(batchedComplete);
        await Assert.That(string.Equals(batchedCompleteDoc.RootElement.GetProperty("event").GetString(), "batched_init_complete", StringComparison.Ordinal)).IsTrue().Because($"Expected batched_init_complete after iOS batched join. Payload: {batchedComplete}");

        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "test done", CancellationToken.None);
    }

    [Test, ApiIntegration]
    public async Task Api_UserSocketEndpoint_RateLimitsThirdJoinWithinOneSecond()
    {
        await using var factory = new InterfoldWebApplicationFactory()
            .WithConfiguration("OCTOCON_PERSISTENCE", "inmemory")
            .WithConfiguration("OCTOCON_DEEPLINK_ADDRESS", "octocon://app")
            .WithConfiguration("OCTOCON_REGION", "nam")
            .WithConfiguration("OCTOCON_AUTH_CHALLENGE_ENABLED", "false");

        var wsClient = factory.Server.CreateWebSocketClient();
        const string socketToken = "integration-test-token";
        var uri = new Uri(WebSocketBasePath(factory.Server), $"api/socket/websocket?token={socketToken}");
        using var ws = await wsClient.ConnectAsync(uri, CancellationToken.None);

        var first = await SendJoinAsync(ws, socketToken, "1");
        var second = await SendJoinAsync(ws, socketToken, "2");
        var third = await SendJoinAsync(ws, socketToken, "3");

        using var firstDoc = JsonDocument.Parse(first);
        using var secondDoc = JsonDocument.Parse(second);
        using var thirdDoc = JsonDocument.Parse(third);

        await Assert.That(string.Equals(firstDoc.RootElement.GetProperty("payload").GetProperty("status").GetString(), "ok", StringComparison.Ordinal)).IsTrue().Because($"Expected first join to be accepted. Payload: {first}");
        await Assert.That(string.Equals(secondDoc.RootElement.GetProperty("payload").GetProperty("status").GetString(), "ok", StringComparison.Ordinal)).IsTrue().Because($"Expected second join to be accepted. Payload: {second}");
        await Assert.That(string.Equals(thirdDoc.RootElement.GetProperty("payload").GetProperty("status").GetString(), "error", StringComparison.Ordinal)).IsTrue().Because($"Expected third join to be rate-limited. Payload: {third}");
        await Assert.That(string.Equals(thirdDoc.RootElement.GetProperty("payload").GetProperty("response").GetProperty("reason").GetString(), "rate_limited", StringComparison.Ordinal)).IsTrue().Because($"Expected reason=rate_limited on third join. Payload: {third}");

        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "test done", CancellationToken.None);
    }

    [Test, ApiIntegration]
    public async Task Api_UserSocketEndpoint_PushesFrontingChangedEvent_AfterFrontStart()
    {
        await using var factory = new InterfoldWebApplicationFactory()
            .WithConfiguration("OCTOCON_PERSISTENCE", "inmemory")
            .WithConfiguration("OCTOCON_DEEPLINK_ADDRESS", "octocon://app")
            .WithConfiguration("OCTOCON_REGION", "nam")
            .WithConfiguration("OCTOCON_AUTH_CHALLENGE_ENABLED", "false");

        var wsClient = factory.Server.CreateWebSocketClient();
        const string socketToken = "integration-test-token";
        const string topic = "system:sys-front-push";
        var uri = new Uri(WebSocketBasePath(factory.Server), $"api/socket/websocket?token={socketToken}");
        var ws = await wsClient.ConnectAsync(uri, CancellationToken.None);

        var joinFrame =
            "{" +
            "\"topic\":\"" + topic + "\"," +
            "\"event\":\"phx_join\"," +
            "\"payload\":{\"token\":\"" + socketToken + "\",\"isReconnect\":true}," +
            "\"ref\":\"1\"," +
            "\"join_ref\":\"1\"" +
            "}";

        await ws.SendAsync(Encoding.UTF8.GetBytes(joinFrame), WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);
        _ = await ReceiveWebSocketTextAsync(ws);

        var createAlterFrame =
            "{" +
            "\"topic\":\"" + topic + "\"," +
            "\"event\":\"endpoint\"," +
            "\"payload\":{" +
            "\"method\":\"POST\"," +
            "\"path\":\"/api/systems/me/alters\"," +
            "\"body\":{\"name\":\"FrontPushAlter\"}" +
            "}," +
            "\"ref\":\"2\"," +
            "\"join_ref\":\"1\"" +
            "}";

        await ws.SendAsync(Encoding.UTF8.GetBytes(createAlterFrame), WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);
        var createAlterReply = await ReceiveWebSocketTextAsync(ws);
        using var createAlterDoc = JsonDocument.Parse(createAlterReply);
        var createPayload = createAlterDoc.RootElement.GetProperty("payload").GetProperty("response");
        await Assert.That(createPayload.GetProperty("status").GetInt32() == 201).IsTrue().Because($"Expected alter create 201. Payload: {createAlterReply}");

        var createBody = createPayload.GetProperty("body").GetString() ?? string.Empty;
        using var createBodyDoc = JsonDocument.Parse(createBody);
        var createdAlterId = createBodyDoc.RootElement.GetProperty("data").GetProperty("alterId").GetInt32();

        var startFrontFrame =
            "{" +
            "\"topic\":\"" + topic + "\"," +
            "\"event\":\"endpoint\"," +
            "\"payload\":{" +
            "\"method\":\"POST\"," +
            "\"path\":\"/api/systems/me/front/start\"," +
            "\"body\":{\"alterId\":" + createdAlterId + "}" +
            "}," +
            "\"ref\":\"3\"," +
            "\"join_ref\":\"1\"" +
            "}";

        await ws.SendAsync(Encoding.UTF8.GetBytes(startFrontFrame), WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);

        string? endpointAck = null;
        string? frontingChangedPush = null;

        for (var i = 0; i < 4 && (endpointAck is null || frontingChangedPush is null); i++)
        {
            var frame = await ReceiveWebSocketTextAsync(ws);
            using var doc = JsonDocument.Parse(frame);
            var root = doc.RootElement;

            var eventName = root.ValueKind == JsonValueKind.Array
                ? root[3].GetString() ?? string.Empty
                : root.GetProperty("event").GetString() ?? string.Empty;

            if (string.Equals(eventName, "phx_reply", StringComparison.Ordinal))
            {
                endpointAck = frame;
                continue;
            }

            if (string.Equals(eventName, "fronting_changed", StringComparison.Ordinal))
            {
                frontingChangedPush = frame;
            }
        }

        await Assert.That(endpointAck is not null).IsTrue().Because("Expected endpoint ack (phx_reply) for front start call.");
        await Assert.That(frontingChangedPush is not null).IsTrue().Because("Expected fronting_changed push after front start call.");

        using var pushDoc = JsonDocument.Parse(frontingChangedPush!);
        var pushPayload = pushDoc.RootElement.GetProperty("payload");
        await Assert.That(pushPayload.TryGetProperty("fronts", out var frontsEl) && frontsEl.ValueKind == JsonValueKind.Array).IsTrue().Because($"Expected fronting_changed payload to include fronts array. Payload: {frontingChangedPush}");

        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "test done", CancellationToken.None);
    }

    [Test, ApiIntegration]
    public async Task Api_UserSocketEndpoint_PushesAlterTagAndFieldsEvents_AfterEndpointWrites()
    {
        await using var factory = new InterfoldWebApplicationFactory()
            .WithConfiguration("OCTOCON_PERSISTENCE", "inmemory")
            .WithConfiguration("OCTOCON_DEEPLINK_ADDRESS", "octocon://app")
            .WithConfiguration("OCTOCON_REGION", "nam")
            .WithConfiguration("OCTOCON_AUTH_CHALLENGE_ENABLED", "false");

        var wsClient = factory.Server.CreateWebSocketClient();
        const string socketToken = "integration-test-token";
        const string topic = "system:sys-domain-fanout";
        var uri = new Uri(WebSocketBasePath(factory.Server), $"api/socket/websocket?token={socketToken}");
        using var ws = await wsClient.ConnectAsync(uri, CancellationToken.None);

        var joinFrame =
            "{" +
            "\"topic\":\"" + topic + "\"," +
            "\"event\":\"phx_join\"," +
            "\"payload\":{\"token\":\"" + socketToken + "\",\"isReconnect\":true}," +
            "\"ref\":\"1\"," +
            "\"join_ref\":\"1\"" +
            "}";

        await ws.SendAsync(Encoding.UTF8.GetBytes(joinFrame), WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);
        _ = await ReceiveWebSocketTextAsync(ws);

        var createAlterFrame =
            "{" +
            "\"topic\":\"" + topic + "\"," +
            "\"event\":\"endpoint\"," +
            "\"payload\":{" +
            "\"method\":\"POST\"," +
            "\"path\":\"/api/systems/me/alters\"," +
            "\"body\":{\"name\":\"DomainFanoutAlter\"}" +
            "}," +
            "\"ref\":\"2\"," +
            "\"join_ref\":\"1\"" +
            "}";

        var alterResult = await SendEndpointAndCaptureAsync(createAlterFrame, "alter_created");
        await Assert.That(alterResult.ack is not null).IsTrue().Because("Expected endpoint ack for alter create.");
        await Assert.That(alterResult.push is not null).IsTrue().Because("Expected alter_created push after alter create.");

        var createTagFrame =
            "{" +
            "\"topic\":\"" + topic + "\"," +
            "\"event\":\"endpoint\"," +
            "\"payload\":{" +
            "\"method\":\"POST\"," +
            "\"path\":\"/api/systems/me/tags\"," +
            "\"body\":{\"name\":\"DomainFanoutTag\"}" +
            "}," +
            "\"ref\":\"3\"," +
            "\"join_ref\":\"1\"" +
            "}";

        var tagResult = await SendEndpointAndCaptureAsync(createTagFrame, "tag_created");
        await Assert.That(tagResult.ack is not null).IsTrue().Because("Expected endpoint ack for tag create.");
        await Assert.That(tagResult.push is not null).IsTrue().Because("Expected tag_created push after tag create.");

        var createFieldFrame =
            "{" +
            "\"topic\":\"" + topic + "\"," +
            "\"event\":\"endpoint\"," +
            "\"payload\":{" +
            "\"method\":\"POST\"," +
            "\"path\":\"/api/settings/fields\"," +
            "\"body\":{\"name\":\"DomainFanoutField\",\"type\":\"text\",\"security_level\":\"private\",\"locked\":false}" +
            "}," +
            "\"ref\":\"4\"," +
            "\"join_ref\":\"1\"" +
            "}";

        var fieldResult = await SendEndpointAndCaptureAsync(createFieldFrame, "fields_updated");
        await Assert.That(fieldResult.ack is not null).IsTrue().Because("Expected endpoint ack for field create.");
        await Assert.That(fieldResult.push is not null).IsTrue().Because("Expected fields_updated push after field create.");

        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "test done", CancellationToken.None);
        return;

        static string EventNameFromFrame(JsonElement root)
        {
            return root.ValueKind == JsonValueKind.Array
                ? root[3].GetString() ?? string.Empty
                : root.GetProperty("event").GetString() ?? string.Empty;
        }

        async Task<(string? ack, string? push)> SendEndpointAndCaptureAsync(string frame, string expectedPushEvent)
        {
            await ws.SendAsync(Encoding.UTF8.GetBytes(frame), WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);
            string? ack = null;
            string? push = null;

            for (var i = 0; i < 5 && (ack is null || push is null); i++)
            {
                var received = await ReceiveWebSocketTextAsync(ws);
                using var doc = JsonDocument.Parse(received);
                var name = EventNameFromFrame(doc.RootElement);

                if (string.Equals(name, "phx_reply", StringComparison.Ordinal))
                {
                    ack = received;
                    continue;
                }

                if (string.Equals(name, expectedPushEvent, StringComparison.Ordinal))
                {
                    push = received;
                }
            }

            return (ack, push);
        }
    }

    [Test, ApiIntegration]
    public async Task Api_UserSocketEndpoint_PushesFriendRequestReceived_ToRecipientSystem()
    {
        await using var factory = new InterfoldWebApplicationFactory()
            .WithConfiguration("OCTOCON_PERSISTENCE", "inmemory")
            .WithConfiguration("OCTOCON_DEEPLINK_ADDRESS", "octocon://app")
            .WithConfiguration("OCTOCON_REGION", "nam")
            .WithConfiguration("OCTOCON_AUTH_CHALLENGE_ENABLED", "false");

        var wsClientFactory = factory.Server.CreateWebSocketClient();
        const string socketToken = "integration-test-token";
        const string senderSystemId = "sys-friend-sender";
        const string recipientSystemId = "sys-friend-recipient";

        var socketUri = new Uri(WebSocketBasePath(factory.Server), $"api/socket/websocket?token={socketToken}");
        using var senderWs = await wsClientFactory.ConnectAsync(socketUri, CancellationToken.None);
        using var recipientWs = await wsClientFactory.ConnectAsync(socketUri, CancellationToken.None);

        await JoinTopicAsync(senderWs, $"system:{senderSystemId}", socketToken);
        await JoinTopicAsync(recipientWs, $"system:{recipientSystemId}", socketToken);

        var sendRequestFrame =
            "{" +
            "\"topic\":\"system:" + senderSystemId + "\"," +
            "\"event\":\"endpoint\"," +
            "\"payload\":{" +
            "\"method\":\"PUT\"," +
            "\"path\":\"/api/friend-requests/" + recipientSystemId + "\"," +
            "\"body\":{}" +
            "}," +
            "\"ref\":\"2\"," +
            "\"join_ref\":\"1\"" +
            "}";

        await senderWs.SendAsync(Encoding.UTF8.GetBytes(sendRequestFrame), WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);

        string? senderAck = null;
        string? senderPush = null;
        for (var i = 0; i < 5 && (senderAck is null || senderPush is null); i++)
        {
            var frame = await ReceiveWebSocketTextAsync(senderWs);
            var eventName = GetEventName(frame);
            if (string.Equals(eventName, "phx_reply", StringComparison.Ordinal))
            {
                senderAck = frame;
                continue;
            }

            if (string.Equals(eventName, "friend_request_sent", StringComparison.Ordinal))
            {
                senderPush = frame;
            }
        }

        await Assert.That(senderAck is not null).IsTrue().Because("Expected endpoint ack on sender socket for friend request send.");
        await Assert.That(senderPush is not null).IsTrue().Because("Expected friend_request_sent push on sender socket.");

        var recipientReadTask = ReceiveWebSocketTextAsync(recipientWs);
        var completed = await Task.WhenAny(recipientReadTask, Task.Delay(TimeSpan.FromSeconds(5)));
        await Assert.That(ReferenceEquals(completed, recipientReadTask)).IsTrue().Because("Timed out waiting for recipient-side friend_request_received push.");

        var recipientFrame = await recipientReadTask;
        await Assert.That(string.Equals(GetEventName(recipientFrame), "friend_request_received", StringComparison.Ordinal)).IsTrue().Because($"Expected recipient push event friend_request_received. Payload: {recipientFrame}");

        await senderWs.CloseAsync(WebSocketCloseStatus.NormalClosure, "test done", CancellationToken.None);
        await recipientWs.CloseAsync(WebSocketCloseStatus.NormalClosure, "test done", CancellationToken.None);
    }

    [Test, ApiIntegration]
    public async Task Api_UserSocketEndpoint_PushesFriendRequestAccepted_ToActorAndRecipient()
    {
        await using var factory = new InterfoldWebApplicationFactory()
            .WithConfiguration("OCTOCON_PERSISTENCE", "inmemory")
            .WithConfiguration("OCTOCON_DEEPLINK_ADDRESS", "octocon://app")
            .WithConfiguration("OCTOCON_REGION", "nam")
            .WithConfiguration("OCTOCON_AUTH_CHALLENGE_ENABLED", "false");

        var wsClientFactory = factory.Server.CreateWebSocketClient();
        const string socketToken = "integration-test-token";
        const string senderSystemId = "sys-accept-sender";
        const string recipientSystemId = "sys-accept-recipient";

        var socketUri = new Uri(WebSocketBasePath(factory.Server), $"api/socket/websocket?token={socketToken}");
        using var senderWs = await wsClientFactory.ConnectAsync(socketUri, CancellationToken.None);
        using var recipientWs = await wsClientFactory.ConnectAsync(socketUri, CancellationToken.None);

        await JoinTopicAsync(senderWs, $"system:{senderSystemId}", socketToken);
        await JoinTopicAsync(recipientWs, $"system:{recipientSystemId}", socketToken);

        var sendRequestFrame =
            "{" +
            "\"topic\":\"system:" + senderSystemId + "\"," +
            "\"event\":\"endpoint\"," +
            "\"payload\":{" +
            "\"method\":\"PUT\"," +
            "\"path\":\"/api/friend-requests/" + recipientSystemId + "\"," +
            "\"body\":{}" +
            "}," +
            "\"ref\":\"2\"," +
            "\"join_ref\":\"1\"" +
            "}";

        await senderWs.SendAsync(Encoding.UTF8.GetBytes(sendRequestFrame), WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);

        _ = await ReceiveWebSocketTextAsync(senderWs);
        _ = await ReceiveWebSocketTextAsync(senderWs);
        var recipientEvent = await ReceiveWebSocketTextAsync(recipientWs);
        await Assert.That(string.Equals(GetEventName(recipientEvent), "friend_request_received", StringComparison.Ordinal)).IsTrue().Because("Expected friend_request_received");

        var acceptFrame =
            "{" +
            "\"topic\":\"system:" + recipientSystemId + "\"," +
            "\"event\":\"endpoint\"," +
            "\"payload\":{" +
            "\"method\":\"POST\"," +
            "\"path\":\"/api/friend-requests/" + senderSystemId + "/accept\"," +
            "\"body\":{}" +
            "}," +
            "\"ref\":\"3\"," +
            "\"join_ref\":\"1\"" +
            "}";

        await recipientWs.SendAsync(Encoding.UTF8.GetBytes(acceptFrame), WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);

        string? recipientAck = null;
        string? recipientFriendAdded = null;
        for (var i = 0; i < 5 && (recipientAck is null || recipientFriendAdded is null); i++)
        {
            var frame = await ReceiveWebSocketTextAsync(recipientWs);
            var eventName = GetEventName(frame);
            if (string.Equals(eventName, "phx_reply", StringComparison.Ordinal))
            {
                recipientAck = frame;
            }
            else if (string.Equals(eventName, "friend_added", StringComparison.Ordinal))
            {
                recipientFriendAdded = frame;
            }
        }

        await Assert.That(recipientAck is not null).IsTrue().Because("Expected endpoint ack on recipient socket for accept.");
        await Assert.That(recipientFriendAdded is not null).IsTrue().Because("Expected friend_added push on recipient socket after accept.");

        string? senderFriendAdded = null;
        string? senderRequestCleared = null;
        for (var i = 0; i < 5 && (senderFriendAdded is null || senderRequestCleared is null); i++)
        {
            var senderTask = ReceiveWebSocketTextAsync(senderWs);
            var senderCompleted = await Task.WhenAny(senderTask, Task.Delay(TimeSpan.FromSeconds(5)));
            await Assert.That(ReferenceEquals(senderCompleted, senderTask)).IsTrue().Because("Timed out waiting for sender-side event after accept.");
            var senderFrame = await senderTask;
            var senderEventName = GetEventName(senderFrame);
            if (string.Equals(senderEventName, "friend_added", StringComparison.Ordinal))
                senderFriendAdded = senderFrame;
            else if (string.Equals(senderEventName, "friend_request_removed", StringComparison.Ordinal))
                senderRequestCleared = senderFrame;
        }

        await Assert.That(senderFriendAdded is not null).IsTrue().Because("Expected friend_added push on sender socket after accept.");
        await Assert.That(senderRequestCleared is not null).IsTrue().Because("Expected friend_request_removed push on sender socket after accept (outgoing request cleanup).");

        await senderWs.CloseAsync(WebSocketCloseStatus.NormalClosure, "test done", CancellationToken.None);
        await recipientWs.CloseAsync(WebSocketCloseStatus.NormalClosure, "test done", CancellationToken.None);
    }

    [Test, ApiIntegration]
    public async Task Api_UserSocketEndpoint_PushesFriendRequestRejected_ToActorAndRecipient()
    {
        await using var factory = new InterfoldWebApplicationFactory()
            .WithConfiguration("OCTOCON_PERSISTENCE", "inmemory")
            .WithConfiguration("OCTOCON_DEEPLINK_ADDRESS", "octocon://app")
            .WithConfiguration("OCTOCON_REGION", "nam")
            .WithConfiguration("OCTOCON_AUTH_CHALLENGE_ENABLED", "false");

        var wsClientFactory = factory.Server.CreateWebSocketClient();
        const string socketToken = "integration-test-token";
        const string senderSystemId = "sys-reject-sender";
        const string recipientSystemId = "sys-reject-recipient";

        var socketUri = new Uri(WebSocketBasePath(factory.Server), $"api/socket/websocket?token={socketToken}");
        using var senderWs = await wsClientFactory.ConnectAsync(socketUri, CancellationToken.None);
        using var recipientWs = await wsClientFactory.ConnectAsync(socketUri, CancellationToken.None);

        await JoinTopicAsync(senderWs, $"system:{senderSystemId}", socketToken);
        await JoinTopicAsync(recipientWs, $"system:{recipientSystemId}", socketToken);

        var sendRequestFrame =
            "{" +
            "\"topic\":\"system:" + senderSystemId + "\"," +
            "\"event\":\"endpoint\"," +
            "\"payload\":{" +
            "\"method\":\"PUT\"," +
            "\"path\":\"/api/friend-requests/" + recipientSystemId + "\"," +
            "\"body\":{}" +
            "}," +
            "\"ref\":\"2\"," +
            "\"join_ref\":\"1\"" +
            "}";

        await senderWs.SendAsync(Encoding.UTF8.GetBytes(sendRequestFrame), WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);

        _ = await ReceiveWebSocketTextAsync(senderWs);
        _ = await ReceiveWebSocketTextAsync(senderWs);
        var recipientEvent = await ReceiveWebSocketTextAsync(recipientWs);
        await Assert.That(string.Equals(GetEventName(recipientEvent), "friend_request_received", StringComparison.Ordinal)).IsTrue().Because("Expected friend_request_received");

        var rejectFrame =
            "{" +
            "\"topic\":\"system:" + recipientSystemId + "\"," +
            "\"event\":\"endpoint\"," +
            "\"payload\":{" +
            "\"method\":\"DELETE\"," +
            "\"path\":\"/api/friend-requests/" + senderSystemId + "\"," +
            "\"body\":{}" +
            "}," +
            "\"ref\":\"3\"," +
            "\"join_ref\":\"1\"" +
            "}";

        await recipientWs.SendAsync(Encoding.UTF8.GetBytes(rejectFrame), WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);

        string? recipientAck = null;
        string? recipientRemoved = null;
        for (var i = 0; i < 5 && (recipientAck is null || recipientRemoved is null); i++)
        {
            var frame = await ReceiveWebSocketTextAsync(recipientWs);
            var eventName = GetEventName(frame);
            if (string.Equals(eventName, "phx_reply", StringComparison.Ordinal))
            {
                recipientAck = frame;
            }
            else if (string.Equals(eventName, "friend_request_removed", StringComparison.Ordinal))
            {
                recipientRemoved = frame;
            }
        }

        await Assert.That(recipientAck is not null).IsTrue().Because("Expected endpoint ack on recipient socket for reject.");
        await Assert.That(recipientRemoved is not null).IsTrue().Because("Expected friend_request_removed push on recipient socket after reject.");

        var senderReadTask = ReceiveWebSocketTextAsync(senderWs);
        var completed = await Task.WhenAny(senderReadTask, Task.Delay(TimeSpan.FromSeconds(5)));
        await Assert.That(ReferenceEquals(completed, senderReadTask)).IsTrue().Because("Timed out waiting for sender-side friend_request_removed push.");

        var senderRemoved = await senderReadTask;
        await Assert.That(string.Equals(GetEventName(senderRemoved), "friend_request_removed", StringComparison.Ordinal)).IsTrue().Because($"Expected sender push event friend_request_removed. Event: {GetEventName(senderRemoved)}");

        await senderWs.CloseAsync(WebSocketCloseStatus.NormalClosure, "test done", CancellationToken.None);
        await recipientWs.CloseAsync(WebSocketCloseStatus.NormalClosure, "test done", CancellationToken.None);
    }

    [Test, ApiIntegration]
    public async Task Api_UserSocketEndpoint_PushesFriendRequestCancelled_ToActorAndRecipient()
    {
        await using var factory = new InterfoldWebApplicationFactory()
            .WithConfiguration("OCTOCON_PERSISTENCE", "inmemory")
            .WithConfiguration("OCTOCON_DEEPLINK_ADDRESS", "octocon://app")
            .WithConfiguration("OCTOCON_REGION", "nam")
            .WithConfiguration("OCTOCON_AUTH_CHALLENGE_ENABLED", "false");

        var wsClientFactory = factory.Server.CreateWebSocketClient();
        const string socketToken = "integration-test-token";
        const string senderSystemId = "sys-cancel-sender";
        const string recipientSystemId = "sys-cancel-recipient";

        var socketUri = new Uri(WebSocketBasePath(factory.Server), $"api/socket/websocket?token={socketToken}");
        using var senderWs = await wsClientFactory.ConnectAsync(socketUri, CancellationToken.None);
        using var recipientWs = await wsClientFactory.ConnectAsync(socketUri, CancellationToken.None);

        await JoinTopicAsync(senderWs, $"system:{senderSystemId}", socketToken);
        await JoinTopicAsync(recipientWs, $"system:{recipientSystemId}", socketToken);

        var sendRequestFrame =
            "{" +
            "\"topic\":\"system:" + senderSystemId + "\"," +
            "\"event\":\"endpoint\"," +
            "\"payload\":{" +
            "\"method\":\"PUT\"," +
            "\"path\":\"/api/friend-requests/" + recipientSystemId + "\"," +
            "\"body\":{}" +
            "}," +
            "\"ref\":\"2\"," +
            "\"join_ref\":\"1\"" +
            "}";

        await senderWs.SendAsync(Encoding.UTF8.GetBytes(sendRequestFrame), WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);

        _ = await ReceiveWebSocketTextAsync(senderWs);
        _ = await ReceiveWebSocketTextAsync(senderWs);

        var recipientReceived = await ReceiveWebSocketTextAsync(recipientWs);
        await Assert.That(string.Equals(GetEventName(recipientReceived), "friend_request_received", StringComparison.Ordinal)).IsTrue().Because("Expected friend_request_received before cancel.");

        var cancelFrame =
            "{" +
            "\"topic\":\"system:" + senderSystemId + "\"," +
            "\"event\":\"endpoint\"," +
            "\"payload\":{" +
            "\"method\":\"DELETE\"," +
            "\"path\":\"/api/friend-requests/" + recipientSystemId + "\"," +
            "\"body\":{}" +
            "}," +
            "\"ref\":\"3\"," +
            "\"join_ref\":\"1\"" +
            "}";

        await senderWs.SendAsync(Encoding.UTF8.GetBytes(cancelFrame), WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);

        string? senderAck = null;
        string? senderRemoved = null;
        for (var i = 0; i < 5 && (senderAck is null || senderRemoved is null); i++)
        {
            var frame = await ReceiveWebSocketTextAsync(senderWs);
            var eventName = GetEventName(frame);
            if (string.Equals(eventName, "phx_reply", StringComparison.Ordinal))
            {
                senderAck = frame;
            }
            else if (string.Equals(eventName, "friend_request_removed", StringComparison.Ordinal))
            {
                senderRemoved = frame;
            }
        }

        await Assert.That(senderAck is not null).IsTrue().Because("Expected endpoint ack on sender socket for cancel.");
        await Assert.That(senderRemoved is not null).IsTrue().Because("Expected friend_request_removed push on sender socket after cancel.");

        var recipientReadTask = ReceiveWebSocketTextAsync(recipientWs);
        var completed = await Task.WhenAny(recipientReadTask, Task.Delay(TimeSpan.FromSeconds(5)));
        await Assert.That(ReferenceEquals(completed, recipientReadTask)).IsTrue().Because("Timed out waiting for recipient-side friend_request_removed push.");

        var recipientRemoved = await recipientReadTask;
        await Assert.That(string.Equals(GetEventName(recipientRemoved), "friend_request_removed", StringComparison.Ordinal)).IsTrue().Because($"Expected recipient push event friend_request_removed. Event: {GetEventName(recipientRemoved)}");

        await senderWs.CloseAsync(WebSocketCloseStatus.NormalClosure, "test done", CancellationToken.None);
        await recipientWs.CloseAsync(WebSocketCloseStatus.NormalClosure, "test done", CancellationToken.None);
    }

    [Test, ApiIntegration]
    public async Task Api_UserSocketEndpoint_PushesFriendRemoved_ToActorAndRecipient()
    {
        await using var factory = new InterfoldWebApplicationFactory()
            .WithConfiguration("OCTOCON_PERSISTENCE", "inmemory")
            .WithConfiguration("OCTOCON_DEEPLINK_ADDRESS", "octocon://app")
            .WithConfiguration("OCTOCON_REGION", "nam")
            .WithConfiguration("OCTOCON_AUTH_CHALLENGE_ENABLED", "false");

        var wsClientFactory = factory.Server.CreateWebSocketClient();
        const string socketToken = "integration-test-token";
        const string senderSystemId = "sys-remove-sender";
        const string recipientSystemId = "sys-remove-recipient";

        var socketUri = new Uri(WebSocketBasePath(factory.Server), $"api/socket/websocket?token={socketToken}");
        using var senderWs = await wsClientFactory.ConnectAsync(socketUri, CancellationToken.None);
        using var recipientWs = await wsClientFactory.ConnectAsync(socketUri, CancellationToken.None);

        await JoinTopicAsync(senderWs, $"system:{senderSystemId}", socketToken);
        await JoinTopicAsync(recipientWs, $"system:{recipientSystemId}", socketToken);

        var sendRequestFrame =
            "{" +
            "\"topic\":\"system:" + senderSystemId + "\"," +
            "\"event\":\"endpoint\"," +
            "\"payload\":{" +
            "\"method\":\"PUT\"," +
            "\"path\":\"/api/friend-requests/" + recipientSystemId + "\"," +
            "\"body\":{}" +
            "}," +
            "\"ref\":\"2\"," +
            "\"join_ref\":\"1\"" +
            "}";

        await senderWs.SendAsync(Encoding.UTF8.GetBytes(sendRequestFrame), WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);

        _ = await ReceiveWebSocketTextAsync(senderWs);
        _ = await ReceiveWebSocketTextAsync(senderWs);

        var recipientReceived = await ReceiveWebSocketTextAsync(recipientWs);
        await Assert.That(string.Equals(GetEventName(recipientReceived), "friend_request_received", StringComparison.Ordinal)).IsTrue().Because("Expected friend_request_received before accept.");

        var acceptFrame =
            "{" +
            "\"topic\":\"system:" + recipientSystemId + "\"," +
            "\"event\":\"endpoint\"," +
            "\"payload\":{" +
            "\"method\":\"POST\"," +
            "\"path\":\"/api/friend-requests/" + senderSystemId + "/accept\"," +
            "\"body\":{}" +
            "}," +
            "\"ref\":\"3\"," +
            "\"join_ref\":\"1\"" +
            "}";

        await recipientWs.SendAsync(Encoding.UTF8.GetBytes(acceptFrame), WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);

        string? recipientAcceptAck = null;
        string? recipientAdded = null;
        for (var i = 0; i < 5 && (recipientAcceptAck is null || recipientAdded is null); i++)
        {
            var frame = await ReceiveWebSocketTextAsync(recipientWs);
            var eventName = GetEventName(frame);
            if (string.Equals(eventName, "phx_reply", StringComparison.Ordinal))
            {
                recipientAcceptAck = frame;
            }
            else if (string.Equals(eventName, "friend_added", StringComparison.Ordinal))
            {
                recipientAdded = frame;
            }
        }

        await Assert.That(recipientAcceptAck is not null).IsTrue().Because("Expected endpoint ack on recipient socket for accept.");
        await Assert.That(recipientAdded is not null).IsTrue().Because("Expected friend_added push on recipient socket after accept.");

        _ = await ReceiveWebSocketTextAsync(senderWs);
        _ = await ReceiveWebSocketTextAsync(senderWs);

        var removeFrame =
            "{" +
            "\"topic\":\"system:" + senderSystemId + "\"," +
            "\"event\":\"endpoint\"," +
            "\"payload\":{" +
            "\"method\":\"DELETE\"," +
            "\"path\":\"/api/friends/" + recipientSystemId + "\"," +
            "\"body\":{}" +
            "}," +
            "\"ref\":\"4\"," +
            "\"join_ref\":\"1\"" +
            "}";

        await senderWs.SendAsync(Encoding.UTF8.GetBytes(removeFrame), WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);

        string? senderRemoveAck = null;
        string? senderRemoved = null;
        for (var i = 0; i < 5 && (senderRemoveAck is null || senderRemoved is null); i++)
        {
            var frame = await ReceiveWebSocketTextAsync(senderWs);
            var eventName = GetEventName(frame);
            if (string.Equals(eventName, "phx_reply", StringComparison.Ordinal))
            {
                senderRemoveAck = frame;
            }
            else if (string.Equals(eventName, "friend_removed", StringComparison.Ordinal))
            {
                senderRemoved = frame;
            }
        }

        await Assert.That(senderRemoveAck is not null).IsTrue().Because("Expected endpoint ack on sender socket for remove.");
        await Assert.That(senderRemoved is not null).IsTrue().Because("Expected friend_removed push on sender socket after remove.");

        var recipientReadTask = ReceiveWebSocketTextAsync(recipientWs);
        var completed = await Task.WhenAny(recipientReadTask, Task.Delay(TimeSpan.FromSeconds(5)));
        await Assert.That(ReferenceEquals(completed, recipientReadTask)).IsTrue().Because("Timed out waiting for recipient-side friend_removed push.");

        var recipientRemoved = await recipientReadTask;
        await Assert.That(string.Equals(GetEventName(recipientRemoved), "friend_removed", StringComparison.Ordinal)).IsTrue().Because($"Expected recipient push event friend_removed. Event: {GetEventName(recipientRemoved)}");

        await senderWs.CloseAsync(WebSocketCloseStatus.NormalClosure, "test done", CancellationToken.None);
        await recipientWs.CloseAsync(WebSocketCloseStatus.NormalClosure, "test done", CancellationToken.None);
    }

    [Test, ApiIntegration]
    public async Task Api_UserSocketEndpoint_PushesFriendTrustedAndUntrusted_ToActor()
    {
        await using var factory = new InterfoldWebApplicationFactory()
            .WithConfiguration("OCTOCON_PERSISTENCE", "inmemory")
            .WithConfiguration("OCTOCON_DEEPLINK_ADDRESS", "octocon://app")
            .WithConfiguration("OCTOCON_REGION", "nam")
            .WithConfiguration("OCTOCON_AUTH_CHALLENGE_ENABLED", "false");

        var wsClientFactory = factory.Server.CreateWebSocketClient();
        const string socketToken = "integration-test-token";
        const string senderSystemId = "sys-trust-sender";
        const string recipientSystemId = "sys-trust-recipient";

        var socketUri = new Uri(WebSocketBasePath(factory.Server), $"api/socket/websocket?token={socketToken}");
        using var senderWs = await wsClientFactory.ConnectAsync(socketUri, CancellationToken.None);
        using var recipientWs = await wsClientFactory.ConnectAsync(socketUri, CancellationToken.None);

        await JoinTopicAsync(senderWs, $"system:{senderSystemId}", socketToken);
        await JoinTopicAsync(recipientWs, $"system:{recipientSystemId}", socketToken);

        var sendRequestFrame =
            "{" +
            "\"topic\":\"system:" + senderSystemId + "\"," +
            "\"event\":\"endpoint\"," +
            "\"payload\":{" +
            "\"method\":\"PUT\"," +
            "\"path\":\"/api/friend-requests/" + recipientSystemId + "\"," +
            "\"body\":{}" +
            "}," +
            "\"ref\":\"2\"," +
            "\"join_ref\":\"1\"" +
            "}";

        await senderWs.SendAsync(Encoding.UTF8.GetBytes(sendRequestFrame), WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);

        _ = await ReceiveWebSocketTextAsync(senderWs);
        _ = await ReceiveWebSocketTextAsync(senderWs);

        var recipientReceived = await ReceiveWebSocketTextAsync(recipientWs);
        await Assert.That(string.Equals(GetEventName(recipientReceived), "friend_request_received", StringComparison.Ordinal)).IsTrue().Because("Expected friend_request_received before accept.");

        var acceptFrame =
            "{" +
            "\"topic\":\"system:" + recipientSystemId + "\"," +
            "\"event\":\"endpoint\"," +
            "\"payload\":{" +
            "\"method\":\"POST\"," +
            "\"path\":\"/api/friend-requests/" + senderSystemId + "/accept\"," +
            "\"body\":{}" +
            "}," +
            "\"ref\":\"3\"," +
            "\"join_ref\":\"1\"" +
            "}";

        await recipientWs.SendAsync(Encoding.UTF8.GetBytes(acceptFrame), WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);

        for (var i = 0; i < 5; i++)
        {
            var frame = await ReceiveWebSocketTextAsync(recipientWs);
            if (string.Equals(GetEventName(frame), "friend_added", StringComparison.Ordinal))
                break;
        }

        _ = await ReceiveWebSocketTextAsync(senderWs);
        _ = await ReceiveWebSocketTextAsync(senderWs);

        var trustFrame =
            "{" +
            "\"topic\":\"system:" + senderSystemId + "\"," +
            "\"event\":\"endpoint\"," +
            "\"payload\":{" +
            "\"method\":\"POST\"," +
            "\"path\":\"/api/friends/" + recipientSystemId + "/trust\"," +
            "\"body\":{}" +
            "}," +
            "\"ref\":\"4\"," +
            "\"join_ref\":\"1\"" +
            "}";

        await senderWs.SendAsync(Encoding.UTF8.GetBytes(trustFrame), WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);

        string? trustAck = null;
        string? trustPush = null;
        for (var i = 0; i < 5 && (trustAck is null || trustPush is null); i++)
        {
            var frame = await ReceiveWebSocketTextAsync(senderWs);
            var eventName = GetEventName(frame);
            if (string.Equals(eventName, "phx_reply", StringComparison.Ordinal))
            {
                trustAck = frame;
            }
            else if (string.Equals(eventName, "friend_trusted", StringComparison.Ordinal))
            {
                trustPush = frame;
            }
        }

        await Assert.That(trustAck is not null).IsTrue().Because("Expected endpoint ack on sender socket for trust.");
        await Assert.That(trustPush is not null).IsTrue().Because("Expected friend_trusted push on sender socket after trust.");

        var untrustFrame =
            "{" +
            "\"topic\":\"system:" + senderSystemId + "\"," +
            "\"event\":\"endpoint\"," +
            "\"payload\":{" +
            "\"method\":\"POST\"," +
            "\"path\":\"/api/friends/" + recipientSystemId + "/untrust\"," +
            "\"body\":{}" +
            "}," +
            "\"ref\":\"5\"," +
            "\"join_ref\":\"1\"" +
            "}";

        await senderWs.SendAsync(Encoding.UTF8.GetBytes(untrustFrame), WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);

        string? untrustAck = null;
        string? untrustPush = null;
        for (var i = 0; i < 5 && (untrustAck is null || untrustPush is null); i++)
        {
            var frame = await ReceiveWebSocketTextAsync(senderWs);
            var eventName = GetEventName(frame);
            if (string.Equals(eventName, "phx_reply", StringComparison.Ordinal))
            {
                untrustAck = frame;
            }
            else if (string.Equals(eventName, "friend_untrusted", StringComparison.Ordinal))
            {
                untrustPush = frame;
            }
        }

        await Assert.That(untrustAck is not null).IsTrue().Because("Expected endpoint ack on sender socket for untrust.");
        await Assert.That(untrustPush is not null).IsTrue().Because("Expected friend_untrusted push on sender socket after untrust.");

        await senderWs.CloseAsync(WebSocketCloseStatus.NormalClosure, "test done", CancellationToken.None);
        await recipientWs.CloseAsync(WebSocketCloseStatus.NormalClosure, "test done", CancellationToken.None);
    }

    [Test, ApiIntegration]
    public async Task Api_UserSocketEndpoint_PushesFriendAdded_OnMutualFriendRequest()
    {
        await using var factory = new InterfoldWebApplicationFactory()
            .WithConfiguration("OCTOCON_PERSISTENCE", "inmemory")
            .WithConfiguration("OCTOCON_DEEPLINK_ADDRESS", "octocon://app")
            .WithConfiguration("OCTOCON_REGION", "nam")
            .WithConfiguration("OCTOCON_AUTH_CHALLENGE_ENABLED", "false");

        var wsClientFactory = factory.Server.CreateWebSocketClient();
        const string socketToken = "integration-test-token";
        const string systemAId = "sys-mutual-a";
        const string systemBId = "sys-mutual-b";

        var socketUri = new Uri(WebSocketBasePath(factory.Server), $"api/socket/websocket?token={socketToken}");
        using var wsA = await wsClientFactory.ConnectAsync(socketUri, CancellationToken.None);
        using var wsB = await wsClientFactory.ConnectAsync(socketUri, CancellationToken.None);

        await JoinTopicAsync(wsA, $"system:{systemAId}", socketToken);
        await JoinTopicAsync(wsB, $"system:{systemBId}", socketToken);

        var sendAFrame =
            "{" +
            "\"topic\":\"system:" + systemAId + "\"," +
            "\"event\":\"endpoint\"," +
            "\"payload\":{" +
            "\"method\":\"PUT\"," +
            "\"path\":\"/api/friend-requests/" + systemBId + "\"," +
            "\"body\":{}" +
            "}," +
            "\"ref\":\"2\"," +
            "\"join_ref\":\"1\"" +
            "}";

        await wsA.SendAsync(Encoding.UTF8.GetBytes(sendAFrame), WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);
        _ = await ReceiveWebSocketTextAsync(wsA);
        _ = await ReceiveWebSocketTextAsync(wsA);
        _ = await ReceiveWebSocketTextAsync(wsB);

        var sendBFrame =
            "{" +
            "\"topic\":\"system:" + systemBId + "\"," +
            "\"event\":\"endpoint\"," +
            "\"payload\":{" +
            "\"method\":\"PUT\"," +
            "\"path\":\"/api/friend-requests/" + systemAId + "\"," +
            "\"body\":{}" +
            "}," +
            "\"ref\":\"3\"," +
            "\"join_ref\":\"1\"" +
            "}";

        await wsB.SendAsync(Encoding.UTF8.GetBytes(sendBFrame), WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);

        string? bAck = null;
        string? bFriendAdded = null;
        for (var i = 0; i < 5 && (bAck is null || bFriendAdded is null); i++)
        {
            var bTask = ReceiveWebSocketTextAsync(wsB);
            var bCompleted = await Task.WhenAny(bTask, Task.Delay(TimeSpan.FromSeconds(5)));
            await Assert.That(ReferenceEquals(bCompleted, bTask)).IsTrue().Because("Timed out waiting for B-side event after mutual send.");
            var bFrame = await bTask;
            var bEventName = GetEventName(bFrame);
            if (string.Equals(bEventName, "phx_reply", StringComparison.Ordinal))
                bAck = bFrame;
            else if (string.Equals(bEventName, "friend_added", StringComparison.Ordinal))
                bFriendAdded = bFrame;
        }

        await Assert.That(bAck is not null).IsTrue().Because("Expected endpoint ack on B socket for mutual send.");
        await Assert.That(bFriendAdded is not null).IsTrue().Because("Expected friend_added push on B socket after mutual send auto-accept.");

        string? aFriendAdded = null;
        string? aRequestCleared = null;
        for (var i = 0; i < 5 && (aFriendAdded is null || aRequestCleared is null); i++)
        {
            var aTask = ReceiveWebSocketTextAsync(wsA);
            var aCompleted = await Task.WhenAny(aTask, Task.Delay(TimeSpan.FromSeconds(5)));
            await Assert.That(ReferenceEquals(aCompleted, aTask)).IsTrue().Because("Timed out waiting for A-side event after mutual send.");
            var aFrame = await aTask;
            var aEventName = GetEventName(aFrame);
            if (string.Equals(aEventName, "friend_added", StringComparison.Ordinal))
                aFriendAdded = aFrame;
            else if (string.Equals(aEventName, "friend_request_removed", StringComparison.Ordinal))
                aRequestCleared = aFrame;
        }

        await Assert.That(aFriendAdded is not null).IsTrue().Because("Expected friend_added push on A socket after mutual send auto-accept.");
        await Assert.That(aRequestCleared is not null).IsTrue().Because("Expected friend_request_removed push on A socket after mutual send auto-accept (outgoing request cleanup).");

        await wsA.CloseAsync(WebSocketCloseStatus.NormalClosure, "test done", CancellationToken.None);
        await wsB.CloseAsync(WebSocketCloseStatus.NormalClosure, "test done", CancellationToken.None);
    }
    
    static async Task JoinTopicAsync(WebSocket ws, string topic, string token)
    {
        var joinFrame =
            "{" +
            "\"topic\":\"" + topic + "\"," +
            "\"event\":\"phx_join\"," +
            "\"payload\":{\"token\":\"" + token + "\",\"isReconnect\":true}," +
            "\"ref\":\"1\"," +
            "\"join_ref\":\"1\"" +
            "}";

        await ws.SendAsync(Encoding.UTF8.GetBytes(joinFrame), WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);
        _ = await ReceiveWebSocketTextAsync(ws);
    }

    static string GetEventName(string frame)
    {
        using var doc = JsonDocument.Parse(frame);
        var root = doc.RootElement;
        return root.ValueKind == JsonValueKind.Array
            ? root[3].GetString() ?? string.Empty
            : root.GetProperty("event").GetString() ?? string.Empty;
    }
    
    async Task<string> SendJoinAsync(WebSocket ws, string socketToken, string refId)
    {
        var joinFrame =
            "{" +
            "\"topic\":\"system:sys-phx-rate-limit\"," +
            "\"event\":\"phx_join\"," +
            "\"payload\":{" +
            "\"token\":\"" + socketToken + "\"," +
            "\"isReconnect\":true" +
            "}," +
            "\"ref\":\"" + refId + "\"," +
            "\"join_ref\":\"1\"" +
            "}";

        await ws.SendAsync(Encoding.UTF8.GetBytes(joinFrame), WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);
        return await ReceiveWebSocketTextAsync(ws);
    }
    
    private static Uri WebSocketBasePath(TestServer server)
    {
        return new Uri($"wss://{server.BaseAddress.Host}");
    }
    
    private static async Task<string> ReceiveWebSocketTextAsync(WebSocket ws)
    {
        var buffer = new byte[8192];
        using var ms = new MemoryStream();

        WebSocketReceiveResult result;
        do
        {
            result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
            if (result.MessageType == WebSocketMessageType.Close)
                throw new InvalidOperationException("WebSocket closed by server.");

            ms.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);

        return Encoding.UTF8.GetString(ms.ToArray());
    }
}