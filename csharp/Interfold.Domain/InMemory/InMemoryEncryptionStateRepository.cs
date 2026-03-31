using System.Collections.Concurrent;
using Interfold.Domain.Settings;

namespace Interfold.Domain.InMemory;

public sealed class InMemoryEncryptionStateRepository : IEncryptionStateRepository
{
    private readonly ConcurrentDictionary<string, EncryptionState> _states = new(StringComparer.Ordinal);

    public Task<EncryptionState?> GetAsync(string systemId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _states.TryGetValue(systemId, out var state);
        return Task.FromResult<EncryptionState?>(state);
    }

    public Task<bool> UpsertAsync(string systemId, bool initialized, string? keyChecksum, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _states[systemId] = new EncryptionState(initialized, keyChecksum);
        return Task.FromResult(true);
    }
}
