using System.Collections.Concurrent;
using Octocon.Domain.Abstractions;

namespace Octocon.Domain.InMemory;

public sealed class InMemoryRegionalAggregateVersionStore : IAggregateVersionStore
{
    private readonly IRegionContext _regionContext;
    private readonly ConcurrentDictionary<string, long> _versions = new();
    private readonly object _sync = new();

    public InMemoryRegionalAggregateVersionStore(IRegionContext regionContext)
    {
        _regionContext = regionContext;
    }

    public Task<long?> GetVersionAsync(
        string aggregateType,
        string aggregateId,
        CancellationToken cancellationToken = default
    )
    {
        var key = BuildKey(aggregateType, aggregateId);
        var exists = _versions.TryGetValue(key, out var value);
        return Task.FromResult<long?>(exists ? value : null);
    }

    public Task<bool> TryAdvanceVersionAsync(
        string aggregateType,
        string aggregateId,
        long? expectedVersion,
        CancellationToken cancellationToken = default
    )
    {
        var key = BuildKey(aggregateType, aggregateId);

        lock (_sync)
        {
            _versions.TryGetValue(key, out var current);

            if (expectedVersion is not null && current != expectedVersion.Value)
            {
                return Task.FromResult(false);
            }

            _versions[key] = current + 1;
            return Task.FromResult(true);
        }
    }

    private string BuildKey(string aggregateType, string aggregateId)
    {
        var targetRegion = _regionContext.ResolveUserRegion(aggregateId);
        var consistency = _regionContext.ResolveConsistency(targetRegion);
        var scope = string.Equals(consistency, "local", StringComparison.OrdinalIgnoreCase)
            ? targetRegion
            : "global";

        return $"{scope}:{aggregateType}:{aggregateId}";
    }
}