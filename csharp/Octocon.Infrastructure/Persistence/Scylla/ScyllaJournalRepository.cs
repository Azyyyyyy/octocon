using Cassandra;
using Octocon.Domain.Abstractions;
using Octocon.Domain.Journals;
using Octocon.Infrastructure.Persistence.Transient;

namespace Octocon.Infrastructure.Persistence.Scylla;

public sealed class ScyllaJournalRepository : IJournalRepository
{
    private readonly IScyllaSessionProvider _sessionProvider;
    private readonly IRegionContext _regionContext;
    private readonly PersistenceRegistrationOptions _options;

    public ScyllaJournalRepository(
        IScyllaSessionProvider sessionProvider,
        IRegionContext regionContext,
        PersistenceRegistrationOptions options)
    {
        _sessionProvider = sessionProvider;
        _regionContext = regionContext;
        _options = options;
    }

    public async Task<string?> CreateGlobalAsync(string systemId, CreateGlobalJournalEntryCommand command, CancellationToken cancellationToken = default)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var scopedSystemId = GetScopedSystemId(systemId);
            var entryId = Guid.NewGuid().ToString("N");

            var insert = new SimpleStatement(
                "INSERT INTO global_journals_by_system (system_id, entry_id, title, content, color, inserted_at) VALUES (?, ?, ?, ?, ?, toTimestamp(now()))",
                scopedSystemId,
                entryId,
                command.Title,
                null,
                null
            );

            await session.ExecuteAsync(insert);
            return entryId;
        }, _options, cancellationToken);
    }

    public async Task<bool> ExistsGlobalAsync(string systemId, string entryId, CancellationToken cancellationToken = default)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var scopedSystemId = GetScopedSystemId(systemId);

            var query = new SimpleStatement(
                "SELECT entry_id FROM global_journals_by_system WHERE system_id = ? AND entry_id = ? LIMIT 1",
                scopedSystemId,
                entryId
            );

            var rows = await session.ExecuteAsync(query);
            return rows.Any();
        }, _options, cancellationToken);
    }

    public async Task<bool> UpdateGlobalAsync(string systemId, UpdateGlobalJournalEntryCommand command, CancellationToken cancellationToken = default)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var scopedSystemId = GetScopedSystemId(systemId);

            var exists = await ExistsGlobalAsync(systemId, command.EntryId, cancellationToken);
            if (!exists)
                return false;

            if (command.Title is not null)
            {
                var q = new SimpleStatement(
                    "UPDATE global_journals_by_system SET title = ? WHERE system_id = ? AND entry_id = ?",
                    command.Title,
                    scopedSystemId,
                    command.EntryId
                );
                await session.ExecuteAsync(q);
            }

            if (command.Content is not null)
            {
                var q = new SimpleStatement(
                    "UPDATE global_journals_by_system SET content = ? WHERE system_id = ? AND entry_id = ?",
                    command.Content,
                    scopedSystemId,
                    command.EntryId
                );
                await session.ExecuteAsync(q);
            }

            if (command.Color is not null)
            {
                var q = new SimpleStatement(
                    "UPDATE global_journals_by_system SET color = ? WHERE system_id = ? AND entry_id = ?",
                    command.Color,
                    scopedSystemId,
                    command.EntryId
                );
                await session.ExecuteAsync(q);
            }

            return true;
        }, _options, cancellationToken);
    }

    public async Task<bool> DeleteGlobalAsync(string systemId, string entryId, CancellationToken cancellationToken = default)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var scopedSystemId = GetScopedSystemId(systemId);

            var exists = await ExistsGlobalAsync(systemId, entryId, cancellationToken);
            if (!exists)
                return false;

            var delete = new SimpleStatement(
                "DELETE FROM global_journals_by_system WHERE system_id = ? AND entry_id = ?",
                scopedSystemId,
                entryId
            );
            await session.ExecuteAsync(delete);

            var deleteState = new SimpleStatement(
                "DELETE FROM global_journal_state_by_system WHERE system_id = ? AND entry_id = ?",
                scopedSystemId,
                entryId
            );
            await session.ExecuteAsync(deleteState);

            var deleteAlters = new SimpleStatement(
                "DELETE FROM global_journal_alters_by_system WHERE system_id = ? AND entry_id = ?",
                scopedSystemId,
                entryId
            );
            await session.ExecuteAsync(deleteAlters);

            return true;
        }, _options, cancellationToken);
    }

    public async Task<bool> SetGlobalLockedAsync(string systemId, string entryId, bool locked, CancellationToken cancellationToken = default)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var scopedSystemId = GetScopedSystemId(systemId);

            var exists = await ExistsGlobalAsync(systemId, entryId, cancellationToken);
            if (!exists)
                return false;

            var upsert = new SimpleStatement(
                "UPDATE global_journal_state_by_system SET locked = ? WHERE system_id = ? AND entry_id = ?",
                locked,
                scopedSystemId,
                entryId
            );
            await session.ExecuteAsync(upsert);

            return true;
        }, _options, cancellationToken);
    }

    public async Task<bool> SetGlobalPinnedAsync(string systemId, string entryId, bool pinned, CancellationToken cancellationToken = default)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var scopedSystemId = GetScopedSystemId(systemId);

            var exists = await ExistsGlobalAsync(systemId, entryId, cancellationToken);
            if (!exists)
                return false;

            var upsert = new SimpleStatement(
                "UPDATE global_journal_state_by_system SET pinned = ? WHERE system_id = ? AND entry_id = ?",
                pinned,
                scopedSystemId,
                entryId
            );
            await session.ExecuteAsync(upsert);

            return true;
        }, _options, cancellationToken);
    }

    public async Task<bool> AttachGlobalAlterAsync(string systemId, string entryId, int alterId, CancellationToken cancellationToken = default)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var scopedSystemId = GetScopedSystemId(systemId);

            var exists = await ExistsGlobalAsync(systemId, entryId, cancellationToken);
            if (!exists)
                return false;

            var insert = new SimpleStatement(
                "INSERT INTO global_journal_alters_by_system (system_id, entry_id, alter_id) VALUES (?, ?, ?)",
                scopedSystemId,
                entryId,
                alterId
            );
            await session.ExecuteAsync(insert);

            return true;
        }, _options, cancellationToken);
    }

    public async Task<bool> DetachGlobalAlterAsync(string systemId, string entryId, int alterId, CancellationToken cancellationToken = default)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var scopedSystemId = GetScopedSystemId(systemId);

            var edgeExistsQuery = new SimpleStatement(
                "SELECT alter_id FROM global_journal_alters_by_system WHERE system_id = ? AND entry_id = ? AND alter_id = ? LIMIT 1",
                scopedSystemId,
                entryId,
                alterId
            );

            var edgeRows = await session.ExecuteAsync(edgeExistsQuery);
            if (!edgeRows.Any())
                return false;

            var delete = new SimpleStatement(
                "DELETE FROM global_journal_alters_by_system WHERE system_id = ? AND entry_id = ? AND alter_id = ?",
                scopedSystemId,
                entryId,
                alterId
            );
            await session.ExecuteAsync(delete);

            return true;
        }, _options, cancellationToken);
    }

    public async Task<string?> CreateAlterAsync(string systemId, CreateAlterJournalEntryCommand command, CancellationToken cancellationToken = default)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var scopedSystemId = GetScopedSystemId(systemId);
            var entryId = Guid.NewGuid().ToString("N");

            var insert = new SimpleStatement(
                "INSERT INTO alter_journals_by_system (system_id, entry_id, alter_id, title, content, color, pinned, locked, inserted_at) VALUES (?, ?, ?, ?, ?, ?, ?, ?, toTimestamp(now()))",
                scopedSystemId,
                entryId,
                command.AlterId,
                command.Title,
                null,
                null,
                false,
                false
            );

            await session.ExecuteAsync(insert);
            return entryId;
        }, _options, cancellationToken);
    }

    public async Task<AlterJournalRef?> GetAlterRefAsync(string systemId, string entryId, CancellationToken cancellationToken = default)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var scopedSystemId = GetScopedSystemId(systemId);

            var query = new SimpleStatement(
                "SELECT entry_id, alter_id FROM alter_journals_by_system WHERE system_id = ? AND entry_id = ? LIMIT 1",
                scopedSystemId,
                entryId
            );

            var row = (await session.ExecuteAsync(query)).FirstOrDefault();
            return row is null
                ? null
                : new AlterJournalRef(row.GetValue<string>("entry_id"), row.GetValue<int>("alter_id"));
        }, _options, cancellationToken);
    }

    public async Task<bool> UpdateAlterAsync(string systemId, UpdateAlterJournalEntryCommand command, CancellationToken cancellationToken = default)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var scopedSystemId = GetScopedSystemId(systemId);

            var exists = await GetAlterRefAsync(systemId, command.EntryId, cancellationToken);
            if (exists is null)
                return false;

            if (command.Title is not null)
            {
                var q = new SimpleStatement(
                    "UPDATE alter_journals_by_system SET title = ? WHERE system_id = ? AND entry_id = ?",
                    command.Title,
                    scopedSystemId,
                    command.EntryId
                );
                await session.ExecuteAsync(q);
            }

            if (command.Content is not null)
            {
                var q = new SimpleStatement(
                    "UPDATE alter_journals_by_system SET content = ? WHERE system_id = ? AND entry_id = ?",
                    command.Content,
                    scopedSystemId,
                    command.EntryId
                );
                await session.ExecuteAsync(q);
            }

            if (command.Color is not null)
            {
                var q = new SimpleStatement(
                    "UPDATE alter_journals_by_system SET color = ? WHERE system_id = ? AND entry_id = ?",
                    command.Color,
                    scopedSystemId,
                    command.EntryId
                );
                await session.ExecuteAsync(q);
            }

            return true;
        }, _options, cancellationToken);
    }

    public async Task<bool> DeleteAlterAsync(string systemId, string entryId, CancellationToken cancellationToken = default)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var scopedSystemId = GetScopedSystemId(systemId);

            var exists = await GetAlterRefAsync(systemId, entryId, cancellationToken);
            if (exists is null)
                return false;

            var delete = new SimpleStatement(
                "DELETE FROM alter_journals_by_system WHERE system_id = ? AND entry_id = ?",
                scopedSystemId,
                entryId
            );
            await session.ExecuteAsync(delete);

            return true;
        }, _options, cancellationToken);
    }

    public async Task<bool> SetAlterLockedAsync(string systemId, string entryId, bool locked, CancellationToken cancellationToken = default)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var scopedSystemId = GetScopedSystemId(systemId);

            var exists = await GetAlterRefAsync(systemId, entryId, cancellationToken);
            if (exists is null)
                return false;

            var q = new SimpleStatement(
                "UPDATE alter_journals_by_system SET locked = ? WHERE system_id = ? AND entry_id = ?",
                locked,
                scopedSystemId,
                entryId
            );
            await session.ExecuteAsync(q);

            return true;
        }, _options, cancellationToken);
    }

    public async Task<bool> SetAlterPinnedAsync(string systemId, string entryId, bool pinned, CancellationToken cancellationToken = default)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var scopedSystemId = GetScopedSystemId(systemId);

            var exists = await GetAlterRefAsync(systemId, entryId, cancellationToken);
            if (exists is null)
                return false;

            var q = new SimpleStatement(
                "UPDATE alter_journals_by_system SET pinned = ? WHERE system_id = ? AND entry_id = ?",
                pinned,
                scopedSystemId,
                entryId
            );
            await session.ExecuteAsync(q);

            return true;
        }, _options, cancellationToken);
    }

    public async Task<IReadOnlyList<AlterJournalReadModel>> ListAlterAsync(string systemId, int alterId, CancellationToken cancellationToken = default)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var scopedSystemId = GetScopedSystemId(systemId);

            var query = new SimpleStatement(
                "SELECT entry_id, alter_id, title, content, color, pinned, locked FROM alter_journals_by_system WHERE system_id = ? AND alter_id = ? ALLOW FILTERING",
                scopedSystemId,
                alterId
            );

            var rows = await session.ExecuteAsync(query);
            return rows
                .Select(row => new AlterJournalReadModel(
                    row.GetValue<string>("entry_id"),
                    row.GetValue<int>("alter_id"),
                    row.GetValue<string>("title"),
                    row.GetValue<string?>("content"),
                    row.GetValue<string?>("color"),
                    row.GetValue<bool>("pinned"),
                    row.GetValue<bool>("locked")))
                .OrderByDescending(e => e.EntryId)
                .ToArray();
        }, _options, cancellationToken);
    }

    public async Task<AlterJournalReadModel?> GetAlterAsync(string systemId, string entryId, CancellationToken cancellationToken = default)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var scopedSystemId = GetScopedSystemId(systemId);

            var query = new SimpleStatement(
                "SELECT entry_id, alter_id, title, content, color, pinned, locked FROM alter_journals_by_system WHERE system_id = ? AND entry_id = ? LIMIT 1",
                scopedSystemId,
                entryId
            );

            var row = (await session.ExecuteAsync(query)).FirstOrDefault();
            return row is null
                ? null
                : new AlterJournalReadModel(
                    row.GetValue<string>("entry_id"),
                    row.GetValue<int>("alter_id"),
                    row.GetValue<string>("title"),
                    row.GetValue<string?>("content"),
                    row.GetValue<string?>("color"),
                    row.GetValue<bool>("pinned"),
                    row.GetValue<bool>("locked"));
        }, _options, cancellationToken);
    }

    public async Task<IReadOnlyList<GlobalJournalReadModel>> ListGlobalAsync(string systemId, CancellationToken cancellationToken = default)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var scopedSystemId = GetScopedSystemId(systemId);

            var entriesQuery = new SimpleStatement(
                "SELECT entry_id, title, content, color FROM global_journals_by_system WHERE system_id = ?",
                scopedSystemId
            );
            var entryRows = await session.ExecuteAsync(entriesQuery);

            var statesQuery = new SimpleStatement(
                "SELECT entry_id, pinned, locked FROM global_journal_state_by_system WHERE system_id = ?",
                scopedSystemId
            );
            var stateRows = await session.ExecuteAsync(statesQuery);
            var stateMap = stateRows.ToDictionary(
                r => r.GetValue<string>("entry_id"),
                r => (Pinned: r.GetValue<bool>("pinned"), Locked: r.GetValue<bool>("locked")));

            var result = new List<GlobalJournalReadModel>();
            foreach (var row in entryRows)
            {
                var eid = row.GetValue<string>("entry_id");

                var altersQuery = new SimpleStatement(
                    "SELECT alter_id FROM global_journal_alters_by_system WHERE system_id = ? AND entry_id = ?",
                    scopedSystemId,
                    eid
                );
                var alterRows = await session.ExecuteAsync(altersQuery);
                var alterIds = alterRows.Select(r => r.GetValue<int>("alter_id")).ToArray();

                stateMap.TryGetValue(eid, out var state);
                result.Add(new GlobalJournalReadModel(
                    eid,
                    row.GetValue<string>("title"),
                    row.GetValue<string?>("content"),
                    row.GetValue<string?>("color"),
                    state.Pinned,
                    state.Locked,
                    alterIds));
            }

            return (IReadOnlyList<GlobalJournalReadModel>)result
                .OrderByDescending(e => e.EntryId)
                .ToArray();
        }, _options, cancellationToken);
    }

    public async Task<GlobalJournalReadModel?> GetGlobalAsync(string systemId, string entryId, CancellationToken cancellationToken = default)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var scopedSystemId = GetScopedSystemId(systemId);

            var entryQuery = new SimpleStatement(
                "SELECT entry_id, title, content, color FROM global_journals_by_system WHERE system_id = ? AND entry_id = ? LIMIT 1",
                scopedSystemId,
                entryId
            );
            var entryRow = (await session.ExecuteAsync(entryQuery)).FirstOrDefault();
            if (entryRow is null)
                return null;

            var stateQuery = new SimpleStatement(
                "SELECT pinned, locked FROM global_journal_state_by_system WHERE system_id = ? AND entry_id = ? LIMIT 1",
                scopedSystemId,
                entryId
            );
            var stateRow = (await session.ExecuteAsync(stateQuery)).FirstOrDefault();

            var altersQuery = new SimpleStatement(
                "SELECT alter_id FROM global_journal_alters_by_system WHERE system_id = ? AND entry_id = ?",
                scopedSystemId,
                entryId
            );
            var alterIds = (await session.ExecuteAsync(altersQuery))
                .Select(r => r.GetValue<int>("alter_id"))
                .ToArray();

            return new GlobalJournalReadModel(
                entryRow.GetValue<string>("entry_id"),
                entryRow.GetValue<string>("title"),
                entryRow.GetValue<string?>("content"),
                entryRow.GetValue<string?>("color"),
                stateRow?.GetValue<bool>("pinned") ?? false,
                stateRow?.GetValue<bool>("locked") ?? false,
                alterIds);
        }, _options, cancellationToken);
    }

    private string GetScopedSystemId(string systemId)
    {
        var region = _regionContext.ResolveUserRegion(systemId);
        return $"{region}:{systemId}";
    }
}
