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
[ClassDataSource<InMemoryWebFactoryFixture>(Shared = SharedType.PerTestSession)]
[ClassDataSource<ScyllaWebFactoryFixture>(Shared = SharedType.PerTestSession)]
[ClassDataSource<CassandraWebFactoryFixture>(Shared = SharedType.PerTestSession)]
public sealed class ReplayParityTests(IWebFactoryFixture fixture) : BaseEndpointTest
{
    public static IEnumerable<string> GetReplayFiles()
    {
        yield return "alter-lifecycle.trace.json";
        yield return "tag-lifecycle.trace.json";
        yield return "fronting-lifecycle.trace.json";
        yield return "poll-lifecycle.trace.json";
        yield return "settings-lifecycle.trace.json";
        yield return "journal-lifecycle.trace.json";
        yield return "friendship-lifecycle.trace.json";
    }
    
    [Test]
    [CombinedDataSources]
    public async Task Replay_PassesAllSteps([MethodDataSource(typeof(ReplayParityTests), nameof(GetReplayFiles))] string fixtureFileName)
    {
        await RunTraceAsync(fixture.Factory, fixtureFileName);
    }

    // -----------------------------------------------------------------------
    // Core runner
    // -----------------------------------------------------------------------

    private static async Task RunTraceAsync(InterfoldWebApplicationFactory factory,  string fixtureFileName)
    {
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", fixtureFileName);

        var trace = ReplayTrace.Load(fixturePath);
        using (Assert.Multiple())
        {
            await Assert.That(File.Exists(fixturePath)).IsTrue();
            await Assert.That(trace.Steps.Count > 0).IsTrue();
        }

        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        // Seed all principals referenced in the trace so Scylla/Cassandra
        // backends have the user rows before operations execute.
        var principals = trace.Steps
            .Select(s => s.PrincipalId)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        foreach (var principal in principals)
        {
            await EnsureUserExistsAsync(client, principal!);
        }

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
            var substitutedBody = SubstituteBodyValue(step.Body.Value, vars);
            var bodyJson = JsonSerializer.Serialize(substitutedBody);
            request.Content = new StringContent(bodyJson, Encoding.UTF8, "application/json");
        }

        if (!string.IsNullOrWhiteSpace(step.PrincipalId))
        {
            AttachPrincipalAuth(request, client, step.PrincipalId);
        }

        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        var requestBody = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync();
        await Assert.That((int)response.StatusCode).IsEqualTo(step.ExpectedStatus)
            .Because($"[{fixtureFileName}] Step '{step.Name}' ({step.Method.ToUpperInvariant()} {path}) expected {step.ExpectedStatus}, got {(int)response.StatusCode}. Request body: {requestBody}. Response body: {body}");

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

    private static object? SubstituteBodyValue(JsonElement element, Dictionary<string, string> vars)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
            {
                var dict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                foreach (var property in element.EnumerateObject())
                {
                    dict[property.Name] = SubstituteBodyValue(property.Value, vars);
                }

                return dict;
            }

            case JsonValueKind.Array:
            {
                var list = new List<object?>();
                foreach (var item in element.EnumerateArray())
                {
                    list.Add(SubstituteBodyValue(item, vars));
                }

                return list;
            }

            case JsonValueKind.String:
            {
                var value = element.GetString() ?? string.Empty;
                if (TryResolvePlaceholderValue(value, vars, out var resolved))
                    return resolved;

                return SubstituteVars(value, vars);
            }

            case JsonValueKind.Number:
                if (element.TryGetInt32(out var intValue)) return intValue;
                if (element.TryGetInt64(out var longValue)) return longValue;
                if (element.TryGetDecimal(out var decimalValue)) return decimalValue;
                return element.GetDouble();

            case JsonValueKind.True:
                return true;

            case JsonValueKind.False:
                return false;

            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                return null;

            default:
                return element.GetRawText();
        }
    }

    private static bool TryResolvePlaceholderValue(
        string value,
        Dictionary<string, string> vars,
        out object? resolved)
    {
        resolved = null;

        if (value.Length < 3 || value[0] != '{' || value[^1] != '}')
            return false;

        var variableName = value[1..^1];
        if (!vars.TryGetValue(variableName, out var variableValue))
            return false;

        if (int.TryParse(variableValue, out var intValue))
        {
            resolved = intValue;
            return true;
        }

        if (long.TryParse(variableValue, out var longValue))
        {
            resolved = longValue;
            return true;
        }

        if (decimal.TryParse(variableValue, out var decimalValue))
        {
            resolved = decimalValue;
            return true;
        }

        if (bool.TryParse(variableValue, out var boolValue))
        {
            resolved = boolValue;
            return true;
        }

        resolved = variableValue;
        return true;
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

        if (jsonPath.Equals("frontId", StringComparison.OrdinalIgnoreCase))
        {
            yield return "front_id";
        }

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
