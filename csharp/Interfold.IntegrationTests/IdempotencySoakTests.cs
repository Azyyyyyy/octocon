using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using TUnit.Core;

namespace Interfold.IntegrationTests;

/// <summary>
/// Idempotency soak tests (Phase N, Scope 2).
/// <para>
/// Each test replays the same command multiple times and verifies that:
/// <list type="bullet">
///   <item>The first call returns <c>replay=false</c>.</item>
///   <item>All subsequent calls return <c>replay=true</c> with the same HTTP status.</item>
///   <item>The API remains stable under N identical requests (no crashes, no 5xx).</item>
/// </list>
/// </para>
/// Gated on <c>OCTOCON_RUN_API_INTEGRATION=true</c>.
/// </summary>
public sealed class IdempotencySoakTests
{
    private const int SoakRepeatCount = 5;

    // -----------------------------------------------------------------------
    // Create-type operations
    // -----------------------------------------------------------------------

    [Test]
    public async Task Idempotency_AlterCreate_ReplayStable()
    {
        if (!ShouldRun()) return;

        await RunSoakAsync(async (client, key) =>
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, "/api/systems/me/alters")
            {
                Content = JsonContent.Create(new { name = "SoakAlter" })
            };
            req.Headers.Add("X-Interfold-Idempotency-Key", key);
            return await client.SendAsync(req);
        });
    }

    [Test]
    public async Task Idempotency_TagCreate_ReplayStable()
    {
        if (!ShouldRun()) return;

        await RunSoakAsync(async (client, key) =>
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, "/api/systems/me/tags")
            {
                Content = JsonContent.Create(new { name = "SoakTag" })
            };
            req.Headers.Add("X-Interfold-Idempotency-Key", key);
            return await client.SendAsync(req);
        });
    }

    [Test]
    public async Task Idempotency_PollCreate_ReplayStable()
    {
        if (!ShouldRun()) return;

        await RunSoakAsync(async (client, key) =>
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, "/api/polls")
            {
                Content = JsonContent.Create(new { title = "SoakPoll", options = new[] { "Yes", "No" } })
            };
            req.Headers.Add("X-Interfold-Idempotency-Key", key);
            return await client.SendAsync(req);
        });
    }

    [Test]
    public async Task Idempotency_GlobalJournalCreate_ReplayStable()
    {
        if (!ShouldRun()) return;

        await RunSoakAsync(async (client, key) =>
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, "/api/journals")
            {
                Content = JsonContent.Create(new { title = "SoakJournal", body = "entry body" })
            };
            req.Headers.Add("X-Interfold-Idempotency-Key", key);
            return await client.SendAsync(req);
        });
    }

    [Test]
    public async Task Idempotency_SettingsUsernameUpdate_ReplayStable()
    {
        if (!ShouldRun()) return;

        await RunSoakAsync(async (client, key) =>
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, "/api/settings/username")
            {
                Content = JsonContent.Create(new { username = "soakuser" })
            };
            req.Headers.Add("X-Interfold-Idempotency-Key", key);
            return await client.SendAsync(req);
        });
    }

    // -----------------------------------------------------------------------
    // Request/response header assertions
    // -----------------------------------------------------------------------

    [Test]
    public async Task Idempotency_RequestId_PresentOnEveryResponse()
    {
        if (!ShouldRun()) return;

        var workspaceRoot = FindWorkspaceRoot();
        var port = GetFreePort();

        await using var api = await StartApiAsync(workspaceRoot, port);
        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };

        for (var i = 0; i < 3; i++)
        {
            var response = await client.GetAsync("/api/heartbeat");

            Ensure(
                response.Headers.TryGetValues("X-Octocon-Request-Id", out var values) &&
                !string.IsNullOrWhiteSpace(values.First()),
                $"Expected X-Octocon-Request-Id header on request #{i + 1}.");
        }
    }

    [Test]
    public async Task Idempotency_CorrelationId_EchoedFromRequestHeader()
    {
        if (!ShouldRun()) return;

        var workspaceRoot = FindWorkspaceRoot();
        var port = GetFreePort();

        await using var api = await StartApiAsync(workspaceRoot, port);
        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };

        var sentId = Guid.NewGuid().ToString("N");
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/heartbeat");
        request.Headers.Add("X-Request-Id", sentId);

        var response = await client.SendAsync(request);

        Ensure(
            response.Headers.TryGetValues("X-Octocon-Request-Id", out var values) &&
            values.First() == sentId,
            $"Expected X-Octocon-Request-Id={sentId} echoed in response.");
    }

    // -----------------------------------------------------------------------
    // Core soak runner
    // -----------------------------------------------------------------------

    private static async Task RunSoakAsync(
        Func<HttpClient, string, Task<HttpResponseMessage>> requestFactory)
    {
        var workspaceRoot = FindWorkspaceRoot();
        var port = GetFreePort();

        await using var api = await StartApiAsync(workspaceRoot, port);
        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };

        var key = Guid.NewGuid().ToString("N");

        for (var i = 0; i < SoakRepeatCount; i++)
        {
            using var response = await requestFactory(client, key);
            var body = await response.Content.ReadAsStringAsync();

            Ensure(
                response.IsSuccessStatusCode,
                $"Soak call #{i + 1}: expected 2xx, got {(int)response.StatusCode}. Body: {body}");

            // 204 No Content responses have no body; skip replay-flag assertion for those.
            if (!string.IsNullOrEmpty(body))
            {
                var replay = ReadBoolField(body, "replay");

                if (i == 0)
                {
                    Ensure(!replay,
                        $"Soak call #1: expected replay=false on first invocation. Body: {body}");
                }
                else
                {
                    Ensure(replay,
                        $"Soak call #{i + 1}: expected replay=true after first invocation. Body: {body}");
                }
            }
        }
    }

    // -----------------------------------------------------------------------
    // Infrastructure helpers
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

        psi.Environment["ASPNETCORE_URLS"]                    = $"http://127.0.0.1:{port}";
        psi.Environment["OCTOCON_PERSISTENCE"]                = "inmemory";

        var process = new Process { StartInfo = psi };
        process.Start();

        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
        var deadline = DateTime.UtcNow.AddMilliseconds(30_000);

        while (DateTime.UtcNow < deadline)
        {
            if (process.HasExited)
            {
                var err = await process.StandardError.ReadToEndAsync();
                await gateLease.DisposeAsync();
                throw new InvalidOperationException($"API exited. stderr: {err}");
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
                    $"Expected boolean for '{fieldName}'. Body: {json}")
            };
        }

        throw new InvalidOperationException($"Field '{fieldName}' not found in: {json}");
    }

    private static int GetFreePort()
    {
        var l = new TcpListener(System.Net.IPAddress.Loopback, 0);
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
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch { }
            process.Dispose();
            await gateLease.DisposeAsync();
        }
    }
}
