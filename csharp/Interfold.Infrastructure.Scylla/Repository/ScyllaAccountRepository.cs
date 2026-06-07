using System.Collections.Concurrent;
using Cassandra;
using System.Security.Cryptography;
using Interfold.Contracts.Configuration;
using Interfold.Contracts.Models.Read;
using Interfold.Domain.Abstractions.Repository;
using Interfold.Infrastructure.Persistence;

namespace Interfold.Infrastructure.Scylla.Repository;

public sealed class ScyllaAccountRepository : IAccountRepository
{
    private readonly record struct LinkTokenEntry(string ScopedSystemId, DateTimeOffset ExpiresAt);

    private static readonly TimeSpan LinkTokenTtl = TimeSpan.FromMinutes(5);
    private readonly object _linkTokenLock = new();
    private readonly ConcurrentDictionary<string, string> _linkTokenBySystem = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, LinkTokenEntry> _systemByLinkToken = new(StringComparer.Ordinal);

    private readonly IScyllaSessionProvider _sessionProvider;
    private readonly IScyllaKeyspaceResolver _keyspaceResolver;
    private readonly PersistenceConfiguration _options;

    public ScyllaAccountRepository(
        IScyllaSessionProvider sessionProvider,
        IScyllaKeyspaceResolver keyspaceResolver,
        PersistenceConfiguration options
    )
    {
        _sessionProvider = sessionProvider;
        _keyspaceResolver = keyspaceResolver;
        _options = options;
    }

    public async Task<bool> UpdateUsernameAsync(string systemId, string username, CancellationToken cancellationToken = default)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var normalizedSystemId = _keyspaceResolver.NormalizeSystemId(systemId);
            var keyspace = _keyspaceResolver.ResolveRegionalKeyspace(systemId);

            // Read old username to maintain lookup table
            var oldRow = (await session.ExecuteAsync(new SimpleStatement(
                $"SELECT username FROM {keyspace}.users WHERE id = ? LIMIT 1",
                normalizedSystemId))).FirstOrDefault();
            var oldUsername = oldRow?.GetValue<string?>("username");

            var batch = new BatchStatement();
            batch.Add(new SimpleStatement(
                $"UPDATE {keyspace}.users SET username = ?, updated_at = toTimestamp(now()) WHERE id = ?",
                username, normalizedSystemId));
            batch.Add(new SimpleStatement(
                $"UPDATE global.user_registry SET username = ?, updated_at = toTimestamp(now()) WHERE user_id = ?",
                username, normalizedSystemId));

            // Remove old lookup entry
            if (!string.IsNullOrWhiteSpace(oldUsername))
            {
                batch.Add(new SimpleStatement(
                    $"DELETE FROM {keyspace}.users_by_username WHERE username = ?", oldUsername));
                batch.Add(new SimpleStatement(
                    "DELETE FROM global.user_registry_by_username WHERE username = ?", oldUsername));
            }

            // Insert new lookup entry
            if (!string.IsNullOrWhiteSpace(username))
            {
                batch.Add(new SimpleStatement(
                    $"INSERT INTO {keyspace}.users_by_username (username, user_id) VALUES (?, ?)",
                    username, normalizedSystemId));
                batch.Add(new SimpleStatement(
                    "INSERT INTO global.user_registry_by_username (username, user_id, region) VALUES (?, ?, ?)",
                    username, normalizedSystemId, keyspace));
            }

            await session.ExecuteAsync(batch);
            return true;
        }, _options, cancellationToken);
    }

    public async Task<bool> UpdateDescriptionAsync(string systemId, string description, CancellationToken cancellationToken = default)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var normalizedSystemId = _keyspaceResolver.NormalizeSystemId(systemId);
            var keyspace = _keyspaceResolver.ResolveRegionalKeyspace(systemId);

            var statement = new SimpleStatement(
                $"UPDATE {keyspace}.users SET description = ?, updated_at = toTimestamp(now()) WHERE id = ?",
                description,
                normalizedSystemId
            );

            await session.ExecuteAsync(statement);
            return true;
        }, _options, cancellationToken);
    }

    public async Task<bool> UpdateAvatarAsync(string systemId, string avatarUrl, CancellationToken cancellationToken = default)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var normalizedSystemId = _keyspaceResolver.NormalizeSystemId(systemId);
            var keyspace = _keyspaceResolver.ResolveRegionalKeyspace(systemId);

            var statement = new SimpleStatement(
                $"UPDATE {keyspace}.users SET avatar_url = ?, updated_at = toTimestamp(now()) WHERE id = ?",
                avatarUrl,
                normalizedSystemId
            );

            await session.ExecuteAsync(statement);
            return true;
        }, _options, cancellationToken);
    }

    public async Task<bool> ClearAvatarAsync(string systemId, CancellationToken cancellationToken = default)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var normalizedSystemId = _keyspaceResolver.NormalizeSystemId(systemId);
            var keyspace = _keyspaceResolver.ResolveRegionalKeyspace(systemId);

            var statement = new SimpleStatement(
                $"UPDATE {keyspace}.users SET avatar_url = ?, updated_at = toTimestamp(now()) WHERE id = ?",
                null,
                normalizedSystemId
            );

            await session.ExecuteAsync(statement);
            return true;
        }, _options, cancellationToken);
    }

    public Task<string> GetOrCreateLinkTokenAsync(string systemId, CancellationToken cancellationToken = default)
    {
        var normalizedSystemId = _keyspaceResolver.NormalizeSystemId(systemId);
        var keyspace = _keyspaceResolver.ResolveRegionalKeyspace(systemId);
        var scopedSystemId = $"{keyspace}:{normalizedSystemId}";
        var systemKey = scopedSystemId;
        var now = DateTimeOffset.UtcNow;

        lock (_linkTokenLock)
        {
            if (_linkTokenBySystem.TryGetValue(systemKey, out var existingToken)
                && _systemByLinkToken.TryGetValue(existingToken, out var existingEntry)
                && existingEntry.ExpiresAt > now)
            {
                return Task.FromResult(existingToken);
            }

            if (!string.IsNullOrWhiteSpace(existingToken))
            {
                _linkTokenBySystem.TryRemove(systemKey, out _);
                _systemByLinkToken.TryRemove(existingToken, out _);
            }

            var token = Guid.NewGuid().ToString();
            _linkTokenBySystem[systemKey] = token;
            _systemByLinkToken[token] = new LinkTokenEntry(scopedSystemId, now.Add(LinkTokenTtl));

            return Task.FromResult(token);
        }
    }

    public Task<string?> GetLinkTokenAsync(string systemId, CancellationToken cancellationToken = default)
    {
        var normalizedSystemId = _keyspaceResolver.NormalizeSystemId(systemId);
        var keyspace = _keyspaceResolver.ResolveRegionalKeyspace(systemId);
        var systemKey = $"{keyspace}:{normalizedSystemId}";
        var now = DateTimeOffset.UtcNow;

        lock (_linkTokenLock)
        {
            if (_linkTokenBySystem.TryGetValue(systemKey, out var token)
                && _systemByLinkToken.TryGetValue(token, out var entry)
                && entry.ExpiresAt > now)
            {
                return Task.FromResult<string?>(token);
            }

            if (!string.IsNullOrWhiteSpace(token))
            {
                _linkTokenBySystem.TryRemove(systemKey, out _);
                _systemByLinkToken.TryRemove(token, out _);
            }

            return Task.FromResult<string?>(null);
        }
    }

    public Task<string?> ResolveSystemIdByLinkTokenAsync(string linkToken, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(linkToken))
        {
            return Task.FromResult<string?>(null);
        }

        var now = DateTimeOffset.UtcNow;
        lock (_linkTokenLock)
        {
            if (_systemByLinkToken.TryGetValue(linkToken, out var entry) && entry.ExpiresAt > now)
            {
                return Task.FromResult<string?>(entry.ScopedSystemId);
            }

            _systemByLinkToken.TryRemove(linkToken, out _);
            foreach (var item in _linkTokenBySystem)
            {
                if (string.Equals(item.Value, linkToken, StringComparison.Ordinal))
                {
                    _linkTokenBySystem.TryRemove(item.Key, out _);
                    break;
                }
            }

            return Task.FromResult<string?>(null);
        }
    }

    public Task<bool> ClearLinkTokenAsync(string systemId, CancellationToken cancellationToken = default)
    {
        var normalizedSystemId = _keyspaceResolver.NormalizeSystemId(systemId);
        var keyspace = _keyspaceResolver.ResolveRegionalKeyspace(systemId);
        var systemKey = $"{keyspace}:{normalizedSystemId}";

        lock (_linkTokenLock)
        {
            if (_linkTokenBySystem.TryRemove(systemKey, out var token))
            {
                _systemByLinkToken.TryRemove(token, out _);
            }

            return Task.FromResult(true);
        }
    }

    public async Task<string?> FindSystemIdByDiscordIdAsync(string discordId, CancellationToken cancellationToken = default)
        => await FindOrCreateSystemIdByRegistryColumnAsync("discord_id", discordId, cancellationToken);

    public async Task<string?> FindSystemIdByEmailAsync(string email, CancellationToken cancellationToken = default)
        => await FindOrCreateSystemIdByRegistryColumnAsync("email", email, cancellationToken);

    public async Task<string?> FindSystemIdByAppleIdAsync(string appleId, CancellationToken cancellationToken = default)
        => await FindOrCreateSystemIdByRegistryColumnAsync("apple_id", appleId, cancellationToken);

    public Task<AccountLinkResult> LinkDiscordToUserAsync(string systemId, string discordId, CancellationToken cancellationToken = default)
        => LinkIdentityAsync(systemId, "discord_id", discordId, cancellationToken);

    public Task<AccountLinkResult> LinkEmailToUserAsync(string systemId, string email, CancellationToken cancellationToken = default)
        => LinkIdentityAsync(systemId, "email", email, cancellationToken);

    public Task<AccountLinkResult> LinkAppleToUserAsync(string systemId, string appleId, CancellationToken cancellationToken = default)
        => LinkIdentityAsync(systemId, "apple_id", appleId, cancellationToken);

    public Task<bool> UnlinkDiscordAsync(string systemId, CancellationToken cancellationToken = default)
        => UnlinkIdentityAsync(systemId, "discord_id", cancellationToken);

    public Task<bool> UnlinkEmailAsync(string systemId, CancellationToken cancellationToken = default)
        => UnlinkIdentityAsync(systemId, "email", cancellationToken);

    public Task<bool> UnlinkAppleAsync(string systemId, CancellationToken cancellationToken = default)
        => UnlinkIdentityAsync(systemId, "apple_id", cancellationToken);

    public async Task<bool> DeleteAsync(string systemId, CancellationToken cancellationToken = default)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var normalizedSystemId = _keyspaceResolver.NormalizeSystemId(systemId);
            var keyspace = _keyspaceResolver.ResolveRegionalKeyspace(systemId);

            // Fetch identity fields to clean up lookup tables
            var userRow = (await session.ExecuteAsync(new SimpleStatement(
                $"SELECT id, discord_id, email, username, apple_id, google_id FROM {keyspace}.users WHERE id = ? LIMIT 1",
                normalizedSystemId))).FirstOrDefault();

            if (userRow is null)
            {
                return true;
            }
            
            var deleteBatch = new BatchStatement();
            deleteBatch.Add(new SimpleStatement($"DELETE FROM {keyspace}.users WHERE id = ?", normalizedSystemId));
            deleteBatch.Add(new SimpleStatement("DELETE FROM global.user_registry WHERE user_id = ?", normalizedSystemId));

            // Clean up denormalized identity lookup tables
            var identityColumns = new[] { "discord_id", "email", "username", "apple_id", "google_id" };
            foreach (var col in identityColumns)
            {
                var value = userRow.GetValue<string?>(col);
                if (!string.IsNullOrWhiteSpace(value))
                {
                    deleteBatch.Add(new SimpleStatement($"DELETE FROM {keyspace}.users_by_{col} WHERE {col} = ?", value));
                    deleteBatch.Add(new SimpleStatement($"DELETE FROM global.user_registry_by_{col} WHERE {col} = ?", value));
                }
            }

            await session.ExecuteAsync(deleteBatch);

            return true;
        }, _options, cancellationToken);
    }

    public async Task<AccountPublicProfileReadModel?> GetPublicProfileAsync(string systemId, CancellationToken cancellationToken = default)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var normalizedSystemId = _keyspaceResolver.NormalizeSystemId(systemId);
            var keyspace = _keyspaceResolver.ResolveRegionalKeyspace(systemId);

            var profileQuery = new SimpleStatement(
                $"SELECT username, avatar_url, description, discord_id, email, apple_id FROM {keyspace}.users WHERE id = ? LIMIT 1",
                normalizedSystemId
            );

            var profile = (await session.ExecuteAsync(profileQuery)).FirstOrDefault();
            if (profile is null)
            {
                return null;
            }

            return new AccountPublicProfileReadModel(
                normalizedSystemId,
                profile.GetValue<string?>("username"),
                profile.GetValue<string?>("description"),
                profile.GetValue<string?>("avatar_url"),
                profile.GetValue<string?>("discord_id"),
                profile.GetValue<string?>("email"),
                profile.GetValue<string?>("apple_id"));
        }, _options, cancellationToken);
    }

    private async Task<string?> TryFindSystemIdByRegistryColumnAsync(string columnName, string value, CancellationToken cancellationToken)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var query = new SimpleStatement(
                $"SELECT user_id, region FROM global.user_registry_by_{columnName} WHERE {columnName} = ? LIMIT 1",
                value
            );

            var row = (await session.ExecuteAsync(query)).FirstOrDefault();
            if (row is not null)
            {
                var userId = NormalizeRegistryUserId(row.GetValue<string>("user_id"));
                var region = row.GetValue<string?>("region") ?? _options.DefaultRegion;
                return $"{region}:{userId}";
            }

            return null;
        }, _options, cancellationToken);
    }

    private async Task<string?> FindOrCreateSystemIdByRegistryColumnAsync(string columnName, string value, CancellationToken cancellationToken)
    {
        var existing = await TryFindSystemIdByRegistryColumnAsync(columnName, value, cancellationToken);
        if (!string.IsNullOrWhiteSpace(existing))
        {
            return existing;
        }

        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            var session = await _sessionProvider.GetSessionAsync(cancellationToken);

            const string idChars = "abcdefghijklmnopqrstuvwxyz";

            // User doesn't exist; auto-create new account
            var newRegion = _options.DefaultRegion; //TODO: Maybe look into geoip-based region resolution here instead of just defaulting?
            var newUserId = Random.Shared.GetString(idChars, 7);
            var keyspace = newRegion; // ResolveRegionalKeyspace would just return the newRegion

            // Write regional user + global registry together to reduce split-write orphans.
            var createUserBatch = new BatchStatement()
                .Add(new SimpleStatement(
                    $"INSERT INTO {keyspace}.users (id, {columnName}, inserted_at, updated_at) VALUES (?, ?, ?, toTimestamp(now()), toTimestamp(now()))",
                    newUserId,
                    value
                ))
                .Add(new SimpleStatement(
                    $"INSERT INTO global.user_registry (user_id, {columnName}, region, inserted_at, updated_at) VALUES (?, ?, ?, toTimestamp(now()), toTimestamp(now()))",
                    newUserId,
                    value,
                    newRegion
                ))
                // Maintain denormalized lookup tables
                .Add(new SimpleStatement(
                    $"INSERT INTO {keyspace}.users_by_{columnName} ({columnName}, user_id) VALUES (?, ?)",
                    value,
                    newUserId
                ))
                .Add(new SimpleStatement(
                    $"INSERT INTO global.user_registry_by_{columnName} ({columnName}, user_id, region) VALUES (?, ?, ?)",
                    value,
                    newUserId,
                    newRegion
                ));

            await session.ExecuteAsync(createUserBatch);

            return $"{newRegion}:{newUserId}";
        }, _options, cancellationToken);
    }

    private string NormalizeRegistryUserId(string userId)
    {
        var normalized = userId;
        for (var i = 0; i < 3; i++)
        {
            var next = _keyspaceResolver.NormalizeSystemId(normalized);
            if (string.Equals(next, normalized, StringComparison.Ordinal))
            {
                break;
            }

            normalized = next;
        }

        return normalized;
    }

    private async Task<AccountLinkResult> LinkIdentityAsync(string systemId, string columnName, string value, CancellationToken cancellationToken)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return AccountLinkResult.UserNotFound;
            }

            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var normalizedSystemId = _keyspaceResolver.NormalizeSystemId(systemId);
            var keyspace = _keyspaceResolver.ResolveRegionalKeyspace(systemId);

            var owner = await TryFindSystemIdByRegistryColumnAsync(columnName, value, cancellationToken);
            if (!string.IsNullOrWhiteSpace(owner))
            {
                var normalizedOwner = NormalizeRegistryUserId(_keyspaceResolver.NormalizeSystemId(owner));
                if (!string.Equals(normalizedOwner, normalizedSystemId, StringComparison.Ordinal))
                {
                    return AccountLinkResult.UserExists;
                }
            }

            var userRow = (await session.ExecuteAsync(new SimpleStatement(
                $"SELECT id, {columnName} FROM {keyspace}.users WHERE id = ? LIMIT 1",
                normalizedSystemId))).FirstOrDefault();

            if (userRow is null)
            {
                return AccountLinkResult.UserNotFound;
            }

            var alreadyLinked = userRow.GetValue<string?>(columnName);
            if (!string.IsNullOrWhiteSpace(alreadyLinked))
            {
                return AccountLinkResult.AlreadyLinked;
            }

            var linkBatch = new BatchStatement();
            linkBatch.Add(new SimpleStatement(
                $"UPDATE {keyspace}.users SET {columnName} = ?, updated_at = toTimestamp(now()) WHERE id = ?",
                value,
                normalizedSystemId));
            linkBatch.Add(new SimpleStatement(
                $"UPDATE global.user_registry SET {columnName} = ?, updated_at = toTimestamp(now()) WHERE user_id = ?",
                value,
                normalizedSystemId));
            // Maintain denormalized lookup tables
            linkBatch.Add(new SimpleStatement(
                $"INSERT INTO {keyspace}.users_by_{columnName} ({columnName}, user_id) VALUES (?, ?)",
                value,
                normalizedSystemId));
            linkBatch.Add(new SimpleStatement(
                $"INSERT INTO global.user_registry_by_{columnName} ({columnName}, user_id, region) VALUES (?, ?, ?)",
                value,
                normalizedSystemId,
                keyspace));
            await session.ExecuteAsync(linkBatch);

            return AccountLinkResult.Success;
        }, _options, cancellationToken);
    }

    private async Task<bool> UnlinkIdentityAsync(string systemId, string columnName, CancellationToken cancellationToken)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var normalizedSystemId = _keyspaceResolver.NormalizeSystemId(systemId);
            var keyspace = _keyspaceResolver.ResolveRegionalKeyspace(systemId);

            // Read old value to delete from lookup tables
            var oldRow = (await session.ExecuteAsync(new SimpleStatement(
                $"SELECT {columnName} FROM {keyspace}.users WHERE id = ? LIMIT 1",
                normalizedSystemId))).FirstOrDefault();
            var oldValue = oldRow?.GetValue<string?>(columnName);

            var unlinkBatch = new BatchStatement();
            unlinkBatch.Add(new SimpleStatement(
                $"UPDATE {keyspace}.users SET {columnName} = ?, updated_at = toTimestamp(now()) WHERE id = ?",
                null,
                normalizedSystemId));
            unlinkBatch.Add(new SimpleStatement(
                $"UPDATE global.user_registry SET {columnName} = ?, updated_at = toTimestamp(now()) WHERE user_id = ?",
                null,
                normalizedSystemId));

            // Remove from denormalized lookup tables if old value existed
            if (!string.IsNullOrWhiteSpace(oldValue))
            {
                unlinkBatch.Add(new SimpleStatement(
                    $"DELETE FROM {keyspace}.users_by_{columnName} WHERE {columnName} = ?",
                    oldValue));
                unlinkBatch.Add(new SimpleStatement(
                    $"DELETE FROM global.user_registry_by_{columnName} WHERE {columnName} = ?",
                    oldValue));
            }

            await session.ExecuteAsync(unlinkBatch);
            return true;
        }, _options, cancellationToken);
    }

}
