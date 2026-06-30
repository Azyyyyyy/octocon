using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Interfold.IntegrationTests.TestServices;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Interfold.IntegrationTests.Controllers;

[ClassDataSource<InMemoryWebFactoryFixture>(Shared = SharedType.PerTestSession)]
[ClassDataSource<ScyllaWebFactoryFixture>(Shared = SharedType.PerTestSession)]
[ClassDataSource<CassandraWebFactoryFixture>(Shared = SharedType.PerTestSession)]
public class AvatarSourceTests(IWebFactoryFixture fixture) : BaseEndpointTest
{
    private const string ExternalSystemAvatar = "https://cdn.example.com/system-avatar.png";
    private const string ExternalAlterAvatar  = "https://cdn.example.com/alter-avatar.png";

    [Test]
    public async Task Api_SettingsAvatarByUrl_PersistsAndExposesExternalSource()
    {
        using var client = fixture.Factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });

        var principalId = $"sys-extavatar-{Guid.NewGuid():N}"[..24];
        await EnsureUserExistsAsync(client, principalId);

        using var uploadRequest = new HttpRequestMessage(HttpMethod.Put, "/api/settings/avatar")
        {
            Content = JsonContent.Create(new { url = ExternalSystemAvatar }),
        };
        AttachPrincipalAuth(uploadRequest, client, principalId);
        var uploadResponse = await client.SendAsync(uploadRequest);
        await Assert.That(uploadResponse.StatusCode).IsEqualTo(HttpStatusCode.NoContent);

        using var profileRequest = new HttpRequestMessage(HttpMethod.Get, $"/api/systems/{principalId}");
        AttachPrincipalAuth(profileRequest, client, principalId);
        var profileResponse = await client.SendAsync(profileRequest);
        var profileBody = await profileResponse.Content.ReadAsStringAsync();

        var avatarUrl = ReadNestedStringField(profileBody, "data", "avatar_url");
        var avatarSource = ReadNestedStringField(profileBody, "data", "avatar_source");

        using (Assert.Multiple())
        {
            await Assert.That(profileResponse.StatusCode).IsEqualTo(HttpStatusCode.OK);
            // Passthrough: stored URL is returned verbatim, never qualified with the API origin.
            await Assert.That(avatarUrl).IsEqualTo(ExternalSystemAvatar);
            await Assert.That(avatarSource).IsEqualTo("external");
        }
    }

    [Test]
    public async Task Api_AlterAvatarByUrl_PersistsAndExposesExternalSource()
    {
        using var client = fixture.Factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });

        var principalId = $"sys-altext-{Guid.NewGuid():N}"[..20];

        using var usernameRequest = new HttpRequestMessage(HttpMethod.Post, "/api/settings/username")
        {
            Content = JsonContent.Create(new { username = "external-alter" }),
        };
        AttachPrincipalAuth(usernameRequest, client, principalId);
        var usernameResponse = await client.SendAsync(usernameRequest);
        await Assert.That(usernameResponse.StatusCode).IsEqualTo(HttpStatusCode.NoContent);

        using var createRequest = new HttpRequestMessage(HttpMethod.Post, "/api/systems/me/alters")
        {
            Content = JsonContent.Create(new { name = "ExternalAvatarAlter" }),
        };
        AttachPrincipalAuth(createRequest, client, principalId);
        var createResponse = await client.SendAsync(createRequest);
        await Assert.That(createResponse.StatusCode).IsEqualTo(HttpStatusCode.Created);

        var alterId = ReadTrailingIntFromLocation(createResponse);

        using var uploadRequest = new HttpRequestMessage(HttpMethod.Put, $"/api/systems/me/alters/{alterId}/avatar")
        {
            Content = JsonContent.Create(new { url = ExternalAlterAvatar }),
        };
        AttachPrincipalAuth(uploadRequest, client, principalId);
        var uploadResponse = await client.SendAsync(uploadRequest);
        await Assert.That(uploadResponse.StatusCode).IsEqualTo(HttpStatusCode.NoContent);

        using var alterRequest = new HttpRequestMessage(HttpMethod.Get, $"/api/systems/{principalId}/alters/{alterId}");
        AttachPrincipalAuth(alterRequest, client, principalId);
        var alterResponse = await client.SendAsync(alterRequest);
        var alterBody = await alterResponse.Content.ReadAsStringAsync();

        var avatarUrl = ReadNestedStringField(alterBody, "data", "avatar_url");
        var avatarSource = ReadNestedStringField(alterBody, "data", "avatar_source");

        using (Assert.Multiple())
        {
            await Assert.That(alterResponse.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(avatarUrl).IsEqualTo(ExternalAlterAvatar);
            await Assert.That(avatarSource).IsEqualTo("external");
        }
    }

    /// <summary>
    /// Shape-only validator covers ftp/file/relative URLs and over-length values uniformly.
    /// We use one of the disallowed-scheme cases as the canonical 400 contract.
    /// </summary>
    [Test]
    public async Task Api_SettingsAvatarByUrl_RejectsNonHttpUrl()
    {
        using var client = fixture.Factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });

        var principalId = $"sys-bad-{Guid.NewGuid():N}"[..16];
        await EnsureUserExistsAsync(client, principalId);

        using var uploadRequest = new HttpRequestMessage(HttpMethod.Put, "/api/settings/avatar")
        {
            Content = JsonContent.Create(new { url = "ftp://example.com/avatar.png" }),
        };
        AttachPrincipalAuth(uploadRequest, client, principalId);
        var uploadResponse = await client.SendAsync(uploadRequest);
        var uploadBody = await uploadResponse.Content.ReadAsStringAsync();

        using (Assert.Multiple())
        {
            await Assert.That(uploadResponse.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
            await Assert.That(ReadJsonStringField(uploadBody, "code")).IsEqualTo("avatar_url_invalid");
        }
    }

    [Test]
    public async Task Api_AlterAvatarByUrl_RejectsRelativeUrl()
    {
        using var client = fixture.Factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });

        var principalId = $"sys-altrel-{Guid.NewGuid():N}"[..20];

        using var usernameRequest = new HttpRequestMessage(HttpMethod.Post, "/api/settings/username")
        {
            Content = JsonContent.Create(new { username = "relative-alter" }),
        };
        AttachPrincipalAuth(usernameRequest, client, principalId);
        await client.SendAsync(usernameRequest);

        using var createRequest = new HttpRequestMessage(HttpMethod.Post, "/api/systems/me/alters")
        {
            Content = JsonContent.Create(new { name = "RelativeAvatarAlter" }),
        };
        AttachPrincipalAuth(createRequest, client, principalId);
        var createResponse = await client.SendAsync(createRequest);
        await Assert.That(createResponse.StatusCode).IsEqualTo(HttpStatusCode.Created);
        var alterId = ReadTrailingIntFromLocation(createResponse);

        using var uploadRequest = new HttpRequestMessage(HttpMethod.Put, $"/api/systems/me/alters/{alterId}/avatar")
        {
            Content = JsonContent.Create(new { url = "/relative/path.png" }),
        };
        AttachPrincipalAuth(uploadRequest, client, principalId);
        var uploadResponse = await client.SendAsync(uploadRequest);
        var uploadBody = await uploadResponse.Content.ReadAsStringAsync();

        using (Assert.Multiple())
        {
            await Assert.That(uploadResponse.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
            await Assert.That(ReadJsonStringField(uploadBody, "code")).IsEqualTo("avatar_url_invalid");
        }
    }

    [Test, NotInParallel("avatar-storage-config")]
    public async Task Api_SettingsAvatarMultipart_ReportsLocalSource()
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

            var principalId = $"sys-localavatar-{Guid.NewGuid():N}"[..24];

            using var uploadRequest = BuildMultipartUploadRequest(client, "/api/settings/avatar", principalId, "avatar.png", "image/png");
            var uploadResponse = await client.SendAsync(uploadRequest);
            await Assert.That(uploadResponse.StatusCode).IsEqualTo(HttpStatusCode.NoContent);

            using var profileRequest = new HttpRequestMessage(HttpMethod.Get, $"/api/systems/{principalId}");
            AttachPrincipalAuth(profileRequest, client, principalId);
            var profileResponse = await client.SendAsync(profileRequest);
            var profileBody = await profileResponse.Content.ReadAsStringAsync();

            var avatarSource = ReadNestedStringField(profileBody, "data", "avatar_source");
            var avatarUrl = ReadNestedStringField(profileBody, "data", "avatar_url");

            using (Assert.Multiple())
            {
                await Assert.That(profileResponse.StatusCode).IsEqualTo(HttpStatusCode.OK);
                await Assert.That(avatarSource).IsEqualTo("local");
                // Local avatars get the request origin prepended; the path must hit our
                // configured public base so external observers can fetch the bytes.
                await Assert.That(UrlPathStartsWith(avatarUrl, $"{publicBase}/{principalId}/self/")).IsTrue();
            }

            // End-to-end serving check (mirrors the sibling assertion in
            // SettingsControllerTests.Api_SettingsAvatarMultipart_PersistsAndServesAvatar):
            // the avatar URL the SPA receives MUST be servable. This test covers the
            // local-avatar-source codepath specifically — the assertion sits here, not in
            // the source-only happy-path tests above, because this is the only test in
            // the file that exercises a real multipart upload (the others use external
            // URLs and never persist bytes).
            using var avatarRequest = new HttpRequestMessage(HttpMethod.Get, avatarUrl);
            // Anonymous on purpose — see the sibling test for the rationale (URL is the capability).
            var avatarResponse = await client.SendAsync(avatarRequest);
            var avatarBytes = await avatarResponse.Content.ReadAsByteArrayAsync();
            using (Assert.Multiple())
            {
                await Assert.That(avatarResponse.StatusCode).IsEqualTo(HttpStatusCode.OK)
                    .Because($"Expected the local-source avatar URL to be servable. URL was '{avatarUrl}'.");
                await Assert.That(avatarBytes.Length).IsGreaterThan(0)
                    .Because("Expected non-zero bytes back from the avatar GET.");
            }
        }
        finally
        {
            if (Directory.Exists(storageRoot))
                Directory.Delete(storageRoot, true);
        }
    }

    private static string ReadJsonStringField(string json, string fieldName)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
            return string.Empty;

        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            if (!prop.Name.Equals(fieldName, StringComparison.OrdinalIgnoreCase))
                continue;

            return prop.Value.ValueKind == JsonValueKind.String ? prop.Value.GetString() ?? string.Empty : string.Empty;
        }

        return string.Empty;
    }
}
