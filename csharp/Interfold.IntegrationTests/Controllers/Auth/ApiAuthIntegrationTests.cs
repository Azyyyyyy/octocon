using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Interfold.IntegrationTests.Attributes;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Interfold.IntegrationTests.Controllers.Auth;

/// <summary>
/// Integration tests for authentication, OAuth flows, and WebSocket upgrades.
/// Refactored to use WebApplicationFactory for in-memory testing.
/// </summary>
public sealed class ApiAuthIntegrationTests
{
    [Test, ApiIntegration]
    public async Task Api_AuthRequest_FallsBackTo403_WhenChallengeDisabled()
    {
        await using var factory = new InterfoldWebApplicationFactory()
            .WithConfiguration("OCTOCON_PERSISTENCE", "inmemory")
            .WithConfiguration("OCTOCON_AUTH_CHALLENGE_ENABLED", "false");
        
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var response = await client.GetAsync("/auth/google");
        var body = await response.Content.ReadAsStringAsync();

        Ensure(response.StatusCode == HttpStatusCode.Forbidden,
            $"Expected /auth/google fallback 403 when challenge is disabled, got {(int)response.StatusCode}. Body: {body}");
    }

    [Test, ApiIntegration]
    public async Task Api_AuthRequest_IssuesChallengeRedirect_WhenChallengeEnabledAndSchemeConfigured()
    {
        await using var factory = new InterfoldWebApplicationFactory()
            .WithConfiguration("OCTOCON_PERSISTENCE", "inmemory")
            .WithConfiguration("OCTOCON_AUTH_CHALLENGE_ENABLED", "true")
            .WithConfiguration("OCTOCON_AUTH_CHALLENGE_GOOGLE_SCHEME", "oauth-google")
            .WithConfiguration("OCTOCON_AUTH_CHALLENGE_GOOGLE_ENDPOINT", "https://accounts.example.test/oauth/authorize");

        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

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

    [Test, ApiIntegration]
    public async Task Api_Heartbeat_ReturnsContractHeader()
    {
        await using var factory = new InterfoldWebApplicationFactory()
            .WithConfiguration("OCTOCON_PERSISTENCE", "inmemory");
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/heartbeat");
        Ensure(response.StatusCode == HttpStatusCode.OK,
            $"Expected heartbeat 200, got {(int)response.StatusCode}. Body: {await response.Content.ReadAsStringAsync()}");

        Ensure(
            response.Headers.TryGetValues("X-Interfold-Contract", out var contractValues) &&
            contractValues.Contains("2026-03-v1", StringComparer.Ordinal),
            "Expected X-Interfold-Contract response header on heartbeat response.");
    }

    [Test, ApiIntegration]
    public async Task Api_UserSocketEndpoint_AllowsWebSocketUpgrade()
    {
        await using var factory = new InterfoldWebApplicationFactory()
            .WithConfiguration("OCTOCON_PERSISTENCE", "inmemory");
        
        using var server = factory.Server;
        var client = server.CreateWebSocketClient();
        
        const string socketToken = "integration-test-token";
        var uri = new Uri(server.BaseAddress, "/api/socket/websocket?token=" + socketToken);
        
        using var ws = await client.ConnectAsync(uri, CancellationToken.None);

        Ensure(ws.State == WebSocketState.Open,
            $"Expected websocket to be open after connecting to /api/socket/websocket, got {ws.State}.");

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
    }

    [Test, ApiIntegration]
    public async Task Api_AuthAndIdempotencyFlow_VerifiesEndToEndBehavior()
    {
        await using var factory = new InterfoldWebApplicationFactory()
            .WithConfiguration("OCTOCON_PERSISTENCE", "inmemory")
            .WithConfiguration("OCTOCON_TEST_AUTH_ALLOW_PRINCIPAL_HEADER", "true");

        using var client = factory.CreateClient();
        var principalId = "sys-api-smoke";

        var heartbeat = await client.GetAsync("/api/heartbeat");
        Ensure(heartbeat.StatusCode == HttpStatusCode.OK,
            $"Expected heartbeat 200, got {(int)heartbeat.StatusCode}.");

        // Unauthorized check
        var unauthorized = await client.PostAsJsonAsync("/api/systems/me/alters", new { name = "NoPrincipal" });
        Ensure(unauthorized.StatusCode == HttpStatusCode.Unauthorized,
            $"Expected 401 for missing principal, got {(int)unauthorized.StatusCode}.");

        // First creation
        var idempotencyKey = Guid.NewGuid().ToString("N");
        using var firstReq = new HttpRequestMessage(HttpMethod.Post, "/api/systems/me/alters")
        {
            Content = JsonContent.Create(new { name = "IntegrationOne" })
        };
        firstReq.Headers.Add("X-Interfold-Principal", principalId);
        firstReq.Headers.Add("X-Interfold-Idempotency-Key", idempotencyKey);

        var firstRes = await client.SendAsync(firstReq);
        var firstBody = await firstRes.Content.ReadAsStringAsync();
        Ensure(firstRes.StatusCode == HttpStatusCode.Created,
            $"Expected 201, got {(int)firstRes.StatusCode}. Body: {firstBody}");
        Ensure(ReadBoolField(firstBody, "replay") == false, "Expected replay=false on first call.");

        // Replay check
        using var secondReq = new HttpRequestMessage(HttpMethod.Post, "/api/systems/me/alters")
        {
            Content = JsonContent.Create(new { name = "IntegrationOne" })
        };
        secondReq.Headers.Add("X-Interfold-Principal", principalId);
        secondReq.Headers.Add("X-Interfold-Idempotency-Key", idempotencyKey);

        var secondRes = await client.SendAsync(secondReq);
        var secondBody = await secondRes.Content.ReadAsStringAsync();
        Ensure(secondRes.StatusCode == HttpStatusCode.Created,
            $"Expected 201 for replay, got {(int)secondRes.StatusCode}. Body: {secondBody}");
        Ensure(ReadBoolField(secondBody, "replay") == true, "Expected replay=true on second call.");

        // Verification of list
        using var listReq = new HttpRequestMessage(HttpMethod.Get, "/api/systems/me/alters");
        listReq.Headers.Add("X-Interfold-Principal", principalId);
        var listRes = await client.SendAsync(listReq);
        var listBody = await listRes.Content.ReadAsStringAsync();
        Ensure(listRes.StatusCode == HttpStatusCode.OK,
            $"Expected 200 for list, got {(int)listRes.StatusCode}. Body: {listBody}");
        
        using var listDoc = JsonDocument.Parse(listBody);
        var altersData = listDoc.RootElement.GetProperty("data");
        Ensure(altersData.GetArrayLength() > 0, "Expected at least one alter in list.");
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

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

    private static bool ReadBoolField(string json, string fieldName)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty(fieldName).GetBoolean();
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
