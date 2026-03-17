using Cassandra;
using Octocon.Domain.Settings;
using Octocon.Infrastructure.Persistence.Transient;

namespace Octocon.Infrastructure.Persistence.Scylla;

public sealed class ScyllaSettingsFieldRepository : ISettingsFieldRepository
{
    private readonly IScyllaSessionProvider _sessionProvider;
    private readonly IScyllaKeyspaceResolver _keyspaceResolver;
    private readonly PersistenceRegistrationOptions _options;

    public ScyllaSettingsFieldRepository(
        IScyllaSessionProvider sessionProvider,
        IScyllaKeyspaceResolver keyspaceResolver,
        PersistenceRegistrationOptions options)
    {
        _sessionProvider = sessionProvider;
        _keyspaceResolver = keyspaceResolver;
        _options = options;
    }

    public async Task<string?> CreateAsync(string systemId, string name, string? value, int position, CancellationToken cancellationToken = default)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var normalizedSystemId = _keyspaceResolver.NormalizeSystemId(systemId);
            var keyspace = _keyspaceResolver.ResolveRegionalKeyspace(systemId);
            var fieldId = Guid.NewGuid();

            var statement = new SimpleStatement(
                $"INSERT INTO {keyspace}.settings_fields (user_id, id, position, name, value, updated_at) VALUES (?, ?, ?, ?, ?, toTimestamp(now()))",
                normalizedSystemId,
                fieldId,
                position,
                name,
                value
            );

            await session.ExecuteAsync(statement);
            return fieldId.ToString("N");
        }, _options, cancellationToken);
    }

    public async Task<bool> UpdateAsync(string systemId, string fieldId, string? name, string? value, CancellationToken cancellationToken = default)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            if (!TryParseUuid(fieldId, out var fieldGuid))
            {
                return false;
            }

            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var normalizedSystemId = _keyspaceResolver.NormalizeSystemId(systemId);
            var keyspace = _keyspaceResolver.ResolveRegionalKeyspace(systemId);

            var exists = await ExistsAsync(session, keyspace, normalizedSystemId, fieldGuid);
            if (!exists)
            {
                return false;
            }

            if (name is not null)
            {
                await session.ExecuteAsync(new SimpleStatement(
                    $"UPDATE {keyspace}.settings_fields SET name = ?, updated_at = toTimestamp(now()) WHERE user_id = ? AND id = ?",
                    name,
                    normalizedSystemId,
                    fieldGuid));
            }

            if (value is not null)
            {
                await session.ExecuteAsync(new SimpleStatement(
                    $"UPDATE {keyspace}.settings_fields SET value = ?, updated_at = toTimestamp(now()) WHERE user_id = ? AND id = ?",
                    value,
                    normalizedSystemId,
                    fieldGuid));
            }

            return true;
        }, _options, cancellationToken);
    }

    public async Task<bool> DeleteAsync(string systemId, string fieldId, CancellationToken cancellationToken = default)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            if (!TryParseUuid(fieldId, out var fieldGuid))
            {
                return false;
            }

            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var normalizedSystemId = _keyspaceResolver.NormalizeSystemId(systemId);
            var keyspace = _keyspaceResolver.ResolveRegionalKeyspace(systemId);

            var exists = await ExistsAsync(session, keyspace, normalizedSystemId, fieldGuid);
            if (!exists)
            {
                return false;
            }

            await session.ExecuteAsync(new SimpleStatement(
                $"DELETE FROM {keyspace}.settings_fields WHERE user_id = ? AND id = ?",
                normalizedSystemId,
                fieldGuid));

            return true;
        }, _options, cancellationToken);
    }

    public async Task<bool> RelocateAsync(string systemId, string fieldId, int position, CancellationToken cancellationToken = default)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            if (!TryParseUuid(fieldId, out var fieldGuid))
            {
                return false;
            }

            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var normalizedSystemId = _keyspaceResolver.NormalizeSystemId(systemId);
            var keyspace = _keyspaceResolver.ResolveRegionalKeyspace(systemId);

            var exists = await ExistsAsync(session, keyspace, normalizedSystemId, fieldGuid);
            if (!exists)
            {
                return false;
            }

            await session.ExecuteAsync(new SimpleStatement(
                $"UPDATE {keyspace}.settings_fields SET position = ?, updated_at = toTimestamp(now()) WHERE user_id = ? AND id = ?",
                position,
                normalizedSystemId,
                fieldGuid));

            return true;
        }, _options, cancellationToken);
    }

    private static async Task<bool> ExistsAsync(ISession session, string keyspace, string normalizedSystemId, Guid fieldId)
    {
        var query = new SimpleStatement(
            $"SELECT id FROM {keyspace}.settings_fields WHERE user_id = ? AND id = ? LIMIT 1",
            normalizedSystemId,
            fieldId);

        return (await session.ExecuteAsync(query)).Any();
    }

    internal static bool TryParseUuid(string value, out Guid guid)
    {
        if (Guid.TryParseExact(value, "N", out guid))
        {
            return true;
        }

        return Guid.TryParse(value, out guid);
    }
}
