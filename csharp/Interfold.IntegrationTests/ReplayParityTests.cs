using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Interfold.IntegrationTests.Replay;
using TUnit.Core;

namespace Interfold.IntegrationTests;

/// <summary>
/// Deterministic replay parity tests (Phase N, Scope 1).
/// <para>
/// Each <c>*.trace.json</c> fixture under <c>Fixtures/</c> is executed against a live
/// in-memory API process.  Every step asserts HTTP status and (where declared) the
/// <c>replay</c> flag in the response body.
/// </para>
/// Gated on <c>OCTOCON_RUN_API_INTEGRATION=true</c>.
/// </summary>
public sealed class ReplayParityTests
{
    [Test]
    public async Task Replay_AlterLifecycle_PassesAllSteps()
    {
        if (!IntegrationTestEnvironment.ShouldRunApiIntegration) return;
        await RunTraceAsync("alter-lifecycle.trace.json");
    }

    [Test]
    public async Task Replay_TagLifecycle_PassesAllSteps()
    {
        if (!IntegrationTestEnvironment.ShouldRunApiIntegration) return;
        await RunTraceAsync("tag-lifecycle.trace.json");
    }

    [Test]
    public async Task Replay_FrontingLifecycle_PassesAllSteps()
    {
        if (!IntegrationTestEnvironment.ShouldRunApiIntegration) return;
        await RunTraceAsync("fronting-lifecycle.trace.json");
    }

    [Test]
    public async Task Replay_PollLifecycle_PassesAllSteps()
    {
        if (!IntegrationTestEnvironment.ShouldRunApiIntegration) return;
        await RunTraceAsync("poll-lifecycle.trace.json");
    }

    [Test]
    public async Task Replay_SettingsLifecycle_PassesAllSteps()
    {
        if (!IntegrationTestEnvironment.ShouldRunApiIntegration) return;
        await RunTraceAsync("settings-lifecycle.trace.json");
    }

    // -----------------------------------------------------------------------
        [Test]
        public async Task Replay_JournalLifecycle_PassesAllSteps()
        {
            if (!IntegrationTestEnvironment.ShouldRunApiIntegration) return;
            await RunTraceAsync("journal-lifecycle.trace.json");
        }

        [Test]
        public async Task Replay_FriendshipLifecycle_PassesAllSteps()
        {
            if (!IntegrationTestEnvironment.ShouldRunApiIntegration) return;
            await RunTraceAsync("friendship-lifecycle.trace.json");
        }

        // -----------------------------------------------------------------------
    // Core runner
    // -----------------------------------------------------------------------

    private static async Task RunTraceAsync(string fixtureFileName)
    {
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", fixtureFileName);
        Ensure(File.Exists(fixturePath), $"Fixture not found: {fixturePath}");

        var trace = ReplayTrace.Load(fixturePath);
        Ensure(trace.Steps.Count > 0, $"Trace '{fixtureFileName}' contains no steps.");

        var workspaceRoot = FindWorkspaceRoot();
        var port = GetFreePort();

        await using var api = await StartApiAsync(workspaceRoot, port);
        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };

        // Mutable variable store for {varName} substitutions across steps.
        var vars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var step in trace.Steps)
        {
            await ExecuteStepAsync(client, step, vars, fixtureFileName);
        }
    }

    private static async Task ExecuteStepAsync(
        HttpClient client,
        ReplayStep step,
        Dictionary<string, string> vars,
        string fixtureFileName)
    {
        var path = SubstituteVars(step.Path, vars);

        using var request = new HttpRequestMessage(
            new HttpMethod(step.Method.ToUpperInvariant()), path);


        if (!string.IsNullOrWhiteSpace(step.IdempotencyKey))
            request.Headers.Add("X-Interfold-Idempotency-Key", step.IdempotencyKey);

        if (step.Body.HasValue)
        {
            var bodyJson = SubstituteVars(step.Body.Value.GetRawText(), vars);
            request.Content = new StringContent(bodyJson, Encoding.UTF8, "application/json");
        }

        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        Ensure(
            (int)response.StatusCode == step.ExpectedStatus,
            $"[{fixtureFileName}] Step '{step.Name}': expected HTTP {step.ExpectedStatus}, " +
            $"got {(int)response.StatusCode}. Body: {body}");

        // Assert replay flag when declared (only when the response has a body).
        if (step.ExpectedReplay.HasValue && !string.IsNullOrEmpty(body))
        {
            var actualReplay = ReadBoolField(body, "replay");
            Ensure(
                actualReplay == step.ExpectedReplay.Value,
                $"[{fixtureFileName}] Step '{step.Name}': expected replay={step.ExpectedReplay.Value}, " +
                $"got replay={actualReplay}. Body: {body}");
        }

        // Capture fields for subsequent steps.
        if (step.CaptureAs is { Count: > 0 } captures && response.IsSuccessStatusCode)
        {
            using var doc = JsonDocument.Parse(body);
            foreach (var (varName, jsonPath) in captures)
            {
                if (TryReadStringField(doc.RootElement, jsonPath, out var value))
                {
                    vars[varName] = value;
                }
                else if (TryReadIntField(doc.RootElement, jsonPath, out var intVal))
                {
                    vars[varName] = intVal.ToString();
                }
                else
                {
                    Ensure(false,
                        $"[{fixtureFileName}] Step '{step.Name}': could not capture '{jsonPath}' from body: {body}");
                }
            }
        }
    }

    // -----------------------------------------------------------------------
    // JSON helpers
    // -----------------------------------------------------------------------

    private static string SubstituteVars(string template, Dictionary<string, string> vars)
    {
        foreach (var (key, value) in vars)
            template = template.Replace($"{{{key}}}", value, StringComparison.OrdinalIgnoreCase);

        return template;
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
                _ => throw new InvalidOperationException(
                    $"Expected boolean for '{fieldName}', got {prop.Value.ValueKind}. Body: {json}")
            };
        }

        throw new InvalidOperationException($"Field '{fieldName}' not found in: {json}");
    }

    private static bool TryReadStringField(JsonElement root, string name, out string value)
    {
        foreach (var prop in root.EnumerateObject())
        {
            if (prop.Name.Equals(name, StringComparison.OrdinalIgnoreCase)
                && prop.Value.ValueKind == JsonValueKind.String)
            {
                value = prop.Value.GetString()!;
                return true;
            }
        }

        // Also search inside a top-level "data" object (201 create responses use {data:{...}, replay:false}).
        foreach (var prop in root.EnumerateObject())
        {
            if (prop.Name.Equals("data", StringComparison.OrdinalIgnoreCase)
                && prop.Value.ValueKind == JsonValueKind.Object)
            {
                foreach (var child in prop.Value.EnumerateObject())
                {
                    if (child.Name.Equals(name, StringComparison.OrdinalIgnoreCase)
                        && child.Value.ValueKind == JsonValueKind.String)
                    {
                        value = child.Value.GetString()!;
                        return true;
                    }
                }
            }
        }

        value = string.Empty;
        return false;
    }

    private static bool TryReadIntField(JsonElement root, string name, out int value)
    {
        foreach (var prop in root.EnumerateObject())
        {
            if (prop.Name.Equals(name, StringComparison.OrdinalIgnoreCase)
                && prop.Value.ValueKind == JsonValueKind.Number
                && prop.Value.TryGetInt32(out value))
            {
                return true;
            }
        }

        // Also search inside a top-level "data" object.
        foreach (var prop in root.EnumerateObject())
        {
            if (prop.Name.Equals("data", StringComparison.OrdinalIgnoreCase)
                && prop.Value.ValueKind == JsonValueKind.Object)
            {
                foreach (var child in prop.Value.EnumerateObject())
                {
                    if (child.Name.Equals(name, StringComparison.OrdinalIgnoreCase)
                        && child.Value.ValueKind == JsonValueKind.Number
                        && child.Value.TryGetInt32(out value))
                    {
                        return true;
                    }
                }
            }
        }

        value = 0;
        return false;
    }

    // -----------------------------------------------------------------------
    // Process / infrastructure helpers (shared with other test suites)
    // -----------------------------------------------------------------------

    private static async Task<RunningApi> StartApiAsync(string workspaceRoot, int port)
    {
        var gateLease = await ApiProcessGate.AcquireAsync();
        var apiProjectPath = Path.Combine(workspaceRoot, "csharp", "Octocon.Api", "Octocon.Api.csproj");

        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"run --no-build --project \"{apiProjectPath}\"",
            WorkingDirectory = workspaceRoot,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false
        };

        psi.Environment["ASPNETCORE_URLS"]                 = $"http://127.0.0.1:{port}";
        psi.Environment["OCTOCON_PERSISTENCE"]             = "inmemory";

        var process = new Process { StartInfo = psi };
        process.Start();

        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
        var deadline = DateTime.UtcNow.AddMilliseconds(30_000);

        while (DateTime.UtcNow < deadline)
        {
            if (process.HasExited)
            {
                var stderr = await process.StandardError.ReadToEndAsync();
                var stdout = await process.StandardOutput.ReadToEndAsync();
                await gateLease.DisposeAsync();
                throw new InvalidOperationException(
                    $"API exited unexpectedly. stdout: {stdout} stderr: {stderr}");
            }

            try
            {
                var r = await http.GetAsync("/api/heartbeat");
                if (r.StatusCode == HttpStatusCode.OK) break;
            }
            catch { /* keep polling */ }

            await Task.Delay(200);
        }

        return new RunningApi(process, gateLease);
    }

    private static int GetFreePort()
    {
        var listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        try { return ((System.Net.IPEndPoint)listener.LocalEndpoint).Port; }
        finally { listener.Stop(); }
    }

    private static string FindWorkspaceRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (Directory.Exists(Path.Combine(current.FullName, "csharp")))
                return current.FullName;
            current = current.Parent;
        }
        throw new InvalidOperationException("Cannot find workspace root.");
    }



    private static void Ensure(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class RunningApi(Process process, IAsyncDisposable gateLease) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch { }
            process.Dispose();
            await gateLease.DisposeAsync();
        }
    }
}
