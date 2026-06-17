using TUnit.Core.Interfaces;

namespace Interfold.IntegrationTests.TestServices;

/// <summary>
/// Fixture chain that creates an <see cref="InterfoldWebApplicationFactory"/> backed by the
/// shared single-node ScyllaDB cluster managed by <see cref="SharedDbFixture"/>. TUnit
/// resolves the dependency automatically via <see cref="ClassDataSourceAttribute{T}"/>.
/// </summary>
/// <remarks>
/// Because this fixture references <see cref="SharedDbFixture"/>, the
/// <see cref="RequiredFixtures.DiscoverRequiredFixtures"/> hook flips
/// <see cref="RequiredFixtures.NeedScylla"/> on for the session, which in turn flips the
/// <c>include-scylla</c> Aspire parameter on. The <c>ScyllaPort</c> property is therefore
/// always populated when this fixture's <see cref="InitializeAsync"/> runs.
/// </remarks>
public sealed class ScyllaWebFactoryFixture : IWebFactoryFixture, IAsyncInitializer
{
    [ClassDataSource<SharedDbFixture>(Shared = SharedType.PerTestSession)]
    public required SharedDbFixture Aspire { get; init; }

    public InterfoldWebApplicationFactory Factory { get; private set; } = null!;

    public Task InitializeAsync()
    {
        Factory = new InterfoldWebApplicationFactory("scylla-postgres", "scylla-single-node")
            .WithConfiguration("OCTOCON_POSTGRES_CONNECTION", Aspire.PostgresConnectionString)
            .WithConfiguration("OCTOCON_SCYLLA_PORT", Aspire.ScyllaPort!.Value.ToString())
            .WithConfiguration("OCTOCON_SINGLE_SCYLLA_INSTANCE", "true")
            .WithConfiguration("OCTOCON_DB_RETRY_ATTEMPTS", "10")
            .WithConfiguration("OCTOCON_DB_RETRY_INITIAL_DELAY_MS", "500")
            .WithConfiguration("OCTOCON_DB_RETRY_MAX_DELAY_MS", "3000");
        return Task.CompletedTask;
    }
}
