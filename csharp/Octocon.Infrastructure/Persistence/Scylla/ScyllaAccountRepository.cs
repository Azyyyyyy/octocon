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

            var statement = new SimpleStatement(
                "UPDATE global.users SET username = ?, updated_at = toTimestamp(now()) WHERE id = ?",
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

            var statement = new SimpleStatement(
                "UPDATE global.users SET description = ?, updated_at = toTimestamp(now()) WHERE id = ?",
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

            var statement = new SimpleStatement(
                "UPDATE global.users SET avatar_url = ?, updated_at = toTimestamp(now()) WHERE id = ?",
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

            var statement = new SimpleStatement(
                "UPDATE global.users SET avatar_url = ?, updated_at = toTimestamp(now()) WHERE id = ?",
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

            var read = new SimpleStatement(
                "SELECT link_token FROM global.users WHERE id = ? LIMIT 1",
                normalizedSystemId
            );

            var existing = (await session.ExecuteAsync(read)).FirstOrDefault()?.GetValue<string?>("link_token");
            if (!string.IsNullOrWhiteSpace(existing))
                return existing;

            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalizedSystemId));
            var token = Convert.ToHexString(hash)[..32].ToLowerInvariant();

            var write = new SimpleStatement(
                "UPDATE global.users SET link_token = ?, updated_at = toTimestamp(now()) WHERE id = ?",
                token,
                normalizedSystemId
            );

            await session.ExecuteAsync(write);
            return token;
        }, _options, cancellationToken);
    }

    public async Task<string?> GetLinkTokenAsync(string systemId, CancellationToken cancellationToken = default)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var normalizedSystemId = _keyspaceResolver.NormalizeSystemId(systemId);

            var read = new SimpleStatement(
                "SELECT link_token FROM global.users WHERE id = ? LIMIT 1",
                normalizedSystemId
            );

            var existing = (await session.ExecuteAsync(read)).FirstOrDefault()?.GetValue<string?>("link_token");
            return string.IsNullOrWhiteSpace(existing) ? null : existing;
        }, _options, cancellationToken);
    }

    public async Task<AccountPublicProfileReadModel?> GetPublicProfileAsync(string systemId, CancellationToken cancellationToken = default)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var normalizedSystemId = _keyspaceResolver.NormalizeSystemId(systemId);

            var profileQuery = new SimpleStatement(
                "SELECT username, avatar_url, description FROM global.users WHERE id = ? LIMIT 1",
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
}
