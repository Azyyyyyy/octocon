using Interfold.Api.Socket;

namespace Interfold.Api.UnitTests.Socket;

/// <summary>
/// Unit tests for <see cref="WebSocketHandler.ResolveLoopbackBaseUri"/>, the helper that
/// translates Kestrel's <c>IServerAddressesFeature.Addresses</c> into a loopback-safe base
/// URL for the WebSocket "endpoint" relay's self-call.
///
/// The pre-fix behaviour built the proxy's outbound URL from the inbound request's
/// <c>Scheme</c> + <c>Host</c>, which crashed in published docker deployments because the
/// inbound Host is the operator-facing hostname:port (e.g. <c>shrimp.local:5001</c>) that
/// the container itself can neither resolve nor reach (port 5001 is the host-side of the
/// docker port mapping; the container listens on <c>ASPNETCORE_HTTP(S)_PORTS</c> internally,
/// default 5100/5101). This helper resolves the URL from Kestrel's actual bindings
/// instead, so the regression cannot return without these tests catching it.
/// </summary>
public sealed class ResolveLoopbackBaseUriTests
{
    /// <summary>The fallback used when no addresses are reported (e.g. under TestServer's
    /// no-op <c>IServerAddressesFeature</c>). Kestrel always reports its bindings in
    /// production so this branch is unreachable there; it exists so the in-memory
    /// integration test harness keeps working.</summary>
    private const string TestServerFallback = "http://localhost";

    [Test]
    public async Task NullAddresses_FallsBackToLocalhost()
    {
        var result = WebSocketHandler.ResolveLoopbackBaseUri(addresses: null);
        await Assert.That(result).IsEqualTo(TestServerFallback)
            .Because("Null Addresses should not crash the proxy — under TestServer's no-op feature the in-memory HttpClient routes by pipeline so the authority is cosmetic.");
    }

    [Test]
    public async Task EmptyAddresses_FallsBackToLocalhost()
    {
        var result = WebSocketHandler.ResolveLoopbackBaseUri(addresses: new List<string>());
        await Assert.That(result).IsEqualTo(TestServerFallback)
            .Because("An empty Addresses collection (TestServer's documented behaviour) should hit the same fallback as null.");
    }

    [Test]
    public async Task SingleHttpListener_LeftAsIs_WhenAlreadySpecific()
    {
        var result = WebSocketHandler.ResolveLoopbackBaseUri(["http://127.0.0.1:5100"]);
        await Assert.That(result).IsEqualTo("http://127.0.0.1:5100")
            .Because("A specific loopback binding has no wildcard to rewrite — the URL is forwarded unchanged.");
    }

    [Test]
    public async Task SingleHttpListener_TrimsTrailingSlash()
    {
        // Kestrel can report addresses with a trailing slash depending on how the URL was
        // configured; HttpRequestMessage tolerates either, but the test pins the canonical
        // shape so we don't emit double-slashes when concatenating the request path.
        var result = WebSocketHandler.ResolveLoopbackBaseUri(["http://127.0.0.1:5100/"]);
        await Assert.That(result).IsEqualTo("http://127.0.0.1:5100")
            .Because("Trailing slash on the base URL would produce '/api//systems' when concatenated with a leading-slash path.");
    }

    [Test]
    public async Task WildcardZeroes_AreRewrittenToLoopback()
    {
        // The exact shape Kestrel reports when bound via `ASPNETCORE_HTTP_PORTS=5100` — see
        // csharp/Interfold.AppHost/InterfoldAppHost.cs:647 (ConfigureApiSelfHostEnv). The
        // outbound HTTP request from inside the container cannot dial 0.0.0.0; it must
        // target the loopback equivalent.
        var result = WebSocketHandler.ResolveLoopbackBaseUri(["http://0.0.0.0:5100"]);
        await Assert.That(result).IsEqualTo("http://127.0.0.1:5100")
            .Because("0.0.0.0 is a wildcard bind address — calling 0.0.0.0 from inside the same process is not portable; loopback is.");
    }

    [Test]
    public async Task IPv6Wildcard_IsRewrittenToLoopback()
    {
        // `[::]` is the IPv6 unspecified address — the canonical wildcard shape Kestrel
        // reports on dual-stack Linux hosts (the deployment topology that triggered the
        // original bug). Must rewrite to loopback for the same reason as 0.0.0.0.
        var result = WebSocketHandler.ResolveLoopbackBaseUri(["http://[::]:5100"]);
        await Assert.That(result).IsEqualTo("http://127.0.0.1:5100")
            .Because("[::] is the IPv6 wildcard — the loopback equivalent for self-calls is 127.0.0.1 (we don't try [::1] because the http transport prefers v4 by default).");
    }

    [Test]
    public async Task PlusWildcard_IsRewrittenToLoopback()
    {
        // `+` is the legacy Windows/HTTP.sys wildcard shape — Kestrel doesn't typically
        // emit it, but accept it defensively so an operator's UseUrls("http://+:5100")
        // can't reintroduce the original bug.
        var result = WebSocketHandler.ResolveLoopbackBaseUri(["http://+:5100"]);
        await Assert.That(result).IsEqualTo("http://127.0.0.1:5100")
            .Because("`+` is the strong-wildcard shape from the HTTP.sys era; covered defensively because UseUrls accepts it.");
    }

    [Test]
    public async Task StarWildcard_IsRewrittenToLoopback()
    {
        // Same rationale as `+` — accept the weak-wildcard form defensively.
        var result = WebSocketHandler.ResolveLoopbackBaseUri(["http://*:5100"]);
        await Assert.That(result).IsEqualTo("http://127.0.0.1:5100")
            .Because("`*` is the weak-wildcard shape from the HTTP.sys era; covered defensively because UseUrls accepts it.");
    }

    [Test]
    public async Task PrefersHttpListener_OverHttps_WhenBothPresent()
    {
        // In the published deployment topology Kestrel binds BOTH http and https endpoints
        // (the AppHost wires ASPNETCORE_HTTP_PORTS and ASPNETCORE_HTTPS_PORTS — see
        // csharp/Interfold.AppHost/InterfoldAppHost.cs:647-648). The self-call must use
        // http because the leaf PFX has SANs for the operator-facing hostname
        // (e.g. api.example.com), not 127.0.0.1 — an HTTPS loopback would fail TLS
        // hostname validation and reintroduce a different flavour of the bug.
        var result = WebSocketHandler.ResolveLoopbackBaseUri([
            "https://[::]:5101",
            "http://[::]:5100",
        ]);
        await Assert.That(result).IsEqualTo("http://127.0.0.1:5100")
            .Because("HTTPS loopback would fail TLS hostname validation because the leaf PFX's SANs target the operator-facing hostname, not 127.0.0.1.");
    }

    [Test]
    public async Task PrefersHttpListener_RegardlessOfOrdering()
    {
        // Same as the previous case but with the http entry listed first. Defends the
        // ordering-stable preference logic against accidental reliance on Kestrel's
        // emission order (which is implementation-defined and may vary across versions).
        var result = WebSocketHandler.ResolveLoopbackBaseUri([
            "http://[::]:5100",
            "https://[::]:5101",
        ]);
        await Assert.That(result).IsEqualTo("http://127.0.0.1:5100")
            .Because("Preference must be based on scheme, not on the order Kestrel emits its bindings.");
    }

    [Test]
    public async Task FallsBackToHttps_WhenOnlyHttpsAvailable()
    {
        // Operator-overridden topology — running an HTTPS-only Kestrel. The helper
        // still returns a usable URL; the caller dialing HTTPS loopback in this niche
        // case is the operator's choice (and documented as a limitation in the code
        // comment in WebSocketHandler.cs).
        var result = WebSocketHandler.ResolveLoopbackBaseUri(["https://[::]:5101"]);
        await Assert.That(result).IsEqualTo("https://127.0.0.1:5101")
            .Because("HTTPS-only topology is unusual but supported — the helper returns a loopback HTTPS URL rather than the fallback.");
    }

    [Test]
    public async Task SpecificBoundIp_IsLeftUntouched()
    {
        // When Kestrel is bound to a specific NIC IP (e.g. via UseUrls("http://192.168.1.5:5100"))
        // the loopback rewrite must NOT fire — the address is already reachable and rewriting
        // it would defeat operator intent.
        var result = WebSocketHandler.ResolveLoopbackBaseUri(["http://192.168.1.5:5100"]);
        await Assert.That(result).IsEqualTo("http://192.168.1.5:5100")
            .Because("A specific NIC bind address is already reachable as-is — only wildcards (0.0.0.0 / [::] / + / *) get rewritten.");
    }

    [Test]
    public async Task LocalhostBinding_IsLeftUntouched()
    {
        // The dev `aspire run` topology: launchSettings drives Kestrel to bind to
        // http://localhost:<port>. The helper should pass the URL through unchanged because
        // `localhost` is already loopback-correct.
        var result = WebSocketHandler.ResolveLoopbackBaseUri(["http://localhost:5100"]);
        await Assert.That(result).IsEqualTo("http://localhost:5100")
            .Because("localhost is already loopback — no wildcard substitution needed.");
    }

    [Test]
    public async Task RegressionGuard_DoesNotReturnOperatorFacingHostname()
    {
        // The original bug surfaced as the proxy dialing `https://shrimp.local:5001/api/...`
        // — the operator-facing hostname + host-side port baked into the inbound request.
        // The helper has no business returning that shape regardless of input: it MUST
        // only ever return either the TestServer fallback or one of the inputs (post
        // wildcard rewrite). This test pins that contract by passing the exact strings
        // that triggered the production failure and asserting the result is neither of
        // them.
        var deploymentTopology = new[]
        {
            "http://0.0.0.0:5100",
            "https://0.0.0.0:5101",
        };
        var result = WebSocketHandler.ResolveLoopbackBaseUri(deploymentTopology);

        using (Assert.Multiple())
        {
            await Assert.That(result).DoesNotContain("shrimp.local")
                .Because("Regression guard: the proxy must never return the inbound request's operator-facing hostname.");
            await Assert.That(result).DoesNotContain(":5001")
                .Because("Regression guard: the proxy must never return the host-side mapped port.");
            await Assert.That(result).IsEqualTo("http://127.0.0.1:5100")
                .Because("Expected the rewritten loopback HTTP URL targeting the container-internal Kestrel port.");
        }
    }
}
