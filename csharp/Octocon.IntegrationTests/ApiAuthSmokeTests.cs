using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using TUnit.Core;

namespace Octocon.IntegrationTests;

public sealed class ApiAuthSmokeTests
{
    [Test]
    public async Task Api_AuthAndIdempotencyFlow_WorksInDevHeaderMode()
    {
        if (!ShouldRunApiIntegration())
            return;

        var workspaceRoot = FindWorkspaceRoot();
        var port = GetFreePort();

        await using var api = await StartApiAsync(
            workspaceRoot,
            port,
            devPrincipalAllowed: true,
            jwtAuthority: null);

        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };

        var heartbeat = await client.GetAsync("/api/heartbeat");
        Ensure(heartbeat.StatusCode == HttpStatusCode.OK,
            $"Expected heartbeat 200, got {(int)heartbeat.StatusCode}. Body: {await heartbeat.Content.ReadAsStringAsync()}");

        Ensure(
            heartbeat.Headers.TryGetValues("X-Octocon-Contract", out var contractValues) &&
            contractValues.Contains("2026-03-v1", StringComparer.Ordinal),
            "Expected X-Octocon-Contract response header on heartbeat response.");

        var nodeRole = await client.GetAsync("/health/node-role");
        var nodeRoleBody = await nodeRole.Content.ReadAsStringAsync();
        Ensure(nodeRole.StatusCode == HttpStatusCode.OK,
            $"Expected node-role health endpoint 200, got {(int)nodeRole.StatusCode}. Body: {nodeRoleBody}");
        Ensure(!string.IsNullOrWhiteSpace(ReadStringField(nodeRoleBody, "role")),
            $"Expected node-role response to include role. Body: {nodeRoleBody}");

        var unauthorized = await client.PostAsJsonAsync("/api/systems/me/alters", new { name = "NoPrincipal" });
        Ensure(unauthorized.StatusCode == HttpStatusCode.Unauthorized,
            $"Expected unauthenticated alter-create to return 401, got {(int)unauthorized.StatusCode}. Body: {await unauthorized.Content.ReadAsStringAsync()}");

        var idempotencyKey = Guid.NewGuid().ToString("N");
        var first = await SendAlterCreateAsync(client, "sys-api-smoke", idempotencyKey, "IntegrationOne");
        Ensure(first.StatusCode == HttpStatusCode.Created,
            $"Expected first alter-create 201, got {(int)first.StatusCode}. Body: {first.Body}");
        Ensure(ReadReplay(first.Body) == false,
            $"Expected first alter-create replay=false. Body: {first.Body}");

        var second = await SendAlterCreateAsync(client, "sys-api-smoke", idempotencyKey, "IntegrationOne");
        Ensure(second.StatusCode == HttpStatusCode.Created,
            $"Expected replay alter-create 201, got {(int)second.StatusCode}. Body: {second.Body}");
        Ensure(ReadReplay(second.Body) == true,
            $"Expected replay alter-create replay=true. Body: {second.Body}");
    }

    [Test]
    public async Task Api_FailsFast_WithoutJwtAuthority_WhenDevHeaderBypassOff()
    {
        if (!ShouldRunApiIntegration())
            return;

        var workspaceRoot = FindWorkspaceRoot();
        var port = GetFreePort();

        using var process = StartApiProcess(
            workspaceRoot,
            port,
            devPrincipalAllowed: false,
            jwtAuthority: null);

        var exited = await WaitForExitAsync(process, timeoutMs: 12000);
        Ensure(exited, "Expected API process to fail fast, but it did not exit in time.");
        Ensure(process.ExitCode != 0, "Expected non-zero exit code when JWT authority is missing with dev bypass off.");

        var stderr = await process.StandardError.ReadToEndAsync();
        var stdout = await process.StandardOutput.ReadToEndAsync();
        var combined = string.Concat(stdout, "\n", stderr);

        Ensure(combined.Contains("OCTOCON_JWT_AUTHORITY", StringComparison.Ordinal),
            $"Expected startup guardrail message mentioning OCTOCON_JWT_AUTHORITY. Output: {combined}");
    }

    [Test]
    public async Task Api_FrontHistoryBetween_IncludesEndedFronts()
    {
        if (!ShouldRunApiIntegration())
            return;

        var workspaceRoot = FindWorkspaceRoot();
        var port = GetFreePort();

        await using var api = await StartApiAsync(
            workspaceRoot,
            port,
            devPrincipalAllowed: true,
            jwtAuthority: null);

        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };

        var principalId = "sys-front-history";
        var startAnchor = DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeSeconds();

        var started = await SendFrontStartAsync(client, principalId, alterId: 101, comment: "phase3-history");
        Ensure(started.StatusCode == HttpStatusCode.Created,
            $"Expected front start 201, got {(int)started.StatusCode}. Body: {started.Body}");

        var startedFrontId = ReadStringField(started.Body, "frontId");
        Ensure(!string.IsNullOrWhiteSpace(startedFrontId),
            $"Expected start response to include frontId. Body: {started.Body}");

        var ended = await SendFrontEndAsync(client, principalId, alterId: 101);
        Ensure(ended.StatusCode == HttpStatusCode.NoContent,
            $"Expected front end 204, got {(int)ended.StatusCode}. Body: {ended.Body}");

        var endAnchor = DateTimeOffset.UtcNow.AddMinutes(1).ToUnixTimeSeconds();
        using var betweenRequest = new HttpRequestMessage(HttpMethod.Get,
            $"/api/systems/me/front/between?start={startAnchor}&end={endAnchor}");
        betweenRequest.Headers.Add("X-Octocon-Dev-Principal", principalId);
        var betweenResponse = await client.SendAsync(betweenRequest);
        var betweenBody = await betweenResponse.Content.ReadAsStringAsync();

        Ensure(betweenResponse.StatusCode == HttpStatusCode.OK,
            $"Expected front between 200, got {(int)betweenResponse.StatusCode}. Body: {betweenBody}");

        using var doc = JsonDocument.Parse(betweenBody);
        Ensure(doc.RootElement.ValueKind == JsonValueKind.Array,
            $"Expected front between payload to be an array. Body: {betweenBody}");
        Ensure(doc.RootElement.GetArrayLength() > 0,
            $"Expected at least one historical front entry. Body: {betweenBody}");

        var row = doc.RootElement.EnumerateArray().First();
        var responseFrontId = ReadStringField(row, "frontId");
        Ensure(string.Equals(responseFrontId, startedFrontId, StringComparison.OrdinalIgnoreCase),
            $"Expected matching frontId from history response. Body: {betweenBody}");

        var responseComment = ReadStringField(row, "comment");
        Ensure(string.Equals(responseComment, "phase3-history", StringComparison.Ordinal),
            $"Expected matching front comment in history response. Body: {betweenBody}");

        var endedAt = ReadNullableStringField(row, "endedAt");
        Ensure(!string.IsNullOrWhiteSpace(endedAt),
            $"Expected ended historical front to include endedAt. Body: {betweenBody}");
    }

    private static async Task<(HttpStatusCode StatusCode, string Body)> SendAlterCreateAsync(
        HttpClient client,
        string principalId,
        string idempotencyKey,
        string alterName)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/systems/me/alters")
        {
            Content = JsonContent.Create(new { name = alterName })
        };

        request.Headers.Add("X-Octocon-Dev-Principal", principalId);
        request.Headers.Add("X-Octocon-Idempotency-Key", idempotencyKey);

        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        return (response.StatusCode, body);
    }

    private static async Task<(HttpStatusCode StatusCode, string Body)> SendFrontStartAsync(
        HttpClient client,
        string principalId,
        int alterId,
        string? comment)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/systems/me/front/start")
        {
            Content = JsonContent.Create(new
            {
                alterId,
                comment,
                idempotencyKey = Guid.NewGuid().ToString("N")
            })
        };

        request.Headers.Add("X-Octocon-Dev-Principal", principalId);

        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        return (response.StatusCode, body);
    }

    private static async Task<(HttpStatusCode StatusCode, string Body)> SendFrontEndAsync(
        HttpClient client,
        string principalId,
        int alterId)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/systems/me/front/end")
        {
            Content = JsonContent.Create(new
            {
                alterId,
                idempotencyKey = Guid.NewGuid().ToString("N")
            })
        };

        request.Headers.Add("X-Octocon-Dev-Principal", principalId);

        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        return (response.StatusCode, body);
    }

    private static bool ReadReplay(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        foreach (var prop in root.EnumerateObject())
        {
            if (!prop.Name.Equals("Replay", StringComparison.OrdinalIgnoreCase))
                continue;

            if (prop.Value.ValueKind == JsonValueKind.True)
                return true;

            if (prop.Value.ValueKind == JsonValueKind.False)
                return false;

            break;
        }

        throw new InvalidOperationException($"Could not find boolean replay flag in response: {json}");
    }

    private static string ReadStringField(string json, string fieldName)
    {
        using var doc = JsonDocument.Parse(json);
        return ReadStringField(doc.RootElement, fieldName);
    }

    private static string ReadStringField(JsonElement root, string fieldName)
    {
        foreach (var prop in root.EnumerateObject())
        {
            if (!prop.Name.Equals(fieldName, StringComparison.OrdinalIgnoreCase))
                continue;

            if (prop.Value.ValueKind == JsonValueKind.String)
                return prop.Value.GetString() ?? string.Empty;

            throw new InvalidOperationException(
                $"Expected string field '{fieldName}', got {prop.Value.ValueKind}.");
        }

        throw new InvalidOperationException($"Field '{fieldName}' not found.");
    }

    private static string? ReadNullableStringField(JsonElement root, string fieldName)
    {
        foreach (var prop in root.EnumerateObject())
        {
            if (!prop.Name.Equals(fieldName, StringComparison.OrdinalIgnoreCase))
                continue;

            return prop.Value.ValueKind switch
            {
                JsonValueKind.Null => null,
                JsonValueKind.String => prop.Value.GetString(),
                _ => throw new InvalidOperationException(
                    $"Expected nullable string field '{fieldName}', got {prop.Value.ValueKind}.")
            };
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

    private static async Task<RunningApi> StartApiAsync(
        string workspaceRoot,
        int port,
        bool devPrincipalAllowed,
        string? jwtAuthority)
    {
        var gateLease = await ApiProcessGate.AcquireAsync();
        var process = StartApiProcess(workspaceRoot, port, devPrincipalAllowed, jwtAuthority);

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

        return new RunningApi(process, gateLease);
    }

    private static Process StartApiProcess(
        string workspaceRoot,
        int port,
        bool devPrincipalAllowed,
        string? jwtAuthority)
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
        psi.Environment["OCTOCON_DEV_ALLOW_HEADER_PRINCIPAL"] = devPrincipalAllowed ? "true" : "false";

        if (string.IsNullOrWhiteSpace(jwtAuthority))
        {
            psi.Environment["OCTOCON_JWT_AUTHORITY"] = string.Empty;
        }
        else
        {
            psi.Environment["OCTOCON_JWT_AUTHORITY"] = jwtAuthority;
        }

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

    private static async Task<bool> WaitForExitAsync(Process process, int timeoutMs)
    {
        var completed = await Task.WhenAny(process.WaitForExitAsync(), Task.Delay(timeoutMs));
        return completed.IsCompletedSuccessfully && process.HasExited;
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

        public RunningApi(Process process, IAsyncDisposable gateLease)
        {
            _process = process;
            _gateLease = gateLease;
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
            await _gateLease.DisposeAsync();
        }
    }
}
