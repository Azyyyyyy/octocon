using Cassandra;
using Interfold.Domain.Journals;
using Interfold.Infrastructure.Configuration;
using Interfold.Infrastructure.Persistence.Transient;

namespace Interfold.Infrastructure.Persistence.Scylla;

public sealed class ScyllaJournalRepository : IJournalRepository
{
    private readonly IScyllaSessionProvider _sessionProvider;
    private readonly IScyllaKeyspaceResolver _keyspaceResolver;
    private readonly PersistenceConfiguration _options;

    public ScyllaJournalRepository(
        IScyllaSessionProvider sessionProvider,
        IScyllaKeyspaceResolver keyspaceResolver,
        PersistenceConfiguration options)
    {
        _sessionProvider = sessionProvider;
        _keyspaceResolver = keyspaceResolver;
        _options = options;
    }

    public async Task<string?> CreateGlobalAsync(string systemId, CreateGlobalJournalEntryCommand command, CancellationToken cancellationToken = default)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var normalizedSystemId = _keyspaceResolver.NormalizeSystemId(systemId);
            var keyspace = _keyspaceResolver.ResolveRegionalKeyspace(systemId);
            var entryId = Guid.NewGuid();

            var insert = new SimpleStatement(
                $"INSERT INTO {keyspace}.global_journals (user_id, id, title, content, color, pinned, locked, inserted_at, updated_at) VALUES (?, ?, ?, ?, ?, ?, ?, toTimestamp(now()), toTimestamp(now()))",
                normalizedSystemId,
                entryId,
                command.Title,
                null,
                null,
                false,
                false
            );

            await session.ExecuteAsync(insert);
            return entryId.ToString("N");
        }, _options, cancellationToken);
    }

    public async Task<bool> ExistsGlobalAsync(string systemId, string entryId, CancellationToken cancellationToken = default)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            if (!TryParseUuid(entryId, out var entryGuid))
            {
                return false;
            }

            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var normalizedSystemId = _keyspaceResolver.NormalizeSystemId(systemId);
            var keyspace = _keyspaceResolver.ResolveRegionalKeyspace(systemId);

            var query = new SimpleStatement(
                $"SELECT id FROM {keyspace}.global_journals WHERE user_id = ? AND id = ? LIMIT 1",
                normalizedSystemId,
                entryGuid
            );

            var rows = await session.ExecuteAsync(query);
            return rows.Any();
        }, _options, cancellationToken);
    }

    public async Task<bool> UpdateGlobalAsync(string systemId, UpdateGlobalJournalEntryCommand command, CancellationToken cancellationToken = default)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            if (!TryParseUuid(command.EntryId, out var entryGuid))
            {
                return false;
            }

            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var normalizedSystemId = _keyspaceResolver.NormalizeSystemId(systemId);
            var keyspace = _keyspaceResolver.ResolveRegionalKeyspace(systemId);

            var exists = await ExistsGlobalAsync(systemId, command.EntryId, cancellationToken);
            if (!exists)
                return false;

            if (command.Title is not null)
            {
                var q = new SimpleStatement(
                    $"UPDATE {keyspace}.global_journals SET title = ?, updated_at = toTimestamp(now()) WHERE user_id = ? AND id = ?",
                    command.Title,
                    normalizedSystemId,
                    entryGuid
                );
                await session.ExecuteAsync(q);
            }

            if (command.Content is not null)
            {
                var q = new SimpleStatement(
                    $"UPDATE {keyspace}.global_journals SET content = ?, updated_at = toTimestamp(now()) WHERE user_id = ? AND id = ?",
                    command.Content,
                    normalizedSystemId,
                    entryGuid
                );
                await session.ExecuteAsync(q);
            }

            if (command.Color is not null)
            {
                var q = new SimpleStatement(
                    $"UPDATE {keyspace}.global_journals SET color = ?, updated_at = toTimestamp(now()) WHERE user_id = ? AND id = ?",
                    command.Color,
                    normalizedSystemId,
                    entryGuid
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
            if (!TryParseUuid(entryId, out var entryGuid))
            {
                return false;
            }

            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var normalizedSystemId = _keyspaceResolver.NormalizeSystemId(systemId);
            var keyspace = _keyspaceResolver.ResolveRegionalKeyspace(systemId);

            var exists = await ExistsGlobalAsync(systemId, entryId, cancellationToken);
            if (!exists)
                return false;

            var delete = new SimpleStatement(
                $"DELETE FROM {keyspace}.global_journals WHERE user_id = ? AND id = ?",
                normalizedSystemId,
                entryGuid
            );
            await session.ExecuteAsync(delete);

            var deleteAlters = new SimpleStatement(
                $"DELETE FROM {keyspace}.global_journal_alters WHERE user_id = ? AND global_journal_id = ?",
                normalizedSystemId,
                entryGuid
            );
            await session.ExecuteAsync(deleteAlters);

            return true;
        }, _options, cancellationToken);
    }

    public async Task<bool> SetGlobalLockedAsync(string systemId, string entryId, bool locked, CancellationToken cancellationToken = default)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            if (!TryParseUuid(entryId, out var entryGuid))
            {
                return false;
            }

            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var normalizedSystemId = _keyspaceResolver.NormalizeSystemId(systemId);
            var keyspace = _keyspaceResolver.ResolveRegionalKeyspace(systemId);

            var exists = await ExistsGlobalAsync(systemId, entryId, cancellationToken);
            if (!exists)
                return false;

            var upsert = new SimpleStatement(
                $"UPDATE {keyspace}.global_journals SET locked = ?, updated_at = toTimestamp(now()) WHERE user_id = ? AND id = ?",
                locked,
                normalizedSystemId,
                entryGuid
            );
            await session.ExecuteAsync(upsert);

            return true;
        }, _options, cancellationToken);
    }

    public async Task<bool> SetGlobalPinnedAsync(string systemId, string entryId, bool pinned, CancellationToken cancellationToken = default)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            if (!TryParseUuid(entryId, out var entryGuid))
            {
                return false;
            }

            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var normalizedSystemId = _keyspaceResolver.NormalizeSystemId(systemId);
            var keyspace = _keyspaceResolver.ResolveRegionalKeyspace(systemId);

            var exists = await ExistsGlobalAsync(systemId, entryId, cancellationToken);
            if (!exists)
                return false;

            var upsert = new SimpleStatement(
                $"UPDATE {keyspace}.global_journals SET pinned = ?, updated_at = toTimestamp(now()) WHERE user_id = ? AND id = ?",
                pinned,
                normalizedSystemId,
                entryGuid
            );
            await session.ExecuteAsync(upsert);

            return true;
        }, _options, cancellationToken);
    }

    public async Task<bool> AttachGlobalAlterAsync(string systemId, string entryId, int alterId, CancellationToken cancellationToken = default)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            if (!TryParseUuid(entryId, out var entryGuid))
            {
                return false;
            }

            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var normalizedSystemId = _keyspaceResolver.NormalizeSystemId(systemId);
            var keyspace = _keyspaceResolver.ResolveRegionalKeyspace(systemId);

            var exists = await ExistsGlobalAsync(systemId, entryId, cancellationToken);
            if (!exists)
                return false;

            var insert = new SimpleStatement(
                $"INSERT INTO {keyspace}.global_journal_alters (user_id, global_journal_id, alter_id, inserted_at, updated_at) VALUES (?, ?, ?, toTimestamp(now()), toTimestamp(now()))",
                normalizedSystemId,
                entryGuid,
                (short)alterId
            );
            await session.ExecuteAsync(insert);

            return true;
        }, _options, cancellationToken);
    }

    public async Task<bool> DetachGlobalAlterAsync(string systemId, string entryId, int alterId, CancellationToken cancellationToken = default)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            if (!TryParseUuid(entryId, out var entryGuid))
            {
                return false;
            }

            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var normalizedSystemId = _keyspaceResolver.NormalizeSystemId(systemId);
            var keyspace = _keyspaceResolver.ResolveRegionalKeyspace(systemId);

            var edgeExistsQuery = new SimpleStatement(
                $"SELECT alter_id FROM {keyspace}.global_journal_alters WHERE user_id = ? AND global_journal_id = ? AND alter_id = ? LIMIT 1",
                normalizedSystemId,
                entryGuid,
                (short)alterId
            );

            var edgeRows = await session.ExecuteAsync(edgeExistsQuery);
            if (!edgeRows.Any())
                return false;

            var delete = new SimpleStatement(
                $"DELETE FROM {keyspace}.global_journal_alters WHERE user_id = ? AND global_journal_id = ? AND alter_id = ?",
                normalizedSystemId,
                entryGuid,
                (short)alterId
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
            var normalizedSystemId = _keyspaceResolver.NormalizeSystemId(systemId);
            var keyspace = _keyspaceResolver.ResolveRegionalKeyspace(systemId);
            var entryId = Guid.NewGuid();

            var insert = new SimpleStatement(
                $"INSERT INTO {keyspace}.alter_journals (user_id, id, alter_id, title, content, color, pinned, locked, inserted_at, updated_at) VALUES (?, ?, ?, ?, ?, ?, ?, ?, toTimestamp(now()), toTimestamp(now()))",
                normalizedSystemId,
                entryId,
                (short)command.AlterId,
                command.Title,
                null,
                null,
                false,
                false
            );

            await session.ExecuteAsync(insert);
            return entryId.ToString("N");
        }, _options, cancellationToken);
    }

    public async Task<AlterJournalRef?> GetAlterRefAsync(string systemId, string entryId, CancellationToken cancellationToken = default)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            if (!TryParseUuid(entryId, out var entryGuid))
            {
                return null;
            }

            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var normalizedSystemId = _keyspaceResolver.NormalizeSystemId(systemId);
            var keyspace = _keyspaceResolver.ResolveRegionalKeyspace(systemId);

            var query = new SimpleStatement(
                $"SELECT id, alter_id FROM {keyspace}.alter_journals WHERE user_id = ? AND id = ? ALLOW FILTERING",
                normalizedSystemId,
                entryGuid
            );

            var row = (await session.ExecuteAsync(query)).FirstOrDefault();
            return row is null
                ? null
                : new AlterJournalRef(row.GetValue<Guid>("id").ToString("N"), row.GetValue<short>("alter_id"));
        }, _options, cancellationToken);
    }

    public async Task<bool> UpdateAlterAsync(string systemId, UpdateAlterJournalEntryCommand command, CancellationToken cancellationToken = default)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            var reference = await GetAlterRefAsync(systemId, command.EntryId, cancellationToken);
            if (reference is null || !TryParseUuid(reference.EntryId, out var entryGuid))
            {
                return false;
            }

            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var normalizedSystemId = _keyspaceResolver.NormalizeSystemId(systemId);
            var keyspace = _keyspaceResolver.ResolveRegionalKeyspace(systemId);

            if (command.Title is not null)
            {
                var q = new SimpleStatement(
                    $"UPDATE {keyspace}.alter_journals SET title = ?, updated_at = toTimestamp(now()) WHERE user_id = ? AND id = ? AND alter_id = ?",
                    command.Title,
                    normalizedSystemId,
                    entryGuid,
                    (short)reference.AlterId
                );
                await session.ExecuteAsync(q);
            }

            if (command.Content is not null)
            {
                var q = new SimpleStatement(
                    $"UPDATE {keyspace}.alter_journals SET content = ?, updated_at = toTimestamp(now()) WHERE user_id = ? AND id = ? AND alter_id = ?",
                    command.Content,
                    normalizedSystemId,
                    entryGuid,
                    (short)reference.AlterId
                );
                await session.ExecuteAsync(q);
            }

            if (command.Color is not null)
            {
                var q = new SimpleStatement(
                    $"UPDATE {keyspace}.alter_journals SET color = ?, updated_at = toTimestamp(now()) WHERE user_id = ? AND id = ? AND alter_id = ?",
                    command.Color,
                    normalizedSystemId,
                    entryGuid,
                    (short)reference.AlterId
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
            var reference = await GetAlterRefAsync(systemId, entryId, cancellationToken);
            if (reference is null || !TryParseUuid(reference.EntryId, out var entryGuid))
            {
                return false;
            }

            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var normalizedSystemId = _keyspaceResolver.NormalizeSystemId(systemId);
            var keyspace = _keyspaceResolver.ResolveRegionalKeyspace(systemId);

            var delete = new SimpleStatement(
                $"DELETE FROM {keyspace}.alter_journals WHERE user_id = ? AND id = ? AND alter_id = ?",
                normalizedSystemId,
                entryGuid,
                (short)reference.AlterId
            );
            await session.ExecuteAsync(delete);

            return true;
        }, _options, cancellationToken);
    }

    public async Task<bool> SetAlterLockedAsync(string systemId, string entryId, bool locked, CancellationToken cancellationToken = default)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            var reference = await GetAlterRefAsync(systemId, entryId, cancellationToken);
            if (reference is null || !TryParseUuid(reference.EntryId, out var entryGuid))
            {
                return false;
            }

            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var normalizedSystemId = _keyspaceResolver.NormalizeSystemId(systemId);
            var keyspace = _keyspaceResolver.ResolveRegionalKeyspace(systemId);

            var q = new SimpleStatement(
                $"UPDATE {keyspace}.alter_journals SET locked = ?, updated_at = toTimestamp(now()) WHERE user_id = ? AND id = ? AND alter_id = ?",
                locked,
                normalizedSystemId,
                entryGuid,
                (short)reference.AlterId
            );
            await session.ExecuteAsync(q);

            return true;
        }, _options, cancellationToken);
    }

    public async Task<bool> SetAlterPinnedAsync(string systemId, string entryId, bool pinned, CancellationToken cancellationToken = default)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            var reference = await GetAlterRefAsync(systemId, entryId, cancellationToken);
            if (reference is null || !TryParseUuid(reference.EntryId, out var entryGuid))
            {
                return false;
            }

            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var normalizedSystemId = _keyspaceResolver.NormalizeSystemId(systemId);
            var keyspace = _keyspaceResolver.ResolveRegionalKeyspace(systemId);

            var q = new SimpleStatement(
                $"UPDATE {keyspace}.alter_journals SET pinned = ?, updated_at = toTimestamp(now()) WHERE user_id = ? AND id = ? AND alter_id = ?",
                pinned,
                normalizedSystemId,
                entryGuid,
                (short)reference.AlterId
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
            var normalizedSystemId = _keyspaceResolver.NormalizeSystemId(systemId);
            var keyspace = _keyspaceResolver.ResolveRegionalKeyspace(systemId);

            var query = new SimpleStatement(
                $"SELECT id, alter_id, title, content, color, pinned, locked FROM {keyspace}.alter_journals WHERE user_id = ? AND alter_id = ?",
                normalizedSystemId,
                (short)alterId
            );

            var rows = await session.ExecuteAsync(query);
            return rows
                .Select(row => new AlterJournalReadModel(
                    row.GetValue<Guid>("id").ToString("N"),
                    row.GetValue<short>("alter_id"),
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
            if (!TryParseUuid(entryId, out var entryGuid))
            {
                return null;
            }

            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var normalizedSystemId = _keyspaceResolver.NormalizeSystemId(systemId);
            var keyspace = _keyspaceResolver.ResolveRegionalKeyspace(systemId);

            var query = new SimpleStatement(
                $"SELECT id, alter_id, title, content, color, pinned, locked FROM {keyspace}.alter_journals WHERE user_id = ? AND id = ? ALLOW FILTERING",
                normalizedSystemId,
                entryGuid
            );

            var row = (await session.ExecuteAsync(query)).FirstOrDefault();
            return row is null
                ? null
                : new AlterJournalReadModel(
                    row.GetValue<Guid>("id").ToString("N"),
                    row.GetValue<short>("alter_id"),
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
            var normalizedSystemId = _keyspaceResolver.NormalizeSystemId(systemId);
            var keyspace = _keyspaceResolver.ResolveRegionalKeyspace(systemId);

            var entriesQuery = new SimpleStatement(
                $"SELECT id, title, content, color, pinned, locked FROM {keyspace}.global_journals WHERE user_id = ?",
                normalizedSystemId
            );
            var entryRows = await session.ExecuteAsync(entriesQuery);

            var result = new List<GlobalJournalReadModel>();
            foreach (var row in entryRows)
            {
                var id = row.GetValue<Guid>("id");
                var altersQuery = new SimpleStatement(
                    $"SELECT alter_id FROM {keyspace}.global_journal_alters WHERE user_id = ? AND global_journal_id = ?",
                    normalizedSystemId,
                    id
                );
                var alterRows = await session.ExecuteAsync(altersQuery);
                var alterIds = alterRows.Select(r => (int)r.GetValue<short>("alter_id")).ToArray();

                result.Add(new GlobalJournalReadModel(
                    id.ToString("N"),
                    row.GetValue<string>("title"),
                    row.GetValue<string?>("content"),
                    row.GetValue<string?>("color"),
                    row.GetValue<bool>("pinned"),
                    row.GetValue<bool>("locked"),
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
            if (!TryParseUuid(entryId, out var entryGuid))
            {
                return null;
            }

            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var normalizedSystemId = _keyspaceResolver.NormalizeSystemId(systemId);
            var keyspace = _keyspaceResolver.ResolveRegionalKeyspace(systemId);

            var entryQuery = new SimpleStatement(
                $"SELECT id, title, content, color, pinned, locked FROM {keyspace}.global_journals WHERE user_id = ? AND id = ? LIMIT 1",
                normalizedSystemId,
                entryGuid
            );
            var entryRow = (await session.ExecuteAsync(entryQuery)).FirstOrDefault();
            if (entryRow is null)
                return null;

            var altersQuery = new SimpleStatement(
                $"SELECT alter_id FROM {keyspace}.global_journal_alters WHERE user_id = ? AND global_journal_id = ?",
                normalizedSystemId,
                entryGuid
            );
            var alterIds = (await session.ExecuteAsync(altersQuery))
                .Select(r => (int)r.GetValue<short>("alter_id"))
                .ToArray();

            return new GlobalJournalReadModel(
                entryRow.GetValue<Guid>("id").ToString("N"),
                entryRow.GetValue<string>("title"),
                entryRow.GetValue<string?>("content"),
                entryRow.GetValue<string?>("color"),
                entryRow.GetValue<bool>("pinned"),
                entryRow.GetValue<bool>("locked"),
                alterIds);
        }, _options, cancellationToken);
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
