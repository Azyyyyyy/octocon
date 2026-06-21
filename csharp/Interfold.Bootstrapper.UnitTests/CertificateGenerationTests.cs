using System.Security.Cryptography.X509Certificates;
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
}
