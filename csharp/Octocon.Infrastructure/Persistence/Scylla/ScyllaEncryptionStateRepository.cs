using Cassandra;
using Octocon.Domain.Abstractions;
using Octocon.Domain.Settings;
using Octocon.Infrastructure.Persistence.Transient;

namespace Octocon.Infrastructure.Persistence.Scylla;

public sealed class ScyllaEncryptionStateRepository : IEncryptionStateRepository
{
    private readonly IScyllaSessionProvider _sessionProvider;
    private readonly IRegionContext _regionContext;
    private readonly PersistenceRegistrationOptions _options;

    public ScyllaEncryptionStateRepository(
        IScyllaSessionProvider sessionProvider,
        IRegionContext regionContext,
        PersistenceRegistrationOptions options)
    {
        _sessionProvider = sessionProvider;
        _regionContext = regionContext;
        _options = options;
    }

    public async Task<EncryptionState?> GetAsync(string systemId, CancellationToken cancellationToken = default)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var scopedSystemId = GetScopedSystemId(systemId);

            var query = new SimpleStatement(
                "SELECT encryption_initialized, encryption_key_checksum FROM account_encryption_by_system WHERE system_id = ? LIMIT 1",
                scopedSystemId
            );

            var row = (await session.ExecuteAsync(query)).FirstOrDefault();
            return row is null
                ? null
                : new EncryptionState(
                    row.GetValue<bool>("encryption_initialized"),
                    row.GetValue<string?>("encryption_key_checksum"));
        }, _options, cancellationToken);
    }

    public async Task<bool> UpsertAsync(string systemId, bool initialized, string? keyChecksum, CancellationToken cancellationToken = default)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var scopedSystemId = GetScopedSystemId(systemId);

            var statement = new SimpleStatement(
                "UPDATE account_encryption_by_system SET encryption_initialized = ?, encryption_key_checksum = ?, updated_at = toTimestamp(now()) WHERE system_id = ?",
                initialized,
                keyChecksum,
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
