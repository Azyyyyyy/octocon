using TUnit.Core.Interfaces;

namespace Interfold.IntegrationTests.TestServices;

/// <summary>
/// Fixture chain that creates an <see cref="InterfoldWebApplicationFactory"/> backed by
/// a Cassandra 5 cluster managed by <see cref="CassandraFixture"/>.
/// TUnit resolves the dependency automatically via <see cref="ClassDataSourceAttribute{T}"/>.
/// </summary>
public sealed class CassandraWebFactoryFixture : IWebFactoryFixture, IAsyncInitializer
{
    [ClassDataSource<CassandraFixture>(Shared = SharedType.PerTestSession)]
    public required CassandraFixture Aspire { get; init; }

    public InterfoldWebApplicationFactory Factory { get; private set; } = null!;

    public Task InitializeAsync()
    {
        Factory = new InterfoldWebApplicationFactory("scylla-postgres", "cassandra")
            .WithConfiguration("OCTOCON_POSTGRES_CONNECTION", Aspire.PostgresConnectionString)
            .WithConfiguration("OCTOCON_SCYLLA_CONTACT_POINTS", "127.0.0.1")
            .WithConfiguration("OCTOCON_SCYLLA_PORT", Aspire.CassandraPort.ToString())
            .WithConfiguration("OCTOCON_SINGLE_SCYLLA_INSTANCE", "true")
            .WithConfiguration("OCTOCON_SCYLLA_KEYSPACE", "nam")
            .WithConfiguration("OCTOCON_DB_RETRY_ATTEMPTS", "10")
            .WithConfiguration("OCTOCON_DB_RETRY_INITIAL_DELAY_MS", "500")
            .WithConfiguration("OCTOCON_DB_RETRY_MAX_DELAY_MS", "3000");
        return Task.CompletedTask;
    }
}
