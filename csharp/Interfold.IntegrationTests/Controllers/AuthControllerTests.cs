using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Interfold.Infrastructure.Configuration;
using Interfold.IntegrationTests.Attributes;
using Interfold.IntegrationTests.TestServices;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using TUnit.Core.Services;

namespace Interfold.IntegrationTests.Controllers;

/// <summary>
/// Integration tests for authentication, OAuth flows, and WebSocket upgrades.
/// Refactored to use WebApplicationFactory for in-memory testing.
/// </summary>
public sealed class AuthControllerTests : BaseEndpointTest
{
    [Test, ApiIntegration]
    [CombinedDataSources]
    public async Task Api_AuthRequest_FallsBackTo403_WhenChallengeDisabled([InterfoldFactoryGenerator] InterfoldWebApplicationFactory factory)
    {
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var response = await client.GetAsync("/auth/google");
        var body = await response.Content.ReadAsStringAsync();

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
    }

    [Test, ApiIntegration]
    [CombinedDataSources]
    public async Task Api_AuthRequest_IssuesChallengeRedirect_WhenChallengeEnabledAndSchemeConfigured([InterfoldFactoryGenerator] InterfoldWebApplicationFactory factory)
    {
        factory
            .WithConfiguration("OCTOCON_AUTH_CHALLENGE_GOOGLE_SCHEME", "oauth-google")
            .WithConfiguration("OCTOCON_AUTH_CHALLENGE_GOOGLE_ENDPOINT", "https://accounts.example.test/oauth/authorize");

        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var response = await client.GetAsync("/auth/google");
        var body = await response.Content.ReadAsStringAsync();

        var locationHeader = response.Headers.Location;
        var location = locationHeader!.ToString();
        using (Assert.Multiple())
        {
            await Assert.That(response.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.Found).IsTrue();
            await Assert.That(locationHeader).IsNotNull();
            await Assert.That(location.StartsWith("https://accounts.example.test/oauth/authorize", StringComparison.Ordinal)).IsTrue();
            await Assert.That(location.Contains("redirect_uri=%2Fauth%2Fgoogle%2Fcallback", StringComparison.Ordinal)).IsTrue();
        }
    }

    //TODO: DO we need this? Maybe can split it up another way...
    [Test, ApiIntegration]
    [CombinedDataSources]
    public async Task Api_AuthAndIdempotencyFlow_VerifiesEndToEndBehavior([InterfoldFactoryGenerator] InterfoldWebApplicationFactory factory)
    {
        using var client = factory.CreateClient();
        var principalId = "sys-api-smoke";

        var heartbeat = await client.GetAsync("/api/heartbeat");
        await Assert.That(heartbeat.StatusCode).IsEqualTo(HttpStatusCode.OK);

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


    [Test, ApiIntegration, Skip("To readd")]
    [CombinedDataSources]
    public async Task Api_FailsFast_WithoutJwtAuthority_WhenDevHeaderBypassOff([InterfoldFactoryGenerator] InterfoldWebApplicationFactory factory)
    {
        using var client = factory.CreateClient();

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

    [Test, ApiIntegration]
    [CombinedDataSources]
    public async Task Api_OAuthCallback_IssuesJwsCompactSerializationToken([InterfoldFactoryGenerator] InterfoldWebApplicationFactory factory)
    {
        factory
            .WithConfiguration("OCTOCON_DEEPLINK_ADDRESS", "octocon://app")
            .WithConfiguration("OCTOCON_REGION", "nam");

        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var discordUid = $"jws-test-{Guid.NewGuid():N}";
        var response = await client.GetAsync($"/auth/discord/callback?uid={Uri.EscapeDataString(discordUid)}");
        var body = await response.Content.ReadAsStringAsync();

        await Assert.That(response.StatusCode)
            .Satisfies(x => x is HttpStatusCode.Redirect or HttpStatusCode.Found
        or HttpStatusCode.MovedPermanently or HttpStatusCode.TemporaryRedirect
            or HttpStatusCode.PermanentRedirect)
            .Because($"Expected OAuth callback redirect response, got {(int)response.StatusCode}. Body: {body}");

        var location = response.Headers.Location
            ?? throw new InvalidOperationException("Expected Location header in redirect response.");

        var query = System.Web.HttpUtility.ParseQueryString(location.Query);
        var token = query["token"];

        await Assert.That(token).IsNotNullOrWhiteSpace();

        var segments = token!.Split('.');
        await Assert.That(segments.Length).IsEqualTo(3);

        using (Assert.Multiple())
        {
            foreach (var (segment, index) in segments.Select((s, i) => (s, i)))
            {
                await Assert.That(segment).IsNotNullOrWhiteSpace();
                await Assert.That(!segment.Contains('+') && !segment.Contains('/') && !segment.Contains('=')).IsTrue();
            }
        }

        var headerBytes = Base64UrlDecodeBytes(segments[0]);
        using var headerDoc = JsonDocument.Parse(headerBytes);
        var alg = headerDoc.RootElement.GetProperty("alg").GetString();
        var typ = headerDoc.RootElement.GetProperty("typ").GetString();
        using (Assert.Multiple())
        {
            await Assert.That(alg).IsEqualTo("ES256");
            await Assert.That(typ).IsEqualTo("JWT");
        }

        var payloadBytes = Base64UrlDecodeBytes(segments[1]);
        using var payloadDoc = JsonDocument.Parse(payloadBytes);
        var root = payloadDoc.RootElement;

        var config = factory.Services.GetRequiredService<IConfiguration>();
        var authConfig = config.Get<AuthenticationConfiguration>();
        var expectedIssuer = authConfig?.JwtAuthority;

        using (Assert.Multiple())
        {
            await Assert.That(
                root.TryGetProperty("iss", out var iss)
                && !string.IsNullOrWhiteSpace(iss.GetString())
                && (string.IsNullOrWhiteSpace(expectedIssuer)
                    || string.Equals(iss.GetString(), expectedIssuer, StringComparison.Ordinal)))
                .IsTrue();
            await Assert.That(root.TryGetProperty("sub", out var sub) && !string.IsNullOrWhiteSpace(sub.GetString())).IsTrue();
        }

        long iatVal = 0;
        long expVal = 0;

        using (Assert.Multiple())
        {
            await Assert.That(root.TryGetProperty("iat", out var iat) && iat.TryGetInt64(out iatVal) && iatVal > 0).IsTrue();
            await Assert.That(root.TryGetProperty("nbf", out var nbf) && nbf.TryGetInt64(out _)).IsTrue();
            await Assert.That(root.TryGetProperty("exp", out var exp) && exp.TryGetInt64(out expVal) && expVal > iatVal).IsTrue();
            await Assert.That(root.TryGetProperty("jti", out var jti) && !string.IsNullOrWhiteSpace(jti.GetString())).IsTrue();
            await Assert.That(root.TryGetProperty("scope", out var scope) &&
                   string.Equals(scope.GetString(), "octocon:deeplink", StringComparison.Ordinal)).IsTrue();
        }
    }
}