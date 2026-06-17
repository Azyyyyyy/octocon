extern alias AppHost;

using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using Interfold.DatabaseBootstrap;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using TUnit.Aspire;

namespace Interfold.IntegrationTests.TestServices;

/// <summary>
/// TUnit.Aspire fixture that manages a single-node ScyllaDB + Postgres cluster via the AppHost.
/// Used as a dependency by <see cref="ScyllaWebFactoryFixture"/> in a fixture chain.
/// </summary>
/// <remarks>
/// The AppHost no longer ships the <c>pg-bootstrap-auth</c> / <c>scylla-bootstrap-auth</c>
/// init containers — that work moved into
/// <see cref="Interfold.Bootstrapper.Phases.DatabaseInitPhase"/>, which shells out to
/// <c>docker compose exec</c> and isn't reachable from in-process tests. This fixture drives
/// the same seed orchestrator (<see cref="PostgresSeeder"/> / <see cref="ScyllaSeeder"/>)
/// against the Aspire-managed containers via the Npgsql + DataStax driver adapters in
/// <see cref="DbInitHelper"/>.
/// </remarks>
public sealed class SingleNodeScyllaFixture : AspireFixture<AppHost::Projects.Interfold_AppHost>
{
    /// <summary>Postgres connection string resolved after startup.</summary>
    public string PostgresConnectionString { get; private set; } = string.Empty;

    /// <summary>Host port for the ScyllaDB CQL endpoint.</summary>
    public int ScyllaPort { get; private set; }

    protected override string[] Args =>
    [
        "Parameters:scylla-mode=single",
        "Parameters:include-api=false",
        "Parameters:include-web=false",
        "Parameters:persistent-containers=false",
        "Ports:postgres=14200",
        "Ports:scylla=19042",
        $"Parameters:postgres-user={TestDbCredentials.PostgresAppUser}",
        $"Parameters:postgres-password={TestDbCredentials.PostgresAppPassword}",
        // db_init bootstrap superuser password. Pinning a deterministic value here keeps the
        // test process and DbInitHelper aligned without having to read back the
        // GenerateParameterDefault output from the AppHost service provider.
        $"Parameters:postgres-init-password={TestDbCredentials.PostgresInitPassword}",
        $"Parameters:scylla-user={TestDbCredentials.ScyllaAppUser}",
        $"Parameters:scylla-password={TestDbCredentials.ScyllaAppPassword}",
        "Parameters:encryption-private-key=TEST"
    ];

    protected override TimeSpan ResourceTimeout => TimeSpan.FromMinutes(5);
    protected override bool EnableTelemetryCollection => false;

    protected override async Task WaitForResourcesAsync(DistributedApplication app, CancellationToken cancellationToken)
    {
        var notifications = app.Services.GetRequiredService<ResourceNotificationService>();

        // Wait for Running (not Healthy) — Aspire testing randomizes host ports,
        // so the hardcoded-port health checks in AppHost never pass in test mode.
        await notifications.WaitForResourceAsync("msg-db", KnownResourceStates.Running, cancellationToken);
        await notifications.WaitForResourceAsync("scylla", KnownResourceStates.Running, cancellationToken);

        // Resolve actual allocated ports (Aspire randomises them in test mode).
        var pgEndpoint = App.GetEndpoint("msg-db", "postgres");
        var scEndpoint = App.GetEndpoint("scylla", "cql");

        // In-process Postgres bootstrap (replaces the removed pg-bootstrap-auth container).
        var baseConn =
            $"Host={pgEndpoint.Host};Port={pgEndpoint.Port};" +
            $"Username={DbInitHelper.PostgresInitUser};Password={TestDbCredentials.PostgresInitPassword};" +
            "Database=postgres;SSL Mode=Disable";
        await DbInitHelper.WaitForPostgresAsync(baseConn, cancellationToken);
        await DbInitHelper.SeedPostgresAsync(baseConn, BuildPostgresSeedOptions(scEndpoint), cancellationToken);

        // In-process Scylla bootstrap (replaces the removed scylla-bootstrap-auth container).
        await DbInitHelper.WaitForScyllaAsync(scEndpoint.Host, scEndpoint.Port, cancellationToken);
        await DbInitHelper.SeedScyllaAsync(scEndpoint.Host, scEndpoint.Port, BuildScyllaSeedOptions(), cancellationToken);

        // msg-db is a generic container (AddContainer), not AddPostgres, so it
        // doesn't expose a connection string. Build it from the endpoint instead.
        PostgresConnectionString =
            $"Host={pgEndpoint.Host};Port={pgEndpoint.Port};" +
            $"Username={TestDbCredentials.PostgresAppUser};Password={TestDbCredentials.PostgresAppPassword};" +
            "Database=octocon;SSL Mode=Disable;Maximum Pool Size=5";
        ScyllaPort = scEndpoint.Port;

        // Verify Postgres is actually reachable as the app user from the host before handing
        // off. Docker Desktop on Windows can delay host port forwarding even after the
        // container reports healthy, and we want a hard failure here rather than during a test.
        await WaitForPostgresConnectivityAsync(PostgresConnectionString, cancellationToken);
    }

    private static PostgresSeedOptions BuildPostgresSeedOptions(Uri scEndpoint)
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
            // The API process runs on the test host and reaches scylla via the host port
            // mapping, so contact_points carries the resolved endpoint host (typically
            // localhost) rather than the container alias the bootstrapper would use in a
            // self-hosted compose deployment.
            ScyllaContactPoints: scEndpoint.Host,
            ScyllaLocalDatacenter: "nam",
            ScyllaAppUser: TestDbCredentials.ScyllaAppUser,
            ScyllaAppPassword: TestDbCredentials.ScyllaAppPassword,
            ScyllaPort: scEndpoint.Port,
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
