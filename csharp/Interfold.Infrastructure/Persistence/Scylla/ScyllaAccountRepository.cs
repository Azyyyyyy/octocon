using Cassandra;
using System.Security.Cryptography;
using System.Text;
using Interfold.Domain.Accounts;
using Interfold.Infrastructure.Configuration;
using Interfold.Infrastructure.Persistence.Transient;

namespace Interfold.Infrastructure.Persistence.Scylla;

public sealed class ScyllaAccountRepository : IAccountRepository
{
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

            var statement = new SimpleStatement(
                $"UPDATE {keyspace}.users SET username = ?, updated_at = toTimestamp(now()) WHERE id = ?",
                username,
                normalizedSystemId
            );

            await session.ExecuteAsync(statement);
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

    public async Task<string> GetOrCreateLinkTokenAsync(string systemId, CancellationToken cancellationToken = default)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var normalizedSystemId = _keyspaceResolver.NormalizeSystemId(systemId);
            var keyspace = _keyspaceResolver.ResolveRegionalKeyspace(systemId);

            var existingQuery = new SimpleStatement(
                $"SELECT link_token FROM {keyspace}.users WHERE id = ? LIMIT 1",
                normalizedSystemId
            );

            var existing = (await session.ExecuteAsync(existingQuery)).FirstOrDefault()?.GetValue<string?>("link_token");
            if (!string.IsNullOrWhiteSpace(existing))
            {
                return existing;
            }

            var token = BuildDeterministicLinkToken(normalizedSystemId);

            var upsert = new SimpleStatement(
                $"UPDATE {keyspace}.users SET link_token = ?, updated_at = toTimestamp(now()) WHERE id = ?",
                token,
                normalizedSystemId
            );

            await session.ExecuteAsync(upsert);
            return token;
        }, _options, cancellationToken);
    }

    public async Task<string?> GetLinkTokenAsync(string systemId, CancellationToken cancellationToken = default)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var normalizedSystemId = _keyspaceResolver.NormalizeSystemId(systemId);
            var keyspace = _keyspaceResolver.ResolveRegionalKeyspace(systemId);

            var tokenQuery = new SimpleStatement(
                $"SELECT link_token FROM {keyspace}.users WHERE id = ? LIMIT 1",
                normalizedSystemId
            );

            return (await session.ExecuteAsync(tokenQuery)).FirstOrDefault()?.GetValue<string?>("link_token");
        }, _options, cancellationToken);
    }

    public async Task<string?> ResolveSystemIdByLinkTokenAsync(string linkToken, CancellationToken cancellationToken = default)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            if (string.IsNullOrWhiteSpace(linkToken))
            {
                return null;
            }

            var session = await _sessionProvider.GetSessionAsync(cancellationToken);

            foreach (var keyspace in EnumerateRegionalKeyspaces())
            {
                var query = new SimpleStatement(
                    $"SELECT id FROM {keyspace}.users WHERE link_token = ? ALLOW FILTERING LIMIT 1",
                    linkToken
                );

                var row = (await session.ExecuteAsync(query)).FirstOrDefault();
                if (row is not null)
                {
                    return $"{keyspace}:{row.GetValue<string>("id")}";
                }
            }

            return null;
        }, _options, cancellationToken);
    }

    public async Task<bool> ClearLinkTokenAsync(string systemId, CancellationToken cancellationToken = default)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var normalizedSystemId = _keyspaceResolver.NormalizeSystemId(systemId);
            var keyspace = _keyspaceResolver.ResolveRegionalKeyspace(systemId);

            var clear = new SimpleStatement(
                $"UPDATE {keyspace}.users SET link_token = ?, updated_at = toTimestamp(now()) WHERE id = ?",
                null,
                normalizedSystemId
            );

            await session.ExecuteAsync(clear);
            return true;
        }, _options, cancellationToken);
    }

    public async Task<string?> FindSystemIdByDiscordIdAsync(string discordId, CancellationToken cancellationToken = default)
        => await FindSystemIdByRegistryColumnAsync("discord_id", discordId, cancellationToken);

    public async Task<string?> FindSystemIdByEmailAsync(string email, CancellationToken cancellationToken = default)
        => await FindSystemIdByRegistryColumnAsync("email", email, cancellationToken);

    public async Task<string?> FindSystemIdByAppleIdAsync(string appleId, CancellationToken cancellationToken = default)
        => await FindSystemIdByRegistryColumnAsync("apple_id", appleId, cancellationToken);

    public Task<AccountLinkResult> LinkDiscordToUserAsync(string systemId, string discordId, CancellationToken cancellationToken = default)
        => LinkIdentityAsync(systemId, "discord_id", discordId, cancellationToken);

    public Task<AccountLinkResult> LinkEmailToUserAsync(string systemId, string email, CancellationToken cancellationToken = default)
        => LinkIdentityAsync(systemId, "email", email, cancellationToken);

    public Task<AccountLinkResult> LinkAppleToUserAsync(string systemId, string appleId, CancellationToken cancellationToken = default)
        => LinkIdentityAsync(systemId, "apple_id", appleId, cancellationToken);

    public async Task<AccountPublicProfileReadModel?> GetPublicProfileAsync(string systemId, CancellationToken cancellationToken = default)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var normalizedSystemId = _keyspaceResolver.NormalizeSystemId(systemId);
            var keyspace = _keyspaceResolver.ResolveRegionalKeyspace(systemId);

            var profileQuery = new SimpleStatement(
                $"SELECT username, avatar_url, description FROM {keyspace}.users WHERE id = ? LIMIT 1",
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
                profile.GetValue<string?>("avatar_url"));
        }, _options, cancellationToken);
    }

    private static string BuildDeterministicLinkToken(string normalizedSystemId)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalizedSystemId));
        return Convert.ToHexString(hash)[..32].ToLowerInvariant();
    }

    private async Task<string?> FindSystemIdByRegistryColumnAsync(string columnName, string value, CancellationToken cancellationToken)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var query = new SimpleStatement(
                $"SELECT user_id, region FROM global.user_registry WHERE {columnName} = ? LIMIT 1",
                value
            );

            var row = (await session.ExecuteAsync(query)).FirstOrDefault();
            if (row is not null)
            {
                var userId = row.GetValue<string>("user_id");
                var region = row.GetValue<string?>("region") ?? _options.DefaultRegion;
                return $"{region}:{userId}";
            }

            const string idChars = "abcdefghijklmnopqrstuvwxyz";

            // User doesn't exist; auto-create new account
            var newRegion = _options.DefaultRegion; //TODO: Maybe look into geoip-based region resolution here instead of just defaulting?
            var newUserId = Random.Shared.GetString(idChars, 7);
            var keyspace = newRegion; // ResolveRegionalKeyspace would just return the newRegion

            // Write regional user + global registry together to reduce split-write orphans.
            var createUserBatch = new BatchStatement()
                .Add(new SimpleStatement(
                    $"INSERT INTO {keyspace}.users (id, {columnName}, inserted_at, updated_at) VALUES (?, ?, toTimestamp(now()), toTimestamp(now()))",
                    newUserId,
                    value
                ))
                .Add(new SimpleStatement(
                    $"INSERT INTO global.user_registry (user_id, {columnName}, region, inserted_at, updated_at) VALUES (?, ?, ?, toTimestamp(now()), toTimestamp(now()))",
                    newUserId,
                    value,
                    newRegion
                ));

            await session.ExecuteAsync(createUserBatch);

            return $"{newRegion}:{newUserId}";
        }, _options, cancellationToken);
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

            var owner = await FindSystemIdByRegistryColumnAsync(columnName, value, cancellationToken);
            if (!string.IsNullOrWhiteSpace(owner))
            {
                var normalizedOwner = _keyspaceResolver.NormalizeSystemId(owner);
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
            await session.ExecuteAsync(linkBatch);

            return AccountLinkResult.Success;
        }, _options, cancellationToken);
    }

    private IEnumerable<string> EnumerateRegionalKeyspaces()
    {
        var defaults = new[] { "nam", "eur", "eas", "sam", "sas", "ocn", "gdpr" };
        var all = defaults.Concat(new[] { _options.DefaultRegion }).Where(x => !string.IsNullOrWhiteSpace(x));
        return all.Select(x => x.ToLowerInvariant()).Distinct(StringComparer.OrdinalIgnoreCase);
    }
}
