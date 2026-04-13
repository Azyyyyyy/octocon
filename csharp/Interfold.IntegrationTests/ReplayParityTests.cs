using Interfold.IntegrationTests.Attributes;
using Microsoft.AspNetCore.Mvc.Testing;
using System.Text;
using System.Text.Json;

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
    [Test, ApiIntegration]
    public async Task Replay_AlterLifecycle_PassesAllSteps()
    {
        await RunTraceAsync("alter-lifecycle.trace.json");
    }

    [Test, ApiIntegration]
    public async Task Replay_TagLifecycle_PassesAllSteps()
    {
        await RunTraceAsync("tag-lifecycle.trace.json");
    }

    [Test, ApiIntegration]
    public async Task Replay_FrontingLifecycle_PassesAllSteps()
    {
        await RunTraceAsync("fronting-lifecycle.trace.json");
    }

    [Test, ApiIntegration]
    public async Task Replay_PollLifecycle_PassesAllSteps()
    {
        await RunTraceAsync("poll-lifecycle.trace.json");
    }

    [Test, ApiIntegration]
    public async Task Replay_SettingsLifecycle_PassesAllSteps()
    {
        await RunTraceAsync("settings-lifecycle.trace.json");
    }

        [Test, ApiIntegration]
        public async Task Replay_JournalLifecycle_PassesAllSteps()
        {
            await RunTraceAsync("journal-lifecycle.trace.json");
        }

        [Test, ApiIntegration]
        public async Task Replay_FriendshipLifecycle_PassesAllSteps()
        {
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

        await using var factory = new InterfoldWebApplicationFactory()
            .WithConfiguration("OCTOCON_PERSISTENCE", "inmemory")
            .WithConfiguration("OCTOCON_AUTH_CHALLENGE_ENABLED", "false");
        
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

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

    private static void Ensure(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
