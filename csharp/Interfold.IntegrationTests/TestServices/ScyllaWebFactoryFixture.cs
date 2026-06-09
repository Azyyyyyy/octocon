using TUnit.Core.Interfaces;

namespace Interfold.IntegrationTests.TestServices;

/// <summary>
/// Fixture chain that creates an <see cref="InterfoldWebApplicationFactory"/> backed by
/// a single-node ScyllaDB cluster managed by <see cref="SingleNodeScyllaFixture"/>.
/// TUnit resolves the dependency automatically via <see cref="ClassDataSourceAttribute{T}"/>.
/// </summary>
public sealed class ScyllaWebFactoryFixture : IWebFactoryFixture, IAsyncInitializer
{
    [ClassDataSource<SingleNodeScyllaFixture>(Shared = SharedType.PerTestSession)]
    public required SingleNodeScyllaFixture Aspire { get; init; }

    public InterfoldWebApplicationFactory Factory { get; private set; } = null!;

    public Task InitializeAsync()
    {
        Factory = new InterfoldWebApplicationFactory("scylla-postgres", "scylla-single-node")
            .WithConfiguration("OCTOCON_POSTGRES_CONNECTION", Aspire.PostgresConnectionString)
            .WithConfiguration("OCTOCON_SCYLLA_CONTACT_POINTS", "127.0.0.1")
            .WithConfiguration("OCTOCON_SCYLLA_PORT", Aspire.ScyllaPort.ToString())
            .WithConfiguration("OCTOCON_SINGLE_SCYLLA_INSTANCE", "true")
            .WithConfiguration("OCTOCON_SCYLLA_KEYSPACE", "nam")
            .WithConfiguration("OCTOCON_DB_RETRY_ATTEMPTS", "10")
            .WithConfiguration("OCTOCON_DB_RETRY_INITIAL_DELAY_MS", "500")
            .WithConfiguration("OCTOCON_DB_RETRY_MAX_DELAY_MS", "3000");
        return Task.CompletedTask;
    }
}
