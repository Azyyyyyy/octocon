using TUnit.Core.Interfaces;

namespace Interfold.IntegrationTests.TestServices;

/// <summary>
/// Standalone fixture (no Aspire, no Docker) that creates an in-memory WebApplicationFactory.
/// Always available — used as the baseline for all integration tests.
/// </summary>
public sealed class InMemoryWebFactoryFixture : IWebFactoryFixture, IAsyncInitializer
{
    public InterfoldWebApplicationFactory Factory { get; private set; } = null!;

    public Task InitializeAsync()
    {
        // OCTOCON_SCYLLA_KEYSPACE defaults to "nam" via PersistenceConfiguration.ScyllaKeyspace,
        // so an explicit override here is redundant. Leave the factory at production defaults.
        Factory = new InterfoldWebApplicationFactory("inmemory");
        return Task.CompletedTask;
    }
}
