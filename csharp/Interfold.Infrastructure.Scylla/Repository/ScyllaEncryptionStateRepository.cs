using Cassandra;
using Interfold.Domain.Settings;
using Interfold.Infrastructure.Configuration;
using Interfold.Infrastructure.Persistence.Transient;

namespace Interfold.Infrastructure.Scylla.Repository;

public sealed class ScyllaEncryptionStateRepository : IEncryptionStateRepository
{
    private readonly IScyllaSessionProvider _sessionProvider;
    private readonly IScyllaKeyspaceResolver _keyspaceResolver;
    private readonly PersistenceConfiguration _options;

    public ScyllaEncryptionStateRepository(
        IScyllaSessionProvider sessionProvider,
        IScyllaKeyspaceResolver keyspaceResolver,
        PersistenceConfiguration options)
    {
        _sessionProvider = sessionProvider;
        _keyspaceResolver = keyspaceResolver;
        _options = options;
    }

    public async Task<EncryptionState?> GetAsync(string systemId, CancellationToken cancellationToken = default)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var normalizedSystemId = _keyspaceResolver.NormalizeSystemId(systemId);
            var keyspace = _keyspaceResolver.ResolveRegionalKeyspace(systemId);

            var query = new SimpleStatement(
                $"SELECT encryption_initialized, encryption_key_checksum FROM {keyspace}.users WHERE id = ? LIMIT 1",
                normalizedSystemId
            );

            var row = (await session.ExecuteAsync(query)).FirstOrDefault();
            return row is null
                ? null
                : new EncryptionState(
                    row.GetValue<bool?>("encryption_initialized") ?? false,
                    row.GetValue<string?>("encryption_key_checksum"));
        }, _options, cancellationToken);
    }

    public async Task<bool> UpsertAsync(string systemId, bool initialized, string? keyChecksum, CancellationToken cancellationToken = default)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var normalizedSystemId = _keyspaceResolver.NormalizeSystemId(systemId);
            var keyspace = _keyspaceResolver.ResolveRegionalKeyspace(systemId);

            var statement = new SimpleStatement(
                $"UPDATE {keyspace}.users SET encryption_initialized = ?, encryption_key_checksum = ?, updated_at = toTimestamp(now()) WHERE id = ?",
                initialized,
                keyChecksum,
                normalizedSystemId
            );

            await session.ExecuteAsync(statement);
            return true;
        }, _options, cancellationToken);
    }
}
