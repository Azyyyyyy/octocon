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
        Factory = new InterfoldWebApplicationFactory("inmemory")
            .WithConfiguration("OCTOCON_DEEPLINK_ADDRESS", "octocon://app")
            .WithConfiguration("OCTOCON_SCYLLA_KEYSPACE", "nam");
        return Task.CompletedTask;
    }
}
