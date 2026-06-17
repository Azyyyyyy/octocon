extern alias AppHost;

using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using Interfold.Contracts.Configuration;
using Interfold.Contracts.Secrets;
using Interfold.DatabaseBootstrap;
using Interfold.Infrastructure.Postgres;
using Interfold.Infrastructure.Scylla;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using TUnit.Aspire;

namespace Interfold.IntegrationTests.TestServices;

/// <summary>
/// Single Aspire host that backs every DB-bound integration test in the suite. Replaces the
/// previous per-fixture hosts (<c>SingleNodeScyllaFixture</c>, <c>CassandraFixture</c>) with
/// one shared launch so cold-start, seeding, and migrations only run once per test session.
/// </summary>
/// <remarks>
/// <para>
/// Conditionally provisions resources based on which <see cref="IWebFactoryFixture"/>
/// implementations the current test session actually references (discovered ahead of time by
/// <see cref="RequiredFixtures.DiscoverRequiredFixtures"/>). Postgres always runs because the
/// shared <c>internal.secrets</c> table is the API's source of truth even for the in-memory
/// persistence path. Scylla and Cassandra only spin up when at least one selected test class
/// references their respective fixture — for an InMemory-only run this fixture isn't even
/// instantiated and Docker is never touched.
/// </para>
/// <para>
/// Migrations against each enabled CQL backend run once here (after seeding) via the static
/// <c>MigrateAsync</c> entry points on <see cref="ScyllaMigrationService"/> and
/// <see cref="PostgresMigrationService"/>. The per-test
/// <see cref="InterfoldWebApplicationFactory"/> strips those hosted services from its host
/// builder so the same migrations don't run again on every factory build.
/// </para>
/// </remarks>
public sealed class SharedDbFixture : AspireFixture<AppHost::Projects.Interfold_AppHost>
{
    /// <summary>Postgres connection string resolved after startup. Always populated.</summary>
    public string PostgresConnectionString { get; private set; } = string.Empty;

    /// <summary>
    /// Host port for the ScyllaDB CQL endpoint, or <c>null</c> when the discovery hook
    /// determined no test in the current run uses the Scylla fixture chain.
    /// </summary>
    public int? ScyllaPort { get; private set; }

    /// <summary>
    /// Host port for the Cassandra CQL endpoint, or <c>null</c> when the discovery hook
    /// determined no test in the current run uses the Cassandra fixture chain.
    /// </summary>
    public int? CassandraPort { get; private set; }

    protected override string[] Args => BuildArgs();

    private static string[] BuildArgs()
    {
        // Toggle each container based on which fixture types the current test session
        // references. The [Before(HookType.TestDiscovery)] hook on BaseEndpointTest has
        // populated RequiredFixtures by the time AspireFixture.InitializeAsync (the only
        // caller of this Args getter) runs.
        var args = new List<string>
        {
            "Parameters:include-api=false",
            "Parameters:include-web=false",
            "Parameters:persistent-containers=false",
            "Parameters:include-postgres=true",
            $"Parameters:include-scylla={(RequiredFixtures.NeedScylla ? "true" : "false")}",
            $"Parameters:include-cassandra={(RequiredFixtures.NeedCassandra ? "true" : "false")}",
            "Ports:postgres=14200",
            "Ports:scylla=19042",
            "Ports:cassandra=19043",
            $"Parameters:postgres-user={TestDbCredentials.PostgresAppUser}",
            $"Parameters:postgres-password={TestDbCredentials.PostgresAppPassword}",
            // db_init bootstrap superuser password. Pinning a deterministic value here keeps
            // the test process and DbInitHelper aligned without having to read back the
            // GenerateParameterDefault output from the AppHost service provider.
            $"Parameters:postgres-init-password={TestDbCredentials.PostgresInitPassword}",
            $"Parameters:scylla-user={TestDbCredentials.ScyllaAppUser}",
            $"Parameters:scylla-password={TestDbCredentials.ScyllaAppPassword}",
            "Parameters:encryption-private-key=TEST",
        };
        return args.ToArray();
    }

    protected override TimeSpan ResourceTimeout => TimeSpan.FromMinutes(5);
    protected override bool EnableTelemetryCollection => false;

    // The AppHost only registers TCP healthchecks against `localhost:<hard-coded port>` when
    // `include-api=true`, so test mode has no Aspire-level health checks at all. The default
    // `AllHealthy` behaviour would also try to drive parameter resources (postgres-user, etc.)
    // through health gates - those never report "Healthy" and would hang the wait. We own the
    // readiness logic in <see cref="WaitForResourcesAsync"/> below (waiting on the actual
    // container resources to reach `Running` via `ResourceNotificationService`), so we tell
    // the base class not to do its own pre-flight wait.
    protected override ResourceWaitBehavior WaitBehavior => ResourceWaitBehavior.None;

    protected override async Task WaitForResourcesAsync(DistributedApplication app, CancellationToken cancellationToken)
    {
        var notifications = app.Services.GetRequiredService<ResourceNotificationService>();

        // Wait for Running (not Healthy) — Aspire testing randomises host ports, so the
        // hardcoded-port health checks declared in AppHost never pass in test mode.
        await notifications.WaitForResourceAsync("msg-db", KnownResourceStates.Running, cancellationToken);
        if (RequiredFixtures.NeedScylla)
            await notifications.WaitForResourceAsync("scylla", KnownResourceStates.Running, cancellationToken);
        if (RequiredFixtures.NeedCassandra)
            await notifications.WaitForResourceAsync("cassandra", KnownResourceStates.Running, cancellationToken);

        // Resolve actual allocated ports (Aspire randomises them in test mode).
        var pgEndpoint = App.GetEndpoint("msg-db", "postgres");
        var scyllaEndpoint = RequiredFixtures.NeedScylla ? App.GetEndpoint("scylla", "cql") : null;
        var cassandraEndpoint = RequiredFixtures.NeedCassandra ? App.GetEndpoint("cassandra", "cql") : null;

        // Postgres bootstrap. Pick the first available CQL endpoint (or a placeholder when
        // neither backend is enabled — the seeder writes scylla connection details into
        // internal.secrets, but those rows are unused by InMemory-only test paths).
        var primaryCqlEndpoint = scyllaEndpoint ?? cassandraEndpoint;

        var initConnectionString =
            $"Host={pgEndpoint.Host};Port={pgEndpoint.Port};" +
            $"Username={DbInitHelper.PostgresInitUser};Password={TestDbCredentials.PostgresInitPassword};" +
            "Database=postgres;SSL Mode=Disable";
        await DbInitHelper.WaitForPostgresAsync(initConnectionString, cancellationToken);
        await DbInitHelper.SeedPostgresAsync(
            initConnectionString,
            BuildPostgresSeedOptions(primaryCqlEndpoint),
            cancellationToken);

        PostgresConnectionString =
            $"Host={pgEndpoint.Host};Port={pgEndpoint.Port};" +
            $"Username={TestDbCredentials.PostgresAppUser};Password={TestDbCredentials.PostgresAppPassword};" +
            "Database=octocon;SSL Mode=Disable;Maximum Pool Size=5";

        // Verify Postgres is actually reachable as the app user from the host before the
        // migration runner connects. Docker Desktop on Windows can delay host port forwarding
        // even after the container reports healthy and we want a hard failure here rather
        // than during the first test.
        await WaitForPostgresConnectivityAsync(PostgresConnectionString, cancellationToken);

        // Run the Postgres migrations once for the whole session. The per-test
        // InterfoldWebApplicationFactory strips PostgresMigrationService from its host so this
        // is the single migration pass against msg-db for the entire run.
        var persistenceConfig = new PersistenceConfiguration
        {
            Mode = "scylla-postgres",
            PostgresConnectionString = PostgresConnectionString,
            IsSingleScyllaInstance = true,
            ScyllaKeyspace = "nam",
        };
        var connectionFactory = new PostgresConnectionFactory(persistenceConfig);
        var secretsStore = new PostgresSecretsStore(connectionFactory);

        await PostgresMigrationService.MigrateAsync(
            persistenceConfig,
            secretsStore,
            NullLoggerFactory.Instance.CreateLogger<PostgresMigrationService>(),
            cancellationToken);

        if (scyllaEndpoint is not null)
        {
            await DbInitHelper.WaitForScyllaAsync(scyllaEndpoint.Host, scyllaEndpoint.Port, cancellationToken);
            await DbInitHelper.SeedScyllaAsync(
                scyllaEndpoint.Host, scyllaEndpoint.Port,
                BuildScyllaSeedOptions(),
                cancellationToken);
            await RunScyllaMigrationsAsync(scyllaEndpoint, persistenceConfig, secretsStore, cancellationToken);
            ScyllaPort = scyllaEndpoint.Port;
        }

        if (cassandraEndpoint is not null)
        {
            await DbInitHelper.WaitForScyllaAsync(cassandraEndpoint.Host, cassandraEndpoint.Port, cancellationToken);
            await DbInitHelper.SeedScyllaAsync(
                cassandraEndpoint.Host, cassandraEndpoint.Port,
                BuildScyllaSeedOptions(),
                cancellationToken);
            await RunScyllaMigrationsAsync(cassandraEndpoint, persistenceConfig, secretsStore, cancellationToken);
            CassandraPort = cassandraEndpoint.Port;
        }
    }

    private static async Task RunScyllaMigrationsAsync(
        Uri cqlEndpoint,
        PersistenceConfiguration persistenceConfig,
        ISecretsStore secretsStore,
        CancellationToken cancellationToken)
    {
        // ScyllaConfigResolver reads contact points + port from IConfiguration when the keys
        // are present; we feed it the host-mapped endpoint so the migration runs against the
        // exact CQL listener the API will hit during the test.
        var migrationConfig = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OCTOCON_SCYLLA_CONTACT_POINTS"] = cqlEndpoint.Host,
                ["OCTOCON_SCYLLA_PORT"] = cqlEndpoint.Port.ToString(),
                ["OCTOCON_SCYLLA_KEYSPACE"] = "nam",
            })
            .Build();

        await ScyllaMigrationService.MigrateAsync(
            persistenceConfig,
            secretsStore,
            migrationConfig,
            NullLoggerFactory.Instance.CreateLogger<ScyllaMigrationService>(),
            cancellationToken);
    }

    private static PostgresSeedOptions BuildPostgresSeedOptions(Uri? cqlEndpoint)
        => new(
            InitUser: DbInitHelper.PostgresInitUser,
            InitPassword: TestDbCredentials.PostgresInitPassword,
            AppUser: TestDbCredentials.PostgresAppUser,
            AppPassword: TestDbCredentials.PostgresAppPassword,
            AdminUser: TestDbCredentials.PostgresAdminUser,
            AdminPassword: TestDbCredentials.PostgresAdminPassword,
            DefaultDatabase: DbInitHelper.DefaultPostgresDb,
            GoogleOAuthClientSecret: "TEST",
            DiscordOAuthClientSecret: "TEST",
            AppleOAuthClientSecret: "TEST",
            EncryptionPepper: "TEST",
            // The API process runs on the test host and reaches scylla/cassandra via the host
            // port mapping, so contact_points carries the resolved endpoint host. When neither
            // backend is enabled (InMemory-only sessions never hit this path because
            // SharedDbFixture isn't constructed), fall back to localhost so the seeded value
            // stays well-formed.
            ScyllaContactPoints: cqlEndpoint?.Host ?? "127.0.0.1",
            ScyllaLocalDatacenter: "nam",
            ScyllaAppUser: TestDbCredentials.ScyllaAppUser,
            ScyllaAppPassword: TestDbCredentials.ScyllaAppPassword,
            ScyllaPort: cqlEndpoint?.Port ?? 9042,
            ScyllaAdminUser: TestDbCredentials.ScyllaAdminUser,
            ScyllaAdminPassword: TestDbCredentials.ScyllaAdminPassword,
            JwtRsa256PrivateKeyPem: TestDbCredentials.JwtRsa256PrivateKeyPem,
            JwtEs256PrivateKeyPem: TestDbCredentials.JwtEs256PrivateKeyPem,
            DeepLinkSecret: TestDbCredentials.DeepLinkSecret,
            LeafPfxPassword: TestDbCredentials.LeafPfxPassword,
            // Tests want db_init's password stable across idempotent reruns within the same
            // fixture session, so we never scramble it. Production callers always set true.
            ScrambleInitUserPassword: false);

    private static ScyllaSeedOptions BuildScyllaSeedOptions()
        => new(
            AppUser: TestDbCredentials.ScyllaAppUser,
            AppPassword: TestDbCredentials.ScyllaAppPassword,
            AdminUser: TestDbCredentials.ScyllaAdminUser,
            AdminPassword: TestDbCredentials.ScyllaAdminPassword,
            LockDefaultCassandra: true);

    private static async Task WaitForPostgresConnectivityAsync(string connectionString, CancellationToken ct)
    {
        for (var attempt = 1; attempt <= 20; attempt++)
        {
            try
            {
                await using var conn = new NpgsqlConnection(connectionString);
                await conn.OpenAsync(ct);
                return;
            }
            catch (Exception) when (attempt < 20 && !ct.IsCancellationRequested)
            {
                await Task.Delay(1000, ct);
            }
        }
    }
}
