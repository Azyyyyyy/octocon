extern alias AppHost;

using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using TUnit.Aspire;

namespace Interfold.IntegrationTests.TestServices;

/// <summary>
/// TUnit.Aspire fixture that manages a single-node ScyllaDB + Postgres cluster via the AppHost.
/// Used as a dependency by <see cref="ScyllaWebFactoryFixture"/> in a fixture chain.
/// </summary>
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
        "Parameters:postgres-user=test_pg_user",
        "Parameters:postgres-password=test_pg_pw_789!Secure",
        "Parameters:scylla-user=test_app_user",
        "Parameters:scylla-password=test_secure_pw_123!Safe",
        "Parameters:scylla-admin-password=test_admin_pw_456!Strong",
        "Parameters:encryption-private-key=TEST",
        "Parameters:google-oauth-client-secret=TEST",
        "Parameters:discord-oauth-client-secret=TEST",
        "Parameters:encryption-pepper=TEST"
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

        // Bootstrap containers depend on healthy DBs and run schema + seeding.
        // Once they finish, everything is ready.
        // Bootstrap containers run a script and exit. Wait for any terminal state.
        string[] terminalStates = [KnownResourceStates.Finished, KnownResourceStates.Exited];
        await notifications.WaitForResourceAsync("pg-bootstrap-auth", terminalStates, cancellationToken);
        await notifications.WaitForResourceAsync("scylla-bootstrap-auth", terminalStates, cancellationToken);

        // Resolve actual allocated ports (may differ from configured values).
        // msg-db is a generic container (AddContainer), not AddPostgres, so it
        // doesn't expose a connection string. Build it from the endpoint instead.
        var pgEndpoint = App.GetEndpoint("msg-db", "postgres");
        PostgresConnectionString = $"Host={pgEndpoint.Host};Port={pgEndpoint.Port};Username=test_pg_user;Password=test_pg_pw_789!Secure;Database=octocon;SSL Mode=Disable;Maximum Pool Size=5";
        ScyllaPort = App.GetEndpoint("scylla", "cql").Port;

        // Verify Postgres is actually reachable from the host before handing off.
        // Docker Desktop on Windows can delay host port forwarding after container reports healthy.
        await WaitForPostgresConnectivityAsync(PostgresConnectionString, cancellationToken);
    }

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
