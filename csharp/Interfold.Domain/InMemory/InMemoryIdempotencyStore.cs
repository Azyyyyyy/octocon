using System.Collections.Concurrent;
using Interfold.Domain.Abstractions;

namespace Interfold.Domain.InMemory;

public sealed class InMemoryIdempotencyStore : IIdempotencyStore
{
    private readonly ConcurrentDictionary<string, IdempotencyMatch> _store = new();

    public Task<IdempotencyMatch?> FindAsync(
        string principalId,
        string operationId,
        string idempotencyKey,
        CancellationToken cancellationToken = default
    )
    {
        var key = BuildKey(principalId, operationId, idempotencyKey);
        _store.TryGetValue(key, out var value);
        return Task.FromResult<IdempotencyMatch?>(value);
    }

    public Task SaveAsync(
        string principalId,
        string operationId,
        string idempotencyKey,
        string payloadHash,
        string outcomeHash,
        string? outcomePayload,
        CancellationToken cancellationToken = default
    )
    {
        var key = BuildKey(principalId, operationId, idempotencyKey);
        _store[key] = new IdempotencyMatch(payloadHash, outcomeHash, outcomePayload);
        return Task.CompletedTask;
    }

    private static string BuildKey(string principalId, string operationId, string idempotencyKey) =>
        $"{principalId}:{operationId}:{idempotencyKey}";
}