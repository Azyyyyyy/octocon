using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Interfold.IntegrationTests.TestServices;

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
