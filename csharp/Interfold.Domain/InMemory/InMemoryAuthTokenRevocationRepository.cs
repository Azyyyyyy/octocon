using Interfold.Domain.Auth;

namespace Interfold.Domain.InMemory;

/// <summary>
/// In-memory implementation of JWT token revocation tracking for testing and development.
/// Stores tokens in a dictionary keyed by JTI.
/// </summary>
public sealed class InMemoryAuthTokenRevocationRepository : IAuthTokenRevocationRepository
{
    private readonly object _lock = new();
    
    private sealed record TokenRecord(
        string Jti,
        string SystemId,
        DateTimeOffset IssuedAt,
        DateTimeOffset ExpiresAt,
        DateTimeOffset? RevokedAt = null
    );

    private readonly Dictionary<string, TokenRecord> _tokens = new();

    public Task RecordTokenAsync(
        string jti,
        string systemId,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jti, nameof(jti));
        ArgumentException.ThrowIfNullOrWhiteSpace(systemId, nameof(systemId));

        lock (_lock)
        {
            _tokens[jti] = new TokenRecord(
                Jti: jti,
                SystemId: systemId,
                IssuedAt: DateTimeOffset.UtcNow,
                ExpiresAt: expiresAt,
                RevokedAt: null
            );
        }

        return Task.CompletedTask;
    }

    public Task<bool> ValidateTokenNotRevokedAsync(
        string jti,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jti, nameof(jti));

        lock (_lock)
        {
            if (!_tokens.TryGetValue(jti, out var record))
            {
                return Task.FromResult(false);
            }

            // Token is valid if: not revoked AND not expired
            var isValid = record.RevokedAt is null && record.ExpiresAt > DateTimeOffset.UtcNow;
            return Task.FromResult(isValid);
        }
    }

    public Task RevokeTokenAsync(
        string jti,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jti, nameof(jti));

        lock (_lock)
        {
            if (_tokens.TryGetValue(jti, out var record) && record.RevokedAt is null)
            {
                _tokens[jti] = record with { RevokedAt = DateTimeOffset.UtcNow };
            }
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<string>> FindTokensBySystemIdAsync(
        string systemId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(systemId, nameof(systemId));

        lock (_lock)
        {
            IReadOnlyList<string> tokens = _tokens.Values
                .Where(t => t.SystemId == systemId
                    && t.RevokedAt is null
                    && t.ExpiresAt > DateTimeOffset.UtcNow)
                .OrderByDescending(t => t.IssuedAt)
                .Select(t => t.Jti)
                .ToList()
                .AsReadOnly();

            return Task.FromResult(tokens);
        }
    }

    public Task<int> CleanupExpiredTokensAsync(
        DateTimeOffset? olderThan = null,
        CancellationToken cancellationToken = default)
    {
        var cleanupBefore = (olderThan ?? DateTimeOffset.UtcNow).UtcDateTime;

        lock (_lock)
        {
            var keysToRemove = _tokens
                .Where(kvp => kvp.Value.RevokedAt is not null
                    || kvp.Value.ExpiresAt.UtcDateTime < cleanupBefore)
                .Select(kvp => kvp.Key)
                .Take(5000)
                .ToList();

            foreach (var key in keysToRemove)
            {
                _tokens.Remove(key);
            }

            return Task.FromResult(keysToRemove.Count);
        }
    }
}
