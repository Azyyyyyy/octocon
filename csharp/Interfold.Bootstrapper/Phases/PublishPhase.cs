using Aspire.Hosting;
using Interfold.AppHostGraph;
using Interfold.Bootstrapper.Cli;
using Interfold.Bootstrapper.Configuration;
using Microsoft.Extensions.Configuration;

namespace Interfold.Bootstrapper.Phases;

/// <summary>
/// Phase 5 — invokes the embedded <see cref="DistributedApplication"/> in publish mode to emit
/// <c>docker-compose.yaml</c> + <c>.env</c> using the resource graph defined by
/// <c>InterfoldAppHost.Configure</c>. Generated secrets are injected through
/// <see cref="IConfiguration"/> so they end up in the <c>.env</c> file instead of leaking into
/// the compose YAML.
/// </summary>
internal static class PublishPhase
{
    /// <summary>
    /// Subdirectory name (two levels deep, created at runtime) used to anchor the relative bind-mount
    /// paths in <c>InterfoldAppHost.Configure</c>. The graph uses paths like
    /// <c>../../scripts/...</c> that resolve against the process CWD. Setting CWD to
    /// <c>{appDir}/_aspire_anchor/inner</c> makes <c>../../scripts</c> point at <c>{appDir}/scripts</c>,
    /// matching the dev layout (<c>csharp/Interfold.AppHost/../../scripts</c>).
    /// </summary>
    private static readonly string[] AnchorSegments = ["_aspire_anchor", "inner"];

    public static async Task RunAsync(
        BootstrapOptions options,
        BootstrapConfig config,
        GeneratedSecrets secrets,
        PhaseLogger logger,
        CancellationToken ct)
    {
        const string Phase = "publish";
        logger.PhaseStart(Phase);

        Directory.CreateDirectory(options.OutputDir);

        var anchor = SetupAnchor(logger);
        var previousCwd = Directory.GetCurrentDirectory();

        try
        {
            Directory.SetCurrentDirectory(anchor);

            await PublishInProcessAsync(options, config, secrets, logger, ct).ConfigureAwait(false);
        }
        finally
        {
            Directory.SetCurrentDirectory(previousCwd);
        }

        var composePath = Path.Combine(options.OutputDir, "docker-compose.yaml");
        if (!File.Exists(composePath))
        {
            // Aspire >=13 sometimes emits to a subdirectory keyed by the environment name.
            // Look one level deeper before giving up.
            var nested = Directory.EnumerateFiles(options.OutputDir, "docker-compose.yaml", SearchOption.AllDirectories).FirstOrDefault();
            if (nested is null)
            {
                logger.PhaseFail(Phase, "compose-not-emitted");
                throw new InvalidOperationException(
                    $"Aspire publish completed but no docker-compose.yaml was produced under {options.OutputDir}.");
            }
            logger.Info($"    compose emitted at {nested}");
            composePath = nested;
        }

        // Aspire 13.x emits .env with empty values for every parameter and bind-mount source,
        // expecting the operator to fill them in before `docker compose up`. The bootstrapper IS
        // the operator, so we rewrite the file in place with the secrets we generated and the
        // bind-mount sources we anchored on the install directory.
        var envPath = Path.Combine(Path.GetDirectoryName(composePath)!, ".env");
        FillEnvFile(envPath, AppContext.BaseDirectory, options.OutputDir, config, secrets, logger);

        logger.PhaseDone(Phase);
    }

    /// <summary>
    /// Pure, side-effect-free computation of the values we expect to substitute into the
    /// Aspire-emitted <c>.env</c>. Returns two dictionaries:
    /// <list type="bullet">
    ///   <item><c>Parameters</c> — keyed by the upper-snake-cased parameter name Aspire writes
    ///         (e.g. <c>POSTGRES_USER</c>), mapped to the secret/config value.</item>
    ///   <item><c>BindMounts</c> — keyed by the <c>service:container-target</c> identifier
    ///         emitted in the <c># Bind mount source for ...</c> comment above each bind-mount
    ///         placeholder, mapped to the absolute host path to substitute in.</item>
    /// </list>
    /// Splitting this out from <see cref="FillEnvFile"/> keeps the IO loop minimal and lets the
    /// unit-test project assert the full key set without staging a real <c>.env</c> on disk.
    /// </summary>
    internal sealed record EnvReplacements(
        IReadOnlyDictionary<string, string> Parameters,
        IReadOnlyDictionary<string, string> BindMounts);

    /// <summary>
    /// Translates the operator-facing <see cref="BootstrapConfig.DatabaseMode"/> enum into the
    /// orthogonal AppHost toggles that <c>InterfoldAppHost</c> reads. Keeping the enum in the
    /// operator-facing config lets <c>interfold.bootstrap.json</c> stay one value wide, while
    /// the resource graph stays driven by the same independent flags the integration tests use
    /// to spin Scylla and Cassandra side-by-side. <see cref="ConfigPhase.Validate"/> has already
    /// rejected any value outside this switch in normal flows; the throw guards internal callers
    /// (notably the unit tests) from silently bypassing validation.
    /// </summary>
    internal static (string IncludeScylla, string IncludeCassandra, string ScyllaTopology) TranslateDatabaseMode(
        string databaseMode) => databaseMode switch
        {
            "single" => ("true", "false", "single"),
            "multi" => ("true", "false", "multi"),
            "cassandra" => ("false", "true", "single"),
            _ => throw new InvalidOperationException(
                $"Unhandled databaseMode '{databaseMode}'. Expected: single | multi | cassandra."),
        };

    internal static EnvReplacements BuildEnvReplacements(
        BootstrapConfig config,
        GeneratedSecrets secrets,
        string baseDir,
        string outputDir)
    {
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // Parameter values - match keys to the parameter names declared in
            // InterfoldAppHost.Configure() (Aspire upper-snake-cases the parameter name).
            ["POSTGRES_USER"] = secrets.PostgresUser,
            ["POSTGRES_PASSWORD"] = secrets.PostgresPassword,
            // Init credential consumed exactly once by DatabaseInitPhase. We deliberately ship
            // the *initial* value here (not the scrambled post-init value) so that operators who
            // nuke their pgdata volume and rerun the bootstrap have a working bootstrap path:
            // the value matches what initdb will set on the recreated db_init role, the
            // bootstrapper uses it once, then scrambles it again in-cluster.
            ["POSTGRES_INIT_PASSWORD"] = secrets.PostgresInitPassword,
            ["SCYLLA_USER"] = secrets.ScyllaUser,
            ["SCYLLA_PASSWORD"] = secrets.ScyllaPassword,
            // SCYLLA_ADMIN_PASSWORD is intentionally absent - the admin role is created by the
            // bootstrapper's DatabaseInitPhase with a fresh random password that lives only in
            // internal.secrets, never in the compose .env.
            ["ENCRYPTION_PRIVATE_KEY"] = secrets.EncryptionPrivateKeyB64,
            // The encryption pepper, OAuth client secrets, JWT signing keys, deep-link HMAC
            // secret, and leaf PFX password all live in internal.secrets exclusively (seeded
            // by DatabaseInitPhase). They are not surfaced as compose env vars and the API
            // reads them through SecretsBootstrapService / a one-shot Npgsql query at
            // startup.
        };

        // Bind-mount source resolution. Aspire emits a comment of the form
        //   # Bind mount source for <service>:<container-target-path>
        // immediately above each bind-mount placeholder. We use that "service:target" key to
        // look up the absolute host path. If Aspire renames or re-orders these in a future
        // release, this map needs to track the change - we'd notice via the test suite.
        // The lookup mirrors the WithBindMount() calls in InterfoldAppHost.Configure().
        var bindMountLookup = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // Bootstrapper-issued TLS material lives under {outputDir}/certs/. Kestrel inside
            // the API container reads leaf.pfx via the path env var set in ConfigureApiSelfHostEnv.
            // The JWT signing PEMs that used to live under secrets/keys/ are now part of
            // internal.secrets (see SeedKeys.cs) — no bind mount needed.
            ["interfold-api:/certs"] = Path.Combine(outputDir, "certs"),
        };

        // Scylla rackdc.properties bind mount is region-keyed. Single mode uses one node named
        // "scylla" with the "nam" region (default); multi mode emits one node per region.
        // The region list mirrors InterfoldAppHost.Configure().
        string[] scyllaRegions = string.Equals(config.DatabaseMode, "multi", StringComparison.OrdinalIgnoreCase)
            ? ["nam", "eur", "sam", "sas", "eas", "ocn", "gdpr"]
            : ["nam"];
        foreach (var region in scyllaRegions)
        {
            var nodeName = scyllaRegions.Length > 1 ? $"scylla-{region}" : "scylla";
            bindMountLookup[$"{nodeName}:/etc/scylla/cassandra-rackdc.properties"] =
                Path.Combine(baseDir, "db", "scylla", $"cassandra-rackdc.{region}.properties");
        }

        return new EnvReplacements(parameters, bindMountLookup);
    }

    /// <summary>
    /// Rewrites the Aspire-emitted <c>.env</c> with concrete values for every secret parameter
    /// and bind-mount source placeholder. Unknown keys are left untouched so future additions in
    /// <see cref="InterfoldAppHost.Configure"/> degrade gracefully (the operator will see the
    /// blank key and we'll know to teach this method about it).
    /// </summary>
    private static void FillEnvFile(
        string envPath,
        string baseDir,
        string outputDir,
        BootstrapConfig config,
        GeneratedSecrets secrets,
        PhaseLogger logger)
    {
        if (!File.Exists(envPath))
        {
            logger.Warn($".env not found at {envPath}; skipping value rewrite");
            return;
        }

        var replacements = BuildEnvReplacements(config, secrets, baseDir, outputDir);
        var (rewritten, skipped) = ApplyReplacementsToEnvFile(envPath, replacements);

        logger.Info($"    .env: filled {rewritten} value(s)");
        if (skipped.Count > 0)
        {
            logger.Warn($".env: {skipped.Count} key(s) left blank: {string.Join(", ", skipped)}");
        }
    }

    /// <summary>
    /// Reads <paramref name="envPath"/>, applies <paramref name="replacements"/> in place, and
    /// rewrites the file. Returns the number of rewritten lines plus the list of <c>KEY=</c>
    /// entries left blank because we had no replacement for them. Internal because the unit-test
    /// project drives it directly against an in-memory .env to exercise the comment-pair logic.
    /// </summary>
    internal static (int Rewritten, IReadOnlyList<string> Skipped) ApplyReplacementsToEnvFile(
        string envPath, EnvReplacements replacements)
    {
        var lines = File.ReadAllLines(envPath);
        string? pendingBindMountKey = null;
        var rewritten = 0;
        var skipped = new List<string>();
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];

            // Track the most recent comment so we can pair it with the following `KEY=` line.
            if (line.StartsWith("# Bind mount source for ", StringComparison.Ordinal))
            {
                pendingBindMountKey = line["# Bind mount source for ".Length..].Trim();
                continue;
            }

            // KEY=VALUE substitution. Aspire writes blank RHS in publish mode.
            var eq = line.IndexOf('=');
            if (eq <= 0)
            {
                pendingBindMountKey = null;
                continue;
            }

            var key = line[..eq];
            string? value = null;

            if (pendingBindMountKey is not null && replacements.BindMounts.TryGetValue(pendingBindMountKey, out var src))
            {
                value = src;
            }
            else if (replacements.Parameters.TryGetValue(key, out var paramVal))
            {
                value = paramVal;
            }

            if (value is not null)
            {
                lines[i] = $"{key}={value}";
                rewritten++;
            }
            else if (line.EndsWith('=') && !key.StartsWith("DISCORD_", StringComparison.Ordinal))
            {
                // A blank value that we did NOT recognise. Surface it - operator may need to fix.
                // Discord secret is optional in the bootstrap config so we intentionally leave it blank.
                skipped.Add(key);
            }

            pendingBindMountKey = null;
        }

        File.WriteAllLines(envPath, lines);
        return (rewritten, skipped);
    }

    private static string SetupAnchor(PhaseLogger logger)
    {
        // AppContext.BaseDirectory is where the published binary lives (e.g. /opt/interfold-bootstrap/).
        // We expect operators to deploy `db/scylla/cassandra-rackdc.*.properties` alongside the binary
        // in the release tarball. The internal.secrets schema is owned by DatabaseInitPhase, so the
        // bootstrap-auth shell scripts and the 000_create_secrets_table.sql bind mount are no longer
        // required on disk - the bootstrapper executes the equivalent SQL/CQL directly via
        // `docker compose exec` against the running postgres/scylla containers.
        var baseDir = AppContext.BaseDirectory;
        var anchor = Path.Combine(baseDir, Path.Combine(AnchorSegments));
        Directory.CreateDirectory(anchor);

        string[] required =
        [
            Path.Combine(baseDir, "db", "scylla", "cassandra-rackdc.nam.properties"),
        ];
        var missing = required.Where(p => !File.Exists(p)).ToList();
        if (missing.Count > 0)
        {
            logger.Warn(
                "expected support files missing under the bootstrapper directory: " +
                string.Join(", ", missing.Select(p => Path.GetRelativePath(baseDir, p))));
            logger.Warn(
                "Compose publish will succeed but `docker compose up` will fail until these files exist.");
        }

        return anchor;
    }

    private static async Task PublishInProcessAsync(
        BootstrapOptions options,
        BootstrapConfig config,
        GeneratedSecrets secrets,
        PhaseLogger logger,
        CancellationToken ct)
    {
        // Aspire 13.x publish CLI passes `--operation publish --step publish --output-path X`
        // (see src/Aspire.Cli/Commands/PublishCommand.cs). Without `--step publish` the AppHost
        // only emits aspire-manifest.json and then blocks waiting for the CLI backchannel to
        // dispatch the next step, which never arrives because we are not running under the CLI.
        // The docker-compose publisher is selected by the registered AddDockerComposeEnvironment
        // in InterfoldAppHost.Configure, not by an explicit `--publisher` arg in 13.x.
        var publishArgs = new[]
        {
            "--operation", "publish",
            "--step", "publish",
            "--output-path", options.OutputDir,
        };

        var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions
        {
            Args = publishArgs,
            // Headless: no dashboard, no browser launch - we just want the artifact emitter to run and exit.
            DisableDashboard = true,
        });

        var (includeScylla, includeCassandra, scyllaTopology) = TranslateDatabaseMode(config.DatabaseMode);

        // Inject all Parameters:* into the in-memory config layer. The graph's AddParameter() calls and
        // default-password guards read from IConfiguration; once published, Aspire writes secret parameter
        // values into the sibling .env file rather than embedding them in the compose YAML.
        var injected = new Dictionary<string, string?>
        {
            ["Parameters:postgres-user"] = secrets.PostgresUser,
            ["Parameters:postgres-password"] = secrets.PostgresPassword,
            ["Parameters:postgres-init-password"] = secrets.PostgresInitPassword,
            ["Parameters:scylla-user"] = secrets.ScyllaUser,
            ["Parameters:scylla-password"] = secrets.ScyllaPassword,
            ["Parameters:encryption-private-key"] = secrets.EncryptionPrivateKeyB64,
            // The encryption pepper and OAuth client secrets used to be Aspire parameters
            // too, but the API now reads them from internal.secrets exclusively (see
            // SeedKeys / SecretsBootstrapService), so we no longer inject them into the
            // AppHost's in-memory config.
            ["Parameters:include-scylla"] = includeScylla,
            ["Parameters:include-cassandra"] = includeCassandra,
            ["Parameters:scylla-topology"] = scyllaTopology,
            // The bootstrapper never builds the API from source — point Aspire at the pre-built image
            // so it emits a compose service referencing that tag directly. See InterfoldAppHost.Configure
            // for how this switches off the AddProject<> code path.
            ["Parameters:api-image"] = config.ApiImage,
            // Self-hosting stacks don't need the Aspire dev dashboard - it would pull an MCR-nightly
            // image at compose-up time which is inappropriate for production deployments.
            ["Parameters:include-dashboard"] = "false",
            // Disable the optional web container for self-hosting by default; operators can flip it via config later.
            ["Parameters:include-web"] = "false",
            ["Ports:postgres"] = config.Ports.Postgres.ToString(),
            ["Ports:scylla"] = config.Ports.Scylla.ToString(),
            ["Ports:api-http"] = config.Ports.ApiHttp.ToString(),
            ["Ports:api-https"] = config.Ports.ApiHttps.ToString(),
            ["Ports:web-http"] = config.Ports.WebHttp.ToString(),
            ["Ports:web-https"] = config.Ports.WebHttps.ToString(),
        };
        builder.Configuration.AddInMemoryCollection(injected);

        InterfoldAppHost.Configure(builder);

        logger.Info("    invoking Aspire publish...");
        await using var app = builder.Build();
        await app.RunAsync(ct).ConfigureAwait(false);
    }
}
