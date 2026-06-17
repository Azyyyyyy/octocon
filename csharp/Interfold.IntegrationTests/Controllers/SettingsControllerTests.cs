using System.Net;
using System.Net.Http.Json;
using Interfold.IntegrationTests.TestServices;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Interfold.IntegrationTests.Controllers;

[ClassDataSource<InMemoryWebFactoryFixture>(Shared = SharedType.PerTestSession)]
[ClassDataSource<ScyllaWebFactoryFixture>(Shared = SharedType.PerTestSession)]
[ClassDataSource<CassandraWebFactoryFixture>(Shared = SharedType.PerTestSession)]
public class SettingsControllerTests(IWebFactoryFixture fixture) : BaseEndpointTest
{
    [Test]
    public async Task SettingsField_InvalidType_FallsBackToText_ReturnsCreatedWithId()
    {
        using var client = fixture.Factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var principal = "parity-field-fallback";
        await EnsureUserExistsAsync(client, principal);

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
    
    [Test]
    public async Task SettingsField_MissingType_FallsBackToText_ReturnsCreatedWithId()
    {
        using var client = fixture.Factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var principal = "parity-field-missing-type";
        await EnsureUserExistsAsync(client, principal);

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
    
    [Test]
    public async Task Idempotency_SettingsUsernameUpdate_ReplayStable()
    {
        await RunSoakAsync(fixture.Factory, async (client, key) =>
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
    public async Task SettingsField_Create_ReturnsCreatedFieldId()
    {
        using var client = fixture.Factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var principal = "parity-field-defaults";
        await EnsureUserExistsAsync(client, principal);

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

    // Both avatar multipart tests mutate OCTOCON_AVATAR_STORAGE_ROOT and
    // OCTOCON_AVATAR_PUBLIC_BASE on the shared factory via WithConfiguration. With
    // IOptionsMonitor live-reload now wired through, those writes flow into every
    // in-flight LocalAvatarStorage save call across the host. Two parallel tests
    // racing on the same keys would interleave their values: the test that called
    // WithConfiguration most recently wins, the other test reads back a URL stamped
    // with the wrong publicBase prefix and fails its UrlPathStartsWith assertion.
    // Serialise the two tests with a shared NotInParallel key so they take turns.
    [Test, NotInParallel("avatar-storage-config")]
    public async Task Api_SettingsAvatarMultipart_PersistsAndServesAvatar()
    {
        var runId = Guid.NewGuid().ToString("N");
        var storageRoot = Path.Combine(Path.GetTempPath(), "octocon-itest", "avatars", runId);
        var publicBase = $"/avatars-itest/{runId}";

        try
        {
            Directory.CreateDirectory(storageRoot);

            // StorageConfiguration is bound via IOptionsMonitor so WithConfiguration writes
            // propagate live to the running host - no factory rebuild needed.
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

    [Test, NotInParallel("avatar-storage-config")]
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