using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace Interfold.IntegrationTests;

/// <summary>
/// Integration tests for node-role detection and the <c>GET /health/node-role</c> endpoint.
/// Gated on <c>OCTOCON_RUN_API_INTEGRATION=true</c>.
/// </summary>
public sealed class NodeRoleIntegrationTests
{
    [Test]
    public async Task NodeRole_DefaultsToAuxiliary_WhenNoEnvVarSet()
    {
        if (!IntegrationTestEnvironment.ShouldRunApiIntegration)
            return;

        var workspaceRoot = FindWorkspaceRoot();
        var port = GetFreePort();

        await using var api = await StartApiAsync(workspaceRoot, port, nodeGroup: null, flyProcessGroup: null);

        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
        var response = await client.GetAsync("/health/node-role");

        Ensure(response.StatusCode == HttpStatusCode.OK,
            $"Expected 200 from /health/node-role, got {(int)response.StatusCode}.");

        var body = await response.Content.ReadAsStringAsync();
        var role = ReadStringField(body, "role");
        var ownsSingletons = ReadBoolField(body, "owns_singletons");

        Ensure(role == "auxiliary",
            $"Expected role=auxiliary when no env var set. Got: {body}");
        Ensure(ownsSingletons == false,
            $"Expected owns_singletons=false for auxiliary. Got: {body}");
    }

    [Test]
    public async Task NodeRole_ReturnsPrimary_WhenOctoconNodeGroupIsPrimary()
    {
        if (!IntegrationTestEnvironment.ShouldRunApiIntegration)
            return;

        var workspaceRoot = FindWorkspaceRoot();
        var port = GetFreePort();

        await using var api = await StartApiAsync(workspaceRoot, port, nodeGroup: "primary", flyProcessGroup: null);

        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
        var response = await client.GetAsync("/health/node-role");

        Ensure(response.StatusCode == HttpStatusCode.OK,
            $"Expected 200 from /health/node-role, got {(int)response.StatusCode}.");

        var body = await response.Content.ReadAsStringAsync();
        var role = ReadStringField(body, "role");
        var ownsSingletons = ReadBoolField(body, "owns_singletons");

        Ensure(role == "primary",
            $"Expected role=primary when OCTOCON_NODE_GROUP=primary. Got: {body}");
        Ensure(ownsSingletons == true,
            $"Expected owns_singletons=true for primary. Got: {body}");
    }

    [Test]
    public async Task NodeRole_ReturnsPrimary_WhenFlyProcessGroupIsPrimary()
    {
        if (!IntegrationTestEnvironment.ShouldRunApiIntegration)
            return;

        var workspaceRoot = FindWorkspaceRoot();
        var port = GetFreePort();

        // FLY_PROCESS_GROUP takes precedence; OCTOCON_NODE_GROUP is not set.
        await using var api = await StartApiAsync(workspaceRoot, port, nodeGroup: null, flyProcessGroup: "primary");

        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
        var response = await client.GetAsync("/health/node-role");

        Ensure(response.StatusCode == HttpStatusCode.OK,
            $"Expected 200 from /health/node-role, got {(int)response.StatusCode}.");

        var body = await response.Content.ReadAsStringAsync();
        var role = ReadStringField(body, "role");

        Ensure(role == "primary",
            $"Expected role=primary when FLY_PROCESS_GROUP=primary. Got: {body}");
    }

    [Test]
    public async Task NodeRole_FlyProcessGroup_TakesPrecedenceOver_OctoconNodeGroup()
    {
        if (!IntegrationTestEnvironment.ShouldRunApiIntegration)
            return;

        var workspaceRoot = FindWorkspaceRoot();
        var port = GetFreePort();

        // FLY_PROCESS_GROUP=sidecar should win over OCTOCON_NODE_GROUP=primary.
        await using var api = await StartApiAsync(workspaceRoot, port, nodeGroup: "primary", flyProcessGroup: "sidecar");

        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
        var response = await client.GetAsync("/health/node-role");

        Ensure(response.StatusCode == HttpStatusCode.OK,
            $"Expected 200 from /health/node-role, got {(int)response.StatusCode}.");

        var body = await response.Content.ReadAsStringAsync();
        var role = ReadStringField(body, "role");
        var ownsSingletons = ReadBoolField(body, "owns_singletons");

        Ensure(role == "sidecar",
            $"Expected role=sidecar (FLY_PROCESS_GROUP wins). Got: {body}");
        Ensure(ownsSingletons == false,
            $"Expected owns_singletons=false for sidecar. Got: {body}");
    }

    [Test]
    public async Task NodeRole_Endpoint_IsAnonymous()
    {
        if (!IntegrationTestEnvironment.ShouldRunApiIntegration)
            return;

        var workspaceRoot = FindWorkspaceRoot();
        var port = GetFreePort();

        await using var api = await StartApiAsync(workspaceRoot, port, nodeGroup: null, flyProcessGroup: null);

        // Un-authenticated request — must not return 401.
        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
        var response = await client.GetAsync("/health/node-role");

        Ensure(response.StatusCode == HttpStatusCode.OK,
            $"Expected /health/node-role to be anonymous (200), got {(int)response.StatusCode}.");
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static async Task<RunningApi> StartApiAsync(
        string workspaceRoot,
        int port,
        string? nodeGroup,
        string? flyProcessGroup)
    {
        var gateLease = await ApiProcessGate.AcquireAsync();
        var process = StartApiProcess(workspaceRoot, port, nodeGroup, flyProcessGroup);

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
        string? nodeGroup,
        string? flyProcessGroup)
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

        // Leave FLY_PROCESS_GROUP and OCTOCON_NODE_GROUP unset unless specified.
        if (!string.IsNullOrWhiteSpace(flyProcessGroup))
            psi.Environment["FLY_PROCESS_GROUP"] = flyProcessGroup;

        if (!string.IsNullOrWhiteSpace(nodeGroup))
            psi.Environment["OCTOCON_NODE_GROUP"] = nodeGroup;

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

    private static string ReadStringField(string json, string fieldName)
    {
        using var doc = JsonDocument.Parse(json);

        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            if (prop.Name.Equals(fieldName, StringComparison.OrdinalIgnoreCase))
                return prop.Value.GetString() ?? string.Empty;
        }

        throw new InvalidOperationException($"Field '{fieldName}' not found in: {json}");
    }

    private static bool ReadBoolField(string json, string fieldName)
    {
        using var doc = JsonDocument.Parse(json);

        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            if (!prop.Name.Equals(fieldName, StringComparison.OrdinalIgnoreCase))
                continue;

            return prop.Value.ValueKind switch
            {
                JsonValueKind.True  => true,
                JsonValueKind.False => false,
                _ => throw new InvalidOperationException($"Expected boolean for '{fieldName}'. Got: {json}")
            };
        }

        throw new InvalidOperationException($"Field '{fieldName}' not found in: {json}");
    }

    private static string FindWorkspaceRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);

        while (current is not null)
        {
            // The workspace root contains the csharp/ directory.
            if (Directory.Exists(Path.Combine(current.FullName, "csharp")))
                return current.FullName;

            current = current.Parent;
        }

        throw new InvalidOperationException("Could not find workspace root containing csharp/ directory.");
    }

    private static int GetFreePort()
    {
        var listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();

        try
        {
            return ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private sealed class RunningApi(Process process, IAsyncDisposable gateLease) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch
            {
                // Best-effort cleanup.
            }

            process.Dispose();
            await gateLease.DisposeAsync();
        }
    }
}
