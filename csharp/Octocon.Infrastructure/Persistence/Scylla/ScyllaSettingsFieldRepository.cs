using Cassandra;
using Octocon.Domain.Abstractions;
using Octocon.Domain.Settings;
using Octocon.Infrastructure.Persistence.Transient;

namespace Octocon.Infrastructure.Persistence.Scylla;

public sealed class ScyllaSettingsFieldRepository : ISettingsFieldRepository
{
    private readonly IScyllaSessionProvider _sessionProvider;
    private readonly IRegionContext _regionContext;
    private readonly PersistenceRegistrationOptions _options;

    public ScyllaSettingsFieldRepository(
        IScyllaSessionProvider sessionProvider,
        IRegionContext regionContext,
        PersistenceRegistrationOptions options)
    {
        _sessionProvider = sessionProvider;
        _regionContext = regionContext;
        _options = options;
    }

    public async Task<string?> CreateAsync(string systemId, string name, string? value, int position, CancellationToken cancellationToken = default)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var scopedSystemId = GetScopedSystemId(systemId);
            var fieldId = Guid.NewGuid().ToString("N");

            var statement = new SimpleStatement(
                "INSERT INTO settings_fields_by_system (system_id, field_id, position, name, value, updated_at) VALUES (?, ?, ?, ?, ?, toTimestamp(now()))",
                scopedSystemId,
                fieldId,
                position,
                name,
                value
            );

            await session.ExecuteAsync(statement);
            return fieldId;
        }, _options, cancellationToken);
    }

    public async Task<bool> UpdateAsync(string systemId, string fieldId, string? name, string? value, CancellationToken cancellationToken = default)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var scopedSystemId = GetScopedSystemId(systemId);

            var exists = await ExistsAsync(session, scopedSystemId, fieldId);
            if (!exists)
                return false;

            if (name is not null)
            {
                await session.ExecuteAsync(new SimpleStatement(
                    "UPDATE settings_fields_by_system SET name = ?, updated_at = toTimestamp(now()) WHERE system_id = ? AND field_id = ?",
                    name,
                    scopedSystemId,
                    fieldId));
            }

            if (value is not null)
            {
                await session.ExecuteAsync(new SimpleStatement(
                    "UPDATE settings_fields_by_system SET value = ?, updated_at = toTimestamp(now()) WHERE system_id = ? AND field_id = ?",
                    value,
                    scopedSystemId,
                    fieldId));
            }

            return true;
        }, _options, cancellationToken);
    }

    public async Task<bool> DeleteAsync(string systemId, string fieldId, CancellationToken cancellationToken = default)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var scopedSystemId = GetScopedSystemId(systemId);

            var exists = await ExistsAsync(session, scopedSystemId, fieldId);
            if (!exists)
                return false;

            await session.ExecuteAsync(new SimpleStatement(
                "DELETE FROM settings_fields_by_system WHERE system_id = ? AND field_id = ?",
                scopedSystemId,
                fieldId));

            return true;
        }, _options, cancellationToken);
    }

    public async Task<bool> RelocateAsync(string systemId, string fieldId, int position, CancellationToken cancellationToken = default)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var scopedSystemId = GetScopedSystemId(systemId);

            var exists = await ExistsAsync(session, scopedSystemId, fieldId);
            if (!exists)
                return false;

            await session.ExecuteAsync(new SimpleStatement(
                "UPDATE settings_fields_by_system SET position = ?, updated_at = toTimestamp(now()) WHERE system_id = ? AND field_id = ?",
                position,
                scopedSystemId,
                fieldId));

            return true;
        }, _options, cancellationToken);
    }

    private static async Task<bool> ExistsAsync(ISession session, string scopedSystemId, string fieldId)
    {
        var query = new SimpleStatement(
            "SELECT field_id FROM settings_fields_by_system WHERE system_id = ? AND field_id = ? LIMIT 1",
            scopedSystemId,
            fieldId);

        return (await session.ExecuteAsync(query)).Any();
    }

    private string GetScopedSystemId(string systemId)
    {
        var region = _regionContext.ResolveUserRegion(systemId);
        return $"{region}:{systemId}";
    }
}
