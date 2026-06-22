using System.Net;
using System.Net.Http.Json;
using Interfold.IntegrationTests.TestServices;

namespace Interfold.IntegrationTests.Controllers;

/// <summary>
/// Regression coverage that documents the env-var seed contract for the in-memory
/// <c>ISecretsStore</c> with a self-describing test name: a future refactor that breaks
/// the <c>OCTOCON_INMEMORY_SECRETS_SEED__*</c> wiring fails here with a clear signal,
/// rather than a flood of red across every other in-memory integration test.
///
/// The factory is constructed inline (no <c>[ClassDataSource]</c>) so the test boots a
/// fresh host whose only secrets material flows through the production env-var path
/// registered in <see cref="Interfold.Infrastructure.InMemory.InMemoryServiceCollectionExtensions"/>
/// — exactly what the published image does when an external runner (e.g. the Kotlin
/// Testcontainers harness) drives it as a separate process.
/// </summary>
public class InMemorySecretsSeedTests : BaseEndpointTest
{
    [Test]
    public async Task Api_InMemorySecretsSeed_PatchesAuthFromEnvVars()
    {
        await using var factory = new InterfoldWebApplicationFactory("inmemory");
        using var client = factory.CreateClient();

        var principalId = $"sys-secrets-seed-{Guid.NewGuid():N}"[..24];

        // POST /api/settings/username is a [Authorize]-gated route. Reaching 204 NoContent
        // proves end-to-end that:
        //   (a) the host started without tripping SecretsBootstrapService's hard
        //       fail-fast on `encryption:pepper` (so OCTOCON_INMEMORY_SECRETS_SEED__ENCRYPTION_PEPPER
        //       was honoured), and
        //   (b) the JWT minted by CreateToken with TestDbCredentials.JwtEs256PrivateKeyPem
        //       verifies against AuthenticationConfiguration.JwtEs256VerificationKeyPems —
        //       which in turn proves OCTOCON_INMEMORY_SECRETS_SEED__AUTH_JWT_ES256_PRIVATE_PEM
        //       was seeded into the store and patched onto AuthenticationConfiguration by
        //       SecretsBootstrapService at startup.
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/settings/username")
        {
            Content = JsonContent.Create(new { username = principalId })
        };
        AttachPrincipalAuth(request, client, principalId);

        var response = await client.SendAsync(request);

        await Assert.That(response.StatusCode)
            .IsEqualTo(HttpStatusCode.NoContent)
            .Because($"Expected 204 NoContent, got {(int)response.StatusCode}. Body: {await response.Content.ReadAsStringAsync()}");
    }
}
