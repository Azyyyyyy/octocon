using Cassandra;
using Octocon.Domain.Fronting;
using Octocon.Infrastructure.Persistence.Transient;

namespace Octocon.Infrastructure.Persistence.Scylla;

public sealed class ScyllaFrontingRepository : IFrontingRepository
{
    private sealed record CurrentFrontRow(Guid FrontId, short AlterId, DateTimeOffset StartedAt);

    private readonly IScyllaSessionProvider _sessionProvider;
    private readonly IScyllaKeyspaceResolver _keyspaceResolver;
    private readonly PersistenceRegistrationOptions _options;

    public ScyllaFrontingRepository(
        IScyllaSessionProvider sessionProvider,
        IScyllaKeyspaceResolver keyspaceResolver,
        PersistenceRegistrationOptions options
    )
    {
        _sessionProvider = sessionProvider;
        _keyspaceResolver = keyspaceResolver;
        _options = options;
    }

    public async Task<bool> IsFrontingAsync(string systemId, int alterId, CancellationToken cancellationToken = default)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var normalizedSystemId = _keyspaceResolver.NormalizeSystemId(systemId);
            var keyspace = _keyspaceResolver.ResolveRegionalKeyspace(systemId);

            var query = new SimpleStatement(
                $"SELECT alter_id FROM {keyspace}.current_fronts WHERE user_id = ? AND alter_id = ? LIMIT 1",
                normalizedSystemId,
                (short)alterId
            );

            var rows = await session.ExecuteAsync(query);
            return rows.Any();
        }, _options, cancellationToken);
    }

    public async Task<string?> StartAsync(
        string systemId,
        int alterId,
        string? comment,
        CancellationToken cancellationToken = default
    )
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var normalizedSystemId = _keyspaceResolver.NormalizeSystemId(systemId);
            var keyspace = _keyspaceResolver.ResolveRegionalKeyspace(systemId);
            var frontGuid = Guid.NewGuid();
            var startedAt = DateTimeOffset.UtcNow;

            var insertCurrent = new SimpleStatement(
                $"INSERT INTO {keyspace}.current_fronts (user_id, alter_id, id, comment, time_start, inserted_at, updated_at) VALUES (?, ?, ?, ?, ?, ?, ?)",
                normalizedSystemId,
                (short)alterId,
                frontGuid,
                comment,
                startedAt,
                startedAt,
                startedAt
            );

            await session.ExecuteAsync(insertCurrent);

            var insertHistory = new SimpleStatement(
                $"INSERT INTO {keyspace}.fronts (user_id, id, alter_id, comment, time_start, inserted_at, updated_at) VALUES (?, ?, ?, ?, ?, ?, ?)",
                normalizedSystemId,
                frontGuid,
                (short)alterId,
                comment,
                startedAt,
                startedAt,
                startedAt
            );

            await session.ExecuteAsync(insertHistory);
            return frontGuid.ToString("N");
        }, _options, cancellationToken);
    }

    public async Task<bool> EndAsync(string systemId, int alterId, CancellationToken cancellationToken = default)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var normalizedSystemId = _keyspaceResolver.NormalizeSystemId(systemId);
            var keyspace = _keyspaceResolver.ResolveRegionalKeyspace(systemId);

            var current = await GetCurrentFrontRowAsync(session, keyspace, normalizedSystemId, (short)alterId);
            if (current is null)
            {
                return false;
            }

            var endedAt = DateTimeOffset.UtcNow;

            await session.ExecuteAsync(new SimpleStatement(
                $"UPDATE {keyspace}.fronts SET time_end = ?, updated_at = ? WHERE user_id = ? AND id = ? AND time_start = ?",
                endedAt,
                endedAt,
                normalizedSystemId,
                current.FrontId,
                current.StartedAt));

            await session.ExecuteAsync(new SimpleStatement(
                $"DELETE FROM {keyspace}.current_fronts WHERE user_id = ? AND alter_id = ?",
                normalizedSystemId,
                (short)alterId));

            await session.ExecuteAsync(new SimpleStatement(
                "UPDATE global.users SET primary_front = null WHERE id = ? IF primary_front = ?",
                normalizedSystemId,
                alterId));

            return true;
        }, _options, cancellationToken);
    }

    public async Task<bool> SetPrimaryAsync(string systemId, int? alterId, CancellationToken cancellationToken = default)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var normalizedSystemId = _keyspaceResolver.NormalizeSystemId(systemId);

            if (alterId is int value)
            {
                var fronting = await IsFrontingAsync(systemId, value, cancellationToken);
                if (!fronting)
                {
                    return false;
                }

                await session.ExecuteAsync(new SimpleStatement(
                    "UPDATE global.users SET primary_front = ?, updated_at = toTimestamp(now()) WHERE id = ?",
                    value,
                    normalizedSystemId));

                return true;
            }

            await session.ExecuteAsync(new SimpleStatement(
                "UPDATE global.users SET primary_front = null, updated_at = toTimestamp(now()) WHERE id = ?",
                normalizedSystemId));

            return true;
        }, _options, cancellationToken);
    }

    public async Task<IReadOnlyList<FrontActiveReadModel>> ListActiveAsync(string systemId, CancellationToken cancellationToken = default)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var normalizedSystemId = _keyspaceResolver.NormalizeSystemId(systemId);
            var keyspace = _keyspaceResolver.ResolveRegionalKeyspace(systemId);

            var primaryQuery = new SimpleStatement(
                "SELECT primary_front FROM global.users WHERE id = ? LIMIT 1",
                normalizedSystemId
            );

            var primaryRow = (await session.ExecuteAsync(primaryQuery)).FirstOrDefault();
            var primaryAlterId = primaryRow?.GetValue<int?>("primary_front");

            var activeQuery = new SimpleStatement(
                $"SELECT alter_id, id, comment, time_start FROM {keyspace}.current_fronts WHERE user_id = ?",
                normalizedSystemId
            );

            var rows = await session.ExecuteAsync(activeQuery);
            return (IReadOnlyList<FrontActiveReadModel>)rows
                .Select(row => new FrontActiveReadModel(
                    row.GetValue<Guid>("id").ToString("N"),
                    row.GetValue<short>("alter_id"),
                    row.GetValue<string?>("comment"),
                    row.GetValue<DateTimeOffset?>("time_start") ?? DateTimeOffset.UtcNow,
                    primaryAlterId == row.GetValue<short>("alter_id")))
                .OrderByDescending(x => x.StartedAt)
                .ToArray();
        }, _options, cancellationToken);
    }

    public async Task<IReadOnlyList<FrontHistoryReadModel>> ListHistoryBetweenAsync(
        string systemId,
        DateTimeOffset startInclusive,
        DateTimeOffset endInclusive,
        CancellationToken cancellationToken = default)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var normalizedSystemId = _keyspaceResolver.NormalizeSystemId(systemId);
            var keyspace = _keyspaceResolver.ResolveRegionalKeyspace(systemId);

            var historyQuery = new SimpleStatement(
                $"SELECT id, alter_id, comment, time_start, time_end FROM {keyspace}.fronts_by_time WHERE user_id = ? AND time_start >= ? AND time_start <= ?",
                normalizedSystemId,
                startInclusive,
                endInclusive
            );

            var historicalRows = await session.ExecuteAsync(historyQuery);
            var historical = historicalRows
                .Select(row => new FrontHistoryReadModel(
                    row.GetValue<Guid>("id").ToString("N"),
                    row.GetValue<short>("alter_id"),
                    row.GetValue<string?>("comment"),
                    row.GetValue<DateTimeOffset>("time_start"),
                    row.GetValue<DateTimeOffset?>("time_end")))
                .ToList();

            var active = await ListActiveAsync(systemId, cancellationToken);
            var activeAsHistory = active
                .Where(x => x.StartedAt >= startInclusive && x.StartedAt <= endInclusive)
                .Select(x => new FrontHistoryReadModel(x.FrontId, x.AlterId, x.Comment, x.StartedAt, null));

            return (IReadOnlyList<FrontHistoryReadModel>)historical
                .Concat(activeAsHistory)
                .DistinctBy(x => x.FrontId)
                .OrderByDescending(x => x.StartedAt)
                .ToArray();
        }, _options, cancellationToken);
    }

    public async Task<FrontActiveReadModel?> GetActiveByFrontIdAsync(string systemId, string frontId, CancellationToken cancellationToken = default)
    {
        var active = await ListActiveAsync(systemId, cancellationToken);
        return active.FirstOrDefault(x => string.Equals(x.FrontId, frontId, StringComparison.Ordinal));
    }

    public async Task<bool> EndByFrontIdAsync(string systemId, string frontId, CancellationToken cancellationToken = default)
    {
        var existing = await GetActiveByFrontIdAsync(systemId, frontId, cancellationToken);
        if (existing is null)
            return false;

        return await EndAsync(systemId, existing.AlterId, cancellationToken);
    }

    public async Task<bool> UpdateCommentByFrontIdAsync(string systemId, string frontId, string comment, CancellationToken cancellationToken = default)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            var existing = await GetActiveByFrontIdAsync(systemId, frontId, cancellationToken);
            if (existing is null)
                return false;

            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var normalizedSystemId = _keyspaceResolver.NormalizeSystemId(systemId);
            var keyspace = _keyspaceResolver.ResolveRegionalKeyspace(systemId);

            var update = new SimpleStatement(
                $"UPDATE {keyspace}.current_fronts SET comment = ?, updated_at = toTimestamp(now()) WHERE user_id = ? AND alter_id = ?",
                comment,
                normalizedSystemId,
                (short)existing.AlterId
            );

            await session.ExecuteAsync(update);

            var updateHistory = new SimpleStatement(
                $"UPDATE {keyspace}.fronts SET comment = ?, updated_at = toTimestamp(now()) WHERE user_id = ? AND id = ? AND time_start = ?",
                comment,
                normalizedSystemId,
                Guid.Parse(existing.FrontId),
                existing.StartedAt);

            await session.ExecuteAsync(updateHistory);
            return true;
        }, _options, cancellationToken);
    }

    private static async Task<CurrentFrontRow?> GetCurrentFrontRowAsync(
        ISession session,
        string keyspace,
        string normalizedSystemId,
        short alterId)
    {
        var query = new SimpleStatement(
            $"SELECT id, alter_id, time_start FROM {keyspace}.current_fronts WHERE user_id = ? AND alter_id = ? LIMIT 1",
            normalizedSystemId,
            alterId);

        var row = (await session.ExecuteAsync(query)).FirstOrDefault();
        if (row is null)
        {
            return null;
        }

        return new CurrentFrontRow(
            row.GetValue<Guid>("id"),
            row.GetValue<short>("alter_id"),
            row.GetValue<DateTimeOffset?>("time_start") ?? DateTimeOffset.UtcNow);
    }
}
