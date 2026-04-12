using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Interfold.IntegrationTests;

public sealed class ScyllaPostgresSmokeTests
{
    [Test]
    public async Task AlterCreate_IdempotentReplay_WorksAgainstLiveAdapters()
    {
        if (!(IntegrationTestEnvironment.ShouldRunLiveIntegration && IntegrationTestEnvironment.HasPostgresConnection))
        {
            return;
        }

        await using var factory = new InterfoldWebApplicationFactory()
            .WithConfiguration("OCTOCON_PERSISTENCE", "scylla-postgres")
            .WithConfiguration("OCTOCON_POSTGRES_CONNECTION", IntegrationTestEnvironment.PostgresConnection)
            .WithConfiguration("OCTOCON_SCYLLA_CONTACT_POINTS", IntegrationTestEnvironment.GetVariable("OCTOCON_TEST_SCYLLA_CONTACT_POINTS", "127.0.0.1"))
            .WithConfiguration("OCTOCON_SCYLLA_USERNAME", IntegrationTestEnvironment.GetVariable("OCTOCON_TEST_SCYLLA_USERNAME", "cassandra"))
            .WithConfiguration("OCTOCON_SCYLLA_PASSWORD", IntegrationTestEnvironment.GetVariable("OCTOCON_TEST_SCYLLA_PASSWORD", "cassandra"))
            .WithConfiguration("OCTOCON_REGION", IntegrationTestEnvironment.GetVariable("OCTOCON_TEST_REGION", "nam"))
            .WithConfiguration("OCTOCON_TEST_AUTH_ALLOW_PRINCIPAL_HEADER", "true");

        using var client = factory.CreateClient();

        var systemId = $"itest-{Guid.NewGuid():N}"[..14];
        var idempotencyKey = Guid.NewGuid().ToString("N");

        var requestContent = new { name = "IntegrationSmoke" };

        // First call
        using var firstReq = new HttpRequestMessage(HttpMethod.Post, "/api/systems/me/alters")
        {
            Content = JsonContent.Create(requestContent)
        };
        firstReq.Headers.Add("X-Interfold-Principal", systemId);
        firstReq.Headers.Add("X-Interfold-Idempotency-Key", idempotencyKey);

        var firstRes = await client.SendAsync(firstReq);
        var firstBody = await firstRes.Content.ReadAsStringAsync();
        
        Ensure(firstRes.StatusCode == HttpStatusCode.Created, 
            $"First API invocation failed. Status: {firstRes.StatusCode}, Body: {firstBody}");
        
        using (var doc = JsonDocument.Parse(firstBody))
        {
            var root = doc.RootElement;
            Ensure(root.TryGetProperty("data", out _), "First API response missing 'data' property.");
            Ensure(root.GetProperty("replay").GetBoolean() == false, 
                $"First API invocation should have replay=false. Body: {firstBody}");
        }

        // Second call (replay)
        using var secondReq = new HttpRequestMessage(HttpMethod.Post, "/api/systems/me/alters")
        {
            Content = JsonContent.Create(requestContent)
        };
        secondReq.Headers.Add("X-Interfold-Principal", systemId);
        secondReq.Headers.Add("X-Interfold-Idempotency-Key", idempotencyKey);

        var secondRes = await client.SendAsync(secondReq);
        var secondBody = await secondRes.Content.ReadAsStringAsync();

        Ensure(secondRes.StatusCode == HttpStatusCode.Created, 
            $"Second API invocation failed. Status: {secondRes.StatusCode}, Body: {secondBody}");

        using (var doc = JsonDocument.Parse(secondBody))
        {
            var root = doc.RootElement;
            Ensure(root.TryGetProperty("data", out _), "Second API response missing 'data' property.");
            Ensure(root.GetProperty("replay").GetBoolean() == true, 
                $"Second API invocation should have replay=true. Body: {secondBody}");
        }
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
