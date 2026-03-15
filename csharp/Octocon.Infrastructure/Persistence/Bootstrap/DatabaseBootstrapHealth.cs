namespace Octocon.Infrastructure.Persistence.Bootstrap;

public interface IDatabaseBootstrapHealthChecker
{
    Task<DatabaseBootstrapHealthResult> CheckAsync(CancellationToken cancellationToken = default);
}

public sealed record DatabaseStoreHealth(string Store, bool Healthy, string Message);

public sealed record DatabaseBootstrapHealthResult(bool Healthy, IReadOnlyList<DatabaseStoreHealth> Stores);

public sealed class InMemoryBootstrapHealthChecker : IDatabaseBootstrapHealthChecker
{
    public Task<DatabaseBootstrapHealthResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<DatabaseStoreHealth> stores =
        [
            new DatabaseStoreHealth("inmemory", true, "In-memory mode does not require external schema bootstrap.")
        ];

        return Task.FromResult(new DatabaseBootstrapHealthResult(true, stores));
    }
}
