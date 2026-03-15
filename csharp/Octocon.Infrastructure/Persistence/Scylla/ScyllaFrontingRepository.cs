using Cassandra;
using Octocon.Domain.Abstractions;
using Octocon.Domain.Fronting;
using Octocon.Infrastructure.Persistence.Transient;

namespace Octocon.Infrastructure.Persistence.Scylla;

public sealed class ScyllaFrontingRepository : IFrontingRepository
{
    private readonly IScyllaSessionProvider _sessionProvider;
    private readonly IRegionContext _regionContext;
    private readonly PersistenceRegistrationOptions _options;

    public ScyllaFrontingRepository(
        IScyllaSessionProvider sessionProvider,
        IRegionContext regionContext,
        PersistenceRegistrationOptions options
    )
    {
        _sessionProvider = sessionProvider;
        _regionContext = regionContext;
        _options = options;
    }

    public async Task<bool> IsFrontingAsync(string systemId, int alterId, CancellationToken cancellationToken = default)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var scopedSystemId = GetScopedSystemId(systemId);

            var query = new SimpleStatement(
                "SELECT alter_id FROM fronting_active_by_system WHERE system_id = ? AND alter_id = ? LIMIT 1",
                scopedSystemId,
                alterId
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
            var scopedSystemId = GetScopedSystemId(systemId);
            var frontId = Guid.NewGuid().ToString("N");

            var insert = new SimpleStatement(
                "INSERT INTO fronting_active_by_system (system_id, alter_id, front_id, comment, started_at) VALUES (?, ?, ?, ?, toTimestamp(now()))",
                scopedSystemId,
                alterId,
                frontId,
                comment
            );

            await session.ExecuteAsync(insert);
            return frontId;
        }, _options, cancellationToken);
    }

    public async Task<bool> EndAsync(string systemId, int alterId, CancellationToken cancellationToken = default)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var scopedSystemId = GetScopedSystemId(systemId);

            var fronting = await IsFrontingAsync(systemId, alterId, cancellationToken);
            if (!fronting)
            {
                return false;
            }

            var delete = new SimpleStatement(
                "DELETE FROM fronting_active_by_system WHERE system_id = ? AND alter_id = ?",
                scopedSystemId,
                alterId
            );

            await session.ExecuteAsync(delete);

            var clearPrimary = new SimpleStatement(
                "DELETE FROM fronting_primary_by_system WHERE system_id = ? IF alter_id = ?",
                scopedSystemId,
                alterId
            );

            await session.ExecuteAsync(clearPrimary);
            return true;
        }, _options, cancellationToken);
    }

    public async Task<bool> SetPrimaryAsync(string systemId, int? alterId, CancellationToken cancellationToken = default)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var scopedSystemId = GetScopedSystemId(systemId);

            if (alterId is int value)
            {
                var fronting = await IsFrontingAsync(systemId, value, cancellationToken);
                if (!fronting)
                {
                    return false;
                }

                var setPrimary = new SimpleStatement(
                    "INSERT INTO fronting_primary_by_system (system_id, alter_id) VALUES (?, ?)",
                    scopedSystemId,
                    value
                );

                await session.ExecuteAsync(setPrimary);
                return true;
            }

            var clear = new SimpleStatement(
                "DELETE FROM fronting_primary_by_system WHERE system_id = ?",
                scopedSystemId
            );

            await session.ExecuteAsync(clear);
            return true;
        }, _options, cancellationToken);
    }

    public async Task<IReadOnlyList<FrontActiveReadModel>> ListActiveAsync(string systemId, CancellationToken cancellationToken = default)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var scopedSystemId = GetScopedSystemId(systemId);

            var primaryQuery = new SimpleStatement(
                "SELECT alter_id FROM fronting_primary_by_system WHERE system_id = ? LIMIT 1",
                scopedSystemId
            );

            var primaryRow = (await session.ExecuteAsync(primaryQuery)).FirstOrDefault();
            var primaryAlterId = primaryRow?.GetValue<int>("alter_id");

            var activeQuery = new SimpleStatement(
                "SELECT alter_id, front_id, comment, started_at FROM fronting_active_by_system WHERE system_id = ?",
                scopedSystemId
            );

            var rows = await session.ExecuteAsync(activeQuery);
            return (IReadOnlyList<FrontActiveReadModel>)rows
                .Select(row => new FrontActiveReadModel(
                    row.GetValue<string>("front_id"),
                    row.GetValue<int>("alter_id"),
                    row.GetValue<string?>("comment"),
                    row.GetValue<DateTimeOffset?>("started_at") ?? DateTimeOffset.UtcNow,
                    primaryAlterId == row.GetValue<int>("alter_id")))
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
            var scopedSystemId = GetScopedSystemId(systemId);

            var update = new SimpleStatement(
                "UPDATE fronting_active_by_system SET comment = ? WHERE system_id = ? AND alter_id = ?",
                comment,
                scopedSystemId,
                existing.AlterId
            );

            await session.ExecuteAsync(update);
            return true;
        }, _options, cancellationToken);
    }

    private string GetScopedSystemId(string systemId)
    {
        var region = _regionContext.ResolveUserRegion(systemId);
        return $"{region}:{systemId}";
    }
}