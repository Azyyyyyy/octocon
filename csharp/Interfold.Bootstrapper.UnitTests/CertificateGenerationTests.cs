using System.Formats.Asn1;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;
using Interfold.Bootstrapper.Cli;
using Interfold.Bootstrapper.Configuration;
using Interfold.Bootstrapper.Phases;
using TUnit.Core;

namespace Interfold.Bootstrapper.UnitTests;

/// <summary>
/// Unit tests for <see cref="CertificatePhase.RunAsync"/>. The phase performs real X.509 work
/// (generates an RSA root CA + leaf, exports PEM/PFX) — fast enough to keep inside the unit
/// project so we get sub-second feedback on cert-related regressions.
/// </summary>
public sealed class CertificateGenerationTests
{
    private static BootstrapOptions OptionsFor(string outputDir) => new(
        Command: BootstrapCommand.Bootstrap,
        ConfigPath: null,
        OutputDir: outputDir,
        SkipPrereqs: true,
        RotateSecrets: false,
        RotateCerts: false,
        NonInteractive: true,
        FaultInject: null,
        PrintPhaseStatus: false);

    // Default to trustStoreInstall=false so unit tests stay hermetic. With the previous default
    // of `true`, CertificatePhase.InstallToTrustStoreAsync would File.Copy the generated root CA
    // into /usr/local/share/ca-certificates/ and shell out to update-ca-certificates — fine when
    // a developer happens to run the suite as root locally, but unprivileged CI runners trip
    // straight into UnauthorizedAccessException on the copy. The trust-store install path is
    // already covered by Interfold.Bootstrapper.IntegrationTests inside Docker; here we only
    // want to exercise the pure-C# cert generation.
    private static (BootstrapConfig Config, GeneratedSecrets Secrets) MakeInputs(
        string? rootCaName = null,
        int? certYears = null,
        IList<string>? domains = null,
        bool trustStoreInstall = false)
    {
        var config = new BootstrapConfig
        {
            Deployment =
            {
                RootCaName = rootCaName ?? "Interfold Root CA",
                CertYears = certYears ?? 5,
                Domains = domains is null ? ["api.example.com"] : [.. domains],
                TrustStoreInstall = trustStoreInstall,
            },
        };
        return (config, SecretsPhase.Generate());
    }

    private static string MakeScratchDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "interfold-certs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static X509Certificate2 LoadCert(string path) => X509CertificateLoader.LoadCertificateFromFile(path);

    [Test]
    public async Task CustomRootCaNameAppearsInCertSubject()
    {
        var outputDir = MakeScratchDir();
        try
        {
            var (config, secrets) = MakeInputs(rootCaName: "Acme Trust Root");
            var options = OptionsFor(outputDir);
            var logger = new PhaseLogger(options);

            await CertificatePhase.RunAsync(options, config, secrets, logger, CancellationToken.None);

            using var root = LoadCert(Path.Combine(outputDir, "certs", "rootCA.crt"));
            await Assert.That(root.Subject).Contains("CN=Acme Trust Root");
        }
        finally
        {
            try { Directory.Delete(outputDir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Test]
    public async Task CustomCertYearsControlsNotAfter()
    {
        var outputDir = MakeScratchDir();
        try
        {
            const int years = 12;
            var (config, secrets) = MakeInputs(certYears: years);
            var options = OptionsFor(outputDir);
            var logger = new PhaseLogger(options);

            var before = DateTimeOffset.UtcNow;
            await CertificatePhase.RunAsync(options, config, secrets, logger, CancellationToken.None);
            var after = DateTimeOffset.UtcNow;

            using var root = LoadCert(Path.Combine(outputDir, "certs", "rootCA.crt"));
            var notAfter = root.NotAfter.ToUniversalTime();

            // notAfter is approximately (notBefore + years). notBefore is constructed as
            // (UtcNow - 5min) at phase invocation time, so notAfter ~= before + years - small
            // window. Allow a 2-minute slop on either side to keep the test stable on slow CI.
            var expectedMin = before.AddYears(years).AddMinutes(-10);
            var expectedMax = after.AddYears(years);
            await Assert.That(notAfter).IsGreaterThanOrEqualTo(expectedMin.UtcDateTime);
            await Assert.That(notAfter).IsLessThanOrEqualTo(expectedMax.UtcDateTime);
        }
        finally
        {
            try { Directory.Delete(outputDir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Test]
    public async Task LeafIsSignedByRoot()
    {
        var outputDir = MakeScratchDir();
        try
        {
            var (config, secrets) = MakeInputs();
            var options = OptionsFor(outputDir);
            var logger = new PhaseLogger(options);

            await CertificatePhase.RunAsync(options, config, secrets, logger, CancellationToken.None);

            using var root = LoadCert(Path.Combine(outputDir, "certs", "rootCA.crt"));
            using var leaf = LoadCert(Path.Combine(outputDir, "certs", "leaf.crt"));

            // Build a chain anchored on the generated root and verify it.
            using var chain = new X509Chain();
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.CustomTrustStore.Add(root);
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            // The leaf is self-signed by our private root CA; the OS trust store has no opinion on it.
            chain.ChainPolicy.VerificationFlags = X509VerificationFlags.NoFlag;

            var built = chain.Build(leaf);
            // Surface the status flags in the failure message if the build fails — useful for diagnosing
            // cert-extension regressions (e.g. someone removes the AKI extension).
            var statuses = chain.ChainStatus.Length == 0
                ? "<none>"
                : string.Join(", ", chain.ChainStatus.Select(s => $"{s.Status}: {s.StatusInformation.Trim()}"));
            await Assert.That(built).IsTrue().Because($"leaf->root chain build failed: {statuses}");

            // Strand 2: leaf SAN is inside the root's permittedSubtrees by construction, so
            // none of the NameConstraints status flags should fire. Asserting their absence
            // is the cheap canary for regressions in BuildNameConstraintsExtension (wrong
            // OID, missing IMPLICIT tag, off-by-one in length prefixes, etc.) — those would
            // typically surface as InvalidNameConstraints / HasNotSupportedNameConstraint
            // even on a within-permitted leaf.
            var ncFlagFired = chain.ChainStatus.Any(s =>
                s.Status == X509ChainStatusFlags.HasNotPermittedNameConstraint
                || s.Status == X509ChainStatusFlags.HasExcludedNameConstraint
                || s.Status == X509ChainStatusFlags.InvalidNameConstraints
                || s.Status == X509ChainStatusFlags.HasNotSupportedNameConstraint);
            await Assert.That(ncFlagFired).IsFalse()
                .Because($"leaf is within permittedSubtrees; no NameConstraints flag should fire. statuses: {statuses}");
        }
        finally
        {
            try { Directory.Delete(outputDir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Test]
    public async Task MultipleDomainsBecomeMultipleSans()
    {
        var outputDir = MakeScratchDir();
        try
        {
            var (config, secrets) = MakeInputs(domains:
                ["api.example.com", "admin.example.com", "www.example.com"]);
            var options = OptionsFor(outputDir);
            var logger = new PhaseLogger(options);

            await CertificatePhase.RunAsync(options, config, secrets, logger, CancellationToken.None);

            using var leaf = LoadCert(Path.Combine(outputDir, "certs", "leaf.crt"));
            // SAN extension OID: 2.5.29.17.
            var san = leaf.Extensions.FirstOrDefault(e => e.Oid?.Value == "2.5.29.17");
            await Assert.That(san).IsNotNull();

            // Format() returns a human-readable representation that includes each DNS name.
            var sanText = san!.Format(multiLine: true);
            await Assert.That(sanText).Contains("api.example.com");
            await Assert.That(sanText).Contains("admin.example.com");
            await Assert.That(sanText).Contains("www.example.com");
        }
        finally
        {
            try { Directory.Delete(outputDir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Test]
    public async Task TrustStoreInstallFalseProducesArtifactsWithoutErroring()
    {
        // With trustStoreInstall=false the phase must still produce a full set of cert artifacts
        // (root/leaf .crt, leaf.key, leaf.pfx) — it just skips the system trust-store install
        // step. This unit test covers the branch; the integration tests verify that no anchors
        // appear under /usr/local/share/ca-certificates on Linux.
        var outputDir = MakeScratchDir();
        try
        {
            var (config, secrets) = MakeInputs(trustStoreInstall: false);
            var options = OptionsFor(outputDir);
            var logger = new PhaseLogger(options);

            await CertificatePhase.RunAsync(options, config, secrets, logger, CancellationToken.None);

            // The artifacts must still exist — only the trust-store install is gated on the flag.
            var certsDir = Path.Combine(outputDir, "certs");
            await Assert.That(File.Exists(Path.Combine(certsDir, "rootCA.crt"))).IsTrue();
            await Assert.That(File.Exists(Path.Combine(certsDir, "leaf.crt"))).IsTrue();
            await Assert.That(File.Exists(Path.Combine(certsDir, "leaf.key"))).IsTrue();
            await Assert.That(File.Exists(Path.Combine(certsDir, "leaf.pfx"))).IsTrue();
        }
        finally
        {
            try { Directory.Delete(outputDir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Test]
    public async Task RootCaKeyIsModeSixHundred()
    {
        // Linux-only. .NET's File.WriteAllText honours umask (typically 0644), which would leave
        // the CA private key world-readable. CertificatePhase explicitly chmods rootCA.key to
        // 0600 after PersistAsync so a non-bootstrapper UID on the host can't read it, and the
        // non-root API container UID (64198) that bind-mounts the same dir RO gets EACCES.
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return;
        }

        var outputDir = MakeScratchDir();
        try
        {
            var (config, secrets) = MakeInputs();
            var options = OptionsFor(outputDir);
            var logger = new PhaseLogger(options);

            await CertificatePhase.RunAsync(options, config, secrets, logger, CancellationToken.None);

            var rootKeyPath = Path.Combine(outputDir, "certs", "rootCA.key");
            await Assert.That(File.Exists(rootKeyPath)).IsTrue();

            var actualMode = File.GetUnixFileMode(rootKeyPath);
            const UnixFileMode expectedMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            await Assert.That(actualMode).IsEqualTo(expectedMode)
                .Because($"rootCA.key must be 0600 (User R+W only); got {actualMode}");
        }
        finally
        {
            try { Directory.Delete(outputDir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Test]
    public async Task RootHasCriticalNameConstraints()
    {
        var outputDir = MakeScratchDir();
        try
        {
            var (config, secrets) = MakeInputs(domains: ["api.example.com"]);
            var options = OptionsFor(outputDir);
            var logger = new PhaseLogger(options);

            await CertificatePhase.RunAsync(options, config, secrets, logger, CancellationToken.None);

            using var root = LoadCert(Path.Combine(outputDir, "certs", "rootCA.crt"));
            var nc = root.Extensions.FirstOrDefault(e => e.Oid?.Value == "2.5.29.30");
            await Assert.That(nc).IsNotNull()
                .Because("Name Constraints extension (OID 2.5.29.30) must be present on the root CA");
            await Assert.That(nc!.Critical).IsTrue()
                .Because("Name Constraints MUST be critical per RFC 5280 §4.2.1.10");
        }
        finally
        {
            try { Directory.Delete(outputDir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Test]
    public async Task PermittedSubtreesMatchDomains()
    {
        var outputDir = MakeScratchDir();
        try
        {
            var domains = new List<string> { "api.example.com", "admin.example.com", "www.example.com" };
            var (config, secrets) = MakeInputs(domains: domains);
            var options = OptionsFor(outputDir);
            var logger = new PhaseLogger(options);

            await CertificatePhase.RunAsync(options, config, secrets, logger, CancellationToken.None);

            using var root = LoadCert(Path.Combine(outputDir, "certs", "rootCA.crt"));
            var nc = root.Extensions.First(e => e.Oid?.Value == "2.5.29.30");

            var parsed = ParseDnsPermittedSubtrees(nc.RawData);
            await Assert.That(parsed).IsEquivalentTo(domains)
                .Because($"permittedSubtrees must contain exactly the configured domains; got [{string.Join(", ", parsed)}]");
        }
        finally
        {
            try { Directory.Delete(outputDir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Test]
    public async Task WildcardDomainsStripStarPrefix()
    {
        // dNSName subtree semantics (RFC 5280 §4.2.1.10) already match every host under the
        // suffix, and the literal `*` character is not legal in an IA5String constraint value.
        // BuildNameConstraintsExtension must therefore collapse `*.example.com` to
        // `example.com` in the permittedSubtrees set.
        var outputDir = MakeScratchDir();
        try
        {
            var (config, secrets) = MakeInputs(domains: ["*.example.com"]);
            var options = OptionsFor(outputDir);
            var logger = new PhaseLogger(options);

            await CertificatePhase.RunAsync(options, config, secrets, logger, CancellationToken.None);

            using var root = LoadCert(Path.Combine(outputDir, "certs", "rootCA.crt"));
            var nc = root.Extensions.First(e => e.Oid?.Value == "2.5.29.30");
            var parsed = ParseDnsPermittedSubtrees(nc.RawData);

            await Assert.That(parsed.Count).IsEqualTo(1);
            await Assert.That(parsed[0]).IsEqualTo("example.com");
        }
        finally
        {
            try { Directory.Delete(outputDir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Test]
    public async Task LeafOutsideConstraintsFailsValidation()
    {
        // Negative test: hand-craft a leaf with a SAN outside the root's permittedSubtrees and
        // confirm that chain.Build rejects it with the expected NameConstraints status flag.
        // This is the load-bearing assertion that a leaked CA private key cannot mint a
        // trusted cert for arbitrary hosts.
        var outputDir = MakeScratchDir();
        try
        {
            var (config, secrets) = MakeInputs(domains: ["api.example.com"]);
            var options = OptionsFor(outputDir);
            var logger = new PhaseLogger(options);

            await CertificatePhase.RunAsync(options, config, secrets, logger, CancellationToken.None);

            var rootCertPem = await File.ReadAllTextAsync(Path.Combine(outputDir, "certs", "rootCA.crt"));
            var rootKeyPem = await File.ReadAllTextAsync(Path.Combine(outputDir, "certs", "rootCA.key"));
            using var root = X509Certificate2.CreateFromPem(rootCertPem, rootKeyPem);

            using var leafKey = RSA.Create(2048);
            var leafReq = new CertificateRequest(
                new X500DistinguishedName("CN=evil.attacker.com"),
                leafKey,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1);
            var sanBuilder = new SubjectAlternativeNameBuilder();
            sanBuilder.AddDnsName("evil.attacker.com");
            leafReq.CertificateExtensions.Add(sanBuilder.Build());
            leafReq.CertificateExtensions.Add(
                new X509BasicConstraintsExtension(false, false, 0, true));
            leafReq.CertificateExtensions.Add(new X509KeyUsageExtension(
                X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
            leafReq.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
                new OidCollection { new("1.3.6.1.5.5.7.3.1") }, false));

            var serial = RandomNumberGenerator.GetBytes(16);
            serial[0] &= 0x7F;

            // Clamp the leaf strictly inside the root's lifetime so a NotTimeValid status can't
            // mask the NameConstraints violation we're actually testing for.
            var notBefore = new DateTimeOffset(root.NotBefore.ToUniversalTime(), TimeSpan.Zero).AddMinutes(1);
            var notAfter = new DateTimeOffset(root.NotAfter.ToUniversalTime(), TimeSpan.Zero).AddSeconds(-1);
            using var badLeaf = leafReq.Create(root, notBefore, notAfter, serial);

            using var chain = new X509Chain();
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.CustomTrustStore.Add(root);
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;

            var built = chain.Build(badLeaf);
            var statuses = chain.ChainStatus.Length == 0
                ? "<none>"
                : string.Join(", ", chain.ChainStatus.Select(s => s.Status.ToString()));

            await Assert.That(built).IsFalse()
                .Because($"chain build must reject a leaf whose SAN is outside the CA's Name Constraints; statuses: {statuses}");

            var ncViolation = chain.ChainStatus.Any(s =>
                s.Status == X509ChainStatusFlags.HasNotPermittedNameConstraint
                || s.Status == X509ChainStatusFlags.HasExcludedNameConstraint
                || s.Status == X509ChainStatusFlags.InvalidNameConstraints
                || s.Status == X509ChainStatusFlags.HasNotSupportedNameConstraint);
            await Assert.That(ncViolation).IsTrue()
                .Because($"chain failed but not for a Name Constraints reason; got: {statuses}");
        }
        finally
        {
            try { Directory.Delete(outputDir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Test]
    public async Task Sha256FingerprintFormatIsColonHexUpperCase()
    {
        // Pure unit test of the FormatSha256Fingerprint helper against a fixed input. SHA-256
        // of an empty byte array is the canonical empty-hash value documented in NIST FIPS
        // 180-4 test vectors. The formatted output must match `openssl dgst -sha256` style
        // exactly (uppercase, colon-separated) so a human comparing fingerprints sees the
        // same characters in both tools.
        var hash = SHA256.HashData(Array.Empty<byte>());
        var formatted = CertificatePhase.FormatSha256Fingerprint(hash);

        await Assert.That(formatted).IsEqualTo(
            "E3:B0:C4:42:98:FC:1C:14:9A:FB:F4:C8:99:6F:B9:24:27:AE:41:E4:64:9B:93:4C:A4:95:99:1B:78:52:B8:55");
    }

    [Test]
    public async Task FingerprintFileWrittenInColonHexUpperCase()
    {
        // End-to-end check on the new rootCA.sha256.txt artefact: it must exist next to
        // rootCA.crt after the certs phase runs, hold the canonical colon-hex SHA-256 of the
        // root cert's DER bytes, and match what `FormatSha256Fingerprint(SHA256.HashData(...))`
        // would compute live. PrintTrustInfo / TrustController both depend on this format.
        var outputDir = MakeScratchDir();
        try
        {
            var (config, secrets) = MakeInputs();
            var options = OptionsFor(outputDir);
            var logger = new PhaseLogger(options);

            await CertificatePhase.RunAsync(options, config, secrets, logger, CancellationToken.None);

            var fingerprintPath = Path.Combine(outputDir, "certs", "rootCA.sha256.txt");
            await Assert.That(File.Exists(fingerprintPath)).IsTrue()
                .Because("rootCA.sha256.txt must be written alongside rootCA.crt");

            var fingerprint = (await File.ReadAllTextAsync(fingerprintPath)).Trim();

            // 32 hex pairs + 31 colon separators = 95 characters.
            await Assert.That(fingerprint.Length).IsEqualTo(95)
                .Because($"colon-hex SHA-256 must be 95 chars; got '{fingerprint}' (len={fingerprint.Length})");
            await Assert.That(Regex.IsMatch(fingerprint, "^([0-9A-F]{2}:){31}[0-9A-F]{2}$")).IsTrue()
                .Because($"fingerprint must match the uppercase colon-hex pattern; got: '{fingerprint}'");

            using var cert = LoadCert(Path.Combine(outputDir, "certs", "rootCA.crt"));
            var expected = CertificatePhase.FormatSha256Fingerprint(SHA256.HashData(cert.RawData));
            await Assert.That(fingerprint).IsEqualTo(expected)
                .Because("the file contents must equal the live hash of the published root CA cert");
        }
        finally
        {
            try { Directory.Delete(outputDir, recursive: true); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// Parses the DER-encoded Name Constraints extension value (OID 2.5.29.30) and returns
    /// the dNSName entries inside permittedSubtrees. Only handles the shape
    /// <c>CertificatePhase.BuildNameConstraintsExtension</c> emits (single permittedSubtrees,
    /// dNSName-only, no excludedSubtrees) so a deliberately narrow parser is fine here.
    /// </summary>
    private static List<string> ParseDnsPermittedSubtrees(byte[] rawExtensionValue)
    {
        var reader = new AsnReader(rawExtensionValue, AsnEncodingRules.DER);
        var nameConstraints = reader.ReadSequence();
        var permitted = nameConstraints.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 0));
        var names = new List<string>();
        while (permitted.HasData)
        {
            var subtree = permitted.ReadSequence();
            var dnsName = subtree.ReadCharacterString(
                UniversalTagNumber.IA5String,
                new Asn1Tag(TagClass.ContextSpecific, 2));
            names.Add(dnsName);
        }
        return names;
    }
}
