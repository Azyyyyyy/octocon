using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using TUnit.Core;

namespace Octocon.IntegrationTests;

public sealed class AvatarMultipartIntegrationTests
{
    [Test]
    public async Task Api_SettingsAvatarMultipart_PersistsAndServesAvatar()
    {
        if (!ShouldRunApiIntegration())
            return;

        var workspaceRoot = FindWorkspaceRoot();
        var port = GetFreePort();
        var runId = Guid.NewGuid().ToString("N");
        var storageRoot = Path.Combine(workspaceRoot, "csharp", "Octocon.Api", "wwwroot", "avatars-itest", runId);
        var publicBase = $"/avatars-itest/{runId}";

        await using var api = await StartApiAsync(workspaceRoot, port, storageRoot, publicBase);
        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };

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

        var staticFileResponse = await client.GetAsync(avatarUrl);
        Ensure(staticFileResponse.StatusCode == HttpStatusCode.OK,
            $"Expected uploaded system avatar file to be served from static path. Status: {(int)staticFileResponse.StatusCode}");
    }

    [Test]
    public async Task Api_AlterAvatarMultipart_PersistsAndReflectsOnPublicAlter()
    {
        if (!ShouldRunApiIntegration())
            return;

        var workspaceRoot = FindWorkspaceRoot();
        var port = GetFreePort();
        var runId = Guid.NewGuid().ToString("N");
        var storageRoot = Path.Combine(workspaceRoot, "csharp", "Octocon.Api", "wwwroot", "avatars-itest", runId);
        var publicBase = $"/avatars-itest/{runId}";

        await using var api = await StartApiAsync(workspaceRoot, port, storageRoot, publicBase);
        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };

        var principalId = $"sys-alter-avatar-{Guid.NewGuid():N}"[..24];

        using var usernameRequest = new HttpRequestMessage(HttpMethod.Post, "/api/settings/username")
        {
            Content = JsonContent.Create(new { username = "avatar-parity" })
        };
        usernameRequest.Headers.Add("X-Octocon-Dev-Principal", principalId);
        var usernameResponse = await client.SendAsync(usernameRequest);
        Ensure(usernameResponse.StatusCode == HttpStatusCode.NoContent,
            $"Expected username bootstrap 204, got {(int)usernameResponse.StatusCode}. Body: {await usernameResponse.Content.ReadAsStringAsync()}");

        using var createRequest = new HttpRequestMessage(HttpMethod.Post, "/api/systems/me/alters")
        {
            Content = JsonContent.Create(new { name = "AvatarTarget" })
        };
        createRequest.Headers.Add("X-Octocon-Dev-Principal", principalId);

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

        var staticFileResponse = await client.GetAsync(alterAvatarUrl);
        Ensure(staticFileResponse.StatusCode == HttpStatusCode.OK,
            $"Expected uploaded alter avatar file to be served from static path. Status: {(int)staticFileResponse.StatusCode}");

        using var deleteReq = new HttpRequestMessage(HttpMethod.Delete, $"/api/systems/me/alters/{alterId}/avatar")
        {
            Content = JsonContent.Create(new { })
        };
        deleteReq.Headers.Add("X-Octocon-Dev-Principal", principalId);
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

    private static HttpRequestMessage BuildMultipartUploadRequest(string path, string principalId, string fileName, string contentType)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, path);

        var data = Encoding.UTF8.GetBytes("octocon-avatar-bytes");
        var fileContent = new ByteArrayContent(data);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue(contentType);

        var form = new MultipartFormDataContent();
        form.Add(fileContent, "file", fileName);

        request.Content = form;
        request.Headers.Add("X-Octocon-Dev-Principal", principalId);
        request.Headers.Add("X-Octocon-Idempotency-Key", Guid.NewGuid().ToString("N"));

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

    private static bool ShouldRunApiIntegration()
    {
        var run = Environment.GetEnvironmentVariable("OCTOCON_RUN_API_INTEGRATION");
        return bool.TryParse(run, out var enabled) && enabled;
    }

    private static string FindWorkspaceRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);

        while (current is not null)
        {
            var marker = Path.Combine(current.FullName, "octocon.sln");
            if (File.Exists(marker))
                return current.FullName;

            current = current.Parent;
        }

        throw new InvalidOperationException("Could not find workspace root containing octocon.sln.");
    }

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();

        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    private static async Task<RunningApi> StartApiAsync(string workspaceRoot, int port, string storageRoot, string publicBase)
    {
        Directory.CreateDirectory(storageRoot);

        var gateLease = await ApiProcessGate.AcquireAsync();
        var process = StartApiProcess(workspaceRoot, port, storageRoot, publicBase);

        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
        var ready = await WaitForApiReadyAsync(process, http, timeoutMs: 30000);

        if (!ready)
        {
            var stderr = await process.StandardError.ReadToEndAsync();
            var stdout = await process.StandardOutput.ReadToEndAsync();
            process.Kill(entireProcessTree: true);
            await gateLease.DisposeAsync();

            throw new InvalidOperationException(
                $"API did not become ready in time. stdout: {stdout} stderr: {stderr}");
        }

        return new RunningApi(process, gateLease, storageRoot);
    }

    private static Process StartApiProcess(string workspaceRoot, int port, string storageRoot, string publicBase)
    {
        var apiProjectPath = Path.Combine(workspaceRoot, "csharp", "Octocon.Api", "Octocon.Api.csproj");

        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"run --no-build --project \"{apiProjectPath}\"",
            WorkingDirectory = workspaceRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        psi.Environment["ASPNETCORE_URLS"] = $"http://127.0.0.1:{port}";
        psi.Environment["OCTOCON_PERSISTENCE"] = "inmemory";
        psi.Environment["OCTOCON_DEV_ALLOW_HEADER_PRINCIPAL"] = "true";
        psi.Environment["OCTOCON_JWT_AUTHORITY"] = string.Empty;
        psi.Environment["OCTOCON_AVATAR_STORAGE_ROOT"] = storageRoot;
        psi.Environment["OCTOCON_AVATAR_PUBLIC_BASE"] = publicBase;

        var process = new Process { StartInfo = psi };
        process.Start();
        return process;
    }

    private static async Task<bool> WaitForApiReadyAsync(Process process, HttpClient client, int timeoutMs)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);

        while (DateTime.UtcNow < deadline)
        {
            if (process.HasExited)
                return false;

            try
            {
                var response = await client.GetAsync("/api/heartbeat");
                if (response.StatusCode == HttpStatusCode.OK)
                    return true;
            }
            catch
            {
                // Keep polling while Kestrel starts.
            }

            await Task.Delay(200);
        }

        return false;
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private sealed class RunningApi : IAsyncDisposable
    {
        private readonly Process _process;
        private readonly IAsyncDisposable _gateLease;
        private readonly string _storageRoot;

        public RunningApi(Process process, IAsyncDisposable gateLease, string storageRoot)
        {
            _process = process;
            _gateLease = gateLease;
            _storageRoot = storageRoot;
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (!_process.HasExited)
                    _process.Kill(entireProcessTree: true);
            }
            catch
            {
                // Best effort process cleanup.
            }

            _process.Dispose();

            try
            {
                if (Directory.Exists(_storageRoot))
                    Directory.Delete(_storageRoot, recursive: true);
            }
            catch
            {
                // Best effort storage cleanup.
            }

            await _gateLease.DisposeAsync();
        }
    }
}
