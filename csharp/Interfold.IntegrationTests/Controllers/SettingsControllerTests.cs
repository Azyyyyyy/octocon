using System.Net;
using System.Net.Http.Json;
using Interfold.IntegrationTests.Attributes;
using Interfold.IntegrationTests.TestServices;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Interfold.IntegrationTests.Controllers;

public class SettingsControllerTests : BaseEndpointTest
{
    [Test, ApiIntegration]
    public async Task Api_SettingsAvatarMultipart_PersistsAndServesAvatar()
    {
        var runId = Guid.NewGuid().ToString("N");
        var storageRoot = Path.Combine(Path.GetTempPath(), "octocon-itest", "avatars", runId);
        var publicBase = $"/avatars-itest/{runId}";

        try
        {
            Directory.CreateDirectory(storageRoot);

            await using var factory = new InterfoldWebApplicationFactory()
                .WithConfiguration("OCTOCON_PERSISTENCE", "inmemory")
                .WithConfiguration("OCTOCON_AVATAR_STORAGE_ROOT", storageRoot)
                .WithConfiguration("OCTOCON_AVATAR_PUBLIC_BASE", publicBase);
            
            using var client = factory.CreateClient();

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
    
    [Test, ApiIntegration]
    public async Task SettingsField_InvalidType_FallsBackToText_ReturnsCreatedWithId()
    {
        await using var factory = new InterfoldWebApplicationFactory()
            .WithConfiguration("OCTOCON_PERSISTENCE", "inmemory")
            .WithConfiguration("OCTOCON_AUTH_CHALLENGE_ENABLED", "false");

        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var principal = "parity-field-fallback";

        // type = "garbage" — Elixir falls back to "text"; C# must do the same
        using var req1 = new HttpRequestMessage(HttpMethod.Post, "/api/settings/fields")
        {
            Content = JsonContent.Create(new { name = "FallbackField", type = "garbage" })
        };
        AttachPrincipalAuth(req1, client, principal);
        var res1 = await client.SendAsync(req1);
        var body1 = await res1.Content.ReadAsStringAsync();

        using (Assert.Multiple())
        {
            await Assert.That(res1.StatusCode).IsEqualTo(HttpStatusCode.Created);
            await Assert.That(ReadNestedString(body1, "data", "id")).IsNotNullOrWhiteSpace();
        }
    }
    
    [Test, ApiIntegration]
    public async Task SettingsField_MissingType_FallsBackToText_ReturnsCreatedWithId()
    {
        await using var factory = new InterfoldWebApplicationFactory()
            .WithConfiguration("OCTOCON_PERSISTENCE", "inmemory")
            .WithConfiguration("OCTOCON_AUTH_CHALLENGE_ENABLED", "false");

        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var principal = "parity-field-missing-type";

        // type absent entirely
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/settings/fields")
        {
            Content = JsonContent.Create(new { name = "NoTypeField" })
        };
        AttachPrincipalAuth(req, client, principal);
        var res = await client.SendAsync(req);
        var body = await res.Content.ReadAsStringAsync();

        using (Assert.Multiple())
        {
            await Assert.That(res.StatusCode).IsEqualTo(HttpStatusCode.Created);
            await Assert.That(ReadNestedString(body, "data", "id")).IsNotNullOrWhiteSpace();
        }
    }
    
    [Test, ApiIntegration]
    public async Task Idempotency_SettingsUsernameUpdate_ReplayStable()
    {
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
    
    [Test, ApiIntegration]
    public async Task SettingsField_Create_ReturnsCreatedFieldId()
    {
        await using var factory = new InterfoldWebApplicationFactory()
            .WithConfiguration("OCTOCON_PERSISTENCE", "inmemory")
            .WithConfiguration("OCTOCON_AUTH_CHALLENGE_ENABLED", "false");

        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var principal = "parity-field-defaults";

        // Create field without explicit security_level.
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/settings/fields")
        {
            Content = JsonContent.Create(new { name = "DefaultSecurityField", type = "text" })
        };
        AttachPrincipalAuth(req, client, principal);
        var res = await client.SendAsync(req);
        var body = await res.Content.ReadAsStringAsync();

        var fieldId = ReadNestedString(body, "data", "id");

        using (Assert.Multiple())
        {
            await Assert.That(res.StatusCode).IsEqualTo(HttpStatusCode.Created);
            await Assert.That(fieldId).IsNotNullOrWhiteSpace();
        }
    }
}