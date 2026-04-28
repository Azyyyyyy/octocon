using System.Text.Json;
using Cassandra;
using Interfold.Contracts.Configuration;
using Interfold.Contracts.Models.Commands;
using Interfold.Contracts.Models.Read;
using Interfold.Domain.Abstractions.Repository;
using Interfold.Infrastructure.Persistence;

namespace Interfold.Infrastructure.Scylla.Repository;

public sealed class ScyllaPollRepository : IPollRepository
{
    private static readonly Dictionary<string, short> PollTypeToCode = new(StringComparer.OrdinalIgnoreCase)
    {
        ["single_choice"] = 0,
        ["vote"] = 0,  // Alias for single_choice
        ["multiple_choice"] = 1,
        ["choice"] = 1,  // Alias for multiple_choice
        ["approval"] = 2
    };

    private static readonly Dictionary<short, string> PollCodeToType = new()
    {
        [0] = "vote",
        [1] = "choice",
        [2] = "approval"
    };

    private readonly IScyllaSessionProvider _sessionProvider;
    private readonly IScyllaKeyspaceResolver _keyspaceResolver;
    private readonly PersistenceConfiguration _options;

    public ScyllaPollRepository(
        IScyllaSessionProvider sessionProvider,
        IScyllaKeyspaceResolver keyspaceResolver,
        PersistenceConfiguration options)
    {
        _sessionProvider = sessionProvider;
        _keyspaceResolver = keyspaceResolver;
        _options = options;
    }

    public async Task<IReadOnlyList<PollReadModel>> ListAsync(string systemId, CancellationToken cancellationToken = default)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var normalizedSystemId = _keyspaceResolver.NormalizeSystemId(systemId);
            var keyspace = _keyspaceResolver.ResolveRegionalKeyspace(systemId);

            var query = new SimpleStatement(
                $"SELECT id, user_id, title, description, type, data, time_end, inserted_at, updated_at FROM {keyspace}.polls WHERE user_id = ?",
                normalizedSystemId
            );

            var rows = await session.ExecuteAsync(query);
            // VERIFIED: 2026-03-17 Elixir polls.ex get_polls() has no explicit sort → database order (ascending). Matches C# OrderBy.
            return rows.Select(ToReadModel).OrderBy(p => p.Id, StringComparer.Ordinal).ToList();
        }, _options, cancellationToken);
    }

    public async Task<PollReadModel?> GetAsync(string systemId, string pollId, CancellationToken cancellationToken = default)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var normalizedSystemId = _keyspaceResolver.NormalizeSystemId(systemId);
            var keyspace = _keyspaceResolver.ResolveRegionalKeyspace(systemId);
            if (!TryParseUuid(pollId, out var pollGuid)) return null;

            var query = new SimpleStatement(
                $"SELECT id, user_id, title, description, type, data, time_end, inserted_at, updated_at FROM {keyspace}.polls WHERE user_id = ? AND id = ? LIMIT 1",
                normalizedSystemId,
                pollGuid
            );

            var row = (await session.ExecuteAsync(query)).FirstOrDefault();
            return row is null ? null : ToReadModel(row);
        }, _options, cancellationToken);
    }

    public async Task<string?> CreateAsync(string systemId, CreatePollCommand command, CancellationToken cancellationToken = default)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var normalizedSystemId = _keyspaceResolver.NormalizeSystemId(systemId);
            var keyspace = _keyspaceResolver.ResolveRegionalKeyspace(systemId);
            var pollGuid = Guid.NewGuid();

            var insert = new SimpleStatement(
                $"INSERT INTO {keyspace}.polls (user_id, id, title, description, type, data, time_end, inserted_at, updated_at) VALUES (?, ?, ?, ?, ?, ?, ?, toTimestamp(now()), toTimestamp(now()))",
                normalizedSystemId,
                pollGuid,
                command.Title,
                command.Description,
                ToPollCode(command.Type),
                "{}",
                command.TimeEnd
            );

            await session.ExecuteAsync(insert);
            return pollGuid.ToString("N");
        }, _options, cancellationToken);
    }

    public async Task<bool> ExistsAsync(string systemId, string pollId, CancellationToken cancellationToken = default)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            if (!TryParseUuid(pollId, out var pollGuid))
            {
                return false;
            }

            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var normalizedSystemId = _keyspaceResolver.NormalizeSystemId(systemId);
            var keyspace = _keyspaceResolver.ResolveRegionalKeyspace(systemId);

            var query = new SimpleStatement(
                $"SELECT id FROM {keyspace}.polls WHERE user_id = ? AND id = ? LIMIT 1",
                normalizedSystemId,
                pollGuid
            );

            var rows = await session.ExecuteAsync(query);
            return rows.Any();
        }, _options, cancellationToken);
    }

    public async Task<bool> UpdateAsync(string systemId, UpdatePollCommand command, CancellationToken cancellationToken = default)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            if (!TryParseUuid(command.Id, out var pollGuid))
            {
                return false;
            }

            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var normalizedSystemId = _keyspaceResolver.NormalizeSystemId(systemId);
            var keyspace = _keyspaceResolver.ResolveRegionalKeyspace(systemId);

            var exists = await ExistsAsync(session, keyspace, normalizedSystemId, pollGuid);
            if (!exists)
                return false;

            var updateBatch = new BatchStatement();

            if (command.Title is not null)
            {
                updateBatch.Add(new SimpleStatement(
                    $"UPDATE {keyspace}.polls SET title = ?, updated_at = toTimestamp(now()) WHERE user_id = ? AND id = ?",
                    command.Title,
                    normalizedSystemId,
                    pollGuid));
            }

            if (command.Description is not null)
            {
                updateBatch.Add(new SimpleStatement(
                    $"UPDATE {keyspace}.polls SET description = ?, updated_at = toTimestamp(now()) WHERE user_id = ? AND id = ?",
                    command.Description,
                    normalizedSystemId,
                    pollGuid));
            }

            if (command.HasTimeEnd)
            {
                updateBatch.Add(new SimpleStatement(
                    $"UPDATE {keyspace}.polls SET time_end = ?, updated_at = toTimestamp(now()) WHERE user_id = ? AND id = ?",
                    command.TimeEnd,
                    normalizedSystemId,
                    pollGuid));
            }

            if (command.Data is not null)
            {
                updateBatch.Add(new SimpleStatement(
                    $"UPDATE {keyspace}.polls SET data = ?, updated_at = toTimestamp(now()) WHERE user_id = ? AND id = ?",
                    command.Data.Value.ToString(),
                    normalizedSystemId,
                    pollGuid));
            }

            if (!updateBatch.IsEmpty)
            {
                await session.ExecuteAsync(updateBatch);
            }

            return true;
        }, _options, cancellationToken);
    }

    public async Task<bool> DeleteAsync(string systemId, string pollId, CancellationToken cancellationToken = default)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            if (!TryParseUuid(pollId, out var pollGuid))
            {
                return false;
            }

            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var normalizedSystemId = _keyspaceResolver.NormalizeSystemId(systemId);
            var keyspace = _keyspaceResolver.ResolveRegionalKeyspace(systemId);

            var exists = await ExistsAsync(session, keyspace, normalizedSystemId, pollGuid);
            if (!exists)
                return false;

            var delete = new SimpleStatement(
                $"DELETE FROM {keyspace}.polls WHERE user_id = ? AND id = ?",
                normalizedSystemId,
                pollGuid
            );
            await session.ExecuteAsync(delete);
            return true;
        }, _options, cancellationToken);
    }

    internal static bool TryParseUuid(string value, out Guid guid)
    {
        if (Guid.TryParseExact(value, "N", out guid)) return true;
        return Guid.TryParse(value, out guid);
    }

    internal static short ToPollCode(string type)
        => PollTypeToCode.TryGetValue(type, out var code) ? code : (short)0;

    internal static string ToPollType(short code)
        => PollCodeToType.TryGetValue(code, out var type) ? type : "vote";

    private static async Task<bool> ExistsAsync(ISession session, string keyspace, string normalizedSystemId, Guid pollGuid)
    {
        var query = new SimpleStatement(
            $"SELECT id FROM {keyspace}.polls WHERE user_id = ? AND id = ? LIMIT 1",
            normalizedSystemId,
            pollGuid
        );

        var rows = await session.ExecuteAsync(query);
        return rows.Any();
    }

    private static DateTimeOffset? ParseTime(string? iso)
    {
        if (string.IsNullOrWhiteSpace(iso)) return null;
        return DateTimeOffset.TryParse(iso, out var value) ? value : null;
    }

    public async Task RemoveAlterFromPollsAsync(string systemId, int alterId, CancellationToken cancellationToken = default)
    {
        var polls = await ListAsync(systemId, cancellationToken);
        var alterIdString = alterId.ToString();

        foreach (var poll in polls)
        {
            var data = poll.Data;
            if (data.ValueKind != JsonValueKind.Object) continue;

            var changed = false;
            var newEntries = new Dictionary<string, JsonElement>();

            foreach (var property in data.EnumerateObject())
            {
                if (property.Name == alterIdString)
                {
                    changed = true;
                    continue;
                }
                newEntries[property.Name] = property.Value;
            }

            if (changed)
            {
                var newData = JsonSerializer.SerializeToElement(newEntries);
                await UpdateAsync(systemId, new UpdatePollCommand(
                    poll.Id,
                    null,
                    null,
                    null,
                    false,
                    newData
                ), cancellationToken);
            }
        }
    }

    private static PollReadModel ToReadModel(Row row)
        => new(
            row.GetValue<Guid>("id").ToString("N"),
            row.GetValue<string>("user_id"),
            row.GetValue<string>("title"),
            row.GetValue<string?>("description"),
            ToPollType(row.GetValue<short>("type")),
            JsonSerializer.Deserialize<JsonElement>(row.GetValue<string?>("data") ?? "{}"),
            row.GetValue<DateTime?>("time_end"),
            row.GetValue<DateTime>("inserted_at"),
            row.GetValue<DateTime>("updated_at")
        );
}
