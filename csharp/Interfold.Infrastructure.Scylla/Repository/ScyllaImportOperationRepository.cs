using Cassandra;
using Interfold.Contracts.Configuration;
using Interfold.Contracts.Models.ImportOperations;
using Interfold.Domain.Abstractions.Repository;
using Interfold.Infrastructure.Persistence;
using Microsoft.Extensions.Logging;

namespace Interfold.Infrastructure.Scylla.Repository;

/// <summary>
/// Cassandra / ScyllaDB port of <see cref="IImportOperationRepository"/>. Talks to the
/// two tables created by <c>004_import_operations.templated.cql</c>:
/// <c>import_operations</c> (history) and <c>active_import_by_system</c> (per-system
/// mutex pointer).
///
/// <para>
/// <b>The LWT mutex.</b> Every mutating call that needs to be racing-safe uses Cassandra
/// Lightweight Transactions (Paxos). <see cref="TryClaimAsync"/> uses
/// <c>INSERT … IF NOT EXISTS</c> on the pointer table — if a concurrent dispatcher just
/// took the slot, this insert fails cleanly and the caller falls through to a SELECT to
/// learn the winning operation_id. Terminal transitions
/// (<see cref="MarkSucceededAsync"/> / <see cref="MarkFailedAsync"/>) release the slot
/// with <c>DELETE … IF operation_id = ?</c> so a stale call from (say) a restart-sweep
/// can't evict an unrelated in-flight operation that took the slot afterwards.
/// </para>
///
/// <para>
/// <b>TimeUuid vs Guid.</b> The contract surface uses <see cref="Guid"/> for portability
/// (controllers and HTTP responses already speak Guid). The driver column type is
/// <c>timeuuid</c> — convert with <c>(TimeUuid)guid</c> at the bind site and
/// <c>row.GetValue&lt;TimeUuid&gt;("operation_id").ToGuid()</c> on read. New ids are
/// minted with <see cref="TimeUuid.NewId()"/> so the clustering order (DESC) sorts by
/// wall-clock creation time without an extra timestamp column.
/// </para>
/// </summary>
public sealed class ScyllaImportOperationRepository : IImportOperationRepository
{
    private readonly IScyllaSessionProvider _sessionProvider;
    private readonly IScyllaKeyspaceResolver _keyspaceResolver;
    private readonly PersistenceConfiguration _options;
    private readonly ILogger<ScyllaImportOperationRepository> _logger;

    public ScyllaImportOperationRepository(
        IScyllaSessionProvider sessionProvider,
        IScyllaKeyspaceResolver keyspaceResolver,
        PersistenceConfiguration options,
        ILogger<ScyllaImportOperationRepository> logger)
    {
        _sessionProvider = sessionProvider;
        _keyspaceResolver = keyspaceResolver;
        _options = options;
        _logger = logger;
    }

    public async Task<ImportOperationClaim> TryClaimAsync(
        string systemId,
        string kind,
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var keyspace = _keyspaceResolver.ResolveRegionalKeyspace(systemId);
            var normalizedSystemId = _keyspaceResolver.NormalizeSystemId(systemId);
            var now = DateTimeOffset.UtcNow;
            var newOperationId = TimeUuid.NewId();

            // LWT INSERT IF NOT EXISTS — Paxos round, but per-system contention is by
            // definition single-digit and we accept the latency cost (a few hundred ms
            // on Cassandra) for the strong dedupe guarantee.
            var claim = new SimpleStatement(
                $"INSERT INTO {keyspace}.active_import_by_system " +
                "(system_id, kind, operation_id, started_at) VALUES (?, ?, ?, ?) IF NOT EXISTS",
                normalizedSystemId, kind, newOperationId, now.UtcDateTime);

            var claimResult = await session.ExecuteAsync(claim);
            var claimRow = claimResult.FirstOrDefault();
            // LWT result shape: a single row with [applied] = true/false plus existing
            // column values when false.
            var applied = claimRow?.GetValue<bool>("[applied]") ?? false;

            if (!applied)
            {
                // Slot was already taken. Read the existing operation id and return it so
                // the caller short-circuits without dispatching a second worker run.
                var existingId = claimRow!.GetValue<TimeUuid>("operation_id").ToGuid();
                _logger.LogInformation(
                    "[import-ops] Collapsed duplicate dispatch for system={SystemId} kind={Kind} onto operation_id={OperationId}.",
                    normalizedSystemId, kind, existingId);
                return new ImportOperationClaim(existingId, IsNew: false);
            }

            // Slot is ours — insert the history row alongside. This insert is not
            // LWT'd: the (system_id, operation_id) pair is unique by construction
            // (operation_id is a fresh TimeUuid), so an ordinary INSERT cannot collide.
            var historyInsert = new SimpleStatement(
                $"INSERT INTO {keyspace}.import_operations " +
                "(system_id, operation_id, kind, status, started_at, idempotency_key) " +
                "VALUES (?, ?, ?, ?, ?, ?)",
                normalizedSystemId, newOperationId, kind,
                ImportOperationStatus.Queued.ToString(),
                now.UtcDateTime, idempotencyKey);
            await session.ExecuteAsync(historyInsert);

            return new ImportOperationClaim(newOperationId.ToGuid(), IsNew: true);
        }, _options, cancellationToken, _logger);
    }

    public async Task MarkRunningAsync(
        string systemId,
        Guid operationId,
        CancellationToken cancellationToken = default)
    {
        await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var keyspace = _keyspaceResolver.ResolveRegionalKeyspace(systemId);
            var normalizedSystemId = _keyspaceResolver.NormalizeSystemId(systemId);

            // Conditional update keeps the transition idempotent: a re-pickup of an
            // already-Running row leaves it untouched and the LWT returns [applied]=false,
            // which we silently swallow.
            var update = new SimpleStatement(
                $"UPDATE {keyspace}.import_operations SET status = ? " +
                "WHERE system_id = ? AND operation_id = ? IF status = ?",
                ImportOperationStatus.Running.ToString(),
                normalizedSystemId, (TimeUuid)operationId,
                ImportOperationStatus.Queued.ToString());
            await session.ExecuteAsync(update);
            return true;
        }, _options, cancellationToken, _logger);
    }

    public async Task MarkSucceededAsync(
        string systemId,
        Guid operationId,
        string kind,
        int alterCount,
        CancellationToken cancellationToken = default)
    {
        await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var keyspace = _keyspaceResolver.ResolveRegionalKeyspace(systemId);
            var normalizedSystemId = _keyspaceResolver.NormalizeSystemId(systemId);
            var now = DateTimeOffset.UtcNow;

            var batch = new BatchStatement();
            batch.Add(new SimpleStatement(
                $"UPDATE {keyspace}.import_operations SET status = ?, finished_at = ?, alter_count = ? " +
                "WHERE system_id = ? AND operation_id = ?",
                ImportOperationStatus.Succeeded.ToString(), now.UtcDateTime, alterCount,
                normalizedSystemId, (TimeUuid)operationId));
            await session.ExecuteAsync(batch);

            await ReleaseSlot(session, keyspace, normalizedSystemId, kind, (TimeUuid)operationId);
            return true;
        }, _options, cancellationToken, _logger);
    }

    public async Task MarkFailedAsync(
        string systemId,
        Guid operationId,
        string kind,
        string errorCode,
        string? errorMessage,
        CancellationToken cancellationToken = default)
    {
        await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var keyspace = _keyspaceResolver.ResolveRegionalKeyspace(systemId);
            var normalizedSystemId = _keyspaceResolver.NormalizeSystemId(systemId);
            var now = DateTimeOffset.UtcNow;

            var update = new SimpleStatement(
                $"UPDATE {keyspace}.import_operations SET status = ?, finished_at = ?, error_code = ?, error_message = ? " +
                "WHERE system_id = ? AND operation_id = ?",
                ImportOperationStatus.Failed.ToString(), now.UtcDateTime, errorCode, errorMessage,
                normalizedSystemId, (TimeUuid)operationId);
            await session.ExecuteAsync(update);

            await ReleaseSlot(session, keyspace, normalizedSystemId, kind, (TimeUuid)operationId);
            return true;
        }, _options, cancellationToken, _logger);
    }

    public async Task<ImportOperationSnapshot?> GetByIdAsync(
        string systemId,
        Guid operationId,
        CancellationToken cancellationToken = default)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var keyspace = _keyspaceResolver.ResolveRegionalKeyspace(systemId);
            var normalizedSystemId = _keyspaceResolver.NormalizeSystemId(systemId);

            var query = new SimpleStatement(
                $"SELECT system_id, operation_id, kind, status, started_at, finished_at, " +
                $"alter_count, error_code, error_message, idempotency_key " +
                $"FROM {keyspace}.import_operations WHERE system_id = ? AND operation_id = ? LIMIT 1",
                normalizedSystemId, (TimeUuid)operationId);

            var rows = await session.ExecuteAsync(query);
            var row = rows.FirstOrDefault();
            return row is null ? null : MapRow(row);
        }, _options, cancellationToken, _logger);
    }

    public async Task<Guid?> GetActiveOperationIdAsync(
        string systemId,
        string kind,
        CancellationToken cancellationToken = default)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var keyspace = _keyspaceResolver.ResolveRegionalKeyspace(systemId);
            var normalizedSystemId = _keyspaceResolver.NormalizeSystemId(systemId);

            var query = new SimpleStatement(
                $"SELECT operation_id FROM {keyspace}.active_import_by_system WHERE system_id = ? AND kind = ?",
                normalizedSystemId, kind);

            var rows = await session.ExecuteAsync(query);
            var row = rows.FirstOrDefault();
            return row is null ? (Guid?)null : row.GetValue<TimeUuid>("operation_id").ToGuid();
        }, _options, cancellationToken, _logger);
    }

    public async Task<IReadOnlyList<ImportOperationSnapshot>> GetStaleRunningAsync(
        TimeSpan olderThan,
        CancellationToken cancellationToken = default)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var keyspace = _keyspaceResolver.DefaultKeyspace;
            var cutoff = DateTimeOffset.UtcNow - olderThan;

            // ALLOW FILTERING is acceptable here: the table is small (one row per import
            // click), this query runs only on startup, and there's no obvious secondary
            // table that would be cheaper. If the table ever grows we can add a dedicated
            // `imports_by_status_started` denormalisation.
            var query = new SimpleStatement(
                $"SELECT system_id, operation_id, kind, status, started_at, finished_at, " +
                $"alter_count, error_code, error_message, idempotency_key " +
                $"FROM {keyspace}.import_operations " +
                "WHERE status = ? AND started_at < ? ALLOW FILTERING",
                ImportOperationStatus.Running.ToString(), cutoff.UtcDateTime);

            var rows = await session.ExecuteAsync(query);
            var list = new List<ImportOperationSnapshot>();
            foreach (var row in rows)
            {
                list.Add(MapRow(row));
            }
            return (IReadOnlyList<ImportOperationSnapshot>)list;
        }, _options, cancellationToken, _logger);
    }

    private static async Task ReleaseSlot(
        ISession session,
        string keyspace,
        string normalizedSystemId,
        string kind,
        TimeUuid operationId)
    {
        // The IF clause guards against evicting an unrelated in-flight operation that took
        // the slot between our terminal call and this delete. [applied]=false here is
        // expected behaviour for the sweep path and is silently swallowed.
        var delete = new SimpleStatement(
            $"DELETE FROM {keyspace}.active_import_by_system " +
            "WHERE system_id = ? AND kind = ? IF operation_id = ?",
            normalizedSystemId, kind, operationId);
        await session.ExecuteAsync(delete);
    }

    private static ImportOperationSnapshot MapRow(Row row)
    {
        var statusText = row.GetValue<string>("status");
        var status = Enum.TryParse<ImportOperationStatus>(statusText, ignoreCase: true, out var parsed)
            ? parsed
            : ImportOperationStatus.Queued;

        var startedAt = new DateTimeOffset(row.GetValue<DateTime>("started_at"), TimeSpan.Zero);
        var finishedAtRaw = row.GetValue<DateTime?>("finished_at");
        DateTimeOffset? finishedAt = finishedAtRaw.HasValue
            ? new DateTimeOffset(finishedAtRaw.Value, TimeSpan.Zero)
            : null;

        return new ImportOperationSnapshot(
            row.GetValue<string>("system_id"),
            row.GetValue<TimeUuid>("operation_id").ToGuid(),
            row.GetValue<string>("kind"),
            status,
            startedAt,
            finishedAt,
            row.GetValue<int?>("alter_count"),
            row.GetValue<string>("error_code"),
            row.GetValue<string>("error_message"),
            row.GetValue<string>("idempotency_key"));
    }
}
