using System.Collections.Concurrent;
using Interfold.Domain.Abstractions.Repository;

namespace Interfold.Infrastructure.InMemory.Repository;

public sealed class InMemoryNotificationTokenRepository : INotificationTokenRepository
{
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _tokensBySystem = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _tokenOwners = new(StringComparer.Ordinal);

    public Task<bool> AddAsync(string systemId, string token, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var normalizedSystemId = NormalizeSystemId(systemId?.Trim() ?? string.Empty);
        var normalizedToken = token.Trim();
        _tokenOwners[normalizedToken] = normalizedSystemId;

        var systemTokens = _tokensBySystem.GetOrAdd(normalizedSystemId, _ => new(StringComparer.Ordinal));
        systemTokens[normalizedToken] = 1;

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

    public Task<bool> RemoveAsync(string token, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var normalizedToken = token.Trim();
        if (_tokenOwners.TryRemove(normalizedToken, out var ownerSystemId) &&
            _tokensBySystem.TryGetValue(ownerSystemId, out var systemTokens))
        {
            systemTokens.TryRemove(normalizedToken, out _);
        }

        return Task.FromResult(true);
    }
}
