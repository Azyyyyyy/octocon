using Interfold.Domain.Alters;

namespace Interfold.Infrastructure.Persistence.Bootstrap;

public interface IDatabaseBootstrapHealthChecker
{
    Task<DatabaseBootstrapHealthResult> CheckAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Extended health checker that validates not just schema, but operational readiness of guarded paths.
/// </summary>
public interface IOperationalHealthChecker
{
    /// <summary>
    /// Validates that guarded visibility query paths work end-to-end.
    /// </summary>
    Task<OperationalHealthResult> CheckGuardedPathsAsync(CancellationToken cancellationToken = default);
}

public sealed record DatabaseStoreHealth(string Store, bool Healthy, string Message);

public sealed record DatabaseBootstrapHealthResult(bool Healthy, IReadOnlyList<DatabaseStoreHealth> Stores);

public sealed record GuardedPathHealth(string Path, bool Healthy, string Message);

public sealed record OperationalHealthResult(bool Healthy, IReadOnlyList<GuardedPathHealth> Paths);

public sealed class InMemoryBootstrapHealthChecker : IDatabaseBootstrapHealthChecker, IOperationalHealthChecker
{
    private readonly IAlterRepository _alterRepository;

    public InMemoryBootstrapHealthChecker(IAlterRepository alterRepository)
    {
        _alterRepository = alterRepository;
    }

    public Task<DatabaseBootstrapHealthResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<DatabaseStoreHealth> stores =
        [
            new DatabaseStoreHealth("inmemory", true, "In-memory mode does not require external schema bootstrap.")
        ];

        return Task.FromResult(new DatabaseBootstrapHealthResult(true, stores));
    }

    /// <summary>
    /// Validates that guarded visibility query paths work end-to-end with in-memory repository.
    /// </summary>
    public async Task<OperationalHealthResult> CheckGuardedPathsAsync(CancellationToken cancellationToken = default)
    {
        var paths = new List<GuardedPathHealth>();

        // Test 1: ListGuardedAsync returns results
        try
        {
            var testSystemId = "test-system";
            var testCallerId = "test-caller";

            // Note: Expected to return empty list since no test data, but verifies path works
            var alters = await _alterRepository.ListGuardedAsync(testSystemId, testCallerId, cancellationToken);

            paths.Add(new GuardedPathHealth(
                "ListGuardedAsync",
                true,
                $"Guarded list query executed successfully (returned {alters.Count} alters)"));
        }
        catch (Exception ex)
        {
            paths.Add(new GuardedPathHealth(
                "ListGuardedAsync",
                false,
                $"Guarded list query failed: {ex.Message}"));
        }

        // Test 2: GetGuardedAsync handles missing entity gracefully
        try
        {
            var testSystemId = "test-system";
            const int testAlterId = 999;
            var testCallerId = "test-caller";

            // Note: Expected to return null, but verifies path works without exception
            var alter = await _alterRepository.GetGuardedAsync(testSystemId, testAlterId, testCallerId, cancellationToken);

            paths.Add(new GuardedPathHealth(
                "GetGuardedAsync",
                true,
                alter == null ? "Guarded get query executed successfully (entity not found, as expected)" : "Guarded get query executed successfully"));
        }
        catch (Exception ex)
        {
            paths.Add(new GuardedPathHealth(
                "GetGuardedAsync",
                false,
                $"Guarded get query failed: {ex.Message}"));
        }

        var healthy = paths.All(p => p.Healthy);
        return new OperationalHealthResult(healthy, paths);
    }
}
