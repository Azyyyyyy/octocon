using Cassandra;
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

    private string GetScopedSystemId(string systemId)
    {
        var region = _regionContext.ResolveUserRegion(systemId);
        return $"{region}:{systemId}";
    }
}
