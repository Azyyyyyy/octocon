using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using TUnit.Core;

namespace Octocon.IntegrationTests;

public sealed class ApiAuthSmokeTests
{
    [Test]
    public async Task Api_AuthRequest_FallsBackTo403_WhenChallengeDisabled()
    {
        if (!ShouldRunApiIntegration())
            return;

        var workspaceRoot = FindWorkspaceRoot();
        var port = GetFreePort();

        await using var api = await StartApiAsync(
            workspaceRoot,
            port,
            devPrincipalAllowed: true,
            jwtAuthority: null);

        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            BaseAddress = new Uri($"http://127.0.0.1:{port}")
        };

        var response = await client.GetAsync("/auth/google");
        var body = await response.Content.ReadAsStringAsync();

        Ensure(response.StatusCode == HttpStatusCode.Forbidden,
            $"Expected /auth/google fallback 403 when challenge is disabled, got {(int)response.StatusCode}. Body: {body}");
    }

    [Test]
    public async Task Api_AuthRequest_IssuesChallengeRedirect_WhenChallengeEnabledAndSchemeConfigured()
    {
        if (!ShouldRunApiIntegration())
            return;

        var workspaceRoot = FindWorkspaceRoot();
        var port = GetFreePort();

        await using var api = await StartApiAsync(
            workspaceRoot,
            port,
            devPrincipalAllowed: true,
            jwtAuthority: null,
            additionalEnv: new Dictionary<string, string?>
            {
                ["OCTOCON_AUTH_CHALLENGE_ENABLED"] = "true",
                ["OCTOCON_AUTH_CHALLENGE_GOOGLE_SCHEME"] = "oauth-google",
                ["OCTOCON_AUTH_CHALLENGE_GOOGLE_ENDPOINT"] = "https://accounts.example.test/oauth/authorize"
            });

        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            BaseAddress = new Uri($"http://127.0.0.1:{port}")
        };

        var response = await client.GetAsync("/auth/google");
        var body = await response.Content.ReadAsStringAsync();

        Ensure(response.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.Found,
            $"Expected /auth/google challenge redirect when enabled, got {(int)response.StatusCode}. Body: {body}");

        var locationHeader = response.Headers.Location;
        Ensure(locationHeader is not null,
            "Expected redirect Location header from /auth/google challenge response.");

        var location = locationHeader!.ToString();
        Ensure(location.StartsWith("https://accounts.example.test/oauth/authorize", StringComparison.Ordinal),
            $"Expected challenge redirect to configured endpoint. Location: {location}");

        Ensure(location.Contains("redirect_uri=%2Fauth%2Fgoogle%2Fcallback", StringComparison.Ordinal),
            $"Expected challenge redirect to include encoded callback redirect_uri. Location: {location}");
    }

    [Test]
    public async Task Api_AuthAndIdempotencyFlow_WorksInDevHeaderMode()
    {
        if (!ShouldRunApiIntegration())
            return;

        var workspaceRoot = FindWorkspaceRoot();
        var port = GetFreePort();

        await using var api = await StartApiAsync(
            workspaceRoot,
            port,
            devPrincipalAllowed: true,
            jwtAuthority: null);

        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };

        var heartbeat = await client.GetAsync("/api/heartbeat");
        Ensure(heartbeat.StatusCode == HttpStatusCode.OK,
            $"Expected heartbeat 200, got {(int)heartbeat.StatusCode}. Body: {await heartbeat.Content.ReadAsStringAsync()}");

        Ensure(
            heartbeat.Headers.TryGetValues("X-Octocon-Contract", out var contractValues) &&
            contractValues.Contains("2026-03-v1", StringComparer.Ordinal),
            "Expected X-Octocon-Contract response header on heartbeat response.");

        var nodeRole = await client.GetAsync("/health/node-role");
        var nodeRoleBody = await nodeRole.Content.ReadAsStringAsync();
        Ensure(nodeRole.StatusCode == HttpStatusCode.OK,
            $"Expected node-role health endpoint 200, got {(int)nodeRole.StatusCode}. Body: {nodeRoleBody}");
        Ensure(!string.IsNullOrWhiteSpace(ReadStringField(nodeRoleBody, "role")),
            $"Expected node-role response to include role. Body: {nodeRoleBody}");

        var unauthorized = await client.PostAsJsonAsync("/api/systems/me/alters", new { name = "NoPrincipal" });
        Ensure(unauthorized.StatusCode == HttpStatusCode.Unauthorized,
            $"Expected unauthenticated alter-create to return 401, got {(int)unauthorized.StatusCode}. Body: {await unauthorized.Content.ReadAsStringAsync()}");

        var idempotencyKey = Guid.NewGuid().ToString("N");
        var first = await SendAlterCreateAsync(client, "sys-api-smoke", idempotencyKey, "IntegrationOne");
        Ensure(first.StatusCode == HttpStatusCode.Created,
            $"Expected first alter-create 201, got {(int)first.StatusCode}. Body: {first.Body}");
        Ensure(ReadReplay(first.Body) == false,
            $"Expected first alter-create replay=false. Body: {first.Body}");

        var second = await SendAlterCreateAsync(client, "sys-api-smoke", idempotencyKey, "IntegrationOne");
        Ensure(second.StatusCode == HttpStatusCode.Created,
            $"Expected replay alter-create 201, got {(int)second.StatusCode}. Body: {second.Body}");
        Ensure(ReadReplay(second.Body) == true,
            $"Expected replay alter-create replay=true. Body: {second.Body}");

        using var listReq = new HttpRequestMessage(HttpMethod.Get, "/api/systems/me/alters");
        listReq.Headers.Add("X-Octocon-Dev-Principal", "sys-api-smoke");
        var listRes = await client.SendAsync(listReq);
        var listBody = await listRes.Content.ReadAsStringAsync();
        Ensure(listRes.StatusCode == HttpStatusCode.OK,
            $"Expected alters list 200, got {(int)listRes.StatusCode}. Body: {listBody}");

        using var listDoc = JsonDocument.Parse(listBody);
        Ensure(listDoc.RootElement.TryGetProperty("data", out var altersData) && altersData.ValueKind == JsonValueKind.Array,
            $"Expected alters list response to include data array. Body: {listBody}");

        int? createdAlterId = null;
        foreach (var item in altersData.EnumerateArray())
        {
            if (!item.TryGetProperty("name", out var nameProp)
                || !string.Equals(nameProp.GetString(), "IntegrationOne", StringComparison.Ordinal))
            {
                continue;
            }

            if (item.TryGetProperty("alterId", out var idProp) && idProp.TryGetInt32(out var parsedId))
            {
                createdAlterId = parsedId;
                break;
            }
        }

        Ensure(createdAlterId.HasValue,
            $"Expected to find created alter in GET /api/systems/me/alters response. Body: {listBody}");

        var createdId = createdAlterId.GetValueOrDefault();

        using var showReq = new HttpRequestMessage(HttpMethod.Get, $"/api/systems/me/alters/{createdId}");
        showReq.Headers.Add("X-Octocon-Dev-Principal", "sys-api-smoke");
        var showRes = await client.SendAsync(showReq);
        var showBody = await showRes.Content.ReadAsStringAsync();
        Ensure(showRes.StatusCode == HttpStatusCode.OK,
            $"Expected alter show 200, got {(int)showRes.StatusCode}. Body: {showBody}");

        using var showDoc = JsonDocument.Parse(showBody);
        Ensure(showDoc.RootElement.TryGetProperty("data", out var showData) && showData.ValueKind == JsonValueKind.Object,
            $"Expected alter show response to include data object. Body: {showBody}");
    }

    [Test]
    public async Task Api_FailsFast_WithoutJwtAuthority_WhenDevHeaderBypassOff()
    {
        if (!ShouldRunApiIntegration())
            return;

        var workspaceRoot = FindWorkspaceRoot();
        var port = GetFreePort();

        using var process = StartApiProcess(
            workspaceRoot,
            port,
            devPrincipalAllowed: false,
            jwtAuthority: null);

        var exited = await WaitForExitAsync(process, timeoutMs: 12000);
        Ensure(exited, "Expected API process to fail fast, but it did not exit in time.");
        Ensure(process.ExitCode != 0, "Expected non-zero exit code when JWT authority is missing with dev bypass off.");

        var stderr = await process.StandardError.ReadToEndAsync();
        var stdout = await process.StandardOutput.ReadToEndAsync();
        var combined = string.Concat(stdout, "\n", stderr);

        Ensure(combined.Contains("OCTOCON_JWT_AUTHORITY", StringComparison.Ordinal),
            $"Expected startup guardrail message mentioning OCTOCON_JWT_AUTHORITY. Output: {combined}");
    }

    [Test]
    public async Task Api_UserSocketEndpoint_AllowsWebSocketUpgrade_WithToken()
    {
        if (!ShouldRunApiIntegration())
            return;

        var workspaceRoot = FindWorkspaceRoot();
        var port = GetFreePort();

        await using var api = await StartApiAsync(
            workspaceRoot,
            port,
            devPrincipalAllowed: true,
            jwtAuthority: null);

        using var ws = new ClientWebSocket();
        const string socketToken = "integration-test-token";
        var uri = new Uri($"ws://127.0.0.1:{port}/api/socket?token={socketToken}");
        await ws.ConnectAsync(uri, CancellationToken.None);

        Ensure(ws.State == WebSocketState.Open,
            $"Expected websocket to be open after connecting to /api/socket, got {ws.State}.");

        var buffer = new byte[2048];
        var received = await ws.ReceiveAsync(buffer, CancellationToken.None);
        var message = Encoding.UTF8.GetString(buffer, 0, received.Count);

        Ensure(received.MessageType == WebSocketMessageType.Text,
            $"Expected text frame from /api/socket, got {received.MessageType}.");
        Ensure(message.Contains("system:socket_ready", StringComparison.Ordinal),
            $"Expected socket readiness event, got payload: {message}");

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

        Ensure(arrayRoot.ValueKind == JsonValueKind.Array,
            $"Expected array-frame reply for array-frame request. Payload: {arrayJoinReply}");
        Ensure(arrayRoot.GetArrayLength() >= 5,
            $"Expected 5-element Phoenix array reply. Payload: {arrayJoinReply}");
        Ensure(string.Equals(arrayRoot[0].GetString(), "51", StringComparison.Ordinal),
            $"Expected join_ref=51 in array reply. Payload: {arrayJoinReply}");
        Ensure(string.Equals(arrayRoot[1].GetString(), "51", StringComparison.Ordinal),
            $"Expected ref=51 in array reply. Payload: {arrayJoinReply}");
        Ensure(string.Equals(arrayRoot[2].GetString(), "system:sys-phx-join", StringComparison.Ordinal),
            $"Expected topic to match array join topic. Payload: {arrayJoinReply}");
        Ensure(string.Equals(arrayRoot[3].GetString(), "phx_reply", StringComparison.Ordinal),
            $"Expected array reply event phx_reply. Payload: {arrayJoinReply}");

        var arrayPayload = arrayRoot[4];
        Ensure(string.Equals(arrayPayload.GetProperty("status").GetString(), "ok", StringComparison.Ordinal),
            $"Expected array join status=ok. Payload: {arrayJoinReply}");
        Ensure(arrayPayload.GetProperty("response").ValueKind == JsonValueKind.Object,
            $"Expected reconnect join response object in array reply. Payload: {arrayJoinReply}");

        var joinFrame =
            "{" +
            "\"topic\":\"system:sys-phx-join\"," +
            "\"event\":\"phx_join\"," +
            "\"payload\":{\"token\":\"" + socketToken + "\"}," +
            "\"ref\":\"1\"," +
            "\"join_ref\":\"1\"" +
            "}";

        var joinBytes = Encoding.UTF8.GetBytes(joinFrame);
        await ws.SendAsync(joinBytes, WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);

        var joinReply = await ReceiveWebSocketTextAsync(ws);

        using var joinDoc = JsonDocument.Parse(joinReply);
        var root = joinDoc.RootElement;

        Ensure(string.Equals(root.GetProperty("event").GetString(), "phx_reply", StringComparison.Ordinal),
            $"Expected Phoenix phx_reply event, got payload: {joinReply}");
        Ensure(string.Equals(root.GetProperty("topic").GetString(), "system:sys-phx-join", StringComparison.Ordinal),
            $"Expected join reply topic to match requested topic. Payload: {joinReply}");

        var payload = root.GetProperty("payload");
        Ensure(string.Equals(payload.GetProperty("status").GetString(), "ok", StringComparison.Ordinal),
            $"Expected phx_join status=ok, got payload: {joinReply}");

        var response = payload.GetProperty("response");
        Ensure(response.TryGetProperty("system", out _),
            $"Expected phx_join response to include 'system' key. Payload: {joinReply}");
        Ensure(response.TryGetProperty("alters", out var altersEl) && altersEl.ValueKind == JsonValueKind.Array,
            $"Expected phx_join response to include 'alters' array. Payload: {joinReply}");
        Ensure(response.TryGetProperty("fronts", out var frontsEl) && frontsEl.ValueKind == JsonValueKind.Array,
            $"Expected phx_join response to include 'fronts' array. Payload: {joinReply}");
        Ensure(response.TryGetProperty("tags", out var tagsEl) && tagsEl.ValueKind == JsonValueKind.Array,
            $"Expected phx_join response to include 'tags' array. Payload: {joinReply}");

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

        Ensure(string.Equals(endpointRoot.GetProperty("event").GetString(), "phx_reply", StringComparison.Ordinal),
            $"Expected endpoint reply to use phx_reply event. Payload: {endpointReply}");

        var endpointPayload = endpointRoot.GetProperty("payload");
        Ensure(string.Equals(endpointPayload.GetProperty("status").GetString(), "ok", StringComparison.Ordinal),
            $"Expected endpoint event status=ok envelope. Payload: {endpointReply}");

        var endpointResponse = endpointPayload.GetProperty("response");
        Ensure(endpointResponse.GetProperty("status").GetInt32() == 200,
            $"Expected proxied /api/heartbeat status=200. Payload: {endpointReply}");

        var proxiedBody = endpointResponse.GetProperty("body").GetString() ?? string.Empty;
        Ensure(proxiedBody.Contains("ACK", StringComparison.Ordinal),
            $"Expected proxied heartbeat body to include ACK. Body: {proxiedBody}");

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

        Ensure(createAlterResponse.GetProperty("status").GetInt32() == 201,
            $"Expected proxied alter-create status=201. Payload: {createAlterReply}");

        var createAlterBody = createAlterResponse.GetProperty("body").GetString() ?? string.Empty;
        Ensure(ReadReplay(createAlterBody) == false,
            $"Expected first socket-proxied alter-create replay=false. Body: {createAlterBody}");

        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "test done", CancellationToken.None);
    }

    [Test]
    public async Task Api_OAuthCallback_IssuesJwsCompactSerializationToken()
    {
        if (!ShouldRunApiIntegration())
            return;

        var workspaceRoot = FindWorkspaceRoot();
        var port = GetFreePort();

        await using var api = await StartApiAsync(
            workspaceRoot,
            port,
            devPrincipalAllowed: true,
            jwtAuthority: "test-authority",
            additionalEnv: new Dictionary<string, string?>
            {
                ["OCTOCON_DEEPLINK_ADDRESS"] = "octocon://app",
                ["OCTOCON_REGION"] = "nam"
            });

        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            BaseAddress = new Uri($"http://127.0.0.1:{port}")
        };

        var discordUid = $"jws-test-{Guid.NewGuid():N}";
        var response = await client.GetAsync($"/auth/discord/callback?uid={Uri.EscapeDataString(discordUid)}");

        Ensure(
            response.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.Found
                or HttpStatusCode.MovedPermanently or HttpStatusCode.TemporaryRedirect
                or HttpStatusCode.PermanentRedirect,
            $"Expected a redirect from /auth/discord/callback, got {(int)response.StatusCode}.");

        var location = response.Headers.Location
            ?? throw new InvalidOperationException("Expected Location header in redirect response.");

        var query = System.Web.HttpUtility.ParseQueryString(location.Query);
        var token = query["token"];

        Ensure(!string.IsNullOrWhiteSpace(token),
            $"Expected 'token' query parameter in redirect URL. Location: {location}");

        // JWS Compact Serialization must have exactly 3 dot-separated segments.
        var segments = token!.Split('.');
        Ensure(segments.Length == 3,
            $"Expected exactly 3 dot-separated JWS segments, got {segments.Length}. Token: {token}");

        // Each segment must be valid base64url (no '+', '/', '=' characters).
        foreach (var (segment, index) in segments.Select((s, i) => (s, i)))
        {
            Ensure(!string.IsNullOrWhiteSpace(segment),
                $"JWS segment {index} must not be empty. Token: {token}");
            Ensure(!segment.Contains('+') && !segment.Contains('/') && !segment.Contains('='),
                $"JWS segment {index} contains invalid base64url characters. Segment: {segment}");
        }

        // Decode and validate the header.
        var headerBytes = Base64UrlDecodeBytes(segments[0]);
        using var headerDoc = System.Text.Json.JsonDocument.Parse(headerBytes);
        var alg = headerDoc.RootElement.GetProperty("alg").GetString();
        var typ = headerDoc.RootElement.GetProperty("typ").GetString();
        Ensure(string.Equals(alg, "HS256", StringComparison.Ordinal),
            $"Expected JWS header alg=HS256, got {alg}.");
        Ensure(string.Equals(typ, "JWT", StringComparison.Ordinal),
            $"Expected JWS header typ=JWT, got {typ}.");

        // Decode and validate the payload.
        var payloadBytes = Base64UrlDecodeBytes(segments[1]);
        using var payloadDoc = System.Text.Json.JsonDocument.Parse(payloadBytes);
        var root = payloadDoc.RootElement;

        Ensure(root.TryGetProperty("iss", out var iss) && iss.GetString() == "test-authority",
            $"Expected payload iss=test-authority. Payload: {System.Text.Encoding.UTF8.GetString(payloadBytes)}");

        Ensure(root.TryGetProperty("sub", out var sub) && !string.IsNullOrWhiteSpace(sub.GetString()),
            $"Expected non-empty payload sub. Payload: {System.Text.Encoding.UTF8.GetString(payloadBytes)}");

        long iatVal = 0, expVal = 0;

        Ensure(root.TryGetProperty("iat", out var iat) && iat.TryGetInt64(out iatVal) && iatVal > 0,
            "Expected positive payload iat claim.");

        Ensure(root.TryGetProperty("nbf", out var nbf) && nbf.TryGetInt64(out _),
            "Expected payload nbf claim.");

        Ensure(root.TryGetProperty("exp", out var exp) && exp.TryGetInt64(out expVal) && expVal > iatVal,
            $"Expected payload exp > iat. iat={iatVal}, exp={expVal}.");

        Ensure(root.TryGetProperty("jti", out var jti) && !string.IsNullOrWhiteSpace(jti.GetString()),
            "Expected non-empty payload jti claim.");

        Ensure(root.TryGetProperty("scope", out var scope) &&
               string.Equals(scope.GetString(), "octocon:deeplink", StringComparison.Ordinal),
            $"Expected payload scope=octocon:deeplink. Got: {scope.GetString()}");
    }

    [Test]
    public async Task Api_FrontHistoryBetween_IncludesEndedFronts()
    {
        if (!ShouldRunApiIntegration())
            return;

        var workspaceRoot = FindWorkspaceRoot();
        var port = GetFreePort();

        await using var api = await StartApiAsync(
            workspaceRoot,
            port,
            devPrincipalAllowed: true,
            jwtAuthority: null);

        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };

        var principalId = "sys-front-history";
        var startAnchor = DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeSeconds();

        var started = await SendFrontStartAsync(client, principalId, alterId: 101, comment: "phase3-history");
        Ensure(started.StatusCode == HttpStatusCode.Created,
            $"Expected front start 201, got {(int)started.StatusCode}. Body: {started.Body}");

        var startedFrontId = ReadStringField(started.Body, "frontId");
        Ensure(!string.IsNullOrWhiteSpace(startedFrontId),
            $"Expected start response to include frontId. Body: {started.Body}");

        var ended = await SendFrontEndAsync(client, principalId, alterId: 101);
        Ensure(ended.StatusCode == HttpStatusCode.NoContent,
            $"Expected front end 204, got {(int)ended.StatusCode}. Body: {ended.Body}");

        var endAnchor = DateTimeOffset.UtcNow.AddMinutes(1).ToUnixTimeSeconds();
        using var betweenRequest = new HttpRequestMessage(HttpMethod.Get,
            $"/api/systems/me/front/between?start={startAnchor}&end={endAnchor}");
        betweenRequest.Headers.Add("X-Octocon-Dev-Principal", principalId);
        var betweenResponse = await client.SendAsync(betweenRequest);
        var betweenBody = await betweenResponse.Content.ReadAsStringAsync();

        Ensure(betweenResponse.StatusCode == HttpStatusCode.OK,
            $"Expected front between 200, got {(int)betweenResponse.StatusCode}. Body: {betweenBody}");

        using var doc = JsonDocument.Parse(betweenBody);
        Ensure(doc.RootElement.ValueKind == JsonValueKind.Array,
            $"Expected front between payload to be an array. Body: {betweenBody}");
        Ensure(doc.RootElement.GetArrayLength() > 0,
            $"Expected at least one historical front entry. Body: {betweenBody}");

        var row = doc.RootElement.EnumerateArray().First();
        var responseFrontId = ReadStringField(row, "frontId");
        Ensure(string.Equals(responseFrontId, startedFrontId, StringComparison.OrdinalIgnoreCase),
            $"Expected matching frontId from history response. Body: {betweenBody}");

        var responseComment = ReadStringField(row, "comment");
        Ensure(string.Equals(responseComment, "phase3-history", StringComparison.Ordinal),
            $"Expected matching front comment in history response. Body: {betweenBody}");

        var endedAt = ReadNullableStringField(row, "endedAt");
        Ensure(!string.IsNullOrWhiteSpace(endedAt),
            $"Expected ended historical front to include endedAt. Body: {betweenBody}");
    }

    private static async Task<(HttpStatusCode StatusCode, string Body)> SendAlterCreateAsync(
        HttpClient client,
        string principalId,
        string idempotencyKey,
        string alterName)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/systems/me/alters")
        {
            Content = JsonContent.Create(new { name = alterName })
        };

        request.Headers.Add("X-Octocon-Dev-Principal", principalId);
        request.Headers.Add("X-Octocon-Idempotency-Key", idempotencyKey);

        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        return (response.StatusCode, body);
    }

    private static async Task<(HttpStatusCode StatusCode, string Body)> SendFrontStartAsync(
        HttpClient client,
        string principalId,
        int alterId,
        string? comment)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/systems/me/front/start")
        {
            Content = JsonContent.Create(new
            {
                alterId,
                comment,
                idempotencyKey = Guid.NewGuid().ToString("N")
            })
        };

        request.Headers.Add("X-Octocon-Dev-Principal", principalId);

        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        return (response.StatusCode, body);
    }

    private static async Task<(HttpStatusCode StatusCode, string Body)> SendFrontEndAsync(
        HttpClient client,
        string principalId,
        int alterId)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/systems/me/front/end")
        {
            Content = JsonContent.Create(new
            {
                alterId,
                idempotencyKey = Guid.NewGuid().ToString("N")
            })
        };

        request.Headers.Add("X-Octocon-Dev-Principal", principalId);

        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        return (response.StatusCode, body);
    }

    private static bool ReadReplay(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        foreach (var prop in root.EnumerateObject())
        {
            if (!prop.Name.Equals("Replay", StringComparison.OrdinalIgnoreCase))
                continue;

            if (prop.Value.ValueKind == JsonValueKind.True)
                return true;

            if (prop.Value.ValueKind == JsonValueKind.False)
                return false;

            break;
        }

        throw new InvalidOperationException($"Could not find boolean replay flag in response: {json}");
    }

    private static string ReadStringField(string json, string fieldName)
    {
        using var doc = JsonDocument.Parse(json);
        return ReadStringField(doc.RootElement, fieldName);
    }

    private static string ReadStringField(JsonElement root, string fieldName)
    {
        foreach (var prop in root.EnumerateObject())
        {
            if (!prop.Name.Equals(fieldName, StringComparison.OrdinalIgnoreCase))
                continue;

            if (prop.Value.ValueKind == JsonValueKind.String)
                return prop.Value.GetString() ?? string.Empty;

            throw new InvalidOperationException(
                $"Expected string field '{fieldName}', got {prop.Value.ValueKind}.");
        }

        throw new InvalidOperationException($"Field '{fieldName}' not found.");
    }

    private static string? ReadNullableStringField(JsonElement root, string fieldName)
    {
        foreach (var prop in root.EnumerateObject())
        {
            if (!prop.Name.Equals(fieldName, StringComparison.OrdinalIgnoreCase))
                continue;

            return prop.Value.ValueKind switch
            {
                JsonValueKind.Null => null,
                JsonValueKind.String => prop.Value.GetString(),
                _ => throw new InvalidOperationException(
                    $"Expected nullable string field '{fieldName}', got {prop.Value.ValueKind}.")
            };
        }

        return null;
    }

    private static bool ShouldRunApiIntegration()
    {
        var run = Environment.GetEnvironmentVariable("OCTOCON_RUN_API_INTEGRATION");
        return bool.TryParse(run, out var enabled) && enabled;
    }

    private static string FindWorkspaceRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);

        while (current is not null)
        {
            var marker = Path.Combine(current.FullName, "octocon.sln");
            if (File.Exists(marker))
                return current.FullName;

            current = current.Parent;
        }

        throw new InvalidOperationException("Could not find workspace root containing octocon.sln.");
    }

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();

        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    private static async Task<RunningApi> StartApiAsync(
        string workspaceRoot,
        int port,
        bool devPrincipalAllowed,
        string? jwtAuthority,
        IReadOnlyDictionary<string, string?>? additionalEnv = null)
    {
        var gateLease = await ApiProcessGate.AcquireAsync();
        var process = StartApiProcess(workspaceRoot, port, devPrincipalAllowed, jwtAuthority, additionalEnv);

        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
        var ready = await WaitForApiReadyAsync(process, http, timeoutMs: 30000);

        if (!ready)
        {
            var stderr = await process.StandardError.ReadToEndAsync();
            var stdout = await process.StandardOutput.ReadToEndAsync();
            process.Kill(entireProcessTree: true);
            await gateLease.DisposeAsync();

            throw new InvalidOperationException(
                $"API did not become ready in time. stdout: {stdout} stderr: {stderr}");
        }

        return new RunningApi(process, gateLease);
    }

    private static Process StartApiProcess(
        string workspaceRoot,
        int port,
        bool devPrincipalAllowed,
        string? jwtAuthority,
        IReadOnlyDictionary<string, string?>? additionalEnv = null)
    {
        var apiProjectPath = Path.Combine(workspaceRoot, "csharp", "Octocon.Api", "Octocon.Api.csproj");

        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"run --no-build --project \"{apiProjectPath}\"",
            WorkingDirectory = workspaceRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        psi.Environment["ASPNETCORE_URLS"] = $"http://127.0.0.1:{port}";
        psi.Environment["OCTOCON_PERSISTENCE"] = "inmemory";
        psi.Environment["OCTOCON_DEV_ALLOW_HEADER_PRINCIPAL"] = devPrincipalAllowed ? "true" : "false";

        if (string.IsNullOrWhiteSpace(jwtAuthority))
        {
            psi.Environment["OCTOCON_JWT_AUTHORITY"] = string.Empty;
        }
        else
        {
            psi.Environment["OCTOCON_JWT_AUTHORITY"] = jwtAuthority;
        }

        if (additionalEnv is not null)
        {
            foreach (var kvp in additionalEnv)
            {
                psi.Environment[kvp.Key] = kvp.Value ?? string.Empty;
            }
        }

        var process = new Process { StartInfo = psi };
        process.Start();
        return process;
    }

    private static async Task<bool> WaitForApiReadyAsync(Process process, HttpClient client, int timeoutMs)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);

        while (DateTime.UtcNow < deadline)
        {
            if (process.HasExited)
                return false;

            try
            {
                var response = await client.GetAsync("/api/heartbeat");
                if (response.StatusCode == HttpStatusCode.OK)
                    return true;
            }
            catch
            {
                // Keep polling while Kestrel starts.
            }

            await Task.Delay(200);
        }

        return false;
    }

    private static async Task<bool> WaitForExitAsync(Process process, int timeoutMs)
    {
        var completed = await Task.WhenAny(process.WaitForExitAsync(), Task.Delay(timeoutMs));
        return completed.IsCompletedSuccessfully && process.HasExited;
    }

    private static async Task<string> ReceiveWebSocketTextAsync(ClientWebSocket ws)
    {
        var buffer = new byte[4096];
        using var stream = new MemoryStream();

        while (true)
        {
            var chunk = await ws.ReceiveAsync(buffer, CancellationToken.None);
            Ensure(chunk.MessageType == WebSocketMessageType.Text,
                $"Expected websocket text frame, got {chunk.MessageType}.");

            await stream.WriteAsync(buffer.AsMemory(0, chunk.Count));

            if (chunk.EndOfMessage)
            {
                return Encoding.UTF8.GetString(stream.ToArray());
            }
        }
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private static byte[] Base64UrlDecodeBytes(string base64Url)
    {
        var padded = base64Url.Replace('-', '+').Replace('_', '/');
        padded = (padded.Length % 4) switch
        {
            2 => padded + "==",
            3 => padded + "=",
            _ => padded
        };
        return Convert.FromBase64String(padded);
    }

    private sealed class RunningApi : IAsyncDisposable
    {
        private readonly Process _process;
        private readonly IAsyncDisposable _gateLease;

        public RunningApi(Process process, IAsyncDisposable gateLease)
        {
            _process = process;
            _gateLease = gateLease;
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (!_process.HasExited)
                    _process.Kill(entireProcessTree: true);
            }
            catch
            {
                // Best effort process cleanup.
            }

            _process.Dispose();
            await _gateLease.DisposeAsync();
        }
    }
}
