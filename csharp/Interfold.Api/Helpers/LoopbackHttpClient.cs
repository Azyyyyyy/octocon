namespace Interfold.Api.Helpers;

/// <summary>
/// Constants and predicates for the API's loopback self-call <c>HttpClient</c>. The named
/// client is registered in <c>Program.cs</c> with a permissive
/// <c>RemoteCertificateValidationCallback</c> so the in-process WebSocket endpoint-relay
/// (see <c>WebSocketHandler.HandleEndpointProxyAsync</c>) can dial the API's own HTTPS
/// listener at <c>127.0.0.1</c> / <c>::1</c> / <c>localhost</c> without tripping over a
/// hostname-mismatch + chain-trust failure.
///
/// <para>
/// <b>Why permissive validation is safe here.</b> The client is only ever used for
/// loopback URIs (the call site enforces this via <see cref="IsLoopbackHost"/>). A loopback
/// connection cannot be intercepted from outside the process — any agent capable of
/// MITM-ing <c>127.0.0.1</c> is already executing inside the container, in which case it
/// owns the keys, the process memory, and everything else of value. TLS validation on
/// loopback is security theatre, not a real defence boundary; meanwhile, default validation
/// against the bootstrapper-issued leaf cert fails because:
/// <list type="bullet">
///   <item>The leaf PFX served by Kestrel carries SANs for the operator-facing hostname
///         (e.g. <c>api.example.com</c>), never for the loopback literal — produces
///         <c>RemoteCertificateNameMismatch</c>.</item>
///   <item>The leaf is signed by the bootstrapper-issued private root CA, which is not in
///         the container's system trust store — produces
///         <c>RemoteCertificateChainErrors</c>.</item>
/// </list>
/// Both fall out together, which is the failure the user observed after the original
/// <c>Request.Host</c>-based bug was fixed.
/// </para>
///
/// <para>
/// External clients (and every other <c>HttpClient</c> the API may resolve via
/// <see cref="IHttpClientFactory"/>) keep the platform-default strict validation. Only this
/// one named client is permissive, and only because its call sites guarantee the
/// destination is loopback.
/// </para>
/// </summary>
public static class LoopbackHttpClient
{
    /// <summary>
    /// The <see cref="IHttpClientFactory"/> client name. Use
    /// <c>IHttpClientFactory.CreateClient(LoopbackHttpClient.Name)</c> for loopback self
    /// calls so the permissive TLS validator is in effect; do NOT use this client for any
    /// non-loopback destination.
    /// </summary>
    public const string Name = "interfold-loopback";

    /// <summary>
    /// Matches the literal host portion of a URI against the canonical loopback shapes.
    /// Hostnames are NOT resolved — a domain that happens to point at 127.0.0.1 via
    /// <c>/etc/hosts</c> is NOT loopback by this definition. The check exists so the
    /// proxy's call site can sanity-check the URL it's about to feed to the permissive
    /// client and bail out cleanly if a future change makes
    /// <c>WebSocketHandler.ResolveLoopbackBaseUri</c> return a non-loopback shape.
    /// </summary>
    /// <remarks>
    /// <see cref="Uri.Host"/> returns IPv6 literals without brackets (e.g. <c>::1</c>
    /// rather than <c>[::1]</c>), and <see cref="Uri.IsLoopback"/> already handles
    /// <c>127.0.0.0/8</c>, <c>::1</c>, and <c>localhost</c> per RFC 3493 — defer to it
    /// rather than re-implementing the host taxonomy.
    /// </remarks>
    public static bool IsLoopbackHost(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        return uri.IsLoopback;
    }
}
