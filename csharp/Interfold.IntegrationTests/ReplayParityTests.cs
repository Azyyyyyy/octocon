using Interfold.IntegrationTests.Attributes;
using Microsoft.AspNetCore.Mvc.Testing;
using System.Text;
using System.Text.Json;
using Interfold.IntegrationTests.Models;
using Interfold.IntegrationTests.TestServices;

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
public sealed class ReplayParityTests : BaseEndpointTest
{
    public static IEnumerable<TestDataRow<string>> GetReplayFiles()
    {
        yield return new("alter-lifecycle.trace.json", DisplayName: "Alter Lifecycle");
        yield return new("tag-lifecycle.trace.json", DisplayName: "Tag Lifecycle");
        yield return new("fronting-lifecycle.trace.json", DisplayName: "fronting Lifecycle");
        yield return new("poll-lifecycle.trace.json", DisplayName: "poll Lifecycle");
        yield return new("settings-lifecycle.trace.json", DisplayName: "settings Lifecycle");
        yield return new("journal-lifecycle.trace.json", DisplayName: "journal Lifecycle");
        yield return new("friendship-lifecycle.trace.json", DisplayName: "friendship Lifecycle");
    }
    
    [Test, ApiIntegration]
    [MethodDataSource(typeof(ReplayParityTests), nameof(GetReplayFiles))]
    public async Task Replay_PassesAllSteps(string fixtureFileName)
    {
        await RunTraceAsync(fixtureFileName);
    }

    // -----------------------------------------------------------------------
    // Core runner
    // -----------------------------------------------------------------------

    private static async Task RunTraceAsync(string fixtureFileName)
    {
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", fixtureFileName);

        var trace = ReplayTrace.Load(fixturePath);
        using (Assert.Multiple())
        {
            await Assert.That(File.Exists(fixturePath)).IsTrue();
            await Assert.That(trace.Steps.Count > 0).IsTrue();
        }

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

        if (!string.IsNullOrWhiteSpace(step.PrincipalId))
        {
            AttachPrincipalAuth(request, client, step.PrincipalId);
        }

        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        await Assert.That((int)response.StatusCode).IsEqualTo(step.ExpectedStatus);

        // Assert replay flag when declared (only when the response has a body).
        if (step.ExpectedReplay.HasValue && !string.IsNullOrEmpty(body))
        {
            var actualReplay = ReadBoolField(body, "replay");
            await Assert.That(actualReplay == step.ExpectedReplay.Value).IsTrue();
        }

        // Capture fields for subsequent steps.
        if (step.CaptureAs is { Count: > 0 } captures && response.IsSuccessStatusCode)
        {
            using var doc = JsonDocument.Parse(body);
            foreach (var (varName, jsonPath) in captures)
            {
                if (TryCaptureValue(doc.RootElement, jsonPath, out var capturedValue))
                {
                    vars[varName] = capturedValue;
                }
                else
                {
                    Assert.Fail($"[{fixtureFileName}] Step '{step.Name}': could not capture '{jsonPath}' from body: {body}");
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

    private static bool TryCaptureValue(JsonElement root, string jsonPath, out string value)
    {
        foreach (var candidate in GetCaptureCandidates(jsonPath))
        {
            if (TryReadStringField(root, candidate, out value))
                return true;

            if (TryReadIntField(root, candidate, out var intValue))
            {
                value = intValue.ToString();
                return true;
            }
        }

        value = string.Empty;
        return false;
    }

    private static IEnumerable<string> GetCaptureCandidates(string jsonPath)
    {
        yield return jsonPath;

        if (jsonPath.Equals("alterId", StringComparison.OrdinalIgnoreCase)
            || jsonPath.Equals("entryId", StringComparison.OrdinalIgnoreCase)
            || jsonPath.Equals("pollId", StringComparison.OrdinalIgnoreCase)
            || jsonPath.Equals("tagId", StringComparison.OrdinalIgnoreCase)
            || jsonPath.Equals("frontId", StringComparison.OrdinalIgnoreCase))
        {
            // Backward compatibility for fixtures that still request legacy create keys.
            yield return "id";
        }
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
}
