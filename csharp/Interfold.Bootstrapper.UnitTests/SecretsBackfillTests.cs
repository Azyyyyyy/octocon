using System.Text.Json;
using Interfold.Bootstrapper.Cli;
using Interfold.Bootstrapper.Configuration;
using Interfold.Bootstrapper.Phases;
using TUnit.Core;

namespace Interfold.Bootstrapper.UnitTests;

/// <summary>
/// Covers the backfill logic in <see cref="SecretsPhase.RunAsync"/>. A pre-existing
/// <c>secrets.json</c> written by an older bootstrapper version may be missing keys that newer
/// phases require (e.g. <c>postgresInitPassword</c>, <c>postgresAdminPassword</c>,
/// <c>scyllaAdminPassword</c>, or the four JWT PEMs). The phase must detect the missing keys,
/// mint replacements, and persist the result while leaving other already-populated fields alone.
/// </summary>
public sealed class SecretsBackfillTests
{
    private static BootstrapOptions OptionsFor(string outputDir, bool rotateSecrets = false) => new(
        Command: BootstrapCommand.Bootstrap,
        ConfigPath: null,
        OutputDir: outputDir,
        SkipPrereqs: true,
        RotateSecrets: rotateSecrets,
        RotateCerts: false,
        NonInteractive: true,
        FaultInject: null,
        PrintPhaseStatus: false);

    /// <summary>Creates an isolated scratch output directory and returns its path.</summary>
    private static string MakeScratchDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "interfold-secrets-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>Writes <paramref name="secrets"/> as JSON to the canonical location under <paramref name="outputDir"/>.</summary>
    private static async Task StagePriorSecretsAsync(string outputDir, GeneratedSecrets secrets)
    {
        var secretsDir = Path.Combine(outputDir, "secrets");
        Directory.CreateDirectory(secretsDir);
        var path = Path.Combine(secretsDir, "secrets.json");
        var json = JsonSerializer.Serialize(secrets, BootstrapJsonContext.Default.GeneratedSecrets);
        await File.WriteAllTextAsync(path, json);
    }

    private static async Task<GeneratedSecrets> LoadPersistedAsync(string outputDir)
    {
        var path = Path.Combine(outputDir, "secrets", "secrets.json");
        var json = await File.ReadAllTextAsync(path);
        return JsonSerializer.Deserialize(json, BootstrapJsonContext.Default.GeneratedSecrets)!;
    }

    [Test]
    public async Task BackfillsMissingPostgresInitPassword()
    {
        var outputDir = MakeScratchDir();
        try
        {
            // Old-format secrets.json: every other field populated, but PostgresInitPassword absent.
            var prior = SecretsPhase.Generate();
            prior.PostgresInitPassword = string.Empty;
            await StagePriorSecretsAsync(outputDir, prior);

            var options = OptionsFor(outputDir);
            var logger = new PhaseLogger(options);
            var result = await SecretsPhase.RunAsync(options, new BootstrapConfig(), logger, CancellationToken.None);

            await Assert.That(result.PostgresInitPassword).IsNotEmpty();
            // The fixture is persisted back to disk; reload to confirm the mutation survived the round-trip.
            var reloaded = await LoadPersistedAsync(outputDir);
            await Assert.That(reloaded.PostgresInitPassword).IsEqualTo(result.PostgresInitPassword);
            // Other fields must be left alone — the backfill is surgical, not a regeneration.
            await Assert.That(reloaded.PostgresPassword).IsEqualTo(prior.PostgresPassword);
        }
        finally
        {
            try { Directory.Delete(outputDir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Test]
    public async Task BackfillsMissingPostgresAdminPassword()
    {
        var outputDir = MakeScratchDir();
        try
        {
            var prior = SecretsPhase.Generate();
            prior.PostgresAdminPassword = string.Empty;
            await StagePriorSecretsAsync(outputDir, prior);

            var options = OptionsFor(outputDir);
            var logger = new PhaseLogger(options);
            var result = await SecretsPhase.RunAsync(options, new BootstrapConfig(), logger, CancellationToken.None);

            await Assert.That(result.PostgresAdminPassword).IsNotEmpty();
            var reloaded = await LoadPersistedAsync(outputDir);
            await Assert.That(reloaded.PostgresAdminPassword).IsEqualTo(result.PostgresAdminPassword);
            await Assert.That(reloaded.PostgresPassword).IsEqualTo(prior.PostgresPassword);
        }
        finally
        {
            try { Directory.Delete(outputDir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Test]
    public async Task BackfillsMissingScyllaAdminPassword()
    {
        var outputDir = MakeScratchDir();
        try
        {
            var prior = SecretsPhase.Generate();
            prior.ScyllaAdminPassword = string.Empty;
            await StagePriorSecretsAsync(outputDir, prior);

            var options = OptionsFor(outputDir);
            var logger = new PhaseLogger(options);
            var result = await SecretsPhase.RunAsync(options, new BootstrapConfig(), logger, CancellationToken.None);

            await Assert.That(result.ScyllaAdminPassword).IsNotEmpty();
            var reloaded = await LoadPersistedAsync(outputDir);
            await Assert.That(reloaded.ScyllaAdminPassword).IsEqualTo(result.ScyllaAdminPassword);
            await Assert.That(reloaded.ScyllaPassword).IsEqualTo(prior.ScyllaPassword);
        }
        finally
        {
            try { Directory.Delete(outputDir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Test]
    public async Task RotateSecretsPreservesLeafPfxPassword()
    {
        // RotateSecrets regenerates DB / encryption credentials but must keep the leaf PFX
        // password in lock-step with the on-disk leaf.pfx (which only changes on rotate-certs).
        // Rotating just the password would invalidate Kestrel's PFX load without delivering any
        // real security benefit.
        var outputDir = MakeScratchDir();
        try
        {
            var prior = SecretsPhase.Generate();
            prior.LeafPfxPassword = "well-known-pfx-password-do-not-rotate-on-secrets-rotate";
            await StagePriorSecretsAsync(outputDir, prior);

            var options = OptionsFor(outputDir, rotateSecrets: true);
            var logger = new PhaseLogger(options);
            var result = await SecretsPhase.RunAsync(options, new BootstrapConfig(), logger, CancellationToken.None);

            await Assert.That(result.LeafPfxPassword).IsEqualTo(prior.LeafPfxPassword);
            // DB credentials, in contrast, MUST have changed - rotate-secrets is the rotate path.
            await Assert.That(result.PostgresPassword).IsNotEqualTo(prior.PostgresPassword);
            await Assert.That(result.ScyllaPassword).IsNotEqualTo(prior.ScyllaPassword);
        }
        finally
        {
            try { Directory.Delete(outputDir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Test]
    public async Task RerunDoesNotEmitKeysDirectory()
    {
        // Inverse of the legacy invariant: after migrating JWT signing material into
        // internal.secrets, SecretsPhase must NEVER emit standalone PEM files on disk. If a
        // future change accidentally reintroduces the keys/ directory, the API container's
        // bind mount also has to come back — this assertion fails first.
        var outputDir = MakeScratchDir();
        try
        {
            var prior = SecretsPhase.Generate();
            await StagePriorSecretsAsync(outputDir, prior);

            var options = OptionsFor(outputDir);
            var logger = new PhaseLogger(options);
            await SecretsPhase.RunAsync(options, new BootstrapConfig(), logger, CancellationToken.None);

            var keysDir = Path.Combine(outputDir, "secrets", "keys");
            await Assert.That(Directory.Exists(keysDir)).IsFalse()
                .Because("JWT PEMs live in internal.secrets exclusively; no keys/ dir should appear.");
        }
        finally
        {
            try { Directory.Delete(outputDir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Test]
    public async Task BackfillsMissingDeepLinkSecret()
    {
        var outputDir = MakeScratchDir();
        try
        {
            var prior = SecretsPhase.Generate();
            prior.DeepLinkSecret = string.Empty;
            await StagePriorSecretsAsync(outputDir, prior);

            var options = OptionsFor(outputDir);
            var logger = new PhaseLogger(options);
            var result = await SecretsPhase.RunAsync(options, new BootstrapConfig(), logger, CancellationToken.None);

            await Assert.That(result.DeepLinkSecret).IsNotEmpty();
            var reloaded = await LoadPersistedAsync(outputDir);
            await Assert.That(reloaded.DeepLinkSecret).IsEqualTo(result.DeepLinkSecret);
            await Assert.That(reloaded.PostgresPassword).IsEqualTo(prior.PostgresPassword);
        }
        finally
        {
            try { Directory.Delete(outputDir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Test]
    public async Task BackfillsMissingJwtRsaPrivatePem()
    {
        var outputDir = MakeScratchDir();
        try
        {
            var prior = SecretsPhase.Generate();
            prior.JwtRsa256PrivateKeyPem = string.Empty;
            prior.JwtRsa256PublicKeyPem = string.Empty;
            await StagePriorSecretsAsync(outputDir, prior);

            var options = OptionsFor(outputDir);
            var logger = new PhaseLogger(options);
            var result = await SecretsPhase.RunAsync(options, new BootstrapConfig(), logger, CancellationToken.None);

            await Assert.That(result.JwtRsa256PrivateKeyPem).Contains("-----BEGIN");
            await Assert.That(result.JwtRsa256PublicKeyPem).Contains("-----BEGIN");
            // ES256 keys were already populated; backfill must not touch them.
            await Assert.That(result.JwtEs256PrivateKeyPem).IsEqualTo(prior.JwtEs256PrivateKeyPem);
        }
        finally
        {
            try { Directory.Delete(outputDir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Test]
    public async Task BackfillsMissingJwtEs256PrivatePem()
    {
        var outputDir = MakeScratchDir();
        try
        {
            var prior = SecretsPhase.Generate();
            prior.JwtEs256PrivateKeyPem = string.Empty;
            prior.JwtEs256PublicKeyPem = string.Empty;
            await StagePriorSecretsAsync(outputDir, prior);

            var options = OptionsFor(outputDir);
            var logger = new PhaseLogger(options);
            var result = await SecretsPhase.RunAsync(options, new BootstrapConfig(), logger, CancellationToken.None);

            await Assert.That(result.JwtEs256PrivateKeyPem).Contains("-----BEGIN");
            await Assert.That(result.JwtEs256PublicKeyPem).Contains("-----BEGIN");
            await Assert.That(result.JwtRsa256PrivateKeyPem).IsEqualTo(prior.JwtRsa256PrivateKeyPem);
        }
        finally
        {
            try { Directory.Delete(outputDir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Test]
    public async Task RotateSecretsRegeneratesJwtAndDeepLink()
    {
        // Rotate-secrets must roll the JWT signing keypair and the deep-link HMAC secret —
        // they're the highest-value bearer material in the system after the encryption pepper.
        var outputDir = MakeScratchDir();
        try
        {
            var prior = SecretsPhase.Generate();
            await StagePriorSecretsAsync(outputDir, prior);

            var options = OptionsFor(outputDir, rotateSecrets: true);
            var logger = new PhaseLogger(options);
            var result = await SecretsPhase.RunAsync(options, new BootstrapConfig(), logger, CancellationToken.None);

            await Assert.That(result.JwtRsa256PrivateKeyPem).IsNotEqualTo(prior.JwtRsa256PrivateKeyPem);
            await Assert.That(result.JwtEs256PrivateKeyPem).IsNotEqualTo(prior.JwtEs256PrivateKeyPem);
            await Assert.That(result.DeepLinkSecret).IsNotEqualTo(prior.DeepLinkSecret);
        }
        finally
        {
            try { Directory.Delete(outputDir, recursive: true); } catch { /* best effort */ }
        }
    }
}
