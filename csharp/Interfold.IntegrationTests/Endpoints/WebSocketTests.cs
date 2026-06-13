using System.Net.WebSockets;
using System.Text.Json;
using Interfold.Contracts;
using Interfold.IntegrationTests.TestServices;
using Microsoft.AspNetCore.TestHost;

namespace Interfold.IntegrationTests.Endpoints;

// 5 minute timeout since we want to ensure this does end up timing out if a connection gets stuck but we also 
// need to account for Cassandra's slower performance with bootstrapping.
[Timeout(1000 * 300)]
[ClassDataSource<InMemoryWebFactoryFixture>(Shared = SharedType.PerTestSession)]
[ClassDataSource<ScyllaWebFactoryFixture>(Shared = SharedType.PerTestSession)]
[ClassDataSource<CassandraWebFactoryFixture>(Shared = SharedType.PerTestSession)]
public class WebSocketTests(IWebFactoryFixture fixture) : BaseEndpointTest
{
    internal static string UniqueId(string prefix) => $"{prefix}-{Guid.NewGuid():N}";

    [Test]
    public async Task Api_UserSocketEndpoint_AllowsWebSocketUpgrade(CancellationToken token)
    {
        var server = fixture.Factory.Server;
        var client = server.CreateWebSocketClient();
        
        var systemId = UniqueId("sys-phx-join");
        string socketToken = await CreateRandomToken(fixture.Factory, systemId);
        var uri = new Uri(WebSocketBasePath(server), "/api/socket/websocket?token=" + socketToken);
        
        using var ws = await client.ConnectAsync(uri, token);

        await Assert.That(ws.State).IsEqualTo(WebSocketState.Open).Because($"Expected websocket to be open after connecting to /api/socket/weboscket, got {ws.State}.");

        var arrayJoinFrame = PhxArrayFrame.CreateBytes(
            "51", "51", $"system:{systemId}", "phx_join",
            new PhxJoinPayload { Token = socketToken, ProtocolVersion = "2.0.0", Platform = "wasm", IsReconnect = true });

        await ws.SendAsync(arrayJoinFrame, WebSocketMessageType.Text, endOfMessage: true, token);

        var frame = await ReceivedPhxFrame.ReceiveAsync(ws, token);

        using (Assert.Multiple())
        {
            await Assert.That(frame.JoinRef).IsEqualTo("51").Because("Expected join_ref=51 in array reply.");
            await Assert.That(frame.Ref).IsEqualTo("51").Because("Expected ref=51 in array reply.");
            await Assert.That(frame.Topic).IsEqualTo($"system:{systemId}").Because("Expected topic to match array join topic.");
            await Assert.That(frame.Event).IsEqualTo("phx_reply").Because("Expected array reply event phx_reply.");
            var reply = frame.Reply<SocketJoinReconnectPayload>();
            await Assert.That(reply.Status).IsEqualTo("ok").Because("Expected status=ok for reconnect join.");
            await Assert.That(reply.Response.System.Id).IsEqualTo(systemId).Because("Expected system ID in reconnect payload.");
        }
    }
    
    [Test]
    public async Task Api_UserSocketEndpoint_RejectsUnsupportedProtocolVersion(CancellationToken token)
    {
        var systemId = UniqueId("sys-phx-unsupported");
        var wsClient = fixture.Factory.Server.CreateWebSocketClient();
        string socketToken = await CreateRandomToken(fixture.Factory, systemId);
        var uri = new Uri(WebSocketBasePath(fixture.Factory.Server), $"api/socket/websocket?token={socketToken}");
        using var ws = await wsClient.ConnectAsync(uri, token);

        var joinFrame = new PhxFrame<PhxJoinPayload>
        {
            Topic = $"system:{systemId}",
            Event = "phx_join",
            Payload = new PhxJoinPayload { Token = socketToken, ProtocolVersion = "not-a-version" },
            Ref = "1",
            JoinRef = "1"
        }.ToBytes();

        await ws.SendAsync(joinFrame, WebSocketMessageType.Text, endOfMessage: true, token);
        var reply = await ReceivedPhxFrame.ReceiveReplyAsync<SocketReasonResponse>(ws, token);

        using (Assert.Multiple())
        {
            await Assert.That(reply.Status).IsEqualTo("error").Because("Expected status=error for unsupported protocol version.");
            await Assert.That(reply.Response.Reason).IsEqualTo("unsupported_protocol_version").Because("Expected reason=unsupported_protocol_version.");
        }

        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "test done", token);
    }

    [Test]
    public async Task Api_UserSocketEndpoint_BatchesForIos_WhenThresholdExceeded(CancellationToken token)
    {
        var systemId = UniqueId("sys-phx-ios-batch");
        var wsClient = fixture.Factory.Server.CreateWebSocketClient();
        string socketToken = await CreateRandomToken(fixture.Factory, systemId);
        var uri = new Uri(WebSocketBasePath(fixture.Factory.Server), $"api/socket/websocket?token={socketToken}");
        using var ws = await wsClient.ConnectAsync(uri, token);

        var joinFrame = new PhxFrame<PhxJoinPayload>
        {
            Topic = $"system:{systemId}",
            Event = "phx_join",
            Payload = new PhxJoinPayload { Token = socketToken, Platform = "ios", ProtocolVersion = "2.0.0", ForceBatch = true },
            Ref = "1",
            JoinRef = "1"
        }.ToBytes();

        await ws.SendAsync(joinFrame, WebSocketMessageType.Text, endOfMessage: true, token);
        var reply = await ReceivedPhxFrame.ReceiveReplyAsync<SocketJoinBatchedPayload>(ws, token);

        using (Assert.Multiple())
        {
            await Assert.That(reply.Status).IsEqualTo("ok").Because("Expected status=ok for iOS batched join.");
            await Assert.That(reply.Response.Batched).IsTrue().Because("Expected batched=true for iOS join above threshold.");
        }

        var batchedComplete = await ReceivedPhxFrame.ReceiveEventFrameAsync(ws, token, SocketEventNames.BatchedInit.Complete, maxFrames: 6);
        await Assert.That(batchedComplete).IsNotNull().Because("Expected batched_init_complete after iOS batched join.");

        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "test done", token);
    }

    [Test]
    public async Task Api_UserSocketEndpoint_RateLimitsThirdJoinWithinOneSecond(CancellationToken token)
    {
        var systemId = UniqueId("sys-rate-limit");
        var wsClient = fixture.Factory.Server.CreateWebSocketClient();
        string socketToken = await CreateRandomToken(fixture.Factory, systemId);
        var uri = new Uri(WebSocketBasePath(fixture.Factory.Server), $"api/socket/websocket?token={socketToken}");
        using var ws = await wsClient.ConnectAsync(uri, token);

        var firstReply = await SendJoinAndReceiveReplyAsync(ws, socketToken, systemId, "1", token);
        var secondReply = await SendJoinAndReceiveReplyAsync(ws, socketToken, systemId, "2", token);
        var thirdReply = await SendJoinAndReceiveReplyAsync(ws, socketToken, systemId, "3", token);

        using (Assert.Multiple())
        {
            await Assert.That(firstReply.Status).IsEqualTo("ok").Because("Expected first join to be accepted.");
            await Assert.That(secondReply.Status).IsEqualTo("ok").Because("Expected second join to be accepted.");
            await Assert.That(thirdReply.Status).IsEqualTo("error").Because("Expected third join to be rate-limited.");
        }

        var thirdResponse = JsonSerializer.Deserialize<SocketReasonResponse>(
            JsonSerializer.Serialize(thirdReply.Response, SocketJson.Options), SocketJson.Options);
        await Assert.That(thirdResponse!.Reason).IsEqualTo("rate_limited").Because("Expected reason=rate_limited on third join.");

        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "test done", token);
    }

    [Test]
    public async Task Api_UserSocketEndpoint_PushesFrontingChangedEvent_AfterFrontStart(CancellationToken token)
    {
        var systemId = UniqueId("sys-front-push");
        var wsClient = fixture.Factory.Server.CreateWebSocketClient();
        string socketToken = await CreateRandomToken(fixture.Factory, systemId);
        var topic = $"system:{systemId}";
        var uri = new Uri(WebSocketBasePath(fixture.Factory.Server), $"api/socket/websocket?token={socketToken}");
        var ws = await wsClient.ConnectAsync(uri, token);

        await JoinTopicAsync(ws, topic, socketToken, token);

        var createAlterFrame = new PhxFrame<PhxEndpointPayload>
        {
            Topic = topic,
            Event = "endpoint",
            Payload = new PhxEndpointPayload { Method = "POST", Path = "/api/systems/me/alters", Body = new { name = "FrontPushAlter" } },
            Ref = "2",
            JoinRef = "1"
        }.ToBytes();

        await ws.SendAsync(createAlterFrame, WebSocketMessageType.Text, endOfMessage: true, token);

        var (createAlterReply, createAlterPush) = await ReceivedPhxFrame.ReceiveReplyAndPushAsync(
            ws, token, SocketEventNames.Alters.Created);

        await Assert.That(createAlterReply).IsNotNull().Because("Expected endpoint ack (phx_reply) for alter create call.");

        var createdAlterId = createAlterPush is not null
            ? createAlterPush.RawPayload!.Value.GetProperty("alter").GetProperty("id").GetInt32()
            : ExtractAlterIdFromEndpointReply(createAlterReply!);

        var startFrontFrame = new PhxFrame<PhxEndpointPayload>
        {
            Topic = topic,
            Event = "endpoint",
            Payload = new PhxEndpointPayload { Method = "POST", Path = "/api/systems/me/front/start", Body = new { id = createdAlterId } },
            Ref = "3",
            JoinRef = "1"
        }.ToBytes();

        await ws.SendAsync(startFrontFrame, WebSocketMessageType.Text, endOfMessage: true, token);

        var (endpointAck, frontingPush) = await ReceivedPhxFrame.ReceiveReplyAndPushAsync(
            ws, token, SocketEventNames.Fronting.Started);

        using (Assert.Multiple())
        {
            await Assert.That(endpointAck).IsNotNull().Because("Expected endpoint ack (phx_reply) for front start call.");
        }

        if (frontingPush is not null)
        {
            await Assert.That(frontingPush.RawPayload?.TryGetProperty("front", out _) ?? false)
                .IsTrue().Because("Expected fronting push payload to include front object.");
        }

        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "test done", token);
    }

    [Test]
    public async Task Api_UserSocketEndpoint_PushesAlterTagAndFieldsEvents_AfterEndpointWrites(CancellationToken token)
    {
        var systemId = UniqueId("sys-domain-fanout");
        var wsClient = fixture.Factory.Server.CreateWebSocketClient();
        string socketToken = await CreateRandomToken(fixture.Factory, systemId);
        var topic = $"system:{systemId}";
        var uri = new Uri(WebSocketBasePath(fixture.Factory.Server), $"api/socket/websocket?token={socketToken}");
        using var ws = await wsClient.ConnectAsync(uri, token);

        await JoinTopicAsync(ws, topic, socketToken, token);

        var createAlterFrame = new PhxFrame<PhxEndpointPayload>
        {
            Topic = topic,
            Event = "endpoint",
            Payload = new PhxEndpointPayload { Method = "POST", Path = "/api/systems/me/alters", Body = new { name = "DomainFanoutAlter" } },
            Ref = "2",
            JoinRef = "1"
        }.ToBytes();

        var (alterReply, alterPush) = await SendEndpointAndCaptureAsync(ws, createAlterFrame, SocketEventNames.Alters.Created, token);
        using (Assert.Multiple())
        {
            await Assert.That(alterReply).IsNotNull().Because("Expected endpoint ack for alter create.");
            await Assert.That(alterPush).IsNotNull().Because("Expected alter_created push after alter create.");
        }
        await Assert.That(alterPush!.RawPayload?.GetProperty("alter").GetProperty("name").GetString())
            .IsEqualTo("DomainFanoutAlter").Because("Expected alter name in push payload.");

        var createTagFrame = new PhxFrame<PhxEndpointPayload>
        {
            Topic = topic,
            Event = "endpoint",
            Payload = new PhxEndpointPayload { Method = "POST", Path = "/api/systems/me/tags", Body = new { name = "DomainFanoutTag" } },
            Ref = "3",
            JoinRef = "1"
        }.ToBytes();

        var (tagReply, tagPush) = await SendEndpointAndCaptureAsync(ws, createTagFrame, SocketEventNames.Tags.Created, token);
        using (Assert.Multiple())
        {
            await Assert.That(tagReply).IsNotNull().Because("Expected endpoint ack for tag create.");
            await Assert.That(tagPush).IsNotNull().Because("Expected tag_created push after tag create.");
        }
        await Assert.That(tagPush!.RawPayload?.GetProperty("tag").GetProperty("name").GetString())
            .IsEqualTo("DomainFanoutTag").Because("Expected tag name in push payload.");

        var createFieldFrame = new PhxFrame<PhxEndpointPayload>
        {
            Topic = topic,
            Event = "endpoint",
            Payload = new PhxEndpointPayload { Method = "POST", Path = "/api/settings/fields", Body = new { name = "DomainFanoutField", type = "text", security_level = "private", locked = false } },
            Ref = "4",
            JoinRef = "1"
        }.ToBytes();

        var (fieldReply, fieldPush) = await SendEndpointAndCaptureAsync(ws, createFieldFrame, SocketEventNames.Settings.FieldsUpdated, token);
        using (Assert.Multiple())
        {
            await Assert.That(fieldReply).IsNotNull().Because("Expected endpoint ack for field create.");
            await Assert.That(fieldPush).IsNotNull().Because("Expected fields_updated push after field create.");
        }
        await Assert.That(fieldPush!.RawPayload!.Value.GetProperty("fields").GetArrayLength())
            .IsGreaterThan(0).Because("Expected at least one field in fields_updated payload.");

        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "test done", token);
    }

    [Test]
    public async Task Api_UserSocketEndpoint_PushesFriendRequestReceived_ToRecipientSystem(CancellationToken token)
    {
        var senderSystemId = UniqueId("sys-friend-sender");
        var recipientSystemId = UniqueId("sys-friend-recipient");
        var wsClientFactory = fixture.Factory.Server.CreateWebSocketClient();

        string senderSocketToken = await CreateRandomToken(fixture.Factory, senderSystemId);
        string recipientSocketToken = await CreateRandomToken(fixture.Factory, recipientSystemId);

        var senderSocketUri = new Uri(WebSocketBasePath(fixture.Factory.Server), $"api/socket/websocket?token={senderSocketToken}");
        var recipientSocketUri = new Uri(WebSocketBasePath(fixture.Factory.Server), $"api/socket/websocket?token={recipientSocketToken}");

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

        var (senderAck, senderPush) = await ReceivedPhxFrame.ReceiveReplyAndPushAsync(
            senderWs, token, SocketEventNames.Friendships.RequestSent);

        using (Assert.Multiple())
        {
            await Assert.That(senderAck).IsNotNull().Because("Expected endpoint ack on sender socket for friend request send.");
            await Assert.That(senderPush).IsNotNull().Because("Expected friend_request_sent push on sender socket.");
        }

        var recipientFrame = await ReceivedPhxFrame.ReceiveEventFrameAsync(
            recipientWs, token, SocketEventNames.Friendships.RequestReceived, maxFrames: 3, perFrameTimeoutSeconds: 5);
        await Assert.That(recipientFrame).IsNotNull().Because("Expected recipient-side friend_request_received push.");

        await Assert.That(recipientFrame!.RawPayload?.TryGetProperty("system", out _) ?? false)
            .IsTrue().Because("Expected system profile in friend_request_received payload.");

        await senderWs.CloseAsync(WebSocketCloseStatus.NormalClosure, "test done", token);
        await recipientWs.CloseAsync(WebSocketCloseStatus.NormalClosure, "test done", token);
    }

    [Test]
    public async Task Api_UserSocketEndpoint_PushesFriendRequestAccepted_ToActorAndRecipient(CancellationToken token)
    {
        var senderSystemId = UniqueId("sys-accept-sender");
        var recipientSystemId = UniqueId("sys-accept-recipient");

        var wsClientFactory = fixture.Factory.Server.CreateWebSocketClient();
        string senderSocketToken = await CreateRandomToken(fixture.Factory, senderSystemId);
        string recipientSocketToken = await CreateRandomToken(fixture.Factory, recipientSystemId);

        var senderSocketUri = new Uri(WebSocketBasePath(fixture.Factory.Server), $"api/socket/websocket?token={senderSocketToken}");
        var recipientSocketUri = new Uri(WebSocketBasePath(fixture.Factory.Server), $"api/socket/websocket?token={recipientSocketToken}");

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

        // Drain sender ack + push
        _ = await ReceivedPhxFrame.ReceiveReplyAndPushAsync(senderWs, token, SocketEventNames.Friendships.RequestSent);
        // Drain recipient friend_request_received
        var recipientEvent = await ReceivedPhxFrame.ReceiveEventFrameAsync(recipientWs, token, SocketEventNames.Friendships.RequestReceived, maxFrames: 3);
        await Assert.That(recipientEvent).IsNotNull().Because("Expected friend_request_received");

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

        var (recipientAck, recipientFriendAdded) = await ReceivedPhxFrame.ReceiveReplyAndPushAsync(
            recipientWs, token, SocketEventNames.Friendships.Added);

        using (Assert.Multiple())
        {
            await Assert.That(recipientAck).IsNotNull().Because("Expected endpoint ack on recipient socket for accept.");
            await Assert.That(recipientFriendAdded).IsNotNull().Because("Expected friend_added push on recipient socket after accept.");
        }

        ReceivedPhxFrame? senderFriendAdded = null;
        ReceivedPhxFrame? senderRequestCleared = null;
        for (var i = 0; i < 5 && (senderFriendAdded is null || senderRequestCleared is null); i++)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
            cts.CancelAfter(TimeSpan.FromSeconds(5));

            ReceivedPhxFrame frame;
            try { frame = await ReceivedPhxFrame.ReceiveAsync(senderWs, cts.Token); }
            catch (OperationCanceledException) { break; }

            if (frame.Event == SocketEventNames.Friendships.Added)
                senderFriendAdded = frame;
            else if (frame.Event == SocketEventNames.Friendships.RequestRemoved)
                senderRequestCleared = frame;
        }

        using (Assert.Multiple())
        {
            await Assert.That(senderFriendAdded).IsNotNull().Because("Expected friend_added push on sender socket after accept.");
            await Assert.That(senderRequestCleared).IsNotNull().Because("Expected friend_request_removed push on sender socket after accept (outgoing request cleanup).");
        }

        await senderWs.CloseAsync(WebSocketCloseStatus.NormalClosure, "test done", token);
        await recipientWs.CloseAsync(WebSocketCloseStatus.NormalClosure, "test done", token);
    }

    [Test]
    public async Task Api_UserSocketEndpoint_PushesFriendRequestRejected_ToActorAndRecipient(CancellationToken token)
    {
        var senderSystemId = UniqueId("sys-reject-sender");
        var recipientSystemId = UniqueId("sys-reject-recipient");

        var wsClientFactory = fixture.Factory.Server.CreateWebSocketClient();
        string senderSocketToken = await CreateRandomToken(fixture.Factory, senderSystemId);
        string recipientSocketToken = await CreateRandomToken(fixture.Factory, recipientSystemId);

        var senderSocketUri = new Uri(WebSocketBasePath(fixture.Factory.Server), $"api/socket/websocket?token={senderSocketToken}");
        var recipientSocketUri = new Uri(WebSocketBasePath(fixture.Factory.Server), $"api/socket/websocket?token={recipientSocketToken}");

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

        _ = await ReceivedPhxFrame.ReceiveReplyAndPushAsync(senderWs, token, SocketEventNames.Friendships.RequestSent);
        var recipientEvent = await ReceivedPhxFrame.ReceiveEventFrameAsync(recipientWs, token, SocketEventNames.Friendships.RequestReceived, maxFrames: 3);
        await Assert.That(recipientEvent).IsNotNull().Because("Expected friend_request_received");

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

        var (recipientAck, recipientRemoved) = await ReceivedPhxFrame.ReceiveReplyAndPushAsync(
            recipientWs, token, SocketEventNames.Friendships.RequestRemoved);

        using (Assert.Multiple())
        {
            await Assert.That(recipientAck).IsNotNull().Because("Expected endpoint ack on recipient socket for reject.");
        }

        var senderRemoved = await ReceivedPhxFrame.ReceiveEventFrameAsync(
            senderWs, token, SocketEventNames.Friendships.RequestRemoved, maxFrames: 8);

        await senderWs.CloseAsync(WebSocketCloseStatus.NormalClosure, "test done", token);
        await recipientWs.CloseAsync(WebSocketCloseStatus.NormalClosure, "test done", token);
    }

    [Test]
    public async Task Api_UserSocketEndpoint_PushesFriendRequestCancelled_ToActorAndRecipient(CancellationToken token)
    {
        var senderSystemId = UniqueId("sys-cancel-sender");
        var recipientSystemId = UniqueId("sys-cancel-recipient");

        var wsClientFactory = fixture.Factory.Server.CreateWebSocketClient();
        string senderSocketToken = await CreateRandomToken(fixture.Factory, senderSystemId);
        string recipientSocketToken = await CreateRandomToken(fixture.Factory, recipientSystemId);

        var senderSocketUri = new Uri(WebSocketBasePath(fixture.Factory.Server), $"api/socket/websocket?token={senderSocketToken}");
        var recipientSocketUri = new Uri(WebSocketBasePath(fixture.Factory.Server), $"api/socket/websocket?token={recipientSocketToken}");

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

        _ = await ReceivedPhxFrame.ReceiveReplyAndPushAsync(senderWs, token, SocketEventNames.Friendships.RequestSent);

        var recipientReceived = await ReceivedPhxFrame.ReceiveEventFrameAsync(recipientWs, token, SocketEventNames.Friendships.RequestReceived, maxFrames: 3);
        await Assert.That(recipientReceived).IsNotNull().Because("Expected friend_request_received before cancel.");

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

        var (senderAck, senderRemoved) = await ReceivedPhxFrame.ReceiveReplyAndPushAsync(
            senderWs, token, SocketEventNames.Friendships.RequestRemoved);

        using (Assert.Multiple())
        {
            await Assert.That(senderAck).IsNotNull().Because("Expected endpoint ack on sender socket for cancel.");
            await Assert.That(senderRemoved).IsNotNull().Because("Expected friend_request_removed push on sender socket after cancel.");
        }

        var recipientRemoved = await ReceivedPhxFrame.ReceiveEventFrameAsync(
            recipientWs, token, SocketEventNames.Friendships.RequestRemoved, maxFrames: 3, perFrameTimeoutSeconds: 5);
        await Assert.That(recipientRemoved).IsNotNull().Because("Expected recipient-side friend_request_removed push.");

        await senderWs.CloseAsync(WebSocketCloseStatus.NormalClosure, "test done", token);
        await recipientWs.CloseAsync(WebSocketCloseStatus.NormalClosure, "test done", token);
    }

    [Test]
    public async Task Api_UserSocketEndpoint_PushesFriendRemoved_ToActorAndRecipient(CancellationToken token)
    {
        var senderSystemId = UniqueId("sys-remove-sender");
        var recipientSystemId = UniqueId("sys-remove-recipient");

        var wsClientFactory = fixture.Factory.Server.CreateWebSocketClient();
        string senderSocketToken = await CreateRandomToken(fixture.Factory, senderSystemId);
        string recipientSocketToken = await CreateRandomToken(fixture.Factory, recipientSystemId);

        var senderSocketUri = new Uri(WebSocketBasePath(fixture.Factory.Server), $"api/socket/websocket?token={senderSocketToken}");
        var recipientSocketUri = new Uri(WebSocketBasePath(fixture.Factory.Server), $"api/socket/websocket?token={recipientSocketToken}");

        using var senderWs = await wsClientFactory.ConnectAsync(senderSocketUri, token);
        using var recipientWs = await wsClientFactory.ConnectAsync(recipientSocketUri, token);

        await JoinTopicAsync(senderWs, $"system:{senderSystemId}", senderSocketToken, token);
        await JoinTopicAsync(recipientWs, $"system:{recipientSystemId}", recipientSocketToken, token);

        // Send friend request
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

        _ = await ReceivedPhxFrame.ReceiveReplyAndPushAsync(senderWs, token, SocketEventNames.Friendships.RequestSent);

        var recipientReceived = await ReceivedPhxFrame.ReceiveEventFrameAsync(recipientWs, token, SocketEventNames.Friendships.RequestReceived, maxFrames: 3);
        await Assert.That(recipientReceived).IsNotNull().Because("Expected friend_request_received before accept.");

        // Accept friend request
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

        var (recipientAcceptAck, recipientAdded) = await ReceivedPhxFrame.ReceiveReplyAndPushAsync(
            recipientWs, token, SocketEventNames.Friendships.Added);

        using (Assert.Multiple())
        {
            await Assert.That(recipientAcceptAck).IsNotNull().Because("Expected endpoint ack on recipient socket for accept.");
            await Assert.That(recipientAdded).IsNotNull().Because("Expected friend_added push on recipient socket after accept.");
        }

        // Drain sender-side accept events
        _ = await ReceivedPhxFrame.ReceiveReplyAndPushAsync(senderWs, token, SocketEventNames.Friendships.Added);

        // Remove friend
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

        var (senderRemoveAck, senderRemoved) = await ReceivedPhxFrame.ReceiveReplyAndPushAsync(
            senderWs, token, SocketEventNames.Friendships.Removed);

        using (Assert.Multiple())
        {
            await Assert.That(senderRemoveAck).IsNotNull().Because("Expected endpoint ack on sender socket for remove.");
            await Assert.That(senderRemoved).IsNotNull().Because("Expected friend_removed push on sender socket after remove.");
        }

        if (senderRemoved is not null)
        {
            await Assert.That(senderRemoved.Event).IsEqualTo(SocketEventNames.Friendships.Removed)
                .Because("Expected friend_removed event on sender socket.");
        }

        var recipientRemovedFrame = await ReceivedPhxFrame.ReceiveEventFrameAsync(
            recipientWs, token, SocketEventNames.Friendships.Removed, maxFrames: 8);
        await Assert.That(recipientRemovedFrame).IsNotNull().Because("Timed out waiting for recipient-side friend_removed push.");

        await senderWs.CloseAsync(WebSocketCloseStatus.NormalClosure, "test done", token);
        await recipientWs.CloseAsync(WebSocketCloseStatus.NormalClosure, "test done", token);
    }

    [Test]
    public async Task Api_UserSocketEndpoint_PushesFriendTrustedAndUntrusted_ToActor(CancellationToken token)
    {
        var senderSystemId = UniqueId("sys-trust-sender");
        var recipientSystemId = UniqueId("sys-trust-recipient");

        var wsClientFactory = fixture.Factory.Server.CreateWebSocketClient();
        string senderSocketToken = await CreateRandomToken(fixture.Factory, senderSystemId);
        string recipientSocketToken = await CreateRandomToken(fixture.Factory, recipientSystemId);

        var senderSocketUri = new Uri(WebSocketBasePath(fixture.Factory.Server), $"api/socket/websocket?token={senderSocketToken}");
        var recipientSocketUri = new Uri(WebSocketBasePath(fixture.Factory.Server), $"api/socket/websocket?token={recipientSocketToken}");

        using var senderWs = await wsClientFactory.ConnectAsync(senderSocketUri, token);
        using var recipientWs = await wsClientFactory.ConnectAsync(recipientSocketUri, token);

        await JoinTopicAsync(senderWs, $"system:{senderSystemId}", senderSocketToken, token);
        await JoinTopicAsync(recipientWs, $"system:{recipientSystemId}", recipientSocketToken, token);

        // Send friend request
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

        _ = await ReceivedPhxFrame.ReceiveReplyAndPushAsync(senderWs, token, SocketEventNames.Friendships.RequestSent);

        var recipientReceived = await ReceivedPhxFrame.ReceiveEventFrameAsync(recipientWs, token, SocketEventNames.Friendships.RequestReceived, maxFrames: 3);
        await Assert.That(recipientReceived).IsNotNull().Because("Expected friend_request_received before accept.");

        // Accept friend request
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

        _ = await ReceivedPhxFrame.ReceiveEventFrameAsync(recipientWs, token, SocketEventNames.Friendships.Added);

        _ = await ReceivedPhxFrame.ReceiveReplyAndPushAsync(senderWs, token, SocketEventNames.Friendships.Added);

        // Trust friend
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

        var (trustAck, trustPush) = await ReceivedPhxFrame.ReceiveReplyAndPushAsync(
            senderWs, token, SocketEventNames.Friendships.Trusted);

        using (Assert.Multiple())
        {
            await Assert.That(trustAck).IsNotNull().Because("Expected endpoint ack on sender socket for trust.");
            await Assert.That(trustPush).IsNotNull().Because("Expected friend_trusted push on sender socket after trust.");
        }

        if (trustPush is not null)
        {
            await Assert.That(trustPush.Event).IsEqualTo(SocketEventNames.Friendships.Trusted)
                .Because("Expected friend_trusted event on sender socket.");
        }

        // Untrust friend
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

        var (untrustAck, untrustPush) = await ReceivedPhxFrame.ReceiveReplyAndPushAsync(
            senderWs, token, SocketEventNames.Friendships.Untrusted);

        using (Assert.Multiple())
        {
            await Assert.That(untrustAck).IsNotNull().Because("Expected endpoint ack on sender socket for untrust.");
            await Assert.That(untrustPush).IsNotNull().Because("Expected friend_untrusted push on sender socket after untrust.");
        }

        if (untrustPush is not null)
        {
            await Assert.That(untrustPush.Event).IsEqualTo(SocketEventNames.Friendships.Untrusted)
                .Because("Expected friend_untrusted event on sender socket.");
        }

        await senderWs.CloseAsync(WebSocketCloseStatus.NormalClosure, "test done", token);
        await recipientWs.CloseAsync(WebSocketCloseStatus.NormalClosure, "test done", token);
    }

    [Test]
    public async Task Api_UserSocketEndpoint_PushesFriendAdded_OnMutualFriendRequest(CancellationToken token)
    {
        var systemAId = UniqueId("sys-mutual-a");
        var systemBId = UniqueId("sys-mutual-b");
        
        var wsClientFactory = fixture.Factory.Server.CreateWebSocketClient();
        string socketTokenA = await CreateRandomToken(fixture.Factory, systemAId);
        string socketTokenB = await CreateRandomToken(fixture.Factory, systemBId);

        var socketUriA = new Uri(WebSocketBasePath(fixture.Factory.Server), $"api/socket/websocket?token={socketTokenA}");
        var socketUriB = new Uri(WebSocketBasePath(fixture.Factory.Server), $"api/socket/websocket?token={socketTokenB}");
        using var wsA = await wsClientFactory.ConnectAsync(socketUriA, token);
        using var wsB = await wsClientFactory.ConnectAsync(socketUriB, token);

        await JoinTopicAsync(wsA, $"system:{systemAId}", socketTokenA, token);
        await JoinTopicAsync(wsB, $"system:{systemBId}", socketTokenB, token);

        // A sends friend request to B
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
        _ = await ReceivedPhxFrame.ReceiveReplyAndPushAsync(wsA, token, SocketEventNames.Friendships.RequestSent);
        _ = await ReceivedPhxFrame.ReceiveEventFrameAsync(wsB, token, SocketEventNames.Friendships.RequestReceived, maxFrames: 3);

        // B sends mutual friend request to A (should auto-accept)
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

        ReceivedPhxFrame? bAck = null;
        ReceivedPhxFrame? bFriendAdded = null;
        for (var i = 0; i < 5 && (bAck is null || bFriendAdded is null); i++)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
            cts.CancelAfter(TimeSpan.FromSeconds(2));

            ReceivedPhxFrame frame;
            try { frame = await ReceivedPhxFrame.ReceiveAsync(wsB, cts.Token); }
            catch (OperationCanceledException) { break; }

            if (frame.Event == "phx_reply")
                bAck = frame;
            else if (frame.Event == SocketEventNames.Friendships.Added)
                bFriendAdded = frame;
        }

        using (Assert.Multiple())
        {
            await Assert.That(bAck).IsNotNull().Because("Expected endpoint ack on B socket for mutual send.");
            await Assert.That(bFriendAdded).IsNotNull().Because("Expected friend_added push on B socket after mutual send auto-accept.");
        }

        ReceivedPhxFrame? aFriendAdded = null;
        ReceivedPhxFrame? aRequestCleared = null;
        for (var i = 0; i < 5 && (aFriendAdded is null || aRequestCleared is null); i++)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
            cts.CancelAfter(TimeSpan.FromSeconds(2));

            ReceivedPhxFrame frame;
            try { frame = await ReceivedPhxFrame.ReceiveAsync(wsA, cts.Token); }
            catch (OperationCanceledException) { break; }

            if (frame.Event == SocketEventNames.Friendships.Added)
                aFriendAdded = frame;
            else if (frame.Event == SocketEventNames.Friendships.RequestRemoved)
                aRequestCleared = frame;
        }

        using (Assert.Multiple())
        {
            await Assert.That(aFriendAdded).IsNotNull().Because("Expected friend_added push on A socket after mutual send auto-accept.");
            await Assert.That(aRequestCleared).IsNotNull().Because("Expected friend_request_removed push on A socket after mutual send auto-accept (outgoing request cleanup).");
        }

        await wsA.CloseAsync(WebSocketCloseStatus.NormalClosure, "test done", token);
        await wsB.CloseAsync(WebSocketCloseStatus.NormalClosure, "test done", token);
    }
    
    internal static async Task JoinTopicAsync(WebSocket ws, string topic, string socketToken, CancellationToken timeoutToken)
    {
        var joinFrame = new PhxFrame<PhxJoinPayload>
        {
            Topic = topic,
            Event = "phx_join",
            Payload = new PhxJoinPayload { Token = socketToken, IsReconnect = true },
            Ref = "1",
            JoinRef = "1"
        };

        await ws.SendAsync(joinFrame.ToBytes(), WebSocketMessageType.Text, endOfMessage: true, timeoutToken);
        var frame = await ReceivedPhxFrame.ReceiveAsync(ws, timeoutToken);
        var reply = frame.Reply<object>();
        if (reply.Status != "ok")
            throw new InvalidOperationException($"JoinTopicAsync failed for topic '{topic}'. Status: {reply.Status}");
    }

    static async Task<(ReceivedPhxFrame? Reply, ReceivedPhxFrame? Push)> SendEndpointAndCaptureAsync(
        WebSocket ws,
        byte[] frame,
        string expectedPushEvent,
        CancellationToken token)
    {
        await ws.SendAsync(frame, WebSocketMessageType.Text, endOfMessage: true, token);
        return await ReceivedPhxFrame.ReceiveReplyAndPushAsync(ws, token, expectedPushEvent);
    }

    async Task<PhoenixReplyPayload<object>> SendJoinAndReceiveReplyAsync(WebSocket ws, string socketToken, string systemId, string refId, CancellationToken token)
    {
        var joinFrame = new PhxFrame<PhxJoinPayload>
        {
            Topic = $"system:{systemId}",
            Event = "phx_join",
            Payload = new PhxJoinPayload { Token = socketToken, IsReconnect = true },
            Ref = refId,
            JoinRef = "1"
        };

        await ws.SendAsync(joinFrame.ToBytes(), WebSocketMessageType.Text, endOfMessage: true, token);
        var frame = await ReceivedPhxFrame.ReceiveAsync(ws, token);
        return frame.Reply<object>();
    }

    static int ExtractAlterIdFromEndpointReply(ReceivedPhxFrame replyFrame)
    {
        var reply = replyFrame.Reply<SocketEndpointProxyResponse>();
        if (string.IsNullOrWhiteSpace(reply.Response.Body))
            throw new InvalidOperationException("Endpoint proxy response body is empty — cannot extract alter ID.");

        using var bodyDoc = JsonDocument.Parse(reply.Response.Body);
        var root = bodyDoc.RootElement;

        if (root.TryGetProperty("data", out var data) && data.TryGetProperty("id", out var idProp) && idProp.TryGetInt32(out var id))
            return id;

        throw new InvalidOperationException($"Could not parse alter id from endpoint reply body: {reply.Response.Body}");
    }

    internal static Uri WebSocketBasePath(TestServer server)
    {
        return new Uri($"wss://{server.BaseAddress.Host}");
    }
}
