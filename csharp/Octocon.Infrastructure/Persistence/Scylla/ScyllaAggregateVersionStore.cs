using Cassandra;
using Octocon.Domain.Abstractions;
using Octocon.Infrastructure.Persistence.Transient;

namespace Octocon.Infrastructure.Persistence.Scylla;

public sealed class ScyllaAggregateVersionStore : IAggregateVersionStore
{
    private readonly IScyllaSessionProvider _sessionProvider;
    private readonly IScyllaKeyspaceResolver _keyspaceResolver;
    private readonly IRegionContext _regionContext;
    private readonly PersistenceRegistrationOptions _options;
    private int _aggregateVersionLwtUnsupported;

    public ScyllaAggregateVersionStore(
        IScyllaSessionProvider sessionProvider,
        IScyllaKeyspaceResolver keyspaceResolver,
        IRegionContext regionContext,
        PersistenceRegistrationOptions options
    )
    {
        _sessionProvider = sessionProvider;
        _keyspaceResolver = keyspaceResolver;
        _regionContext = regionContext;
        _options = options;
    }

    public async Task<long?> GetVersionAsync(
        string aggregateType,
        string aggregateId,
        CancellationToken cancellationToken = default
    )
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync<long?>(async () =>
        {
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var (scope, type, normalizedAggregateId) = ResolveScope(aggregateType, aggregateId);
            return await GetVersionAsync(session, scope, type, normalizedAggregateId);
        }, _options, cancellationToken);
    }

    public async Task<bool> TryAdvanceVersionAsync(
        string aggregateType,
        string aggregateId,
        long? expectedVersion,
        CancellationToken cancellationToken = default
    )
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var (scope, type, normalizedAggregateId) = ResolveScope(aggregateType, aggregateId);

            if (Volatile.Read(ref _aggregateVersionLwtUnsupported) == 1)
            {
                return await TryAdvanceWithoutLwtAsync(session, scope, type, normalizedAggregateId, expectedVersion);
            }

            try
            {
                return await TryAdvanceWithLwtAsync(session, scope, type, normalizedAggregateId, expectedVersion);
            }
            catch (InvalidQueryException ex) when (IsLwtUnsupportedWithTablets(ex))
            {
                Volatile.Write(ref _aggregateVersionLwtUnsupported, 1);
                return await TryAdvanceWithoutLwtAsync(session, scope, type, normalizedAggregateId, expectedVersion);
            }
        }, _options, cancellationToken);
    }

    private async Task<long?> GetVersionAsync(ISession session, string scope, string aggregateType, string aggregateId)
    {
        var globalKeyspace = _keyspaceResolver.ResolveGlobalKeyspace();

        var query = new SimpleStatement(
            $"SELECT version FROM {globalKeyspace}.aggregate_versions_by_region WHERE region_scope = ? AND aggregate_type = ? AND aggregate_id = ? LIMIT 1",
            scope,
            aggregateType,
            aggregateId
        );

        var rows = await session.ExecuteAsync(query);
        var row = rows.FirstOrDefault();
        return row is null ? null : row.GetValue<long>("version");
    }

    private async Task<bool> TryAdvanceWithLwtAsync(ISession session, string scope, string aggregateType, string aggregateId, long? expectedVersion)
    {
        var globalKeyspace = _keyspaceResolver.ResolveGlobalKeyspace();
        var current = await GetVersionAsync(session, scope, aggregateType, aggregateId);

        if (current is null)
        {
            if (expectedVersion is not null && expectedVersion.Value != 0)
            {
                return false;
            }

            var create = new SimpleStatement(
                $"INSERT INTO {globalKeyspace}.aggregate_versions_by_region (region_scope, aggregate_type, aggregate_id, version) VALUES (?, ?, ?, ?) IF NOT EXISTS",
                scope,
                aggregateType,
                aggregateId,
                1L
            );

            var createResult = await session.ExecuteAsync(create);
            return createResult.FirstOrDefault()?.GetValue<bool>("[applied]") ?? false;
        }

        if (expectedVersion is not null && current.Value != expectedVersion.Value)
        {
            return false;
        }

        var next = current.Value + 1;
        var update = new SimpleStatement(
            $"UPDATE {globalKeyspace}.aggregate_versions_by_region SET version = ? WHERE region_scope = ? AND aggregate_type = ? AND aggregate_id = ? IF version = ?",
            next,
            scope,
            aggregateType,
            aggregateId,
            current.Value
        );

        var updateResult = await session.ExecuteAsync(update);
        return updateResult.FirstOrDefault()?.GetValue<bool>("[applied]") ?? false;
    }

    private async Task<bool> TryAdvanceWithoutLwtAsync(ISession session, string scope, string aggregateType, string aggregateId, long? expectedVersion)
    {
        var globalKeyspace = _keyspaceResolver.ResolveGlobalKeyspace();
        var current = await GetVersionAsync(session, scope, aggregateType, aggregateId);

        if (current is null)
        {
            if (expectedVersion is not null && expectedVersion.Value != 0)
            {
                return false;
            }

            var create = new SimpleStatement(
                $"INSERT INTO {globalKeyspace}.aggregate_versions_by_region (region_scope, aggregate_type, aggregate_id, version) VALUES (?, ?, ?, ?)",
                scope,
                aggregateType,
                aggregateId,
                1L
            );

            await session.ExecuteAsync(create);
            return true;
        }

        if (expectedVersion is not null && current.Value != expectedVersion.Value)
        {
            return false;
        }

        var next = current.Value + 1;
        var update = new SimpleStatement(
            $"UPDATE {globalKeyspace}.aggregate_versions_by_region SET version = ? WHERE region_scope = ? AND aggregate_type = ? AND aggregate_id = ?",
            next,
            scope,
            aggregateType,
            aggregateId
        );

        await session.ExecuteAsync(update);
        return true;
    }

    private static bool IsLwtUnsupportedWithTablets(InvalidQueryException ex)
        => ex.Message.Contains("LWT is not yet supported with tablets", StringComparison.OrdinalIgnoreCase);

    private (string Scope, string AggregateType, string AggregateId) ResolveScope(string aggregateType, string aggregateId)
    {
        var normalizedAggregateId = _keyspaceResolver.NormalizeSystemId(aggregateId);
        var targetRegion = _keyspaceResolver.ResolveRegionalKeyspace(aggregateId);
        var consistency = _regionContext.ResolveConsistency(targetRegion);
        var scope = string.Equals(consistency, "local", StringComparison.OrdinalIgnoreCase)
            ? targetRegion
            : "global";

        return (scope, aggregateType, normalizedAggregateId);
    }
}
