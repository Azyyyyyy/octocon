extern alias AppHost;

using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using Interfold.DatabaseBootstrap;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Aspire;

namespace Interfold.IntegrationTests.TestServices;

/// <summary>
/// TUnit.Aspire fixture that manages a 7-node multi-DC ScyllaDB cluster via the AppHost.
/// Used only for topology verification tests — not for API integration testing.
/// Opt-in via OCTOCON_RUN_MULTI_NODE=true environment variable.
/// </summary>
/// <remarks>
/// The AppHost no longer ships the <c>pg-bootstrap-auth</c> / <c>scylla-bootstrap-auth</c>
/// init containers — that work moved into
/// <see cref="Interfold.Bootstrapper.Phases.DatabaseInitPhase"/>. This fixture drives the
/// same shared <see cref="PostgresSeeder"/> / <see cref="ScyllaSeeder"/> via the in-process
/// driver adapters in <see cref="DbInitHelper"/>. The seed targets the first node
/// (<c>scylla-nam</c>); Scylla replicates <c>system_auth.roles</c> across the cluster
/// automatically.
/// </remarks>
public sealed class MultiNodeScyllaFixture : AspireFixture<AppHost::Projects.Interfold_AppHost>
{
    /// <summary>Process-wide singleton, set during <see cref="InitializeAsync"/>.</summary>
    internal static MultiNodeScyllaFixture? Instance { get; private set; }

    public bool IsAvailable { get; private set; }

    /// <summary>Host port for the first Scylla node's CQL endpoint (scylla-nam).</summary>
    public int ScyllaPort { get; private set; }

    /// <summary>Multi-node tests are opt-in; set OCTOCON_RUN_MULTI_NODE=true to enable.</summary>
    private static bool IsMultiNodeEnabled =>
        string.Equals(Environment.GetEnvironmentVariable("OCTOCON_RUN_MULTI_NODE"), "true", StringComparison.OrdinalIgnoreCase);

    protected override string[] Args =>
    [
        "Parameters:scylla-mode=multi",
        "Parameters:include-api=false",
        "Parameters:include-web=false",
        "Parameters:persistent-containers=false",
        "Ports:postgres=34200",
        "Ports:scylla=39042",
        $"Parameters:postgres-user={TestDbCredentials.PostgresAppUser}",
        $"Parameters:postgres-password={TestDbCredentials.PostgresAppPassword}",
        $"Parameters:postgres-init-password={TestDbCredentials.PostgresInitPassword}",
        $"Parameters:scylla-user={TestDbCredentials.ScyllaAppUser}",
        $"Parameters:scylla-password={TestDbCredentials.ScyllaAppPassword}",
        "Parameters:encryption-private-key=TEST"
    ];

    // Multi-node clusters take longer to form
    protected override TimeSpan ResourceTimeout => TimeSpan.FromMinutes(5);
    protected override bool EnableTelemetryCollection => false;

    public override async Task InitializeAsync()
    {
        if (!IsMultiNodeEnabled)
        {
            IsAvailable = false;
            return;
        }

        await base.InitializeAsync();
    }

    protected override async Task WaitForResourcesAsync(DistributedApplication app, CancellationToken cancellationToken)
    {
        var notifications = app.Services.GetRequiredService<ResourceNotificationService>();

        string[] regions = ["nam", "eur", "sam", "sas", "eas", "ocn", "gdpr"];
        foreach (var region in regions)
        {
            await notifications.WaitForResourceAsync($"scylla-{region}", KnownResourceStates.Running, cancellationToken);
        }

        await notifications.WaitForResourceAsync("msg-db", KnownResourceStates.Running, cancellationToken);

        // Resolve allocated ports. Seed against the first node — Scylla propagates
        // system_auth changes across the cluster via gossip.
        var pgEndpoint = App.GetEndpoint("msg-db", "postgres");
        var scEndpoint = App.GetEndpoint("scylla-nam", "cql");

        var baseConn =
            $"Host={pgEndpoint.Host};Port={pgEndpoint.Port};" +
            $"Username={DbInitHelper.PostgresInitUser};Password={TestDbCredentials.PostgresInitPassword};" +
            "Database=postgres;SSL Mode=Disable";
        await DbInitHelper.WaitForPostgresAsync(baseConn, cancellationToken);
        await DbInitHelper.SeedPostgresAsync(baseConn, BuildPostgresSeedOptions(scEndpoint), cancellationToken);

        await DbInitHelper.WaitForScyllaAsync(scEndpoint.Host, scEndpoint.Port, cancellationToken);
        await DbInitHelper.SeedScyllaAsync(scEndpoint.Host, scEndpoint.Port, BuildScyllaSeedOptions(), cancellationToken);

        ScyllaPort = scEndpoint.Port;
        IsAvailable = true;
        Instance = this;
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
            ScrambleInitUserPassword: false);

    private static ScyllaSeedOptions BuildScyllaSeedOptions()
        => new(
            AppUser: TestDbCredentials.ScyllaAppUser,
            AppPassword: TestDbCredentials.ScyllaAppPassword,
            AdminUser: TestDbCredentials.ScyllaAdminUser,
            AdminPassword: TestDbCredentials.ScyllaAdminPassword,
            LockDefaultCassandra: true);
}
