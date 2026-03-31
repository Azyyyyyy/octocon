using System.Collections.Concurrent;
using Interfold.Domain.Abstractions;

namespace Interfold.Domain.InMemory;

public sealed class InMemoryAggregateVersionStore : IAggregateVersionStore
{
    private readonly ConcurrentDictionary<string, long> _versions = new();
    private readonly object _sync = new();

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

    private static string BuildKey(string aggregateType, string aggregateId) => $"{aggregateType}:{aggregateId}";
}