using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Interfold.IntegrationTests;

/// <summary>
/// Idempotency soak tests (Phase N, Scope 2).
/// Each test replays the same command multiple times and verifies that:
/// - The first call returns replay=false.
/// - All subsequent calls return replay=true with the same HTTP status.
/// - The API remains stable under N identical requests.
/// </summary>
public sealed class IdempotencySoakTests
{
    private const int SoakRepeatCount = 5;

    [Test]
    public async Task Idempotency_AlterCreate_ReplayStable()
    {
        if (!IntegrationTestEnvironment.ShouldRunApiIntegration) return;

        await RunSoakAsync(async (client, key) =>
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, "/api/systems/me/alters")
            {
                Content = JsonContent.Create(new { name = "SoakAlter" })
            };
            req.Headers.Add("X-Interfold-Idempotency-Key", key);
            return await client.SendAsync(req);
        });
    }

    [Test]
    public async Task Idempotency_TagCreate_ReplayStable()
    {
        if (!IntegrationTestEnvironment.ShouldRunApiIntegration) return;

        await RunSoakAsync(async (client, key) =>
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, "/api/systems/me/tags")
            {
                Content = JsonContent.Create(new { name = "SoakTag" })
            };
            req.Headers.Add("X-Interfold-Idempotency-Key", key);
            return await client.SendAsync(req);
        });
    }

    [Test]
    public async Task Idempotency_PollCreate_ReplayStable()
    {
        if (!IntegrationTestEnvironment.ShouldRunApiIntegration) return;

        await RunSoakAsync(async (client, key) =>
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, "/api/polls")
            {
                Content = JsonContent.Create(new { title = "SoakPoll", options = new[] { "Yes", "No" } })
            };
            req.Headers.Add("X-Interfold-Idempotency-Key", key);
            return await client.SendAsync(req);
        });
    }

    [Test]
    public async Task Idempotency_GlobalJournalCreate_ReplayStable()
    {
        if (!IntegrationTestEnvironment.ShouldRunApiIntegration) return;

        await RunSoakAsync(async (client, key) =>
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, "/api/journals")
            {
                Content = JsonContent.Create(new { title = "SoakJournal", body = "entry body" })
            };
            req.Headers.Add("X-Interfold-Idempotency-Key", key);
            return await client.SendAsync(req);
        });
    }

    [Test]
    public async Task Idempotency_SettingsUsernameUpdate_ReplayStable()
    {
        if (!IntegrationTestEnvironment.ShouldRunApiIntegration) return;

        await RunSoakAsync(async (client, key) =>
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, "/api/settings/username")
            {
                Content = JsonContent.Create(new { username = "soakuser" })
            };
            req.Headers.Add("X-Interfold-Idempotency-Key", key);
            return await client.SendAsync(req);
        });
    }

    [Test]
    public async Task Idempotency_RequestId_PresentOnEveryResponse()
    {
        if (!IntegrationTestEnvironment.ShouldRunApiIntegration) return;

        await using var factory = new InterfoldWebApplicationFactory()
            .WithConfiguration("OCTOCON_PERSISTENCE", "inmemory");
        using var client = factory.CreateClient();

        for (var i = 0; i < 3; i++)
        {
            var response = await client.GetAsync("/api/heartbeat");

            Ensure(
                response.Headers.TryGetValues("X-Interfold-Request-Id", out var values) &&
                !string.IsNullOrWhiteSpace(values.First()),
                $"Expected X-Interfold-Request-Id header on request #{i + 1}.");
        }
    }

    [Test]
    public async Task Idempotency_CorrelationId_EchoedFromRequestHeader()
    {
        if (!IntegrationTestEnvironment.ShouldRunApiIntegration) return;

        await using var factory = new InterfoldWebApplicationFactory()
            .WithConfiguration("OCTOCON_PERSISTENCE", "inmemory");
        using var client = factory.CreateClient();

        var sentId = Guid.NewGuid().ToString("N");
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/heartbeat");
        request.Headers.Add("X-Request-Id", sentId);

        var response = await client.SendAsync(request);

        Ensure(
            response.Headers.TryGetValues("X-Interfold-Request-Id", out var values) &&
            values.First() == sentId,
            $"Expected X-Interfold-Request-Id={sentId} echoed in response.");
    }

    private static async Task RunSoakAsync(
        Func<HttpClient, string, Task<HttpResponseMessage>> requestFactory)
    {
        await using var factory = new InterfoldWebApplicationFactory()
            .WithConfiguration("OCTOCON_PERSISTENCE", "inmemory");
        using var client = factory.CreateClient();

        var key = Guid.NewGuid().ToString("N");

        for (var i = 0; i < SoakRepeatCount; i++)
        {
            using var response = await requestFactory(client, key);
            var body = await response.Content.ReadAsStringAsync();

            Ensure(
                response.IsSuccessStatusCode,
                $"Soak call #{i + 1}: expected 2xx, got {(int)response.StatusCode}. Body: {body}");

            if (!string.IsNullOrEmpty(body))
            {
                var replay = ReadBoolField(body, "replay");

                if (i == 0)
                {
                    Ensure(!replay,
                        $"Soak call #1: expected replay=false on first invocation. Body: {body}");
                }
                else
                {
                    Ensure(replay,
                        $"Soak call #{i + 1}: expected replay=true after first invocation. Body: {body}");
                }
            }
        }
    }

    private static bool ReadBoolField(string json, string fieldName)
    {
        using var doc = JsonDocument.Parse(json);

        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            if (!prop.Name.Equals(fieldName, StringComparison.OrdinalIgnoreCase))
                continue;

            return prop.Value.ValueKind switch
            {
                JsonValueKind.True  => true,
                JsonValueKind.False => false,
                _ => throw new InvalidOperationException(
                    $"Expected boolean for '{fieldName}'. Body: {json}")
            };
        }

        throw new InvalidOperationException($"Field '{fieldName}' not found in: {json}");
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
