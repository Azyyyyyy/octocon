using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Interfold.IntegrationTests.TestServices;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Interfold.IntegrationTests.Controllers;

/// <summary>
/// Integration tests for authentication, OAuth flows, and WebSocket upgrades.
/// Refactored to use WebApplicationFactory for in-memory testing.
/// </summary>
[ClassDataSource<InMemoryWebFactoryFixture>(Shared = SharedType.PerTestSession)]
[ClassDataSource<ScyllaWebFactoryFixture>(Shared = SharedType.PerTestSession)]
[ClassDataSource<CassandraWebFactoryFixture>(Shared = SharedType.PerTestSession)]
public sealed class AuthControllerTests(IWebFactoryFixture fixture) : BaseEndpointTest
{
    [Test]
    public async Task Api_AuthRequest_FallsBackTo403_WhenChallengeDisabled()
    {
        using var client = fixture.Factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var response = await client.GetAsync("/auth/google");
        var body = await response.Content.ReadAsStringAsync();

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
    }

    //TODO: DO we need this? Maybe can split it up another way...
    [Test]
    public async Task Api_AuthAndIdempotencyFlow_VerifiesEndToEndBehavior()
    {
        using var client = fixture.Factory.CreateClient();
        var principalId = "sys-api-smoke";

        // Unauthorized check
        var unauthorized = await client.PostAsJsonAsync("/api/systems/me/alters", new { name = "NoPrincipal" });
        await Assert.That(unauthorized.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);

        // First creation
        var idempotencyKey = Guid.NewGuid().ToString("N");
        using var firstReq = new HttpRequestMessage(HttpMethod.Post, "/api/systems/me/alters")
        {
            Content = JsonContent.Create(new { name = "IntegrationOne" })
        };
        AttachPrincipalAuth(firstReq, client, principalId);
        firstReq.Headers.Add("X-Interfold-Idempotency-Key", idempotencyKey);

        var firstRes = await client.SendAsync(firstReq);
        var firstBody = await firstRes.Content.ReadAsStringAsync();
        using (Assert.Multiple())
        {
            await Assert.That(firstRes.StatusCode).IsEqualTo(HttpStatusCode.Created);
            await Assert.That(ReadBoolField(firstBody, "replay")).IsFalse();
        }

        // Replay check
        using var secondReq = new HttpRequestMessage(HttpMethod.Post, "/api/systems/me/alters")
        {
            Content = JsonContent.Create(new { name = "IntegrationOne" })
        };
        AttachPrincipalAuth(secondReq, client, principalId);
        secondReq.Headers.Add("X-Interfold-Idempotency-Key", idempotencyKey);

        var secondRes = await client.SendAsync(secondReq);
        var secondBody = await secondRes.Content.ReadAsStringAsync();
        using (Assert.Multiple())
        {
            await Assert.That(secondRes.StatusCode).IsEqualTo(HttpStatusCode.Created);
            await Assert.That(ReadBoolField(secondBody, "replay")).IsTrue();
        }

        // Verification of list
        using var listReq = new HttpRequestMessage(HttpMethod.Get, "/api/systems/me/alters");
        AttachPrincipalAuth(listReq, client, principalId);
        var listRes = await client.SendAsync(listReq);
        var listBody = await listRes.Content.ReadAsStringAsync();
        
        using var listDoc = JsonDocument.Parse(listBody);
        var altersData = listDoc.RootElement.GetProperty("data");
        using (Assert.Multiple())
        {
            await Assert.That(listRes.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(altersData.GetArrayLength()).IsGreaterThan(0);
        }
    }


    [Test, Skip("To readd")]
    public async Task Api_FailsFast_WithoutJwtAuthority_WhenDevHeaderBypassOff()
    {
        using var client = fixture.Factory.CreateClient();

        //TODO: Readd
        /*var exited = await WaitForExitAsync(process, timeoutMs: 12000);
        Ensure(exited, "Expected API process to fail fast, but it did not exit in time.");
        Ensure(process.ExitCode != 0, "Expected non-zero exit code when JWT authority is missing with dev bypass off.");

        var stderr = await process.StandardError.ReadToEndAsync();
        var stdout = await process.StandardOutput.ReadToEndAsync();
        var combined = string.Concat(stdout, "\n", stderr);

        Ensure(combined.Contains("OCTOCON_JWT_AUTHORITY", StringComparison.Ordinal),
            $"Expected startup guardrail message mentioning OCTOCON_JWT_AUTHORITY. Output: {combined}");*/
    }
}
