using Cassandra;
using System.Security.Cryptography;
using System.Text;
using Octocon.Domain.Accounts;
using Octocon.Infrastructure.Persistence.Transient;

namespace Octocon.Infrastructure.Persistence.Scylla;

public sealed class ScyllaAccountRepository : IAccountRepository
{
    private readonly IScyllaSessionProvider _sessionProvider;
    private readonly IScyllaKeyspaceResolver _keyspaceResolver;
    private readonly PersistenceRegistrationOptions _options;

    public ScyllaAccountRepository(
        IScyllaSessionProvider sessionProvider,
        IScyllaKeyspaceResolver keyspaceResolver,
        PersistenceRegistrationOptions options
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
            var normalizedSystemId = _keyspaceResolver.NormalizeSystemId(systemId);
            return BuildDeterministicLinkToken(normalizedSystemId);
        }, _options, cancellationToken);
    }

    public async Task<string?> GetLinkTokenAsync(string systemId, CancellationToken cancellationToken = default)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var normalizedSystemId = _keyspaceResolver.NormalizeSystemId(systemId);
            var keyspace = _keyspaceResolver.ResolveRegionalKeyspace(systemId);

            var existsQuery = new SimpleStatement(
                $"SELECT id FROM {keyspace}.users WHERE id = ? LIMIT 1",
                normalizedSystemId
            );

            var exists = (await session.ExecuteAsync(existsQuery)).Any();
            return exists ? BuildDeterministicLinkToken(normalizedSystemId) : null;
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
}
