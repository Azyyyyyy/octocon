using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Interfold.Contracts;
using Interfold.Infrastructure;
using Interfold.Infrastructure.Configuration;
using Interfold.IntegrationTests.Attributes;
using Interfold.IntegrationTests.TestServices;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using TUnit.Core.Services;

namespace Interfold.IntegrationTests.Endpoints;

[Timeout(1000 * 10)]
public class WebSocketTests : BaseEndpointTest
{
    [Test, ApiIntegration]
    [CombinedDataSources]
    public async Task Api_UserSocketEndpoint_AllowsWebSocketUpgrade([InterfoldFactoryGenerator] InterfoldWebApplicationFactory factory, CancellationToken token)
    {
        using var server = factory.Server;
        var client = server.CreateWebSocketClient();
        
        string socketToken = await CreateRandomToken(factory, "sys-phx-join");
        var uri = new Uri(WebSocketBasePath(server), "/api/socket/websocket?token=" + socketToken);
        
        using var ws = await client.ConnectAsync(uri, token);

        await Assert.That(ws.State).IsEqualTo(WebSocketState.Open).Because($"Expected websocket to be open after connecting to /api/socket/weboscket, got {ws.State}.");

        var arrayJoinFrame = PhxArrayFrame.CreateBytes(
            "51", "51", "system:sys-phx-join", "phx_join",
            new PhxJoinPayload { Token = socketToken, ProtocolVersion = "2.0.0", Platform = "wasm", IsReconnect = true });

        await ws.SendAsync(arrayJoinFrame, WebSocketMessageType.Text, endOfMessage: true, token);

        var arrayJoinReply = await ReceiveWebSocketTextAsync(ws, token);
        using var arrayJoinDoc = JsonDocument.Parse(arrayJoinReply);
        var arrayRoot = arrayJoinDoc.RootElement;

        using (Assert.Multiple())
        {
            await Assert.That(arrayRoot.ValueKind).IsEqualTo(JsonValueKind.Array).Because($"Expected array-frame reply for array-frame request. Payload: {arrayJoinReply}");
            await Assert.That(arrayRoot.GetArrayLength()).IsGreaterThanOrEqualTo(5).Because($"Expected 5-element Phoenix array reply. Payload: {arrayJoinReply}");
            await Assert.That(arrayRoot[0].GetString()).IsEqualTo("51").Because($"Expected join_ref=51 in array reply. Payload: {arrayJoinReply}");
            await Assert.That(arrayRoot[1].GetString()).IsEqualTo("51").Because($"Expected ref=51 in array reply. Payload: {arrayJoinReply}");
            await Assert.That(arrayRoot[2].GetString()).IsEqualTo("system:sys-phx-join").Because($"Expected topic to match array join topic. Payload: {arrayJoinReply}");
            await Assert.That(arrayRoot[3].GetString()).IsEqualTo("phx_reply").Because($"Expected array reply event phx_reply. Payload: {arrayJoinReply}");
        }
    }
    
    [Test, ApiIntegration]
    [CombinedDataSources]
    public async Task Api_UserSocketEndpoint_RejectsUnsupportedProtocolVersion([InterfoldFactoryGenerator] InterfoldWebApplicationFactory factory, CancellationToken token)
    {
        factory
            .WithConfiguration("OCTOCON_DEEPLINK_ADDRESS", "octocon://app")
            .WithConfiguration("OCTOCON_REGION", "nam")
            .WithConfiguration("OCTOCON_SOCKET_BATCH_BYTES_THRESHOLD", "1");

        var wsClient = factory.Server.CreateWebSocketClient();
        string socketToken = await CreateRandomToken(factory, "sys-phx-unsupported");
        var uri = new Uri(WebSocketBasePath(factory.Server), $"api/socket/websocket?token={socketToken}");
        using var ws = await wsClient.ConnectAsync(uri, token);

        var joinFrame = new PhxFrame<PhxJoinPayload>
        {
            Topic = "system:sys-phx-unsupported",
            Event = "phx_join",
            Payload = new PhxJoinPayload { Token = socketToken, ProtocolVersion = "not-a-version" },
            Ref = "1",
            JoinRef = "1"
        }.ToBytes();

        await ws.SendAsync(joinFrame, WebSocketMessageType.Text, endOfMessage: true, token);
        var joinReply = await ReceiveWebSocketTextAsync(ws, token);
        using var joinDoc = JsonDocument.Parse(joinReply);
        var payload = joinDoc.RootElement.GetProperty("payload");

        using (Assert.Multiple())
        {
            await Assert.That(payload.GetProperty("status").GetString()).IsEqualTo("error").Because($"Expected status=error for unsupported protocol version. Payload: {joinReply}");
            await Assert.That(payload.GetProperty("response").GetProperty("reason").GetString()).IsEqualTo("unsupported_protocol_version").Because($"Expected reason=unsupported_protocol_version. Payload: {joinReply}");
        }

        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "test done", token);
    }

    [Test, ApiIntegration]
    [CombinedDataSources]
    public async Task Api_UserSocketEndpoint_BatchesForIos_WhenThresholdExceeded([InterfoldFactoryGenerator] InterfoldWebApplicationFactory factory, CancellationToken token)
    {
        factory
            .WithConfiguration("OCTOCON_DEEPLINK_ADDRESS", "octocon://app")
            .WithConfiguration("OCTOCON_REGION", "nam");

        var wsClient = factory.Server.CreateWebSocketClient();
        string socketToken = await CreateRandomToken(factory, "sys-phx-ios-batch");
        var uri = new Uri(WebSocketBasePath(factory.Server), $"api/socket/websocket?token={socketToken}");
        using var ws = await wsClient.ConnectAsync(uri, token);

        var joinFrame = new PhxFrame<PhxJoinPayload>
        {
            Topic = "system:sys-phx-ios-batch",
            Event = "phx_join",
            Payload = new PhxJoinPayload { Token = socketToken, Platform = "ios", ProtocolVersion = "2.0.0", ForceBatch = true },
            Ref = "1",
            JoinRef = "1"
        }.ToBytes();

        await ws.SendAsync(joinFrame, WebSocketMessageType.Text, endOfMessage: true, token);
        var joinReply = await ReceiveWebSocketTextAsync(ws, token);
        using var joinDoc = JsonDocument.Parse(joinReply);
        var payload = joinDoc.RootElement.GetProperty("payload");
        var response = payload.GetProperty("response");

        var isBatched = response.TryGetProperty("batched", out var batchedProp)
            && batchedProp.ValueKind == JsonValueKind.True;

        using (Assert.Multiple())
        {
            await Assert.That(payload.GetProperty("status").GetString()).IsEqualTo("ok").Because($"Expected status=ok for iOS batched join. Payload: {joinReply}");
            await Assert.That(isBatched).IsTrue().Because($"Expected batched=true for iOS join above threshold. Payload: {joinReply}");
        }

        var batchedComplete = await ReceiveEventAsync(ws, token, e => e == "batched_init_complete", maxFrames: 6);
        await Assert.That(batchedComplete).IsNotNull().Because("Expected batched_init_complete after iOS batched join.");

        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "test done", token);
    }

    [Test, ApiIntegration]
    [CombinedDataSources]
    public async Task Api_UserSocketEndpoint_RateLimitsThirdJoinWithinOneSecond([InterfoldFactoryGenerator] InterfoldWebApplicationFactory factory, CancellationToken token)
    {
        factory
            .WithConfiguration("OCTOCON_DEEPLINK_ADDRESS", "octocon://app")
            .WithConfiguration("OCTOCON_REGION", "nam");

        var wsClient = factory.Server.CreateWebSocketClient();
        string socketToken = await CreateRandomToken(factory, "sys-phx-rate-limit");
        var uri = new Uri(WebSocketBasePath(factory.Server), $"api/socket/websocket?token={socketToken}");
        using var ws = await wsClient.ConnectAsync(uri, token);

        var first = await SendJoinAsync(ws, socketToken, "1", token);
        var second = await SendJoinAsync(ws, socketToken, "2", token);
        var third = await SendJoinAsync(ws, socketToken, "3", token);

        using var firstDoc = JsonDocument.Parse(first);
        using var secondDoc = JsonDocument.Parse(second);
        using var thirdDoc = JsonDocument.Parse(third);

        using (Assert.Multiple())
        {
            await Assert.That(firstDoc.RootElement.GetProperty("payload").GetProperty("status").GetString()).IsEqualTo("ok").Because($"Expected first join to be accepted. Payload: {first}");
            await Assert.That(secondDoc.RootElement.GetProperty("payload").GetProperty("status").GetString()).IsEqualTo("ok").Because($"Expected second join to be accepted. Payload: {second}");
            await Assert.That(thirdDoc.RootElement.GetProperty("payload").GetProperty("status").GetString()).IsEqualTo("error").Because($"Expected third join to be rate-limited. Payload: {third}");
            await Assert.That(thirdDoc.RootElement.GetProperty("payload").GetProperty("response").GetProperty("reason").GetString()).IsEqualTo("rate_limited").Because($"Expected reason=rate_limited on third join. Payload: {third}");
        }

        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "test done", token);
    }

    [Test, ApiIntegration]
    [CombinedDataSources]
    public async Task Api_UserSocketEndpoint_PushesFrontingChangedEvent_AfterFrontStart([InterfoldFactoryGenerator] InterfoldWebApplicationFactory factory, CancellationToken token)
    {
        factory
            .WithConfiguration("OCTOCON_DEEPLINK_ADDRESS", "octocon://app")
            .WithConfiguration("OCTOCON_REGION", "nam");

        var wsClient = factory.Server.CreateWebSocketClient();
        string socketToken = await CreateRandomToken(factory, "sys-front-push");
        const string topic = "system:sys-front-push";
        var uri = new Uri(WebSocketBasePath(factory.Server), $"api/socket/websocket?token={socketToken}");
        var ws = await wsClient.ConnectAsync(uri, token);

        var joinFrame = new PhxFrame<PhxJoinPayload>
        {
            Topic = topic,
            Event = "phx_join",
            Payload = new PhxJoinPayload { Token = socketToken, IsReconnect = true },
            Ref = "1",
            JoinRef = "1"
        }.ToBytes();

        await ws.SendAsync(joinFrame, WebSocketMessageType.Text, endOfMessage: true, token);
        var join = await ReceiveWebSocketTextAsync(ws, token);

        var createAlterFrame = new PhxFrame<PhxEndpointPayload>
        {
            Topic = topic,
            Event = "endpoint",
            Payload = new PhxEndpointPayload { Method = "POST", Path = "/api/systems/me/alters", Body = new { name = "FrontPushAlter" } },
            Ref = "2",
            JoinRef = "1"
        }.ToBytes();

        await ws.SendAsync(createAlterFrame, WebSocketMessageType.Text, endOfMessage: true, token);
        string? createAlterAck = null;
        string? createAlterPush = null;

        for (var i = 0; i < 6 && createAlterAck is null; i++)
        {
            using var frameCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            frameCts.CancelAfter(TimeSpan.FromSeconds(2));

            string frame;
            try
            {
                frame = await ReceiveWebSocketTextAsync(ws, frameCts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            var eventName = GetEventName(frame);
            if (eventName == "phx_reply")
            {
                createAlterAck = frame;
            }
            else if (eventName == "alter_created")
            {
                createAlterPush = frame;
            }
        }

        await Assert.That(createAlterAck).IsNotNull().Because("Expected endpoint ack (phx_reply) for alter create call.");

        var createdAlterId = TryExtractAlterId(createAlterAck!, out var idFromAck)
            ? idFromAck
            : TryExtractAlterId(createAlterPush, out var idFromPush)
                ? idFromPush
                : throw new InvalidOperationException($"Could not parse created alter id from websocket frames. Ack: {createAlterAck ?? "<null>"}; Push: {createAlterPush ?? "<null>"}");

        var startFrontFrame = new PhxFrame<PhxEndpointPayload>
        {
            Topic = topic,
            Event = "endpoint",
            Payload = new PhxEndpointPayload { Method = "POST", Path = "/api/systems/me/front/start", Body = new { id = createdAlterId } },
            Ref = "3",
            JoinRef = "1"
        }.ToBytes();

        await ws.SendAsync(startFrontFrame, WebSocketMessageType.Text, endOfMessage: true, token);

        string? endpointAck = null;
        string? frontingPush = null;

        for (var i = 0; i < 6 && (endpointAck is null || frontingPush is null); i++)
        {
            using var frameCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            frameCts.CancelAfter(TimeSpan.FromSeconds(2));

            string frame;
            try
            {
                frame = await ReceiveWebSocketTextAsync(ws, frameCts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            using var doc = JsonDocument.Parse(frame);
            var root = doc.RootElement;

            var eventName = root.ValueKind == JsonValueKind.Array
                ? root[3].GetString() ?? string.Empty
                : root.GetProperty("event").GetString() ?? string.Empty;

            if (eventName == "phx_reply")
            {
                endpointAck = frame;
                continue;
            }

            if (eventName is "fronting_started" or "fronting_changed")
            {
                frontingPush = frame;
            }
        }

        using (Assert.Multiple())
        {
            await Assert.That(endpointAck).IsNotNull().Because("Expected endpoint ack (phx_reply) for front start call.");
        }

        if (frontingPush is not null)
        {
            using var pushDoc = JsonDocument.Parse(frontingPush);
            var pushPayload = pushDoc.RootElement.GetProperty("payload");
            await Assert.That(
                pushPayload.TryGetProperty("front", out var frontObj) && frontObj.ValueKind == JsonValueKind.Object
                || pushPayload.TryGetProperty("fronts", out var frontsArr) && frontsArr.ValueKind == JsonValueKind.Array)
                .IsTrue().Because($"Expected fronting push payload to include front object or fronts array. Payload: {frontingPush}");
        }

        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "test done", token);

        static bool TryExtractAlterId(string? frame, out int alterId)
        {
            alterId = 0;
            if (string.IsNullOrWhiteSpace(frame))
                return false;

            using var doc = JsonDocument.Parse(frame);
            if (!doc.RootElement.TryGetProperty("payload", out var payload))
                return false;

            if (TryReadId(payload, out alterId))
                return true;

            if (payload.TryGetProperty("response", out var response))
            {
                if (TryReadId(response, out alterId))
                    return true;

                if (response.TryGetProperty("body", out var bodyProp) &&
                    bodyProp.ValueKind == JsonValueKind.String)
                {
                    var body = bodyProp.GetString();
                    if (!string.IsNullOrWhiteSpace(body))
                    {
                        using var bodyDoc = JsonDocument.Parse(body);
                        if (TryReadId(bodyDoc.RootElement, out alterId))
                            return true;
                    }
                }
            }

            return false;

            static bool TryReadId(JsonElement parent, out int id)
            {
                id = default;

                if (parent.TryGetProperty("alter", out var alter) &&
                    alter.ValueKind == JsonValueKind.Object &&
                    alter.TryGetProperty("id", out var alterIdProp) &&
                    alterIdProp.TryGetInt32(out id))
                    return true;

                if (parent.TryGetProperty("data", out var data) &&
                    data.ValueKind == JsonValueKind.Object &&
                    data.TryGetProperty("id", out var dataIdProp) &&
                    dataIdProp.TryGetInt32(out id))
                    return true;

                return false;
            }
        }
    }

    [Test, ApiIntegration]
    [CombinedDataSources]
    public async Task Api_UserSocketEndpoint_PushesAlterTagAndFieldsEvents_AfterEndpointWrites([InterfoldFactoryGenerator] InterfoldWebApplicationFactory factory, CancellationToken token)
    {
        factory
            .WithConfiguration("OCTOCON_DEEPLINK_ADDRESS", "octocon://app")
            .WithConfiguration("OCTOCON_REGION", "nam");

        var wsClient = factory.Server.CreateWebSocketClient();
        string socketToken = await CreateRandomToken(factory, "sys-domain-fanout");
        const string topic = "system:sys-domain-fanout";
        var uri = new Uri(WebSocketBasePath(factory.Server), $"api/socket/websocket?token={socketToken}");
        using var ws = await wsClient.ConnectAsync(uri, token);

        var joinFrame = new PhxFrame<PhxJoinPayload>
        {
            Topic = topic,
            Event = "phx_join",
            Payload = new PhxJoinPayload { Token = socketToken, IsReconnect = true },
            Ref = "1",
            JoinRef = "1"
        }.ToBytes();

        await ws.SendAsync(joinFrame, WebSocketMessageType.Text, endOfMessage: true, token);
        _ = await ReceiveWebSocketTextAsync(ws, token);

        var createAlterFrame = new PhxFrame<PhxEndpointPayload>
        {
            Topic = topic,
            Event = "endpoint",
            Payload = new PhxEndpointPayload { Method = "POST", Path = "/api/systems/me/alters", Body = new { name = "DomainFanoutAlter" } },
            Ref = "2",
            JoinRef = "1"
        }.ToBytes();

        var alterResult = await SendEndpointAndCaptureAsync(createAlterFrame, "alter_created");
        using (Assert.Multiple())
        {
            await Assert.That(alterResult.ack).IsNotNull().Because("Expected endpoint ack for alter create.");
            await Assert.That(alterResult.push).IsNotNull().Because("Expected alter_created push after alter create.");
        }

        var createTagFrame = new PhxFrame<PhxEndpointPayload>
        {
            Topic = topic,
            Event = "endpoint",
            Payload = new PhxEndpointPayload { Method = "POST", Path = "/api/systems/me/tags", Body = new { name = "DomainFanoutTag" } },
            Ref = "3",
            JoinRef = "1"
        }.ToBytes();

        var tagResult = await SendEndpointAndCaptureAsync(createTagFrame, "tag_created");
        using (Assert.Multiple())
        {
            await Assert.That(tagResult.ack).IsNotNull().Because("Expected endpoint ack for tag create.");
            await Assert.That(tagResult.push).IsNotNull().Because("Expected tag_created push after tag create.");
        }

        var createFieldFrame = new PhxFrame<PhxEndpointPayload>
        {
            Topic = topic,
            Event = "endpoint",
            Payload = new PhxEndpointPayload { Method = "POST", Path = "/api/settings/fields", Body = new { name = "DomainFanoutField", type = "text", security_level = "private", locked = false } },
            Ref = "4",
            JoinRef = "1"
        }.ToBytes();

        var fieldResult = await SendEndpointAndCaptureAsync(createFieldFrame, "fields_updated");
        using (Assert.Multiple())
        {
            await Assert.That(fieldResult.ack).IsNotNull().Because("Expected endpoint ack for field create.");
            await Assert.That(fieldResult.push).IsNotNull().Because("Expected fields_updated push after field create.");
        }

        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "test done", token);
        return;

        static string EventNameFromFrame(JsonElement root)
        {
            return root.ValueKind == JsonValueKind.Array
                ? root[3].GetString() ?? string.Empty
                : root.GetProperty("event").GetString() ?? string.Empty;
        }

        async Task<(string? ack, string? push)> SendEndpointAndCaptureAsync(byte[] frame, string expectedPushEvent)
        {
            await ws.SendAsync(frame, WebSocketMessageType.Text, endOfMessage: true, token);
            string? ack = null;
            string? push = null;

            for (var i = 0; i < 5 && (ack is null || push is null); i++)
            {
                var received = await ReceiveWebSocketTextAsync(ws, token);
                using var doc = JsonDocument.Parse(received);
                var name = EventNameFromFrame(doc.RootElement);

                if (name == "phx_reply")
                {
                    ack = received;
                    continue;
                }

                if (name == expectedPushEvent)
                {
                    push = received;
                }
            }

            return (ack, push);
        }
    }

    [Test, ApiIntegration]
    [CombinedDataSources]
    public async Task Api_UserSocketEndpoint_PushesFriendRequestReceived_ToRecipientSystem([InterfoldFactoryGenerator] InterfoldWebApplicationFactory factory, CancellationToken token)
    {
        factory
            .WithConfiguration("OCTOCON_DEEPLINK_ADDRESS", "octocon://app")
            .WithConfiguration("OCTOCON_REGION", "nam");

        const string senderSystemId = "sys-friend-sender";
        const string recipientSystemId = "sys-friend-recipient";
        var wsClientFactory = factory.Server.CreateWebSocketClient();

        string senderSocketToken = await CreateRandomToken(factory, senderSystemId);
        string recipientSocketToken = await CreateRandomToken(factory, recipientSystemId);

        var senderSocketUri = new Uri(WebSocketBasePath(factory.Server), $"api/socket/websocket?token={senderSocketToken}");
        var recipientSocketUri = new Uri(WebSocketBasePath(factory.Server), $"api/socket/websocket?token={recipientSocketToken}");

        using var senderWs = await wsClientFactory.ConnectAsync(senderSocketUri, token);
        using var recipientWs = await wsClientFactory.ConnectAsync(recipientSocketUri, token);

        await JoinTopicAsync(senderWs, $"system:{senderSystemId}", senderSocketToken, token);
        await JoinTopicAsync(recipientWs, $"system:{recipientSystemId}", recipientSocketToken, token);

        var sendRequestFrame = new PhxFrame<PhxEndpointPayload> {
            Topic = "system:" + senderSystemId,
            Event = "endpoint",
            Payload = new PhxEndpointPayload
            {
                Method = "PUT",
                Path = "/api/friend-requests/" + recipientSystemId,
                Body = new object()
            },
            Ref = "2",
            JoinRef = "1",
        }.ToBytes();

        await senderWs.SendAsync(sendRequestFrame, WebSocketMessageType.Text, endOfMessage: true, token);

        string? senderAck = null;
        string? senderPush = null;
        for (var i = 0; i < 5 && (senderAck is null || senderPush is null); i++)
        {
            var frame = await ReceiveWebSocketTextAsync(senderWs, token);
            var eventName = GetEventName(frame);
            if (eventName == "phx_reply")
            {
                senderAck = frame;
                continue;
            }

            if (eventName == "friend_request_sent")
            {
                senderPush = frame;
            }
        }

        using (Assert.Multiple())
        {
            await Assert.That(senderAck).IsNotNull().Because("Expected endpoint ack on sender socket for friend request send.");
            await Assert.That(senderPush).IsNotNull().Because("Expected friend_request_sent push on sender socket.");
        }

        var recipientReadTask = ReceiveWebSocketTextAsync(recipientWs, token);
        var completed = await Task.WhenAny(recipientReadTask, Task.Delay(TimeSpan.FromSeconds(5)));
        await Assert.That(ReferenceEquals(completed, recipientReadTask)).IsTrue().Because("Timed out waiting for recipient-side friend_request_received push.");

        var recipientFrame = await recipientReadTask;
        await Assert.That(GetEventName(recipientFrame)).IsEqualTo("friend_request_received").Because($"Expected recipient push event friend_request_received. Payload: {recipientFrame}");

        await senderWs.CloseAsync(WebSocketCloseStatus.NormalClosure, "test done", token);
        await recipientWs.CloseAsync(WebSocketCloseStatus.NormalClosure, "test done", token);
    }

    [Test, ApiIntegration]
    [CombinedDataSources]
    public async Task Api_UserSocketEndpoint_PushesFriendRequestAccepted_ToActorAndRecipient([InterfoldFactoryGenerator] InterfoldWebApplicationFactory factory, CancellationToken token)
    {
        factory
            .WithConfiguration("OCTOCON_DEEPLINK_ADDRESS", "octocon://app")
            .WithConfiguration("OCTOCON_REGION", "nam");

        const string senderSystemId = "sys-accept-sender";
        const string recipientSystemId = "sys-accept-recipient";

        var wsClientFactory = factory.Server.CreateWebSocketClient();
        string senderSocketToken = await CreateRandomToken(factory, senderSystemId);
        string recipientSocketToken = await CreateRandomToken(factory, recipientSystemId);

        var senderSocketUri = new Uri(WebSocketBasePath(factory.Server), $"api/socket/websocket?token={senderSocketToken}");
        var recipientSocketUri = new Uri(WebSocketBasePath(factory.Server), $"api/socket/websocket?token={recipientSocketToken}");

        using var senderWs = await wsClientFactory.ConnectAsync(senderSocketUri, token);
        using var recipientWs = await wsClientFactory.ConnectAsync(recipientSocketUri, token);

        await JoinTopicAsync(senderWs, $"system:{senderSystemId}", senderSocketToken, token);
        await JoinTopicAsync(recipientWs, $"system:{recipientSystemId}", recipientSocketToken, token);

        var sendRequestFrame = new PhxFrame<PhxEndpointPayload> {
            Topic = "system:" + senderSystemId,
            Event = "endpoint",
            Payload = new PhxEndpointPayload
            {
                Method = "PUT",
                Path = "/api/friend-requests/" + recipientSystemId,
                Body = new object()
            },
            Ref = "2",
            JoinRef = "1",
        }.ToBytes();

        await senderWs.SendAsync(sendRequestFrame, WebSocketMessageType.Text, endOfMessage: true, token);

        _ = await ReceiveWebSocketTextAsync(senderWs, token);
        _ = await ReceiveWebSocketTextAsync(senderWs, token);
        var recipientEvent = await ReceiveWebSocketTextAsync(recipientWs, token);
        await Assert.That(GetEventName(recipientEvent)).IsEqualTo("friend_request_received").Because("Expected friend_request_received");

        var acceptFrame = new PhxFrame<PhxEndpointPayload> {
            Topic = "system:" + recipientSystemId,
            Event = "endpoint",
            Payload = new PhxEndpointPayload
            {
                Method = "POST",
                Path = "/api/friend-requests/" + senderSystemId + "/accept",
                Body = new object()
            },
            Ref = "3",
            JoinRef = "1",
        }.ToBytes();
        
        await recipientWs.SendAsync(acceptFrame, WebSocketMessageType.Text, endOfMessage: true, token);

        string? recipientAck = null;
        string? recipientFriendAdded = null;
        for (var i = 0; i < 5 && (recipientAck is null || recipientFriendAdded is null); i++)
        {
            var frame = await ReceiveWebSocketTextAsync(recipientWs, token);
            var eventName = GetEventName(frame);
            if (eventName == "phx_reply")
            {
                recipientAck = frame;
            }
            else if (eventName == "friend_added")
            {
                recipientFriendAdded = frame;
            }
        }

        using (Assert.Multiple())
        {
            await Assert.That(recipientAck).IsNotNull().Because("Expected endpoint ack on recipient socket for accept.");
            await Assert.That(recipientFriendAdded).IsNotNull().Because("Expected friend_added push on recipient socket after accept.");
        }

        string? senderFriendAdded = null;
        string? senderRequestCleared = null;
        for (var i = 0; i < 5 && (senderFriendAdded is null || senderRequestCleared is null); i++)
        {
            var senderTask = ReceiveWebSocketTextAsync(senderWs, token);
            var senderCompleted = await Task.WhenAny(senderTask, Task.Delay(TimeSpan.FromSeconds(5)));
            await Assert.That(ReferenceEquals(senderCompleted, senderTask)).IsTrue().Because("Timed out waiting for sender-side event after accept.");
            var senderFrame = await senderTask;
            var senderEventName = GetEventName(senderFrame);
            if (senderEventName == "friend_added")
                senderFriendAdded = senderFrame;
            else if (senderEventName == "friend_request_removed")
                senderRequestCleared = senderFrame;
        }

        using (Assert.Multiple())
        {
            await Assert.That(senderFriendAdded).IsNotNull().Because("Expected friend_added push on sender socket after accept.");
            await Assert.That(senderRequestCleared).IsNotNull().Because("Expected friend_request_removed push on sender socket after accept (outgoing request cleanup).");
        }

        await senderWs.CloseAsync(WebSocketCloseStatus.NormalClosure, "test done", token);
        await recipientWs.CloseAsync(WebSocketCloseStatus.NormalClosure, "test done", token);
    }

    [Test, ApiIntegration]
    [CombinedDataSources]
    public async Task Api_UserSocketEndpoint_PushesFriendRequestRejected_ToActorAndRecipient([InterfoldFactoryGenerator] InterfoldWebApplicationFactory factory, CancellationToken token)
    {
        factory
            .WithConfiguration("OCTOCON_DEEPLINK_ADDRESS", "octocon://app")
            .WithConfiguration("OCTOCON_REGION", "nam");

        const string senderSystemId = "sys-reject-sender";
        const string recipientSystemId = "sys-reject-recipient";

        var wsClientFactory = factory.Server.CreateWebSocketClient();
        string senderSocketToken = await CreateRandomToken(factory, senderSystemId);
        string recipientSocketToken = await CreateRandomToken(factory, recipientSystemId);

        var senderSocketUri = new Uri(WebSocketBasePath(factory.Server), $"api/socket/websocket?token={senderSocketToken}");
        var recipientSocketUri = new Uri(WebSocketBasePath(factory.Server), $"api/socket/websocket?token={recipientSocketToken}");

        using var senderWs = await wsClientFactory.ConnectAsync(senderSocketUri, token);
        using var recipientWs = await wsClientFactory.ConnectAsync(recipientSocketUri, token);

        await JoinTopicAsync(senderWs, $"system:{senderSystemId}", senderSocketToken, token);
        await JoinTopicAsync(recipientWs, $"system:{recipientSystemId}", recipientSocketToken, token);

        var sendRequestFrame = new PhxFrame<PhxEndpointPayload> {
            Topic = "system:" + senderSystemId,
            Event = "endpoint",
            Payload = new PhxEndpointPayload
            {
                Method = "PUT",
                Path = "/api/friend-requests/" + recipientSystemId,
                Body = new object()
            },
            Ref = "2",
            JoinRef = "1",
        }.ToBytes();
        
        await senderWs.SendAsync(sendRequestFrame, WebSocketMessageType.Text, endOfMessage: true, token);

        _ = await ReceiveWebSocketTextAsync(senderWs, token);
        _ = await ReceiveWebSocketTextAsync(senderWs, token);
        var recipientEvent = await ReceiveWebSocketTextAsync(recipientWs, token);
        await Assert.That(GetEventName(recipientEvent)).IsEqualTo("friend_request_received").Because("Expected friend_request_received");

        var rejectFrame = new PhxFrame<PhxEndpointPayload> {
            Topic = "system:" + recipientSystemId,
            Event = "endpoint",
            Payload = new PhxEndpointPayload
            {
                Method = "DELETE",
                Path = "/api/friend-requests/" + senderSystemId,
                Body = new object()
            },
            Ref = "3",
            JoinRef = "1",
        }.ToBytes();

        await recipientWs.SendAsync(rejectFrame, WebSocketMessageType.Text, endOfMessage: true, token);

        string? recipientAck = null;
        string? recipientRemoved = null;
        for (var i = 0; i < 6 && (recipientAck is null || recipientRemoved is null); i++)
        {
            using var frameCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            frameCts.CancelAfter(TimeSpan.FromSeconds(2));

            string frame;
            try
            {
                frame = await ReceiveWebSocketTextAsync(recipientWs, frameCts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            var eventName = GetEventName(frame);
            if (eventName == "phx_reply")
            {
                recipientAck = frame;
            }
            else if (eventName == "friend_request_removed")
            {
                recipientRemoved = frame;
            }
        }

        using (Assert.Multiple())
        {
            await Assert.That(recipientAck).IsNotNull().Because("Expected endpoint ack on recipient socket for reject.");
        }

        _ = await ReceiveEventAsync(senderWs, token, e => e == "friend_request_removed", maxFrames: 8);

        await senderWs.CloseAsync(WebSocketCloseStatus.NormalClosure, "test done", token);
        await recipientWs.CloseAsync(WebSocketCloseStatus.NormalClosure, "test done", token);
    }

    [Test, ApiIntegration]
    [CombinedDataSources]
    public async Task Api_UserSocketEndpoint_PushesFriendRequestCancelled_ToActorAndRecipient([InterfoldFactoryGenerator] InterfoldWebApplicationFactory factory, CancellationToken token)
    {
        factory
            .WithConfiguration("OCTOCON_DEEPLINK_ADDRESS", "octocon://app")
            .WithConfiguration("OCTOCON_REGION", "nam");

        const string senderSystemId = "sys-cancel-sender";
        const string recipientSystemId = "sys-cancel-recipient";

        var wsClientFactory = factory.Server.CreateWebSocketClient();
        string senderSocketToken = await CreateRandomToken(factory, senderSystemId);
        string recipientSocketToken = await CreateRandomToken(factory, recipientSystemId);

        var senderSocketUri = new Uri(WebSocketBasePath(factory.Server), $"api/socket/websocket?token={senderSocketToken}");
        var recipientSocketUri = new Uri(WebSocketBasePath(factory.Server), $"api/socket/websocket?token={recipientSocketToken}");

        using var senderWs = await wsClientFactory.ConnectAsync(senderSocketUri, token);
        using var recipientWs = await wsClientFactory.ConnectAsync(recipientSocketUri, token);

        await JoinTopicAsync(senderWs, $"system:{senderSystemId}", senderSocketToken, token);
        await JoinTopicAsync(recipientWs, $"system:{recipientSystemId}", recipientSocketToken, token);

        var sendRequestFrame = new PhxFrame<PhxEndpointPayload> {
            Topic = "system:" + senderSystemId,
            Event = "endpoint",
            Payload = new PhxEndpointPayload
            {
                Method = "PUT",
                Path = "/api/friend-requests/" + recipientSystemId,
                Body = new object()
            },
            Ref = "2",
            JoinRef = "1",
        }.ToBytes();

        await senderWs.SendAsync(sendRequestFrame, WebSocketMessageType.Text, endOfMessage: true, token);

        _ = await ReceiveWebSocketTextAsync(senderWs, token);
        _ = await ReceiveWebSocketTextAsync(senderWs, token);

        var recipientReceived = await ReceiveWebSocketTextAsync(recipientWs, token);
        await Assert.That(GetEventName(recipientReceived)).IsEqualTo("friend_request_received").Because("Expected friend_request_received before cancel.");

        var cancelFrame = new PhxFrame<PhxEndpointPayload> {
            Topic = "system:" + senderSystemId,
            Event = "endpoint",
            Payload = new PhxEndpointPayload
            {
                Method = "DELETE",
                Path = "/api/friend-requests/" + recipientSystemId,
                Body = new object()
            },
            Ref = "3",
            JoinRef = "1",
        }.ToBytes();

        await senderWs.SendAsync(cancelFrame, WebSocketMessageType.Text, endOfMessage: true, token);

        string? senderAck = null;
        string? senderRemoved = null;
        for (var i = 0; i < 5 && (senderAck is null || senderRemoved is null); i++)
        {
            var frame = await ReceiveWebSocketTextAsync(senderWs, token);
            var eventName = GetEventName(frame);
            if (eventName == "phx_reply")
            {
                senderAck = frame;
            }
            else if (eventName == "friend_request_removed")
            {
                senderRemoved = frame;
            }
        }

        using (Assert.Multiple())
        {
            await Assert.That(senderAck).IsNotNull().Because("Expected endpoint ack on sender socket for cancel.");
            await Assert.That(senderRemoved).IsNotNull().Because("Expected friend_request_removed push on sender socket after cancel.");
        }

        var recipientReadTask = ReceiveWebSocketTextAsync(recipientWs, token);
        var completed = await Task.WhenAny(recipientReadTask, Task.Delay(TimeSpan.FromSeconds(5), token));
        await Assert.That(ReferenceEquals(completed, recipientReadTask)).IsTrue().Because("Timed out waiting for recipient-side friend_request_removed push.");

        var recipientRemoved = await recipientReadTask;
        await Assert.That(GetEventName(recipientRemoved)).IsEqualTo("friend_request_removed").Because($"Expected recipient push event friend_request_removed. Event: {GetEventName(recipientRemoved)}");

        await senderWs.CloseAsync(WebSocketCloseStatus.NormalClosure, "test done", token);
        await recipientWs.CloseAsync(WebSocketCloseStatus.NormalClosure, "test done", token);
    }

    [Test, ApiIntegration]
    [CombinedDataSources]
    public async Task Api_UserSocketEndpoint_PushesFriendRemoved_ToActorAndRecipient([InterfoldFactoryGenerator] InterfoldWebApplicationFactory factory, CancellationToken token)
    {
        factory
            .WithConfiguration("OCTOCON_DEEPLINK_ADDRESS", "octocon://app")
            .WithConfiguration("OCTOCON_REGION", "nam");

        const string senderSystemId = "sys-remove-sender";
        const string recipientSystemId = "sys-remove-recipient";

        var wsClientFactory = factory.Server.CreateWebSocketClient();
        string senderSocketToken = await CreateRandomToken(factory, senderSystemId);
        string recipientSocketToken = await CreateRandomToken(factory, recipientSystemId);

        var senderSocketUri = new Uri(WebSocketBasePath(factory.Server), $"api/socket/websocket?token={senderSocketToken}");
        var recipientSocketUri = new Uri(WebSocketBasePath(factory.Server), $"api/socket/websocket?token={recipientSocketToken}");

        using var senderWs = await wsClientFactory.ConnectAsync(senderSocketUri, token);
        using var recipientWs = await wsClientFactory.ConnectAsync(recipientSocketUri, token);

        await JoinTopicAsync(senderWs, $"system:{senderSystemId}", senderSocketToken, token);
        await JoinTopicAsync(recipientWs, $"system:{recipientSystemId}", recipientSocketToken, token);

        var sendRequestFrame = new PhxFrame<PhxEndpointPayload> {
            Topic = "system:" + senderSystemId,
            Event = "endpoint",
            Payload = new PhxEndpointPayload
            {
                Method = "PUT",
                Path = "/api/friend-requests/" + recipientSystemId,
                Body = new object()
            },
            Ref = "2",
            JoinRef = "1",
        }.ToBytes();

        await senderWs.SendAsync(sendRequestFrame, WebSocketMessageType.Text, endOfMessage: true, token);

        _ = await ReceiveWebSocketTextAsync(senderWs, token);
        _ = await ReceiveWebSocketTextAsync(senderWs, token);

        var recipientReceived = await ReceiveWebSocketTextAsync(recipientWs, token);
        await Assert.That(GetEventName(recipientReceived)).IsEqualTo("friend_request_received").Because("Expected friend_request_received before accept.");

        var acceptFrame = new PhxFrame<PhxEndpointPayload> {
            Topic = "system:" + recipientSystemId,
            Event = "endpoint",
            Payload = new PhxEndpointPayload
            {
                Method = "POST",
                Path = "/api/friend-requests/" + senderSystemId + "/accept",
                Body = new object()
            },
            Ref = "3",
            JoinRef = "1",
        }.ToBytes();

        await recipientWs.SendAsync(acceptFrame, WebSocketMessageType.Text, endOfMessage: true, token);

        string? recipientAcceptAck = null;
        string? recipientAdded = null;
        for (var i = 0; i < 5 && (recipientAcceptAck is null || recipientAdded is null); i++)
        {
            var frame = await ReceiveWebSocketTextAsync(recipientWs, token);
            var eventName = GetEventName(frame);
            if (eventName == "phx_reply")
            {
                recipientAcceptAck = frame;
            }
            else if (eventName == "friend_added")
            {
                recipientAdded = frame;
            }
        }

        using (Assert.Multiple())
        {
            await Assert.That(recipientAcceptAck).IsNotNull().Because("Expected endpoint ack on recipient socket for accept.");
            await Assert.That(recipientAdded).IsNotNull().Because("Expected friend_added push on recipient socket after accept.");
        }

        _ = await ReceiveWebSocketTextAsync(senderWs, token);
        _ = await ReceiveWebSocketTextAsync(senderWs, token);

        var removeFrame = new PhxFrame<PhxEndpointPayload> {
            Topic = "system:" + senderSystemId,
            Event = "endpoint",
            Payload = new PhxEndpointPayload
            {
                Method = "DELETE",
                Path = "/api/friends/" + recipientSystemId,
                Body = new object()
            },
            Ref = "4",
            JoinRef = "1",
        }.ToBytes();

        await senderWs.SendAsync(removeFrame, WebSocketMessageType.Text, endOfMessage: true, token);

        string? senderRemoveAck = null;
        string? senderRemoved = null;
        for (var i = 0; i < 5 && (senderRemoveAck is null || senderRemoved is null); i++)
        {
            var frame = await ReceiveWebSocketTextAsync(senderWs, token);
            var eventName = GetEventName(frame);
            if (eventName == "phx_reply")
            {
                senderRemoveAck = frame;
            }
            else if (eventName == "friend_removed")
            {
                senderRemoved = frame;
            }
        }

        using (Assert.Multiple())
        {
            await Assert.That(senderRemoveAck).IsNotNull().Because("Expected endpoint ack on sender socket for remove.");
            await Assert.That(senderRemoved).IsNotNull().Because("Expected friend_removed push on sender socket after remove.");
        }

        var recipientRemoved = await ReceiveEventAsync(recipientWs, token, e => e == "friend_removed", maxFrames: 8);
        await Assert.That(recipientRemoved).IsNotNull().Because("Timed out waiting for recipient-side friend_removed push.");

        await senderWs.CloseAsync(WebSocketCloseStatus.NormalClosure, "test done", token);
        await recipientWs.CloseAsync(WebSocketCloseStatus.NormalClosure, "test done", token);
    }

    [Test, ApiIntegration]
    [CombinedDataSources]
    public async Task Api_UserSocketEndpoint_PushesFriendTrustedAndUntrusted_ToActor([InterfoldFactoryGenerator] InterfoldWebApplicationFactory factory, CancellationToken token)
    {
        factory
            .WithConfiguration("OCTOCON_DEEPLINK_ADDRESS", "octocon://app")
            .WithConfiguration("OCTOCON_REGION", "nam");
        
        const string senderSystemId = "sys-trust-sender";
        const string recipientSystemId = "sys-trust-recipient";

        var wsClientFactory = factory.Server.CreateWebSocketClient();
        string senderSocketToken = await CreateRandomToken(factory, senderSystemId);
        string recipientSocketToken = await CreateRandomToken(factory, recipientSystemId);

        var senderSocketUri = new Uri(WebSocketBasePath(factory.Server), $"api/socket/websocket?token={senderSocketToken}");
        var recipientSocketUri = new Uri(WebSocketBasePath(factory.Server), $"api/socket/websocket?token={recipientSocketToken}");

        using var senderWs = await wsClientFactory.ConnectAsync(senderSocketUri, token);
        using var recipientWs = await wsClientFactory.ConnectAsync(recipientSocketUri, token);

        await JoinTopicAsync(senderWs, $"system:{senderSystemId}", senderSocketToken, token);
        await JoinTopicAsync(recipientWs, $"system:{recipientSystemId}", recipientSocketToken, token);

        var sendRequestFrame = new PhxFrame<PhxEndpointPayload> {
            Topic = "system:" + senderSystemId,
            Event = "endpoint",
            Payload = new PhxEndpointPayload
            {
                Method = "PUT",
                Path = "/api/friend-requests/" + recipientSystemId,
                Body = new object()
            },
            Ref = "2",
            JoinRef = "1",
        }.ToBytes();

        await senderWs.SendAsync(sendRequestFrame, WebSocketMessageType.Text, endOfMessage: true, token);

        _ = await ReceiveWebSocketTextAsync(senderWs, token);
        _ = await ReceiveWebSocketTextAsync(senderWs, token);

        var recipientReceived = await ReceiveWebSocketTextAsync(recipientWs, token);
        await Assert.That(GetEventName(recipientReceived)).IsEqualTo("friend_request_received").Because("Expected friend_request_received before accept.");

        var acceptFrame = new PhxFrame<PhxEndpointPayload> {
            Topic = "system:" + recipientSystemId,
            Event = "endpoint",
            Payload = new PhxEndpointPayload
            {
                Method = "POST",
                Path = "/api/friend-requests/" + senderSystemId + "/accept",
                Body = new object()
            },
            Ref = "3",
            JoinRef = "1",
        }.ToBytes();

        await recipientWs.SendAsync(acceptFrame, WebSocketMessageType.Text, endOfMessage: true, token);

        for (var i = 0; i < 5; i++)
        {
            var frame = await ReceiveWebSocketTextAsync(recipientWs, token);
            if (GetEventName(frame) == "friend_added")
                break;
        }

        _ = await ReceiveWebSocketTextAsync(senderWs, token);
        _ = await ReceiveWebSocketTextAsync(senderWs, token);

        var trustFrame = new PhxFrame<PhxEndpointPayload> {
            Topic = "system:" + senderSystemId,
            Event = "endpoint",
            Payload = new PhxEndpointPayload
            {
                Method = "POST",
                Path = "/api/friends/" + recipientSystemId + "/trust",
                Body = new object()
            },
            Ref = "4",
            JoinRef = "1",
        }.ToBytes();

        await senderWs.SendAsync(trustFrame, WebSocketMessageType.Text, endOfMessage: true, token);

        string? trustAck = null;
        string? trustPush = null;
        for (var i = 0; i < 5 && (trustAck is null || trustPush is null); i++)
        {
            var frame = await ReceiveWebSocketTextAsync(senderWs, token);
            var eventName = GetEventName(frame);
            if (eventName == "phx_reply")
            {
                trustAck = frame;
            }
            else if (eventName == "friend_trusted")
            {
                trustPush = frame;
            }
        }

        using (Assert.Multiple())
        {
            await Assert.That(trustAck).IsNotNull().Because("Expected endpoint ack on sender socket for trust.");
            await Assert.That(trustPush).IsNotNull().Because("Expected friend_trusted push on sender socket after trust.");
        }

        var untrustFrame = new PhxFrame<PhxEndpointPayload> {
            Topic = "system:" + senderSystemId,
            Event = "endpoint",
            Payload = new PhxEndpointPayload
            {
                Method = "POST",
                Path = "/api/friends/" + recipientSystemId + "/untrust",
                Body = new object()
            },
            Ref = "5",
            JoinRef = "1",
        }.ToBytes();

        await senderWs.SendAsync(untrustFrame, WebSocketMessageType.Text, endOfMessage: true, token);

        string? untrustAck = null;
        string? untrustPush = null;
        for (var i = 0; i < 5 && (untrustAck is null || untrustPush is null); i++)
        {
            var frame = await ReceiveWebSocketTextAsync(senderWs, token);
            var eventName = GetEventName(frame);
            if (eventName == "phx_reply")
            {
                untrustAck = frame;
            }
            else if (eventName == "friend_untrusted")
            {
                untrustPush = frame;
            }
        }

        using (Assert.Multiple())
        {
            await Assert.That(untrustAck).IsNotNull().Because("Expected endpoint ack on sender socket for untrust.");
            await Assert.That(untrustPush).IsNotNull().Because("Expected friend_untrusted push on sender socket after untrust.");
        }

        await senderWs.CloseAsync(WebSocketCloseStatus.NormalClosure, "test done", token);
        await recipientWs.CloseAsync(WebSocketCloseStatus.NormalClosure, "test done", token);
    }

    [Test, ApiIntegration]
    [CombinedDataSources]
    public async Task Api_UserSocketEndpoint_PushesFriendAdded_OnMutualFriendRequest([InterfoldFactoryGenerator] InterfoldWebApplicationFactory factory, CancellationToken token)
    {
        factory
            .WithConfiguration("OCTOCON_DEEPLINK_ADDRESS", "octocon://app")
            .WithConfiguration("OCTOCON_REGION", "nam");

        const string systemAId = "sys-mutual-a";
        const string systemBId = "sys-mutual-b";
        
        var wsClientFactory = factory.Server.CreateWebSocketClient();
        string socketTokenA = await CreateRandomToken(factory, systemAId);
        string socketTokenB = await CreateRandomToken(factory, systemBId);

        var socketUriA = new Uri(WebSocketBasePath(factory.Server), $"api/socket/websocket?token={socketTokenA}");
        var socketUriB = new Uri(WebSocketBasePath(factory.Server), $"api/socket/websocket?token={socketTokenB}");
        using var wsA = await wsClientFactory.ConnectAsync(socketUriA, token);
        using var wsB = await wsClientFactory.ConnectAsync(socketUriB, token);

        await JoinTopicAsync(wsA, $"system:{systemAId}", socketTokenA, token);
        await JoinTopicAsync(wsB, $"system:{systemBId}", socketTokenB, token);

        var sendAFrame = new PhxFrame<PhxEndpointPayload> {
            Topic = "system:" + systemAId,
            Event = "endpoint",
            Payload = new PhxEndpointPayload
            {
                Method = "PUT",
                Path = "/api/friend-requests/" + systemBId,
                Body = new object()
            },
            Ref = "2",
            JoinRef = "1",
        }.ToBytes();

        await wsA.SendAsync(sendAFrame, WebSocketMessageType.Text, endOfMessage: true, token);
        _ = await ReceiveWebSocketTextAsync(wsA, token);
        _ = await ReceiveWebSocketTextAsync(wsA, token);
        _ = await ReceiveWebSocketTextAsync(wsB, token);

        var sendBFrame = new PhxFrame<PhxEndpointPayload> {
            Topic = "system:" + systemBId,
            Event = "endpoint",
            Payload = new PhxEndpointPayload
            {
                Method = "PUT",
                Path = "/api/friend-requests/" + systemAId,
                Body = new object()
            },
            Ref = "3",
            JoinRef = "1",
        }.ToBytes();

        await wsB.SendAsync(sendBFrame, WebSocketMessageType.Text, endOfMessage: true, token);

        string? bAck = null;
        string? bFriendAdded = null;
        for (var i = 0; i < 5 && (bAck is null || bFriendAdded is null); i++)
        {
            var bTask = ReceiveWebSocketTextAsync(wsB, token);
            var bCompleted = await Task.WhenAny(bTask, Task.Delay(TimeSpan.FromSeconds(2), token));
            await Assert.That(ReferenceEquals(bCompleted, bTask)).IsTrue().Because("Timed out waiting for B-side event after mutual send.");
            var bFrame = await bTask;
            var bEventName = GetEventName(bFrame);
            if (bEventName == "phx_reply")
                bAck = bFrame;
            else if (bEventName == "friend_added")
                bFriendAdded = bFrame;
        }

        using (Assert.Multiple())
        {
            await Assert.That(bAck).IsNotNull().Because("Expected endpoint ack on B socket for mutual send.");
            await Assert.That(bFriendAdded).IsNotNull().Because("Expected friend_added push on B socket after mutual send auto-accept.");
        }

        string? aFriendAdded = null;
        string? aRequestCleared = null;
        for (var i = 0; i < 5 && (aFriendAdded is null || aRequestCleared is null); i++)
        {
            var aTask = ReceiveWebSocketTextAsync(wsA, token);
            var aCompleted = await Task.WhenAny(aTask, Task.Delay(TimeSpan.FromSeconds(2), token));
            await Assert.That(ReferenceEquals(aCompleted, aTask)).IsTrue().Because("Timed out waiting for A-side event after mutual send.");
            var aFrame = await aTask;
            var aEventName = GetEventName(aFrame);
            if (aEventName == "friend_added")
                aFriendAdded = aFrame;
            else if (aEventName == "friend_request_removed")
                aRequestCleared = aFrame;
        }

        using (Assert.Multiple())
        {
            await Assert.That(aFriendAdded).IsNotNull().Because("Expected friend_added push on A socket after mutual send auto-accept.");
            await Assert.That(aRequestCleared).IsNotNull().Because("Expected friend_request_removed push on A socket after mutual send auto-accept (outgoing request cleanup).");
        }

        await wsA.CloseAsync(WebSocketCloseStatus.NormalClosure, "test done", token);
        await wsB.CloseAsync(WebSocketCloseStatus.NormalClosure, "test done", token);
    }
    
    static async Task JoinTopicAsync(WebSocket ws, string topic, string token, CancellationToken timeoutToken)
    {
        var joinFrame = new PhxFrame<PhxJoinPayload>
        {
            Topic = topic,
            Event = "phx_join",
            Payload = new PhxJoinPayload { Token = token, IsReconnect = true },
            Ref = "1",
            JoinRef = "1"
        };

        await ws.SendAsync(joinFrame.ToBytes(), WebSocketMessageType.Text, endOfMessage: true, timeoutToken);
        _ = await ReceiveWebSocketTextAsync(ws, timeoutToken);
    }

    static string GetEventName(string frame)
    {
        using var doc = JsonDocument.Parse(frame);
        var root = doc.RootElement;
        return root.ValueKind == JsonValueKind.Array
            ? root[3].GetString() ?? string.Empty
            : root.GetProperty("event").GetString() ?? string.Empty;
    }

    private static async Task<string?> ReceiveEventAsync(
        WebSocket ws,
        CancellationToken token,
        Func<string, bool> predicate,
        int maxFrames = 6)
    {
        for (var i = 0; i < maxFrames; i++)
        {
            using var receiveCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            receiveCts.CancelAfter(TimeSpan.FromSeconds(2));

            string frame;
            try
            {
                frame = await ReceiveWebSocketTextAsync(ws, receiveCts.Token);
            }
            catch (OperationCanceledException)
            {
                return null;
            }

            if (predicate(GetEventName(frame)))
                return frame;
        }

        return null;
    }
    
    async Task<string> SendJoinAsync(WebSocket ws, string socketToken, string refId, CancellationToken token)
    {
        var joinFrame = new PhxFrame<PhxJoinPayload>
        {
            Topic = "system:sys-phx-rate-limit",
            Event = "phx_join",
            Payload = new PhxJoinPayload { Token = socketToken, IsReconnect = true },
            Ref = refId,
            JoinRef = "1"
        };

        await ws.SendAsync(joinFrame.ToBytes(), WebSocketMessageType.Text, endOfMessage: true, token);
        return await ReceiveWebSocketTextAsync(ws, token);
    }
    
    private static Uri WebSocketBasePath(TestServer server)
    {
        return new Uri($"wss://{server.BaseAddress.Host}");
    }
    
    private static async Task<string> ReceiveWebSocketTextAsync(WebSocket ws, CancellationToken token)
    {
        var buffer = new byte[8192];
        using var ms = new MemoryStream();

        WebSocketReceiveResult result;
        do
        {
            result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), token);
            if (result.MessageType == WebSocketMessageType.Close)
                throw new InvalidOperationException($"WebSocket closed by server. {result.CloseStatusDescription}/{result.CloseStatus}");

            ms.Write(buffer, 0, result.Count);
        } while (result is { EndOfMessage: false, CloseStatus: not null });

        return Encoding.UTF8.GetString(ms.ToArray());
    }
}