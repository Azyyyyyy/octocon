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
            var globalKeyspace = _keyspaceResolver.ResolveGlobalKeyspace();

            var query = new SimpleStatement(
                $"SELECT version FROM {globalKeyspace}.aggregate_versions_by_region WHERE region_scope = ? AND aggregate_type = ? AND aggregate_id = ? LIMIT 1",
                scope,
                type,
                normalizedAggregateId
            );

            var rows = await session.ExecuteAsync(query);
            var row = rows.FirstOrDefault();
            return row is null ? null : row.GetValue<long>("version");
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
            var globalKeyspace = _keyspaceResolver.ResolveGlobalKeyspace();

            var current = await GetVersionAsync(aggregateType, aggregateId, cancellationToken);

            if (current is null)
            {
                if (expectedVersion is not null && expectedVersion.Value != 0)
                {
                    return false;
                }

                var create = new SimpleStatement(
                    $"INSERT INTO {globalKeyspace}.aggregate_versions_by_region (region_scope, aggregate_type, aggregate_id, version) VALUES (?, ?, ?, ?) IF NOT EXISTS",
                    scope,
                    type,
                    normalizedAggregateId,
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
                type,
                normalizedAggregateId,
                current.Value
            );

            var updateResult = await session.ExecuteAsync(update);
            return updateResult.FirstOrDefault()?.GetValue<bool>("[applied]") ?? false;
        }, _options, cancellationToken);
    }

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