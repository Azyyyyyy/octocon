using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using TUnit.Core;

namespace Octocon.IntegrationTests;

/// <summary>
/// Phase 4 parity regression tests.
/// <para>
/// Covers three remaining gaps identified in the endpoint diff:
/// <list type="bullet">
///   <item>Alter-journal nested and flat path form parity.</item>
///   <item>Settings field invalid-type fallback to "text" through the HTTP layer.</item>
///   <item>Legacy route regression — removed paths must 404, not accidentally serve.</item>
/// </list>
/// </para>
/// Gated on <c>OCTOCON_RUN_API_INTEGRATION=true</c>.
/// </summary>
public sealed class ParityRegressionTests
{
    // -----------------------------------------------------------------------
    // 1. Alter journal nested-route parity
    // -----------------------------------------------------------------------

    [Test]
    public async Task AlterJournal_NestedCreate_Returns201WithDataAndReplay()
    {
        if (!ShouldRun()) return;

        var workspaceRoot = FindWorkspaceRoot();
        var port = GetFreePort();
        await using var api = await StartApiAsync(workspaceRoot, port);
        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };

        var principal = "parity-alter-journal";
        var alterId = await CreateAlterAsync(client, principal, "JournalHolder");

        // POST /api/systems/me/alters/:id/journals  →  201 + {data, replay}
        using var createReq = new HttpRequestMessage(HttpMethod.Post, $"/api/systems/me/alters/{alterId}/journals")
        {
            Content = JsonContent.Create(new { title = "NestedParityJournal" })
        };
        createReq.Headers.Add("X-Octocon-Dev-Principal", principal);
        var createRes = await client.SendAsync(createReq);
        var createBody = await createRes.Content.ReadAsStringAsync();

        Ensure(createRes.StatusCode == HttpStatusCode.Created,
            $"Expected alter-journal create 201, got {(int)createRes.StatusCode}. Body: {createBody}");

        var entryId = ReadNestedString(createBody, "data", "entryId");
        Ensure(!string.IsNullOrWhiteSpace(entryId),
            $"Expected entryId in create response body. Body: {createBody}");

        var replay = ReadBool(createBody, "replay");
        Ensure(!replay,
            $"Expected replay=false on first create. Body: {createBody}");

        // GET /api/systems/me/alters/:id/journals  →  200 + {data:[...]}
        using var listReq = new HttpRequestMessage(HttpMethod.Get, $"/api/systems/me/alters/{alterId}/journals");
        listReq.Headers.Add("X-Octocon-Dev-Principal", principal);
        var listRes = await client.SendAsync(listReq);
        var listBody = await listRes.Content.ReadAsStringAsync();

        Ensure(listRes.StatusCode == HttpStatusCode.OK,
            $"Expected alter-journal list 200, got {(int)listRes.StatusCode}. Body: {listBody}");
        Ensure(listBody.Contains("data", StringComparison.OrdinalIgnoreCase),
            $"Expected data envelope in list response. Body: {listBody}");

        // GET /api/systems/me/alters/journals/:journalId  →  200 + {data:{...}}
        using var showReq = new HttpRequestMessage(HttpMethod.Get, $"/api/systems/me/alters/journals/{entryId}");
        showReq.Headers.Add("X-Octocon-Dev-Principal", principal);
        var showRes = await client.SendAsync(showReq);
        var showBody = await showRes.Content.ReadAsStringAsync();

        Ensure(showRes.StatusCode == HttpStatusCode.OK,
            $"Expected alter-journal show 200, got {(int)showRes.StatusCode}. Body: {showBody}");

        var shownId = ReadNestedString(showBody, "data", "entryId");
        Ensure(string.Equals(shownId, entryId, StringComparison.OrdinalIgnoreCase),
            $"Expected shown entryId to match created. Body: {showBody}");

        // PATCH /api/systems/me/alters/journals/:journalId  →  204
        using var patchReq = new HttpRequestMessage(HttpMethod.Patch, $"/api/systems/me/alters/journals/{entryId}")
        {
            Content = JsonContent.Create(new { title = "UpdatedParityJournal" })
        };
        patchReq.Headers.Add("X-Octocon-Dev-Principal", principal);
        var patchRes = await client.SendAsync(patchReq);

        Ensure(patchRes.StatusCode == HttpStatusCode.NoContent,
            $"Expected alter-journal update 204, got {(int)patchRes.StatusCode}. Body: {await patchRes.Content.ReadAsStringAsync()}");

        // DELETE /api/systems/me/alters/journals/:journalId  →  204
        using var deleteReq = new HttpRequestMessage(HttpMethod.Delete, $"/api/systems/me/alters/journals/{entryId}");
        deleteReq.Headers.Add("X-Octocon-Dev-Principal", principal);
        var deleteRes = await client.SendAsync(deleteReq);

        Ensure(deleteRes.StatusCode == HttpStatusCode.NoContent,
            $"Expected alter-journal delete 204, got {(int)deleteRes.StatusCode}. Body: {await deleteRes.Content.ReadAsStringAsync()}");
    }

    [Test]
    public async Task AlterJournal_ShowAfterDelete_Returns404()
    {
        if (!ShouldRun()) return;

        var workspaceRoot = FindWorkspaceRoot();
        var port = GetFreePort();
        await using var api = await StartApiAsync(workspaceRoot, port);
        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };

        var principal = "parity-alter-journal-404";
        var alterId = await CreateAlterAsync(client, principal, "JournalHolder404");

        using var createReq = new HttpRequestMessage(HttpMethod.Post, $"/api/systems/me/alters/{alterId}/journals")
        {
            Content = JsonContent.Create(new { title = "ToDelete" })
        };
        createReq.Headers.Add("X-Octocon-Dev-Principal", principal);
        var createRes = await client.SendAsync(createReq);
        var entryId = ReadNestedString(await createRes.Content.ReadAsStringAsync(), "data", "entryId");

        using var deleteReq = new HttpRequestMessage(HttpMethod.Delete, $"/api/systems/me/alters/journals/{entryId}");
        deleteReq.Headers.Add("X-Octocon-Dev-Principal", principal);
        await client.SendAsync(deleteReq);

        using var showReq = new HttpRequestMessage(HttpMethod.Get, $"/api/systems/me/alters/journals/{entryId}");
        showReq.Headers.Add("X-Octocon-Dev-Principal", principal);
        var showRes = await client.SendAsync(showReq);

        Ensure(showRes.StatusCode == HttpStatusCode.NotFound,
            $"Expected 404 after delete, got {(int)showRes.StatusCode}.");
    }

    // -----------------------------------------------------------------------
    // 2. Settings field type-fallback parity
    // -----------------------------------------------------------------------

    [Test]
    public async Task SettingsField_InvalidType_FallsBackToText_Returns204()
    {
        if (!ShouldRun()) return;

        var workspaceRoot = FindWorkspaceRoot();
        var port = GetFreePort();
        await using var api = await StartApiAsync(workspaceRoot, port);
        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };

        var principal = "parity-field-fallback";

        // type = "garbage" — Elixir falls back to "text"; C# must do the same
        using var req1 = new HttpRequestMessage(HttpMethod.Post, "/api/settings/fields")
        {
            Content = JsonContent.Create(new { name = "FallbackField", type = "garbage" })
        };
        req1.Headers.Add("X-Octocon-Dev-Principal", principal);
        var res1 = await client.SendAsync(req1);

        Ensure(res1.StatusCode == HttpStatusCode.NoContent,
            $"Expected settings field create with invalid type to return 204 (fallback to text), " +
            $"got {(int)res1.StatusCode}. Body: {await res1.Content.ReadAsStringAsync()}");
    }

    [Test]
    public async Task SettingsField_MissingType_FallsBackToText_Returns204()
    {
        if (!ShouldRun()) return;

        var workspaceRoot = FindWorkspaceRoot();
        var port = GetFreePort();
        await using var api = await StartApiAsync(workspaceRoot, port);
        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };

        var principal = "parity-field-missing-type";

        // type absent entirely
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/settings/fields")
        {
            Content = JsonContent.Create(new { name = "NoTypeField" })
        };
        req.Headers.Add("X-Octocon-Dev-Principal", principal);
        var res = await client.SendAsync(req);

        Ensure(res.StatusCode == HttpStatusCode.NoContent,
            $"Expected settings field create with missing type to return 204 (fallback to text), " +
            $"got {(int)res.StatusCode}. Body: {await res.Content.ReadAsStringAsync()}");
    }

    // -----------------------------------------------------------------------
    // 3. Legacy route regression — removed paths must 404
    // -----------------------------------------------------------------------

    [Test]
    public async Task LegacyRoute_SystemsMePolls_Returns404()
    {
        if (!ShouldRun()) return;

        var workspaceRoot = FindWorkspaceRoot();
        var port = GetFreePort();
        await using var api = await StartApiAsync(workspaceRoot, port);
        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };

        var principal = "parity-legacy-routes";

        foreach (var (method, path) in new[]
        {
            (HttpMethod.Get,  "/api/systems/me/polls"),
            (HttpMethod.Post, "/api/systems/me/polls"),
        })
        {
            using var req = new HttpRequestMessage(method, path)
            {
                Content = method == HttpMethod.Post
                    ? JsonContent.Create(new { title = "LegacyPoll" })
                    : null
            };
            req.Headers.Add("X-Octocon-Dev-Principal", principal);
            var res = await client.SendAsync(req);

            Ensure(res.StatusCode == HttpStatusCode.NotFound,
                $"Expected {method} {path} to return 404 (removed legacy path), got {(int)res.StatusCode}.");
        }
    }

    [Test]
    public async Task LegacyRoute_SystemsMeJournals_Returns404()
    {
        if (!ShouldRun()) return;

        var workspaceRoot = FindWorkspaceRoot();
        var port = GetFreePort();
        await using var api = await StartApiAsync(workspaceRoot, port);
        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };

        var principal = "parity-legacy-journals";

        foreach (var (method, path) in new[]
        {
            (HttpMethod.Get,  "/api/systems/me/journals"),
            (HttpMethod.Post, "/api/systems/me/journals"),
        })
        {
            using var req = new HttpRequestMessage(method, path)
            {
                Content = method == HttpMethod.Post
                    ? JsonContent.Create(new { title = "LegacyJournal" })
                    : null
            };
            req.Headers.Add("X-Octocon-Dev-Principal", principal);
            var res = await client.SendAsync(req);

            Ensure(res.StatusCode == HttpStatusCode.NotFound,
                $"Expected {method} {path} to return 404 (removed legacy path), got {(int)res.StatusCode}.");
        }
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static async Task<int> CreateAlterAsync(HttpClient client, string principal, string name)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/systems/me/alters")
        {
            Content = JsonContent.Create(new { name })
        };
        req.Headers.Add("X-Octocon-Dev-Principal", principal);
        var res = await client.SendAsync(req);
        var body = await res.Content.ReadAsStringAsync();

        Ensure(res.StatusCode == HttpStatusCode.Created,
            $"Helper CreateAlterAsync: expected 201, got {(int)res.StatusCode}. Body: {body}");

        using var doc = JsonDocument.Parse(body);
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            if (!prop.Name.Equals("data", StringComparison.OrdinalIgnoreCase) ||
                prop.Value.ValueKind != JsonValueKind.Object) continue;

            foreach (var child in prop.Value.EnumerateObject())
            {
                if (child.Name.Equals("alterId", StringComparison.OrdinalIgnoreCase) &&
                    child.Value.TryGetInt32(out var id))
                    return id;
            }
        }

        throw new InvalidOperationException($"Could not parse alterId from create response. Body: {body}");
    }

    private static string ReadNestedString(string json, string parentKey, string childKey)
    {
        using var doc = JsonDocument.Parse(json);
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            if (!prop.Name.Equals(parentKey, StringComparison.OrdinalIgnoreCase) ||
                prop.Value.ValueKind != JsonValueKind.Object) continue;

            foreach (var child in prop.Value.EnumerateObject())
            {
                if (child.Name.Equals(childKey, StringComparison.OrdinalIgnoreCase) &&
                    child.Value.ValueKind == JsonValueKind.String)
                    return child.Value.GetString() ?? string.Empty;
            }
        }
        return string.Empty;
    }

    private static bool ReadBool(string json, string key)
    {
        using var doc = JsonDocument.Parse(json);
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            if (!prop.Name.Equals(key, StringComparison.OrdinalIgnoreCase)) continue;
            return prop.Value.ValueKind == JsonValueKind.True;
        }
        throw new InvalidOperationException($"Field '{key}' not found in: {json}");
    }

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
            RedirectStandardError = true,
            UseShellExecute = false
        };

        psi.Environment["ASPNETCORE_URLS"] = $"http://127.0.0.1:{port}";
        psi.Environment["OCTOCON_PERSISTENCE"] = "inmemory";
        psi.Environment["OCTOCON_DEV_ALLOW_HEADER_PRINCIPAL"] = "true";
        psi.Environment["OCTOCON_JWT_AUTHORITY"] = string.Empty;

        var process = new Process { StartInfo = psi };
        process.Start();

        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
        var deadline = DateTime.UtcNow.AddMilliseconds(30_000);

        while (DateTime.UtcNow < deadline)
        {
            if (process.HasExited)
            {
                var stderr = await process.StandardError.ReadToEndAsync();
                await gateLease.DisposeAsync();
                throw new InvalidOperationException($"API exited. stderr: {stderr}");
            }

            try
            {
                if ((await http.GetAsync("/api/heartbeat")).StatusCode == HttpStatusCode.OK)
                    break;
            }
            catch { }

            await Task.Delay(200);
        }

        return new RunningApi(process, gateLease);
    }

    private static int GetFreePort()
    {
        var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        l.Start();
        try { return ((System.Net.IPEndPoint)l.LocalEndpoint).Port; }
        finally { l.Stop(); }
    }

    private static string FindWorkspaceRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "csharp")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("Cannot find workspace root.");
    }

    private static bool ShouldRun()
    {
        var run = Environment.GetEnvironmentVariable("OCTOCON_RUN_API_INTEGRATION");
        return bool.TryParse(run, out var v) && v;
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class RunningApi(Process process, IAsyncDisposable gateLease) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            process.Dispose();
            await gateLease.DisposeAsync();
        }
    }
}
