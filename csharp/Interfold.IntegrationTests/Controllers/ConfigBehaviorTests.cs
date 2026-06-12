using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Interfold.Contracts.Configuration;
using Interfold.IntegrationTests.TestServices;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Interfold.IntegrationTests.Controllers;

/// <summary>
/// Tests that verify configuration/environment-variable driven behavior.
/// SharedType.None ensures each test gets its own fresh InMemory fixture instance,
/// so WithConfiguration mutations are isolated per test.
/// </summary>
[ClassDataSource<InMemoryWebFactoryFixture>(Shared = SharedType.None)]
public sealed class ConfigBehaviorTests(IWebFactoryFixture fixture) : BaseEndpointTest
{
        [Test]
    public async Task NodeRole_ReturnsPrimary_WhenOctoconNodeGroupIsPrimary()
    {
        fixture.Factory.WithConfiguration("OCTOCON_NODE_GROUP", "primary");
        using var client = fixture.Factory.CreateClient();

        var response = await client.GetAsync("/health/node-role");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        var body = await response.Content.ReadAsStringAsync();
        var role = ReadStringField(body, "role");
        var ownsSingletons = ReadBoolField(body, "owns_singletons");

        using (Assert.Multiple())
        {
            await Assert.That(role).IsEqualTo("primary");
            await Assert.That(ownsSingletons).IsTrue();
        }
    }

    [Test]
    public async Task NodeRole_ReturnsPrimary_WhenFlyProcessGroupIsPrimary()
    {
        fixture.Factory.WithConfiguration("FLY_PROCESS_GROUP", "primary");
        using var client = fixture.Factory.CreateClient();

        var response = await client.GetAsync("/health/node-role");

        var body = await response.Content.ReadAsStringAsync();
        var role = ReadStringField(body, "role");
        using (Assert.Multiple())
        {
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(role).IsEqualTo("primary");
        }
    }

    [Test]
    public async Task NodeRole_FlyProcessGroup_TakesPrecedenceOver_OctoconNodeGroup()
    {
        fixture.Factory
            .WithConfiguration("OCTOCON_NODE_GROUP", "primary")
            .WithConfiguration("FLY_PROCESS_GROUP", "sidecar");
        using var client = fixture.Factory.CreateClient();

        var response = await client.GetAsync("/health/node-role");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        var body = await response.Content.ReadAsStringAsync();
        var role = ReadStringField(body, "role");
        var ownsSingletons = ReadBoolField(body, "owns_singletons");

        using (Assert.Multiple())
        {
            await Assert.That(role).IsEqualTo("sidecar");
            await Assert.That(ownsSingletons).IsFalse();
        }
    }

    [Test]
    public async Task Api_AuthRequest_IssuesChallengeRedirect_WhenChallengeEnabledAndSchemeConfigured()
    {
        fixture.Factory
            .WithConfiguration("OCTOCON_AUTH_CHALLENGE_GOOGLE_SCHEME", "oauth-google")
            .WithConfiguration("OCTOCON_AUTH_CHALLENGE_GOOGLE_ENDPOINT", "https://accounts.example.test/oauth/authorize");

        using var client = fixture.Factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var response = await client.GetAsync("/auth/google");

        var locationHeader = response.Headers.Location;
        var location = locationHeader!.ToString();
        using (Assert.Multiple())
        {
            await Assert.That(response.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.Found).IsTrue();
            await Assert.That(locationHeader).IsNotNull();
            await Assert.That(location.StartsWith("https://accounts.example.test/oauth/authorize", StringComparison.Ordinal)).IsTrue();
            await Assert.That(location.Contains("%2Fauth%2Fgoogle%2Fcallback", StringComparison.Ordinal)).IsTrue();
        }
    }

    [Test]
    public async Task Api_OAuthCallback_IssuesJwsCompactSerializationToken()
    {
        fixture.Factory
            .WithConfiguration("OCTOCON_DEEPLINK_ADDRESS", "octocon://app")
            .WithConfiguration("OCTOCON_SCYLLA_KEYSPACE", "nam");

        using var client = fixture.Factory.CreateClient(new WebApplicationFactoryClientOptions
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

        var config = fixture.Factory.Services.GetRequiredService<IConfiguration>();
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

    [Test]
    public async Task Api_SettingsAvatarMultipart_PersistsAndServesAvatar()
    {
        var runId = Guid.NewGuid().ToString("N");
        var storageRoot = Path.Combine(Path.GetTempPath(), "octocon-itest", "avatars", runId);
        var publicBase = $"/avatars-itest/{runId}";

        try
        {
            Directory.CreateDirectory(storageRoot);

            fixture.Factory
                .WithConfiguration("OCTOCON_AVATAR_STORAGE_ROOT", storageRoot)
                .WithConfiguration("OCTOCON_AVATAR_PUBLIC_BASE", publicBase);

            using var client = fixture.Factory.CreateClient();

            var principalId = $"sys-avatar-{Guid.NewGuid():N}"[..18];

            using var uploadRequest = BuildMultipartUploadRequest(client, "/api/settings/avatar", principalId, "avatar-system.png", "image/png");
            var uploadResponse = await client.SendAsync(uploadRequest);
            await Assert.That(uploadResponse.StatusCode).IsEqualTo(HttpStatusCode.NoContent);

            using var profileRequest = new HttpRequestMessage(HttpMethod.Get, $"/api/systems/{principalId}");
            AttachPrincipalAuth(profileRequest, client, principalId);
            var profileResponse = await client.SendAsync(profileRequest);
            var profileBody = await profileResponse.Content.ReadAsStringAsync();

            var avatarUrl = ReadNestedStringField(profileBody, "data", "avatar_url");
            using (Assert.Multiple())
            {
                await Assert.That(profileResponse.StatusCode).IsEqualTo(HttpStatusCode.OK);
                await Assert.That(avatarUrl).IsNotNullOrWhiteSpace();
                await Assert.That(UrlPathStartsWith(avatarUrl, $"{publicBase}/{principalId}/self/")).IsTrue();
            }
        }
        finally
        {
            if (Directory.Exists(storageRoot))
                Directory.Delete(storageRoot, true);
        }
    }

    [Test]
    public async Task Api_AlterAvatarMultipart_PersistsAndReflectsOnPublicAlter()
    {
        var runId = Guid.NewGuid().ToString("N");
        var storageRoot = Path.Combine(Path.GetTempPath(), "octocon-itest", "avatars", runId);
        var publicBase = $"/avatars-itest/{runId}";

        try
        {
            Directory.CreateDirectory(storageRoot);

            fixture.Factory
                .WithConfiguration("OCTOCON_AVATAR_STORAGE_ROOT", storageRoot)
                .WithConfiguration("OCTOCON_AVATAR_PUBLIC_BASE", publicBase);

            using var client = fixture.Factory.CreateClient();

            var principalId = $"sys-alter-avatar-{Guid.NewGuid():N}"[..24];

            using var usernameRequest = new HttpRequestMessage(HttpMethod.Post, "/api/settings/username")
            {
                Content = JsonContent.Create(new { username = "avatar-parity" })
            };
            AttachPrincipalAuth(usernameRequest, client, principalId);
            var usernameResponse = await client.SendAsync(usernameRequest);
            await Assert.That(usernameResponse.StatusCode).IsEqualTo(HttpStatusCode.NoContent);

            using var createRequest = new HttpRequestMessage(HttpMethod.Post, "/api/systems/me/alters")
            {
                Content = JsonContent.Create(new { name = "AvatarTarget" })
            };
            AttachPrincipalAuth(createRequest, client, principalId);

            var createResponse = await client.SendAsync(createRequest);
            await Assert.That(createResponse.StatusCode).IsEqualTo(HttpStatusCode.Created);

            var alterId = ReadTrailingIntFromLocation(createResponse);

            using var uploadRequest = BuildMultipartUploadRequest(client, $"/api/systems/me/alters/{alterId}/avatar", principalId, "avatar-alter.png", "image/png");
            var uploadResponse = await client.SendAsync(uploadRequest);
            await Assert.That(uploadResponse.StatusCode).IsEqualTo(HttpStatusCode.NoContent);

            using var publicAlterRequest = new HttpRequestMessage(HttpMethod.Get, $"/api/systems/{principalId}/alters/{alterId}");
            AttachPrincipalAuth(publicAlterRequest, client, principalId);
            var publicAlterResponse = await client.SendAsync(publicAlterRequest);
            var publicAlterBody = await publicAlterResponse.Content.ReadAsStringAsync();

            var expectedPrefix = $"{publicBase}/{principalId}/{alterId}/";
            var alterAvatarUrl = ReadNestedStringField(publicAlterBody, "data", "avatar_url");
            using (Assert.Multiple())
            {
                await Assert.That(publicAlterResponse.StatusCode).IsEqualTo(HttpStatusCode.OK);
                await Assert.That(alterAvatarUrl).IsNotNullOrWhiteSpace();
                await Assert.That(UrlPathStartsWith(alterAvatarUrl, expectedPrefix)).IsTrue();
            }

            using var deleteReq = new HttpRequestMessage(HttpMethod.Delete, $"/api/systems/me/alters/{alterId}/avatar")
            {
                Content = JsonContent.Create(new { })
            };
            AttachPrincipalAuth(deleteReq, client, principalId);
            var deleteRes = await client.SendAsync(deleteReq);
            await Assert.That(deleteRes.StatusCode).IsEqualTo(HttpStatusCode.NoContent);

            using var afterDeleteRequest = new HttpRequestMessage(HttpMethod.Get, $"/api/systems/{principalId}/alters/{alterId}");
            AttachPrincipalAuth(afterDeleteRequest, client, principalId);
            var afterDeleteResponse = await client.SendAsync(afterDeleteRequest);
            var afterDeleteBody = await afterDeleteResponse.Content.ReadAsStringAsync();

            var staleAvatarUrl = ReadNestedStringField(afterDeleteBody, "data", "avatar_url");
            using (Assert.Multiple())
            {
                await Assert.That(afterDeleteResponse.StatusCode).IsEqualTo(HttpStatusCode.OK);
                await Assert.That(staleAvatarUrl).IsNullOrWhiteSpace();
            }
        }
        finally
        {
            if (Directory.Exists(storageRoot))
                Directory.Delete(storageRoot, true);
        }
    }
}
