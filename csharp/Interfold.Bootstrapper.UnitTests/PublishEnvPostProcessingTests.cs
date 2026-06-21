using Interfold.Bootstrapper.Configuration;
using Interfold.Bootstrapper.Phases;
using TUnit.Core;

namespace Interfold.Bootstrapper.UnitTests;

/// <summary>
/// Unit tests for <see cref="PublishPhase.BuildEnvReplacements"/>. The pure function returns the
/// keys the bootstrapper plans to substitute into the Aspire-emitted <c>.env</c>; the integration
/// tests confirm that those same keys are present in the <em>real</em> emitted file, but this
/// unit-level check catches drift in either direction at sub-second speed.
///
/// After the JWT / OAuth-secret / leaf-PFX-password migration into <c>internal.secrets</c>,
/// the parameter dict shrunk from 10 to 6 keys; the subsequent addition of <c>POSTGRES_DB</c>
/// (operator-tunable application database name) bumped it back to 7. The API bind-mount set
/// dropped from two (<c>/keys</c> + <c>/certs</c>) to one (<c>/certs</c>). Single-mode total is
/// now 7 + 1 + 1 = 9.
/// </summary>
public sealed class PublishEnvPostProcessingTests
{
    private static (BootstrapConfig Config, GeneratedSecrets Secrets) MakeInputs(
        string? apiImage = null,
        string databaseMode = "single")
    {
        var config = new BootstrapConfig
        {
            DatabaseMode = databaseMode,
            ApiImage = apiImage ?? "ghcr.io/interfold/api:latest",
        };
        // OAuth client secrets flow through PostgresSeedOptions into internal.secrets;
        // they no longer appear in the env replacements. Setting them here only exercises
        // the irrelevant config path.
        config.OAuth.GoogleClientSecret = "google-secret-from-config";
        config.OAuth.DiscordClientSecret = "discord-secret-from-config";
        return (config, SecretsPhase.Generate());
    }

    [Test]
    public async Task BuildEnvReplacementsProducesAllSevenParameterKeys()
    {
        var (config, secrets) = MakeInputs();
        const string baseDir = "/var/lib/interfold";
        const string outputDir = "/srv/interfold/deploy";

        var replacements = PublishPhase.BuildEnvReplacements(config, secrets, baseDir, outputDir);

        string[] required =
        [
            "POSTGRES_USER",
            "POSTGRES_PASSWORD",
            "POSTGRES_INIT_PASSWORD",
            "POSTGRES_DB",
            "SCYLLA_USER",
            "SCYLLA_PASSWORD",
            "ENCRYPTION_PRIVATE_KEY",
        ];
        foreach (var key in required)
        {
            await Assert.That(replacements.Parameters.ContainsKey(key)).IsTrue()
                .Because($"missing parameter key '{key}' in env replacements");
            await Assert.That(replacements.Parameters[key]).IsNotEmpty()
                .Because($"parameter '{key}' must be non-empty");
        }

        // The encryption pepper, OAuth client secrets, JWT material, deep-link secret, and
        // the leaf PFX password must NOT appear here any more — they all live inside
        // internal.secrets and are loaded at startup (SecretsBootstrapService / Program.cs
        // Kestrel loader).
        await Assert.That(replacements.Parameters.ContainsKey("ENCRYPTION_PEPPER")).IsFalse();
        await Assert.That(replacements.Parameters.ContainsKey("GOOGLE_OAUTH_CLIENT_SECRET")).IsFalse();
        await Assert.That(replacements.Parameters.ContainsKey("DISCORD_OAUTH_CLIENT_SECRET")).IsFalse();
        await Assert.That(replacements.Parameters.ContainsKey("LEAF_PFX_PASSWORD")).IsFalse();
        // Admin credentials must also stay inside internal.secrets exclusively.
        await Assert.That(replacements.Parameters.ContainsKey("SCYLLA_ADMIN_PASSWORD")).IsFalse();
        await Assert.That(replacements.Parameters.ContainsKey("POSTGRES_ADMIN_PASSWORD")).IsFalse();
    }

    [Test]
    public async Task BuildEnvReplacementsCarriesPostgresDatabaseNameFromConfig()
    {
        // POSTGRES_DB is operator-tunable via BootstrapConfig.PostgresDatabase; the env
        // replacement must use the configured value verbatim so the API connection string
        // (Database=<value>) lines up with the database DatabaseInitPhase actually creates.
        var (config, secrets) = MakeInputs();
        config.PostgresDatabase = "my_custom_db";

        var replacements = PublishPhase.BuildEnvReplacements(config, secrets, "/base", "/out");

        await Assert.That(replacements.Parameters.ContainsKey("POSTGRES_DB")).IsTrue();
        await Assert.That(replacements.Parameters["POSTGRES_DB"]).IsEqualTo("my_custom_db");
    }

    [Test]
    public async Task BuildEnvReplacementsProducesAllNineKeysInSingleMode()
    {
        var (config, secrets) = MakeInputs(databaseMode: "single");
        const string baseDir = "/var/lib/interfold";
        const string outputDir = "/srv/interfold/deploy";

        var replacements = PublishPhase.BuildEnvReplacements(config, secrets, baseDir, outputDir);

        var total = replacements.Parameters.Count + replacements.BindMounts.Count;
        // Single mode: 7 parameter keys + 1 API bind mount (/certs) + 1 Scylla rackdc bind mount = 9.
        await Assert.That(total).IsEqualTo(9);
    }

    [Test]
    public async Task BindMountPathsResolveToAbsoluteUnderOutputDir()
    {
        var (config, secrets) = MakeInputs();
        var baseDir = Path.Combine(Path.GetTempPath(), "interfold-basedir");
        var outputDir = Path.Combine(Path.GetTempPath(), "interfold-outdir");

        var replacements = PublishPhase.BuildEnvReplacements(config, secrets, baseDir, outputDir);

        // The /keys bind mount was removed — JWT signing material now lives in internal.secrets.
        await Assert.That(replacements.BindMounts.ContainsKey("interfold-api:/keys")).IsFalse();

        var apiCerts = replacements.BindMounts["interfold-api:/certs"];
        await Assert.That(Path.IsPathFullyQualified(apiCerts)).IsTrue();
        await Assert.That(apiCerts).StartsWith(outputDir);

        // The single-mode Scylla rackdc mount lives under the bootstrapper's baseDir (where the
        // release tarball drops the bundled rackdc.* files), not under outputDir.
        var scyllaRackdc = replacements.BindMounts["scylla:/etc/scylla/cassandra-rackdc.properties"];
        await Assert.That(Path.IsPathFullyQualified(scyllaRackdc)).IsTrue();
        await Assert.That(scyllaRackdc).StartsWith(baseDir);
        await Assert.That(scyllaRackdc).EndsWith("cassandra-rackdc.nam.properties");
    }

    [Test]
    public async Task MultiModeAddsOneBindMountPerScyllaRegion()
    {
        var (config, secrets) = MakeInputs(databaseMode: "multi");
        var replacements = PublishPhase.BuildEnvReplacements(config, secrets,
            baseDir: "/base", outputDir: "/out");

        // Multi mode emits 7 region nodes (nam, eur, sam, sas, eas, ocn, gdpr).
        string[] regions = ["nam", "eur", "sam", "sas", "eas", "ocn", "gdpr"];
        foreach (var region in regions)
        {
            var key = $"scylla-{region}:/etc/scylla/cassandra-rackdc.properties";
            await Assert.That(replacements.BindMounts.ContainsKey(key)).IsTrue()
                .Because($"missing bind mount for region {region}");
            await Assert.That(replacements.BindMounts[key]).EndsWith($"cassandra-rackdc.{region}.properties");
        }
    }

    [Test]
    public async Task TranslateDatabaseModeSingleProducesScyllaSingleTopology()
    {
        // single mode is the default and what most installs run. PublishPhase wires the trio
        // straight into the AppHost config, so this assertion pins the mapping byte-for-byte.
        var (includeScylla, includeCassandra, topology) = PublishPhase.TranslateDatabaseMode("single");

        await Assert.That(includeScylla).IsEqualTo("true");
        await Assert.That(includeCassandra).IsEqualTo("false");
        await Assert.That(topology).IsEqualTo("single");
    }

    [Test]
    public async Task TranslateDatabaseModeMultiProducesScyllaMultiTopology()
    {
        // multi mode keeps Cassandra off and flips topology to multi - this is the only
        // route to the 7-region Scylla layout from the bootstrapper.
        var (includeScylla, includeCassandra, topology) = PublishPhase.TranslateDatabaseMode("multi");

        await Assert.That(includeScylla).IsEqualTo("true");
        await Assert.That(includeCassandra).IsEqualTo("false");
        await Assert.That(topology).IsEqualTo("multi");
    }

    [Test]
    public async Task TranslateDatabaseModeCassandraSwapsBackends()
    {
        // cassandra mode is the only configuration that disables Scylla entirely.
        // Topology stays "single" because the cassandra branch in InterfoldAppHost
        // ignores topology, but emitting "single" keeps the parameter set well-formed.
        var (includeScylla, includeCassandra, topology) = PublishPhase.TranslateDatabaseMode("cassandra");

        await Assert.That(includeScylla).IsEqualTo("false");
        await Assert.That(includeCassandra).IsEqualTo("true");
        await Assert.That(topology).IsEqualTo("single");
    }

    [Test]
    public async Task TranslateDatabaseModeRejectsUnknownValue()
    {
        // ConfigPhase.Validate is the operator-facing rejection point, but the helper
        // also throws so internal callers that bypass validation surface a clear error
        // instead of silently emitting an empty parameter set.
        var ex = Assert.Throws<InvalidOperationException>(() => PublishPhase.TranslateDatabaseMode("triple"));

        await Assert.That(ex.Message).Contains("databaseMode");
        await Assert.That(ex.Message).Contains("triple");
    }

    [Test]
    public async Task WebHttpsOffDoesNotAddOctoconWebBindMounts()
    {
        var (config, secrets) = MakeInputs();
        config.Deployment.WebHttps = false;
        var replacements = PublishPhase.BuildEnvReplacements(config, secrets, "/base", "/out");

        await Assert.That(replacements.BindMounts.ContainsKey("octocon-web:/certs")).IsFalse()
            .Because("octocon-web:/certs must only appear when deployment.webHttps=true");
        await Assert.That(replacements.BindMounts.ContainsKey(
            "octocon-web:/etc/nginx/templates/default.conf.template")).IsFalse()
            .Because("nginx template bind mount must only appear when deployment.webHttps=true");
    }

    [Test]
    public async Task WebHttpsOnAddsCertsAndNginxTemplateBindMounts()
    {
        var (config, secrets) = MakeInputs();
        config.Deployment.WebHttps = true;
        var baseDir = Path.Combine(Path.GetTempPath(), "interfold-basedir");
        var outputDir = Path.Combine(Path.GetTempPath(), "interfold-outdir");

        var replacements = PublishPhase.BuildEnvReplacements(config, secrets, baseDir, outputDir);

        // /certs is shared with the API: both services bind-mount {outputDir}/certs/, so the leaf
        // PFX (read by Kestrel) and the leaf CRT/KEY (read by nginx) come from the same on-disk
        // material rotated by CertificatePhase.
        await Assert.That(replacements.BindMounts.ContainsKey("octocon-web:/certs")).IsTrue();
        var webCerts = replacements.BindMounts["octocon-web:/certs"];
        await Assert.That(Path.IsPathFullyQualified(webCerts)).IsTrue();
        await Assert.That(webCerts).StartsWith(outputDir);
        await Assert.That(replacements.BindMounts["interfold-api:/certs"]).IsEqualTo(webCerts)
            .Because("API and web tiers must read from the same on-disk certs directory");

        // The nginx template lives next to the bootstrapper binary (baseDir), not under outputDir,
        // mirroring how cassandra-rackdc.* properties are shipped.
        const string nginxKey = "octocon-web:/etc/nginx/templates/default.conf.template";
        await Assert.That(replacements.BindMounts.ContainsKey(nginxKey)).IsTrue();
        var nginxTemplate = replacements.BindMounts[nginxKey];
        await Assert.That(Path.IsPathFullyQualified(nginxTemplate)).IsTrue();
        await Assert.That(nginxTemplate).StartsWith(baseDir);
        await Assert.That(nginxTemplate).EndsWith("default.conf.template");
    }

    [Test]
    public async Task ApiImageOverrideDoesNotLeakIntoEnvReplacements()
    {
        // The API container image flows through Aspire's `Parameters:api-image` configuration
        // (which Aspire bakes into the compose YAML as a `image:` line), NOT through the .env.
        // This test pins that contract: changing config.ApiImage must not change the env
        // replacement keys or values — only the compose YAML.
        var (configA, secretsA) = MakeInputs(apiImage: "ghcr.io/interfold/api:v1.2.3");
        var (configB, secretsB) = MakeInputs(apiImage: "private-registry.example.com/api:custom-tag");
        // Pin secrets so the only difference is ApiImage.
        secretsB.PostgresPassword = secretsA.PostgresPassword;
        secretsB.PostgresInitPassword = secretsA.PostgresInitPassword;
        secretsB.PostgresAdminPassword = secretsA.PostgresAdminPassword;
        secretsB.ScyllaPassword = secretsA.ScyllaPassword;
        secretsB.ScyllaAdminPassword = secretsA.ScyllaAdminPassword;
        secretsB.EncryptionPrivateKeyB64 = secretsA.EncryptionPrivateKeyB64;

        var a = PublishPhase.BuildEnvReplacements(configA, secretsA, "/base", "/out");
        var b = PublishPhase.BuildEnvReplacements(configB, secretsB, "/base", "/out");

        // Every key on both sides should map to the same value.
        await Assert.That(a.Parameters.Count).IsEqualTo(b.Parameters.Count);
        foreach (var kv in a.Parameters)
        {
            await Assert.That(b.Parameters.ContainsKey(kv.Key)).IsTrue();
            await Assert.That(b.Parameters[kv.Key]).IsEqualTo(kv.Value);
        }

        // And no key named anything image-y appears in either set.
        foreach (var key in a.Parameters.Keys)
        {
            await Assert.That(key.Contains("IMAGE", StringComparison.OrdinalIgnoreCase)).IsFalse()
                .Because($"unexpected image-related key '{key}' leaked into env replacements");
        }
    }
}
