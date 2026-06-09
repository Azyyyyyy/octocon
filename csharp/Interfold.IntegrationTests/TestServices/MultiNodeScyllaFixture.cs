extern alias AppHost;

using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Aspire;

namespace Interfold.IntegrationTests.TestServices;

/// <summary>
/// TUnit.Aspire fixture that manages a 7-node multi-DC ScyllaDB cluster via the AppHost.
/// Used only for topology verification tests — not for API integration testing.
/// Opt-in via OCTOCON_RUN_MULTI_NODE=true environment variable.
/// </summary>
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
        string[] terminalStates = [KnownResourceStates.Finished, KnownResourceStates.Exited];
        await notifications.WaitForResourceAsync("pg-bootstrap-auth", terminalStates, cancellationToken);
        await notifications.WaitForResourceAsync("scylla-bootstrap-auth", terminalStates, cancellationToken);

        ScyllaPort = App.GetEndpoint("scylla-nam", "cql").Port;
        IsAvailable = true;
        Instance = this;
    }
}
