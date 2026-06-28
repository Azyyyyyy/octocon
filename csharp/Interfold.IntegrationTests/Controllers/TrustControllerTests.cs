using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Interfold.IntegrationTests.TestServices;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Interfold.IntegrationTests.Controllers;

/// <summary>
/// HTTP-level coverage of <c>TrustController</c> (<c>/.well-known/interfold-root-ca.*</c>).
/// Each test constructs a fresh factory so the env-bound <see cref="Interfold.Contracts.Configuration.TrustOptions"/>
/// snapshot — read once at startup via <see cref="Microsoft.Extensions.Options.IOptions{T}"/> —
/// captures the per-test path the case under test wants to exercise. In-memory persistence is
/// used because the trust routes don't touch any persistence surface and the inmemory factory
/// boots in well under a second.
/// </summary>
public class TrustControllerTests : BaseEndpointTest
{
    [Test]
    public async Task ReturnsNotFoundWhenRootCaPathUnset()
    {
        // Dev-mode parity: no /certs bind mount → bootstrapper doesn't emit the OCTOCON_TRUST_*
        // env vars → the controller has nothing to serve. The contract is "404 on every
        // route", not "500 because IO failed", so users running `aspire run` locally just
        // see a clean miss instead of a noisy stack trace.
        await using var factory = new InterfoldWebApplicationFactory("inmemory");
        using var client = factory.CreateClient();

        foreach (var path in new[]
                 {
                     "/.well-known/interfold-root-ca.crt",
                     "/.well-known/interfold-root-ca.pem",
                     "/.well-known/interfold-root-ca.sha256",
                 })
        {
            var response = await client.GetAsync(path);
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound)
                .Because($"{path} must 404 when TrustOptions paths are unset");
        }
    }

    [Test]
    public async Task ReturnsCertWithCorrectContentTypeAndBytes()
    {
        // Mint a self-signed CA cert + write its SHA-256 sidecar to a temp dir, then point
        // TrustOptions at those paths. The controller must hand back the exact DER for the
        // .crt route (re-encoded from PEM in-controller via X509CertificateLoader) and the
        // verbatim PEM bytes for the .pem route — byte-for-byte. Anything else is a content
        // corruption regression.
        var (certPath, fingerprintPath, expectedDer, expectedFingerprint, cleanup) = CreateTempCertFiles();
        try
        {
            await using var factory = new InterfoldWebApplicationFactory("inmemory")
                .WithConfiguration("OCTOCON_TRUST_ROOT_CA_PATH", certPath)
                .WithConfiguration("OCTOCON_TRUST_ROOT_CA_FINGERPRINT_PATH", fingerprintPath);
            using var client = factory.CreateClient();

            var crt = await client.GetAsync("/.well-known/interfold-root-ca.crt");
            await Assert.That(crt.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(crt.Content.Headers.ContentType?.MediaType).IsEqualTo("application/pkix-cert");
            var crtBytes = await crt.Content.ReadAsByteArrayAsync();
            await Assert.That(crtBytes).IsEquivalentTo(expectedDer)
                .Because("the .crt route must serve the cert in DER form");

            var pem = await client.GetAsync("/.well-known/interfold-root-ca.pem");
            await Assert.That(pem.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(pem.Content.Headers.ContentType?.MediaType).IsEqualTo("application/x-pem-file");
            var pemBytes = await pem.Content.ReadAsByteArrayAsync();
            await Assert.That(pemBytes).IsEquivalentTo(await File.ReadAllBytesAsync(certPath))
                .Because("the .pem route must stream the on-disk bytes verbatim");

            var sha256 = await client.GetAsync("/.well-known/interfold-root-ca.sha256");
            await Assert.That(sha256.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(sha256.Content.Headers.ContentType?.MediaType).IsEqualTo("text/plain");
            var sha256Text = (await sha256.Content.ReadAsStringAsync()).Trim();
            await Assert.That(sha256Text).IsEqualTo(expectedFingerprint);

            // Legacy MIME negotiation: clients that explicitly Accept application/x-x509-ca-cert
            // (Safari / iOS profile flow) get the legacy type back; default is the modern
            // application/pkix-cert from the unsuffixed request above.
            using var legacyReq = new HttpRequestMessage(HttpMethod.Get, "/.well-known/interfold-root-ca.crt");
            legacyReq.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/x-x509-ca-cert"));
            var legacy = await client.SendAsync(legacyReq);
            await Assert.That(legacy.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(legacy.Content.Headers.ContentType?.MediaType).IsEqualTo("application/x-x509-ca-cert");
        }
        finally
        {
            cleanup();
        }
    }

    [Test]
    public async Task ReturnsEtagMatchingFingerprintFile()
    {
        // The ETag is the only mechanism that propagates a --rotate-certs through downstream
        // caches before Cache-Control max-age expires. Locking the value to the fingerprint
        // file's contents (verbatim, quoted) protects that contract.
        var (certPath, fingerprintPath, _, expectedFingerprint, cleanup) = CreateTempCertFiles();
        try
        {
            await using var factory = new InterfoldWebApplicationFactory("inmemory")
                .WithConfiguration("OCTOCON_TRUST_ROOT_CA_PATH", certPath)
                .WithConfiguration("OCTOCON_TRUST_ROOT_CA_FINGERPRINT_PATH", fingerprintPath);
            using var client = factory.CreateClient();

            foreach (var path in new[]
                     {
                         "/.well-known/interfold-root-ca.crt",
                         "/.well-known/interfold-root-ca.pem",
                         "/.well-known/interfold-root-ca.sha256",
                     })
            {
                var response = await client.GetAsync(path);
                await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

                var etag = response.Headers.ETag?.Tag;
                await Assert.That(etag).IsEqualTo($"\"{expectedFingerprint}\"")
                    .Because($"{path} must return ETag derived from rootCA.sha256.txt");

                var cacheControl = response.Headers.CacheControl?.ToString();
                await Assert.That(cacheControl).IsNotNull();
                await Assert.That(cacheControl!).Contains("public");
                await Assert.That(cacheControl!).Contains("max-age=60");
                await Assert.That(cacheControl!).Contains("must-revalidate");
            }
        }
        finally
        {
            cleanup();
        }
    }

    [Test]
    public async Task HeadRequestsReturnHeadersWithoutBody()
    {
        // HEAD is mandatory wherever GET is supported (RFC 9110 §9.3.2) and is the request
        // CDNs / browsers / curl -I issue when revalidating a cached artefact - including
        // the bootstrapper integration test's curl-based ETag check. Without [HttpHead]
        // alongside [HttpGet] on each action, the routing layer rejects HEAD as
        // "no matching endpoint", which then falls through to the default authorize policy
        // and returns 401 with a WWW-Authenticate: Bearer challenge - the exact regression
        // we shipped once. This test pins HEAD against the same matrix the GET tests cover
        // and asserts the cache contract (ETag, Cache-Control) is identical so cache
        // revalidation behaves correctly.
        var (certPath, fingerprintPath, _, expectedFingerprint, cleanup) = CreateTempCertFiles();
        try
        {
            await using var factory = new InterfoldWebApplicationFactory("inmemory")
                .WithConfiguration("OCTOCON_TRUST_ROOT_CA_PATH", certPath)
                .WithConfiguration("OCTOCON_TRUST_ROOT_CA_FINGERPRINT_PATH", fingerprintPath);
            using var client = factory.CreateClient();

            foreach (var path in new[]
                     {
                         "/.well-known/interfold-root-ca.crt",
                         "/.well-known/interfold-root-ca.pem",
                         "/.well-known/interfold-root-ca.sha256",
                     })
            {
                using var req = new HttpRequestMessage(HttpMethod.Head, path);
                var response = await client.SendAsync(req);

                await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK)
                    .Because($"HEAD {path} must return 200 (not 401 / 405) so cache revalidation works");

                var etag = response.Headers.ETag?.Tag;
                await Assert.That(etag).IsEqualTo($"\"{expectedFingerprint}\"")
                    .Because($"HEAD {path} must publish the same ETag as GET so conditional caches stay in sync");

                var body = await response.Content.ReadAsByteArrayAsync();
                await Assert.That(body.Length).IsEqualTo(0)
                    .Because($"HEAD {path} must omit the response body (RFC 9110 §9.3.2)");
            }
        }
        finally
        {
            cleanup();
        }
    }

    [Test]
    public async Task WellKnownRoutesBypassHttpsRedirect()
    {
        // The trust-distribution bootstrap requires plain HTTP at /.well-known/* because
        // end-user devices fetching the root CA have no trust path to HTTPS yet. A
        // UseHttpsRedirection 308 to https:// would either (a) fail TLS handshake because
        // the device doesn't trust the leaf, or (b) redirect to the container-internal
        // HTTPS port which isn't reachable from external clients - either way the bootstrap
        // breaks. Program.cs bypasses HttpsRedirection for /.well-known/* exclusively;
        // this test pins that bypass against the matrix of routes the controller serves
        // and asserts the rest of the pipeline still redirects (so the bypass stays narrowly
        // scoped to the trust-bootstrap surface and nothing else).
        var (certPath, fingerprintPath, _, _, cleanup) = CreateTempCertFiles();
        try
        {
            // HTTPS_PORT activates HttpsRedirectionMiddleware in-test - without it the
            // middleware logs a warning and lets every request through, so a regression
            // would slip past the assertion below. The exact port doesn't matter because
            // we're not following redirects.
            await using var factory = new InterfoldWebApplicationFactory("inmemory")
                .WithConfiguration("OCTOCON_TRUST_ROOT_CA_PATH", certPath)
                .WithConfiguration("OCTOCON_TRUST_ROOT_CA_FINGERPRINT_PATH", fingerprintPath)
                .WithConfiguration("HTTPS_PORT", "443");
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false,
            });

            foreach (var path in new[]
                     {
                         "/.well-known/interfold-root-ca.crt",
                         "/.well-known/interfold-root-ca.pem",
                         "/.well-known/interfold-root-ca.sha256",
                     })
            {
                var response = await client.GetAsync(path);
                await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK)
                    .Because($"{path} must serve the artefact directly over plain HTTP, not redirect to HTTPS");
                await Assert.That((int)response.StatusCode is >= 300 and < 400).IsFalse()
                    .Because($"{path} must NOT issue any 3xx redirect - clients fetching the root CA have no HTTPS trust path");
            }

            // Negative control: every other path must still redirect, proving the middleware
            // is wired and the bypass is narrowly scoped to /.well-known/* only.
            var control = await client.GetAsync("/api/i-do-not-exist");
            await Assert.That((int)control.StatusCode is >= 300 and < 400).IsTrue()
                .Because($"non-.well-known paths must still go through HttpsRedirection (got {(int)control.StatusCode})");
        }
        finally
        {
            cleanup();
        }
    }

    /// <summary>
    /// Produces a temp directory holding <c>rootCA.crt</c> (PEM) and <c>rootCA.sha256.txt</c>
    /// (uppercase colon-hex) for a freshly-minted self-signed CA. Returns the paths, the
    /// expected DER bytes for byte-for-byte response assertions, the expected fingerprint
    /// string, and a cleanup callback. Failing the cleanup is non-fatal — the temp dir
    /// gets garbage-collected by the OS' tmp reaper.
    /// </summary>
    private static (string certPath, string fingerprintPath, byte[] expectedDer, string expectedFingerprint, Action cleanup) CreateTempCertFiles()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), "interfold-trust-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmpDir);

        using var key = RSA.Create(2048);
        var req = new CertificateRequest(
            new X500DistinguishedName("CN=Test Trust Controller Root"),
            key,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(certificateAuthority: true, hasPathLengthConstraint: true, pathLengthConstraint: 0, critical: true));

        using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddYears(1));
        var certPath = Path.Combine(tmpDir, "rootCA.crt");
        File.WriteAllText(certPath, cert.ExportCertificatePem());

        var hash = SHA256.HashData(cert.RawData);
        var fingerprint = ToColonHexUpper(hash);
        var fingerprintPath = Path.Combine(tmpDir, "rootCA.sha256.txt");
        File.WriteAllText(fingerprintPath, fingerprint + Environment.NewLine);

        var expectedDer = (byte[])cert.RawData.Clone();

        void Cleanup()
        {
            try { Directory.Delete(tmpDir, recursive: true); } catch { /* best effort */ }
        }

        return (certPath, fingerprintPath, expectedDer, fingerprint, Cleanup);
    }

    private static string ToColonHexUpper(byte[] hash)
    {
        var sb = new System.Text.StringBuilder(hash.Length * 3);
        for (var i = 0; i < hash.Length; i++)
        {
            if (i > 0) sb.Append(':');
            sb.Append(hash[i].ToString("X2"));
        }
        return sb.ToString();
    }
}
