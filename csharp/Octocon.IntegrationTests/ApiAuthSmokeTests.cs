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

        var unauthorized = await client.PostAsJsonAsync("/api/systems/me/alters", new { name = "NoPrincipal" });
        Ensure(unauthorized.StatusCode == HttpStatusCode.Unauthorized,
            $"Expected unauthenticated alter-create to return 401, got {(int)unauthorized.StatusCode}. Body: {await unauthorized.Content.ReadAsStringAsync()}");

        var idempotencyKey = Guid.NewGuid().ToString("N");
        var first = await SendAlterCreateAsync(client, "sys-api-smoke", idempotencyKey, "IntegrationOne");
        Ensure(first.StatusCode == HttpStatusCode.OK,
            $"Expected first alter-create 200, got {(int)first.StatusCode}. Body: {first.Body}");
        Ensure(ReadReplay(first.Body) == false,
            $"Expected first alter-create replay=false. Body: {first.Body}");

        var second = await SendAlterCreateAsync(client, "sys-api-smoke", idempotencyKey, "IntegrationOne");
        Ensure(second.StatusCode == HttpStatusCode.OK,
            $"Expected replay alter-create 200, got {(int)second.StatusCode}. Body: {second.Body}");
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
        var process = StartApiProcess(workspaceRoot, port, devPrincipalAllowed, jwtAuthority);

        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
        var ready = await WaitForApiReadyAsync(process, http, timeoutMs: 30000);

        if (!ready)
        {
            var stderr = await process.StandardError.ReadToEndAsync();
            var stdout = await process.StandardOutput.ReadToEndAsync();
            process.Kill(entireProcessTree: true);

            throw new InvalidOperationException(
                $"API did not become ready in time. stdout: {stdout} stderr: {stderr}");
        }

        return new RunningApi(process);
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
            Arguments = $"run --project \"{apiProjectPath}\"",
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

        public RunningApi(Process process)
        {
            _process = process;
        }

        public ValueTask DisposeAsync()
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
            return ValueTask.CompletedTask;
        }
    }
}
