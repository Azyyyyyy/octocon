using System.Collections.Concurrent;
using Interfold.Contracts.Models;
using Interfold.Domain.Abstractions.Repository;

namespace Interfold.Infrastructure.InMemory.Repository;

public sealed class InMemoryEncryptionStateRepository : IEncryptionStateRepository
{
    private readonly ConcurrentDictionary<string, EncryptionState> _states = new(StringComparer.Ordinal);

    public Task<EncryptionState?> GetAsync(string systemId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var normalizedSystemId = NormalizeSystemId(systemId);
        _states.TryGetValue(normalizedSystemId, out var state);
        return Task.FromResult(state);
    }

    public Task<bool> UpsertAsync(string systemId, bool initialized, string? keyChecksum, string? salt, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var normalizedSystemId = NormalizeSystemId(systemId);
        _states[normalizedSystemId] = new EncryptionState(initialized, keyChecksum, salt);
        return Task.FromResult(true);
    }

    private static string NormalizeSystemId(string systemId)
    {
        if (string.IsNullOrWhiteSpace(systemId))
            return systemId;

        var separator = systemId.IndexOf(':');
        if (separator <= 0 || separator >= systemId.Length - 1)
            return systemId;

        return systemId[(separator + 1)..];
    }
}
