using Interfold.Api.Helpers;

namespace Interfold.Api.UnitTests.Helpers;

/// <summary>
/// Unit tests for <see cref="LoopbackHttpClient.IsLoopbackHost"/>, the
/// defence-in-depth gate the WebSocket endpoint relay applies before sending a
/// request through the named loopback <see cref="HttpClient"/> (which is
/// configured with a permissive TLS validator). If a future change makes
/// <c>WebSocketHandler.ResolveLoopbackBaseUri</c> return a non-loopback shape,
/// this predicate is what stops the relay from silently downgrading TLS
/// validation on an external dial-out.
/// </summary>
public sealed class LoopbackHttpClientTests
{
    [Test]
    public async Task IPv4Loopback_IsLoopback()
    {
        var result = LoopbackHttpClient.IsLoopbackHost(new Uri("https://127.0.0.1:5101/api/foo"));
        await Assert.That(result).IsTrue()
            .Because("127.0.0.1 is the canonical IPv4 loopback literal used by ResolveLoopbackBaseUri's wildcard rewrite.");
    }

    [Test]
    public async Task IPv4LoopbackEntireRange_IsLoopback()
    {
        // Uri.IsLoopback defers to IPAddress.IsLoopback, which considers the full
        // 127.0.0.0/8 range loopback per RFC 3493. Pin that behaviour so a future
        // change to a stricter literal-only check is caught.
        var result = LoopbackHttpClient.IsLoopbackHost(new Uri("https://127.42.0.99:5101/api/foo"));
        await Assert.That(result).IsTrue()
            .Because("RFC 3493 / IPAddress.IsLoopback treats the full 127.0.0.0/8 block as loopback.");
    }

    [Test]
    public async Task IPv6Loopback_IsLoopback()
    {
        var result = LoopbackHttpClient.IsLoopbackHost(new Uri("https://[::1]:5101/api/foo"));
        await Assert.That(result).IsTrue()
            .Because("::1 is the IPv6 loopback literal — relevant when the helper resolves a dual-stack binding.");
    }

    [Test]
    public async Task LocalhostHostname_IsLoopback()
    {
        // Uri.IsLoopback treats the literal `localhost` token as loopback without
        // performing DNS resolution. That's intentional: it means the TestServer
        // fallback URL (`http://localhost`) lights up the relaxed-validator path
        // even though there's no real socket behind it.
        var result = LoopbackHttpClient.IsLoopbackHost(new Uri("http://localhost/api/foo"));
        await Assert.That(result).IsTrue()
            .Because("`localhost` is treated as loopback without DNS resolution so the TestServer fallback works.");
    }

    [Test]
    public async Task OperatorFacingHostname_IsNotLoopback()
    {
        // The exact shape that triggered the original bug — the inbound Host header
        // baked into a dial-out URL. If a future change ever re-emits this from
        // ResolveLoopbackBaseUri, the call-site gate must reject it so the
        // permissive validator doesn't run against the real api.example.com cert.
        var result = LoopbackHttpClient.IsLoopbackHost(new Uri("https://pineapple.local:5001/api/foo"));
        await Assert.That(result).IsFalse()
            .Because("Operator-facing hostnames are NOT loopback — the permissive validator must never run against them.");
    }

    [Test]
    public async Task LanIp_IsNotLoopback()
    {
        // A specific NIC bind (see ResolveLoopbackBaseUriTests.SpecificBoundIp_*) —
        // reachable as-is but NOT loopback, so the relaxed validator must not
        // apply. The relay would fail closed here, which is the safe direction.
        var result = LoopbackHttpClient.IsLoopbackHost(new Uri("http://192.168.1.5:5100/api/foo"));
        await Assert.That(result).IsFalse()
            .Because("Specific NIC binds are reachable but cross-host — TLS validation must remain strict for them.");
    }

    [Test]
    public async Task NullUri_Throws()
    {
        // Pin the argument contract — the call site treats a null Uri as a programmer
        // error rather than silently allowing it to be classified as non-loopback.
        await Assert.That(static () => LoopbackHttpClient.IsLoopbackHost(uri: null!))
            .Throws<ArgumentNullException>()
            .Because("The call site passes a parsed Uri; a null here would be a programming error, not a runtime input.");
    }
}
