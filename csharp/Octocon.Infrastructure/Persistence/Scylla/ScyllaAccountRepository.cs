using Cassandra;
using System.Security.Cryptography;
using System.Text;
using Octocon.Domain.Abstractions;
using Octocon.Domain.Accounts;
using Octocon.Infrastructure.Persistence.Transient;

namespace Octocon.Infrastructure.Persistence.Scylla;

public sealed class ScyllaAccountRepository : IAccountRepository
{
    private readonly IScyllaSessionProvider _sessionProvider;
    private readonly IRegionContext _regionContext;
    private readonly PersistenceRegistrationOptions _options;

    public ScyllaAccountRepository(
        IScyllaSessionProvider sessionProvider,
        IRegionContext regionContext,
        PersistenceRegistrationOptions options
    )
    {
        _sessionProvider = sessionProvider;
        _regionContext = regionContext;
        _options = options;
    }

    public async Task<bool> UpdateUsernameAsync(string systemId, string username, CancellationToken cancellationToken = default)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var scopedSystemId = GetScopedSystemId(systemId);

            var statement = new SimpleStatement(
                "UPDATE account_profiles_by_system SET username = ?, updated_at = toTimestamp(now()) WHERE system_id = ?",
                username,
                scopedSystemId
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
            var scopedSystemId = GetScopedSystemId(systemId);

            var statement = new SimpleStatement(
                "UPDATE account_descriptions_by_system SET description = ?, updated_at = toTimestamp(now()) WHERE system_id = ?",
                description,
                scopedSystemId
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
            var scopedSystemId = GetScopedSystemId(systemId);

            var statement = new SimpleStatement(
                "UPDATE account_profiles_by_system SET avatar_url = ?, updated_at = toTimestamp(now()) WHERE system_id = ?",
                avatarUrl,
                scopedSystemId
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
            var scopedSystemId = GetScopedSystemId(systemId);

            var statement = new SimpleStatement(
                "UPDATE account_profiles_by_system SET avatar_url = ?, updated_at = toTimestamp(now()) WHERE system_id = ?",
                null,
                scopedSystemId
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
            var scopedSystemId = GetScopedSystemId(systemId);

            var read = new SimpleStatement(
                "SELECT link_token FROM account_profiles_by_system WHERE system_id = ? LIMIT 1",
                scopedSystemId
            );

            var existing = (await session.ExecuteAsync(read)).FirstOrDefault()?.GetValue<string?>("link_token");
            if (!string.IsNullOrWhiteSpace(existing))
                return existing;

            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(scopedSystemId));
            var token = Convert.ToHexString(hash)[..32].ToLowerInvariant();

            var write = new SimpleStatement(
                "UPDATE account_profiles_by_system SET link_token = ?, updated_at = toTimestamp(now()) WHERE system_id = ?",
                token,
                scopedSystemId
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
            var scopedSystemId = GetScopedSystemId(systemId);

            var read = new SimpleStatement(
                "SELECT link_token FROM account_profiles_by_system WHERE system_id = ? LIMIT 1",
                scopedSystemId
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
            var scopedSystemId = GetScopedSystemId(systemId);

            var profileQuery = new SimpleStatement(
                "SELECT username, avatar_url FROM account_profiles_by_system WHERE system_id = ? LIMIT 1",
                scopedSystemId
            );

            var descriptionQuery = new SimpleStatement(
                "SELECT description FROM account_descriptions_by_system WHERE system_id = ? LIMIT 1",
                scopedSystemId
            );

            var profile = (await session.ExecuteAsync(profileQuery)).FirstOrDefault();
            var description = (await session.ExecuteAsync(descriptionQuery)).FirstOrDefault();

            if (profile is null && description is null)
            {
                return null;
            }

            return new AccountPublicProfileReadModel(
                systemId,
                profile?.GetValue<string?>("username"),
                description?.GetValue<string?>("description"),
                profile?.GetValue<string?>("avatar_url"));
        }, _options, cancellationToken);
    }

    private string GetScopedSystemId(string systemId)
    {
        var region = _regionContext.ResolveUserRegion(systemId);
        return $"{region}:{systemId}";
    }
}
