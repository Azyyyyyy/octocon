using Cassandra;
using Octocon.Domain.Abstractions;
using Octocon.Domain.Alters;
using Octocon.Infrastructure.Persistence.Transient;

namespace Octocon.Infrastructure.Persistence.Scylla;

public sealed class ScyllaAlterRepository : IAlterRepository
{
    private readonly IScyllaSessionProvider _sessionProvider;
    private readonly IRegionContext _regionContext;
    private readonly PersistenceRegistrationOptions _options;

    public ScyllaAlterRepository(
        IScyllaSessionProvider sessionProvider,
        IRegionContext regionContext,
        PersistenceRegistrationOptions options
    )
    {
        _sessionProvider = sessionProvider;
        _regionContext = regionContext;
        _options = options;
    }

    public async Task<int?> CreateAsync(string systemId, CreateAlterCommand command, CancellationToken cancellationToken = default)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var scopedSystemId = GetScopedSystemId(systemId);

            var nextIdQuery = new SimpleStatement(
                "SELECT alter_id FROM alters_by_system WHERE system_id = ? ORDER BY alter_id DESC LIMIT 1",
                scopedSystemId
            );

            var rows = await session.ExecuteAsync(nextIdQuery);
            var current = rows.FirstOrDefault()?.GetValue<int>("alter_id") ?? 0;
            var next = current + 1;

            var insert = new SimpleStatement(
                "INSERT INTO alters_by_system (system_id, alter_id, name, alias) VALUES (?, ?, ?, ?)",
                scopedSystemId,
                next,
                command.Name,
                null
            );

            await session.ExecuteAsync(insert);
            return (int?)next;
        }, _options, cancellationToken);
    }

    public async Task<bool> ExistsAsync(string systemId, int alterId, CancellationToken cancellationToken = default)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var scopedSystemId = GetScopedSystemId(systemId);

            var query = new SimpleStatement(
                "SELECT alter_id FROM alters_by_system WHERE system_id = ? AND alter_id = ? LIMIT 1",
                scopedSystemId,
                alterId
            );

            var rows = await session.ExecuteAsync(query);
            return rows.Any();
        }, _options, cancellationToken);
    }

    public async Task<bool> UpdateAsync(string systemId, UpdateAlterCommand command, CancellationToken cancellationToken = default)
    {
        var session = await _sessionProvider.GetSessionAsync(cancellationToken);
        var scopedSystemId = GetScopedSystemId(systemId);

        var exists = await ExistsAsync(systemId, command.AlterId, cancellationToken);
        if (!exists)
        {
            return false;
        }

        if (command.Name is not null)
        {
            var updateName = new SimpleStatement(
                "UPDATE alters_by_system SET name = ? WHERE system_id = ? AND alter_id = ?",
                command.Name,
                scopedSystemId,
                command.AlterId
            );

            await session.ExecuteAsync(updateName);
        }

        if (command.Alias is not null)
        {
            var updateAlias = new SimpleStatement(
                "UPDATE alters_by_system SET alias = ? WHERE system_id = ? AND alter_id = ?",
                command.Alias,
                scopedSystemId,
                command.AlterId
            );

            await session.ExecuteAsync(updateAlias);
        }

        return true;
    }

    public async Task<bool> DeleteAsync(string systemId, int alterId, CancellationToken cancellationToken = default)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var scopedSystemId = GetScopedSystemId(systemId);

            var exists = await ExistsAsync(systemId, alterId, cancellationToken);
            if (!exists)
            {
                return false;
            }

            var deleteAlter = new SimpleStatement(
                "DELETE FROM alters_by_system WHERE system_id = ? AND alter_id = ?",
                scopedSystemId,
                alterId
            );
            await session.ExecuteAsync(deleteAlter);

            // Clean up active fronting state and primary pointer for deleted alter.
            var deleteFrontActive = new SimpleStatement(
                "DELETE FROM fronting_active_by_system WHERE system_id = ? AND alter_id = ?",
                scopedSystemId,
                alterId
            );
            await session.ExecuteAsync(deleteFrontActive);

            var clearPrimary = new SimpleStatement(
                "DELETE FROM fronting_primary_by_system WHERE system_id = ? IF alter_id = ?",
                scopedSystemId,
                alterId
            );
            await session.ExecuteAsync(clearPrimary);

            // Remove alter from any tags under this system.
            var membershipQuery = new SimpleStatement(
                "SELECT tag_id FROM tag_alters_by_system WHERE system_id = ? ALLOW FILTERING",
                scopedSystemId
            );

            var membershipRows = await session.ExecuteAsync(membershipQuery);
            foreach (var row in membershipRows)
            {
                var tagId = row.GetValue<string>("tag_id");
                var deleteMembership = new SimpleStatement(
                    "DELETE FROM tag_alters_by_system WHERE system_id = ? AND tag_id = ? AND alter_id = ?",
                    scopedSystemId,
                    tagId,
                    alterId
                );
                await session.ExecuteAsync(deleteMembership);
            }

            return true;
        }, _options, cancellationToken);
    }

    public async Task<bool> AliasTakenByOtherAsync(
        string systemId,
        int alterId,
        string alias,
        CancellationToken cancellationToken = default
    )
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var scopedSystemId = GetScopedSystemId(systemId);

            var query = new SimpleStatement(
                "SELECT alter_id, alias FROM alters_by_system WHERE system_id = ?",
                scopedSystemId
            );

            var rows = await session.ExecuteAsync(query);
            return rows.Any(row =>
            {
                var existingId = row.GetValue<int>("alter_id");
                var existingAlias = row.GetValue<string?>("alias");

                return existingId != alterId &&
                       !string.IsNullOrWhiteSpace(existingAlias) &&
                       string.Equals(existingAlias, alias, StringComparison.OrdinalIgnoreCase);
            });
        }, _options, cancellationToken);
    }

    private string GetScopedSystemId(string systemId)
    {
        var region = _regionContext.ResolveUserRegion(systemId);
        return $"{region}:{systemId}";
    }
}