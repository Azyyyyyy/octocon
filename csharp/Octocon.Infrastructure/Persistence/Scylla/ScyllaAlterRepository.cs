using Cassandra;
using Microsoft.Extensions.Logging;
using Octocon.Domain.Alters;
using Octocon.Infrastructure.Persistence.Transient;

namespace Octocon.Infrastructure.Persistence.Scylla;

public sealed class ScyllaAlterRepository : IAlterRepository
{
    private readonly IScyllaSessionProvider _sessionProvider;
    private readonly IScyllaKeyspaceResolver _keyspaceResolver;
    private readonly PersistenceRegistrationOptions _options;
    private readonly ILogger<ScyllaAlterRepository> _logger;

    public ScyllaAlterRepository(
        IScyllaSessionProvider sessionProvider,
        IScyllaKeyspaceResolver keyspaceResolver,
        PersistenceRegistrationOptions options,
        ILogger<ScyllaAlterRepository> logger
    )
    {
        _sessionProvider = sessionProvider;
        _keyspaceResolver = keyspaceResolver;
        _options = options;
        _logger = logger;
    }

    public async Task<int?> CreateAsync(string systemId, CreateAlterCommand command, CancellationToken cancellationToken = default)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var normalizedSystemId = _keyspaceResolver.NormalizeSystemId(systemId);
            var keyspace = _keyspaceResolver.ResolveRegionalKeyspace(systemId);

            var nextIdQuery = new SimpleStatement(
                $"SELECT id FROM {keyspace}.alters WHERE user_id = ? ORDER BY id DESC LIMIT 1",
                normalizedSystemId
            );

            var rows = await session.ExecuteAsync(nextIdQuery);
            var current = rows.FirstOrDefault()?.GetValue<short>("id") ?? (short)0;
            var next = (short)(current + 1);

            var insert = new SimpleStatement(
                $"INSERT INTO {keyspace}.alters (user_id, id, name, alias, inserted_at, updated_at) VALUES (?, ?, ?, ?, toTimestamp(now()), toTimestamp(now()))",
                normalizedSystemId,
                next,
                command.Name,
                null
            );

            await session.ExecuteAsync(insert);
            return (int?)next;
        }, _options, cancellationToken, _logger);
    }

    public async Task<bool> ExistsAsync(string systemId, int alterId, CancellationToken cancellationToken = default)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var normalizedSystemId = _keyspaceResolver.NormalizeSystemId(systemId);
            var keyspace = _keyspaceResolver.ResolveRegionalKeyspace(systemId);

            var query = new SimpleStatement(
                $"SELECT id FROM {keyspace}.alters WHERE user_id = ? AND id = ? LIMIT 1",
                normalizedSystemId,
                (short)alterId
            );

            var rows = await session.ExecuteAsync(query);
            return rows.Any();
        }, _options, cancellationToken, _logger);
    }

    public async Task<bool> UpdateAsync(string systemId, UpdateAlterCommand command, CancellationToken cancellationToken = default)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var normalizedSystemId = _keyspaceResolver.NormalizeSystemId(systemId);
            var keyspace = _keyspaceResolver.ResolveRegionalKeyspace(systemId);

            var exists = await ExistsAsync(systemId, command.AlterId, cancellationToken);
            if (!exists)
            {
                return false;
            }

            if (command.Name is not null)
            {
                var updateName = new SimpleStatement(
                    $"UPDATE {keyspace}.alters SET name = ?, updated_at = toTimestamp(now()) WHERE user_id = ? AND id = ?",
                    command.Name,
                    normalizedSystemId,
                    (short)command.AlterId
                );

                await session.ExecuteAsync(updateName);
            }

            if (command.Alias is not null)
            {
                var updateAlias = new SimpleStatement(
                    $"UPDATE {keyspace}.alters SET alias = ?, updated_at = toTimestamp(now()) WHERE user_id = ? AND id = ?",
                    command.Alias,
                    normalizedSystemId,
                    (short)command.AlterId
                );

                await session.ExecuteAsync(updateAlias);
            }

            return true;
        }, _options, cancellationToken, _logger);
    }

    public async Task<bool> DeleteAsync(string systemId, int alterId, CancellationToken cancellationToken = default)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var normalizedSystemId = _keyspaceResolver.NormalizeSystemId(systemId);
            var keyspace = _keyspaceResolver.ResolveRegionalKeyspace(systemId);
            var alterIdShort = (short)alterId;

            var exists = await ExistsAsync(systemId, alterId, cancellationToken);
            if (!exists)
            {
                return false;
            }

            await session.ExecuteAsync(new SimpleStatement(
                $"DELETE FROM {keyspace}.alters WHERE user_id = ? AND id = ?",
                normalizedSystemId,
                alterIdShort));

            await session.ExecuteAsync(new SimpleStatement(
                $"DELETE FROM {keyspace}.current_fronts WHERE user_id = ? AND alter_id = ?",
                normalizedSystemId,
                alterIdShort));

            await session.ExecuteAsync(new SimpleStatement(
                $"UPDATE {keyspace}.users SET primary_front = null WHERE id = ? IF primary_front = ?",
                normalizedSystemId,
                alterId));

            var membershipRows = await session.ExecuteAsync(new SimpleStatement(
                $"SELECT tag_id FROM {keyspace}.alter_tags WHERE user_id = ? AND alter_id = ?",
                normalizedSystemId,
                alterIdShort));

            foreach (var row in membershipRows)
            {
                var tagId = row.GetValue<Guid>("tag_id");
                await session.ExecuteAsync(new SimpleStatement(
                    $"DELETE FROM {keyspace}.alter_tags WHERE user_id = ? AND tag_id = ? AND alter_id = ?",
                    normalizedSystemId,
                    tagId,
                    alterIdShort));
            }

            return true;
        }, _options, cancellationToken, _logger);
    }

    public async Task<IReadOnlyList<AlterPublicReadModel>> ListAsync(string systemId, CancellationToken cancellationToken = default)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var normalizedSystemId = _keyspaceResolver.NormalizeSystemId(systemId);
            var keyspace = _keyspaceResolver.ResolveRegionalKeyspace(systemId);

            var query = new SimpleStatement(
                $"SELECT id, name, alias FROM {keyspace}.alters WHERE user_id = ?",
                normalizedSystemId
            );

            var rows = await session.ExecuteAsync(query);
            return rows
                .Select(row => new AlterPublicReadModel(
                    row.GetValue<short>("id"),
                    row.GetValue<string>("name"),
                    row.GetValue<string?>("alias")))
                .OrderBy(x => x.AlterId)
                .ToArray();
        }, _options, cancellationToken, _logger);
    }

    public async Task<AlterPublicReadModel?> GetAsync(string systemId, int alterId, CancellationToken cancellationToken = default)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var normalizedSystemId = _keyspaceResolver.NormalizeSystemId(systemId);
            var keyspace = _keyspaceResolver.ResolveRegionalKeyspace(systemId);

            var query = new SimpleStatement(
                $"SELECT id, name, alias FROM {keyspace}.alters WHERE user_id = ? AND id = ? LIMIT 1",
                normalizedSystemId,
                (short)alterId
            );

            var row = (await session.ExecuteAsync(query)).FirstOrDefault();
            return row is null
                ? null
                : new AlterPublicReadModel(
                    row.GetValue<short>("id"),
                    row.GetValue<string>("name"),
                    row.GetValue<string?>("alias"));
        }, _options, cancellationToken, _logger);
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
            var normalizedSystemId = _keyspaceResolver.NormalizeSystemId(systemId);
            var keyspace = _keyspaceResolver.ResolveRegionalKeyspace(systemId);

            var query = new SimpleStatement(
                $"SELECT id, alias FROM {keyspace}.alters WHERE user_id = ? AND alias = ?",
                normalizedSystemId,
                alias
            );

            var rows = await session.ExecuteAsync(query);
            return rows.Any(row => row.GetValue<short>("id") != (short)alterId);
        }, _options, cancellationToken, _logger);
    }
}
