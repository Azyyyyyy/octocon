using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Interfold.Bootstrapper.Cli;
using Interfold.Bootstrapper.Configuration;
using Interfold.Bootstrapper.Util;

namespace Interfold.Bootstrapper.Phases;

/// <summary>
/// Phase 4 — generates a root CA and a leaf TLS certificate (with SANs) for the configured domains,
/// emits <c>.crt</c>/<c>.key</c>/<c>.pfx</c> files, and installs the root CA into the system trust
/// store. All-C# port of <c>scripts/create-certs.sh</c>.
/// </summary>
internal static partial class CertificatePhase
{
    private const string CertsRelativeDir = "certs";
    private const string DebianAnchorsDir = "/usr/local/share/ca-certificates";
    private const string RedHatAnchorsDir = "/etc/pki/ca-trust/source/anchors";
    private const string TrustAnchorFileName = "interfold-root-ca.crt";

    public static async Task RunAsync(
        BootstrapOptions options,
        BootstrapConfig config,
        GeneratedSecrets secrets,
        PhaseLogger logger,
        CancellationToken ct)
    {
        const string Phase = "certs";
        logger.PhaseStart(Phase);

        var certsDir = Path.Combine(options.OutputDir, CertsRelativeDir);
        var rootCrtPath = Path.Combine(certsDir, "rootCA.crt");
        var leafCrtPath = Path.Combine(certsDir, "leaf.crt");
        var leafKeyPath = Path.Combine(certsDir, "leaf.key");
        var leafPfxPath = Path.Combine(certsDir, "leaf.pfx");

        var allPresent = File.Exists(rootCrtPath) && File.Exists(leafCrtPath)
                         && File.Exists(leafKeyPath) && File.Exists(leafPfxPath);
        if (allPresent && !options.RotateCerts)
        {
            logger.PhaseSkip(Phase, "already-present");
            return;
        }

        if (allPresent)
        {
            logger.Info("    --rotate-certs set: regenerating root CA + leaf");
        }

        Directory.CreateDirectory(certsDir);

        var (rootCert, rootKey) = GenerateRootCa(config.Deployment.RootCaName, config.Deployment.CertYears);
        var (leafCert, leafKey) = GenerateLeaf(rootCert, rootKey, config.Deployment.Domains, config.Deployment.CertYears);

        await PersistAsync(rootCert, rootKey, leafCert, leafKey, secrets.LeafPfxPassword,
            rootCrtPath, certsDir, leafCrtPath, leafKeyPath, leafPfxPath, ct).ConfigureAwait(false);

        // The published API container reads `/certs/leaf.pfx` through a read-only bind mount as a
        // non-root user (the .NET SDK container builds default to UID 64198). Without world-read
        // bits the container process gets EACCES on startup, so we mark the bind-mounted files
        // 0644. The PFX is password-protected, the root key never leaves the host, and the
        // directory itself should be locked down at the OS level.
        ChmodReadable(leafPfxPath, logger);
        ChmodReadable(leafCrtPath, logger);
        ChmodReadable(rootCrtPath, logger);

        rootCert.Dispose();
        leafCert.Dispose();
        rootKey.Dispose();
        leafKey.Dispose();

        if (config.Deployment.TrustStoreInstall && RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            await InstallToTrustStoreAsync(rootCrtPath, logger, ct).ConfigureAwait(false);
        }
        else
        {
            logger.Info("    skipping trust-store install (trustStoreInstall=false or non-Linux host)");
        }

        logger.PhaseDone(Phase);
    }

    private static (X509Certificate2 Cert, RSA Key) GenerateRootCa(string rootCaName, int years)
    {
        var key = RSA.Create(2048);
        var subject = new X500DistinguishedName($"CN={rootCaName}");

        var req = new CertificateRequest(subject, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        // CA with pathlen:0 — can sign leaves but not intermediates.
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(certificateAuthority: true, hasPathLengthConstraint: true, pathLengthConstraint: 0, critical: true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, critical: true));
        req.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(req.PublicKey, critical: false));

        var notBefore = DateTimeOffset.UtcNow.AddMinutes(-5);
        var notAfter = notBefore.AddYears(years);
        var cert = req.CreateSelfSigned(notBefore, notAfter);
        return (cert, key);
    }

    private static (X509Certificate2 Cert, RSA Key) GenerateLeaf(
        X509Certificate2 issuer,
        RSA issuerKey,
        IReadOnlyList<string> domains,
        int years)
    {
        var key = RSA.Create(2048);
        var primarySubject = $"CN={domains[0]}";
        var req = new CertificateRequest(new X500DistinguishedName(primarySubject), key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(certificateAuthority: false, hasPathLengthConstraint: false, pathLengthConstraint: 0, critical: true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, critical: true));
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection
            {
                new("1.3.6.1.5.5.7.3.1"), // serverAuth
            },
            critical: false));

        var sanBuilder = new SubjectAlternativeNameBuilder();
        foreach (var domain in domains)
        {
            sanBuilder.AddDnsName(domain);
        }
        req.CertificateExtensions.Add(sanBuilder.Build());

        req.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(req.PublicKey, critical: false));
        req.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(issuer, includeKeyIdentifier: true, includeIssuerAndSerial: false));

        var notBefore = DateTimeOffset.UtcNow.AddMinutes(-5);
        var requestedNotAfter = notBefore.AddYears(years);
        // The leaf's lifetime cannot extend past the issuer's. The root CA was generated a few
        // microseconds-to-seconds earlier, so its NotAfter is already slightly before
        // `requestedNotAfter`; X509 issuance rejects the cert if we don't clamp.
        // Subtract one second from the issuer cap to leave a definite ordering even when the
        // platform truncates fractional seconds at PEM export time.
        var issuerCap = new DateTimeOffset(issuer.NotAfter.ToUniversalTime()).AddSeconds(-1);
        var notAfter = requestedNotAfter > issuerCap ? issuerCap : requestedNotAfter;

        // CertificateRequest.Create needs a random serial number; use 16 cryptographic bytes.
        var serial = RandomNumberGenerator.GetBytes(16);
        // X.509 serial numbers are unsigned but the high bit must be 0 to keep the DER encoding positive.
        serial[0] &= 0x7F;

        var signed = req.Create(issuer, notBefore, notAfter, serial);
        // The cert returned by Create() doesn't carry the private key; attach it for export.
        var withKey = signed.CopyWithPrivateKey(key);
        signed.Dispose();
        return (withKey, key);
    }

    private static async Task PersistAsync(
        X509Certificate2 rootCert,
        RSA rootKey,
        X509Certificate2 leafCert,
        RSA leafKey,
        string pfxPassword,
        string rootCrtPath,
        string certsDir,
        string leafCrtPath,
        string leafKeyPath,
        string leafPfxPath,
        CancellationToken ct)
    {
        await File.WriteAllTextAsync(rootCrtPath, rootCert.ExportCertificatePem(), ct).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(certsDir, "rootCA.key"), rootKey.ExportPkcs8PrivateKeyPem(), ct).ConfigureAwait(false);
        await File.WriteAllTextAsync(leafCrtPath, leafCert.ExportCertificatePem(), ct).ConfigureAwait(false);
        await File.WriteAllTextAsync(leafKeyPath, leafKey.ExportPkcs8PrivateKeyPem(), ct).ConfigureAwait(false);

        // Export the leaf as PFX bundled with the root for full chain. AES-256 + SHA-256 matches modern
        // PFX defaults (older OpenSSL 1.x produced 3DES PFXs that some platforms now reject).
        var pfxBytes = leafCert.Export(X509ContentType.Pfx, pfxPassword);
        await File.WriteAllBytesAsync(leafPfxPath, pfxBytes, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 0644 - readable by any UID, writable only by owner. Mirrors <c>SecretsPhase.ChmodReadable</c>.
    /// Used for cert files that are bind-mounted into the API container, which runs as a
    /// non-root user and cannot read the bootstrapper's default 0600 files.
    /// </summary>
    private static void ChmodReadable(string path, PhaseLogger logger)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux) &&
            !RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return;
        }

        const int s_644 = 0x1A4; // 0o644
        var rc = NativeMethods.chmod(path, s_644);
        if (rc != 0)
        {
            var err = Marshal.GetLastPInvokeError();
            logger.Warn($"chmod({path}, 0644) failed: errno={err} (cert file written but permissions not adjusted)");
        }
    }

    private static partial class NativeMethods
    {
        [System.Runtime.InteropServices.LibraryImport("libc", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
        internal static partial int chmod(string path, int mode);
    }

    private static async Task InstallToTrustStoreAsync(string rootCrtPath, PhaseLogger logger, CancellationToken ct)
    {
        if (Directory.Exists(DebianAnchorsDir))
        {
            var dst = Path.Combine(DebianAnchorsDir, TrustAnchorFileName);
            File.Copy(rootCrtPath, dst, overwrite: true);
            var run = await ProcessRunner.RunAsync("update-ca-certificates", [], ct: ct).ConfigureAwait(false);
            if (run.ExitCode != 0)
            {
                logger.Warn($"update-ca-certificates exited {run.ExitCode}: {run.StdErr.Trim()}");
            }
            else
            {
                logger.Info("    root CA installed via update-ca-certificates");
            }
        }
        else if (Directory.Exists(RedHatAnchorsDir))
        {
            var dst = Path.Combine(RedHatAnchorsDir, TrustAnchorFileName);
            File.Copy(rootCrtPath, dst, overwrite: true);
            var run = await ProcessRunner.RunAsync("update-ca-trust", ["extract"], ct: ct).ConfigureAwait(false);
            if (run.ExitCode != 0)
            {
                logger.Warn($"update-ca-trust extract exited {run.ExitCode}: {run.StdErr.Trim()}");
            }
            else
            {
                logger.Info("    root CA installed via update-ca-trust extract");
            }
        }
        else
        {
            logger.Warn(
                "no known trust-store path found (looked for /usr/local/share/ca-certificates and " +
                "/etc/pki/ca-trust/source/anchors). Install the generated rootCA.crt manually.");
        }
    }
}
