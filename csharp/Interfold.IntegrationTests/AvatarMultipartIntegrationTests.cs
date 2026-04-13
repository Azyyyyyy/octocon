using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Interfold.IntegrationTests.Attributes;

namespace Interfold.IntegrationTests;

public sealed class AvatarMultipartIntegrationTests
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

            using var uploadRequest = BuildMultipartUploadRequest("/api/settings/avatar", principalId, "avatar-system.png", "image/png");
            var uploadResponse = await client.SendAsync(uploadRequest);
            Ensure(uploadResponse.StatusCode == HttpStatusCode.NoContent,
                $"Expected settings avatar multipart upload 204, got {(int)uploadResponse.StatusCode}. Body: {await uploadResponse.Content.ReadAsStringAsync()}");

            var profileResponse = await client.GetAsync($"/api/systems/{principalId}");
            var profileBody = await profileResponse.Content.ReadAsStringAsync();
            Ensure(profileResponse.StatusCode == HttpStatusCode.OK,
                $"Expected public system profile 200 after upload, got {(int)profileResponse.StatusCode}. Body: {profileBody}");

            var avatarUrl = ReadNestedStringField(profileBody, "data", "avatar_url");
            Ensure(!string.IsNullOrWhiteSpace(avatarUrl), "Expected avatar_url to be set on public system profile.");
            Ensure(avatarUrl.StartsWith($"{publicBase}/{principalId}/self/", StringComparison.Ordinal),
                $"Expected system avatar URL to be under /avatars/{{system}}/self. Actual: {avatarUrl}");
        }
        finally
        {
            if (Directory.Exists(storageRoot))
                Directory.Delete(storageRoot, true);
        }
    }

    [Test, ApiIntegration]
    public async Task Api_AlterAvatarMultipart_PersistsAndReflectsOnPublicAlter()
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

            var principalId = $"sys-alter-avatar-{Guid.NewGuid():N}"[..24];

            using var usernameRequest = new HttpRequestMessage(HttpMethod.Post, "/api/settings/username")
            {
                Content = JsonContent.Create(new { username = "avatar-parity" })
            };
            usernameRequest.Headers.Add("X-Interfold-Principal", principalId);
            var usernameResponse = await client.SendAsync(usernameRequest);
            Ensure(usernameResponse.StatusCode == HttpStatusCode.NoContent,
                $"Expected username bootstrap 204, got {(int)usernameResponse.StatusCode}. Body: {await usernameResponse.Content.ReadAsStringAsync()}");

            using var createRequest = new HttpRequestMessage(HttpMethod.Post, "/api/systems/me/alters")
            {
                Content = JsonContent.Create(new { name = "AvatarTarget" })
            };
            createRequest.Headers.Add("X-Interfold-Principal", principalId);

            var createResponse = await client.SendAsync(createRequest);
            var createBody = await createResponse.Content.ReadAsStringAsync();
            Ensure(createResponse.StatusCode == HttpStatusCode.Created,
                $"Expected alter create 201, got {(int)createResponse.StatusCode}. Body: {createBody}");

            var alterId = ReadTrailingIntFromLocation(createResponse);

            using var uploadRequest = BuildMultipartUploadRequest($"/api/systems/me/alters/{alterId}/avatar", principalId, "avatar-alter.png", "image/png");
            var uploadResponse = await client.SendAsync(uploadRequest);
            Ensure(uploadResponse.StatusCode == HttpStatusCode.NoContent,
                $"Expected alter avatar multipart upload 204, got {(int)uploadResponse.StatusCode}. Body: {await uploadResponse.Content.ReadAsStringAsync()}");

            var publicAlterResponse = await client.GetAsync($"/api/systems/{principalId}/alters/{alterId}");
            var publicAlterBody = await publicAlterResponse.Content.ReadAsStringAsync();
            Ensure(publicAlterResponse.StatusCode == HttpStatusCode.OK,
                $"Expected public alter 200 after avatar upload, got {(int)publicAlterResponse.StatusCode}. Body: {publicAlterBody}");

            var expectedPrefix = $"{publicBase}/{principalId}/{alterId}/";
            var alterAvatarUrl = FindStringContaining(publicAlterBody, expectedPrefix);
            Ensure(!string.IsNullOrWhiteSpace(alterAvatarUrl),
                $"Expected public alter payload to include avatar URL with prefix '{expectedPrefix}'. Body: {publicAlterBody}");

            using var deleteReq = new HttpRequestMessage(HttpMethod.Delete, $"/api/systems/me/alters/{alterId}/avatar")
            {
                Content = JsonContent.Create(new { })
            };
            deleteReq.Headers.Add("X-Interfold-Principal", principalId);
            var deleteRes = await client.SendAsync(deleteReq);
            Ensure(deleteRes.StatusCode == HttpStatusCode.NoContent,
                $"Expected alter avatar delete 204, got {(int)deleteRes.StatusCode}. Body: {await deleteRes.Content.ReadAsStringAsync()}");

            var afterDeleteResponse = await client.GetAsync($"/api/systems/{principalId}/alters/{alterId}");
            var afterDeleteBody = await afterDeleteResponse.Content.ReadAsStringAsync();
            Ensure(afterDeleteResponse.StatusCode == HttpStatusCode.OK,
                $"Expected public alter 200 after avatar delete, got {(int)afterDeleteResponse.StatusCode}. Body: {afterDeleteBody}");

            var staleAvatarUrl = FindStringContaining(afterDeleteBody, expectedPrefix);
            Ensure(string.IsNullOrWhiteSpace(staleAvatarUrl),
                $"Expected no avatar URL under '{expectedPrefix}' after delete. Body: {afterDeleteBody}");
        }
        finally
        {
            if (Directory.Exists(storageRoot))
                Directory.Delete(storageRoot, true);
        }
    }

    private static HttpRequestMessage BuildMultipartUploadRequest(string path, string principalId, string fileName, string contentType)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, path);
        request.Headers.Add("X-Interfold-Principal", principalId);
        request.Headers.Add("X-Interfold-Idempotency-Key", Guid.NewGuid().ToString("N"));

        var data = Encoding.UTF8.GetBytes("octocon-avatar-bytes");
        var fileContent = new ByteArrayContent(data);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue(contentType);

        var form = new MultipartFormDataContent();
        form.Add(fileContent, "file", fileName);

        request.Content = form;

        return request;
    }

    private static int ReadTrailingIntFromLocation(HttpResponseMessage response)
    {
        var location = response.Headers.Location?.ToString();
        if (string.IsNullOrWhiteSpace(location))
            throw new InvalidOperationException("Expected Location header on alter-create response.");

        var segment = location.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        if (!int.TryParse(segment, out var id) || id <= 0)
            throw new InvalidOperationException($"Could not parse alter id from Location header '{location}'.");

        return id;
    }

    private static string ReadNestedStringField(string json, string parentField, string childField)
    {
        using var doc = JsonDocument.Parse(json);

        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            if (!prop.Name.Equals(parentField, StringComparison.OrdinalIgnoreCase))
                continue;

            if (prop.Value.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException($"Expected object for field '{parentField}'.");

            foreach (var child in prop.Value.EnumerateObject())
            {
                if (!child.Name.Equals(childField, StringComparison.OrdinalIgnoreCase))
                    continue;

                return child.Value.ValueKind == JsonValueKind.String
                    ? child.Value.GetString() ?? string.Empty
                    : string.Empty;
            }
        }

        return string.Empty;
    }

    private static string FindStringContaining(string json, string expectedSubstring)
    {
        using var doc = JsonDocument.Parse(json);
        return FindStringContaining(doc.RootElement, expectedSubstring) ?? string.Empty;
    }

    private static string? FindStringContaining(JsonElement element, string expectedSubstring)
    {
        if (element.ValueKind == JsonValueKind.String)
        {
            var value = element.GetString();
            if (!string.IsNullOrWhiteSpace(value) && value.Contains(expectedSubstring, StringComparison.Ordinal))
                return value;

            return null;
        }

        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                var found = FindStringContaining(property.Value, expectedSubstring);
                if (found is not null)
                    return found;
            }
        }

        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                var found = FindStringContaining(item, expectedSubstring);
                if (found is not null)
                    return found;
            }
        }

        return null;
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
