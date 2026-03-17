using Cassandra;
using Octocon.Domain.Friendships;
using Octocon.Infrastructure.Persistence.Transient;

namespace Octocon.Infrastructure.Persistence.Scylla;

public sealed class ScyllaFriendshipRepository : IFriendshipRepository
{
    private const short FriendLevel = 0;
    private const short TrustedFriendLevel = 1;

    private readonly IScyllaSessionProvider _sessionProvider;
    private readonly IScyllaKeyspaceResolver _keyspaceResolver;
    private readonly PersistenceRegistrationOptions _options;

    public ScyllaFriendshipRepository(
        IScyllaSessionProvider sessionProvider,
        IScyllaKeyspaceResolver keyspaceResolver,
        PersistenceRegistrationOptions options)
    {
        _sessionProvider = sessionProvider;
        _keyspaceResolver = keyspaceResolver;
        _options = options;
    }

    public async Task<IReadOnlyList<FriendshipReadModel>> ListFriendshipsAsync(string systemId, CancellationToken cancellationToken = default)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var normalizedSystemId = _keyspaceResolver.NormalizeSystemId(systemId);

            var query = new SimpleStatement(
                "SELECT friend_id, level, since FROM global.friendships WHERE user_id = ?",
                normalizedSystemId);

            var rows = await session.ExecuteAsync(query);
            var result = new List<FriendshipReadModel>();

            foreach (var row in rows)
            {
                var friendId = row.GetValue<string>("friend_id");
                var level = ToDomainLevel(row.GetValue<short>("level"));
                var since = row.GetValue<DateTimeOffset?>("since") ?? DateTimeOffset.UtcNow;

                var profile = await GetFriendProfileAsync(session, friendId);
                var fronting = await GetFrontingAsync(session, friendId);

                result.Add(new FriendshipReadModel(profile, level, since, fronting));
            }

            return result.OrderByDescending(x => x.Since).ToList();
        }, _options, cancellationToken);
    }

    public async Task<FriendshipReadModel?> GetFriendshipAsync(string systemId, string friendSystemId, CancellationToken cancellationToken = default)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var normalizedSystemId = _keyspaceResolver.NormalizeSystemId(systemId);
            var normalizedFriendSystemId = _keyspaceResolver.NormalizeSystemId(friendSystemId);

            var query = new SimpleStatement(
                "SELECT friend_id, level, since FROM global.friendships WHERE user_id = ? AND friend_id = ? LIMIT 1",
                normalizedSystemId,
                normalizedFriendSystemId);

            var row = (await session.ExecuteAsync(query)).FirstOrDefault();
            if (row is null)
            {
                return null;
            }

            var since = row.GetValue<DateTimeOffset?>("since") ?? DateTimeOffset.UtcNow;
            var profile = await GetFriendProfileAsync(session, normalizedFriendSystemId);
            var fronting = await GetFrontingAsync(session, normalizedFriendSystemId);

            return new FriendshipReadModel(
                profile,
                ToDomainLevel(row.GetValue<short>("level")),
                since,
                fronting);
        }, _options, cancellationToken);
    }

    public async Task<bool> RemoveFriendshipAsync(string systemId, string friendSystemId, CancellationToken cancellationToken = default)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var normalizedSystemId = _keyspaceResolver.NormalizeSystemId(systemId);
            var normalizedFriendId = _keyspaceResolver.NormalizeSystemId(friendSystemId);

            var exists = await ExistsFriendshipAsync(session, normalizedSystemId, normalizedFriendId);
            if (!exists)
            {
                return false;
            }

            await session.ExecuteAsync(new SimpleStatement(
                "DELETE FROM global.friendships WHERE user_id = ? AND friend_id = ?",
                normalizedSystemId,
                normalizedFriendId));

            await session.ExecuteAsync(new SimpleStatement(
                "DELETE FROM global.friendships WHERE user_id = ? AND friend_id = ?",
                normalizedFriendId,
                normalizedSystemId));

            return true;
        }, _options, cancellationToken);
    }

    public async Task<bool> SetTrustedAsync(string systemId, string friendSystemId, bool trusted, CancellationToken cancellationToken = default)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var normalizedSystemId = _keyspaceResolver.NormalizeSystemId(systemId);
            var normalizedFriendSystemId = _keyspaceResolver.NormalizeSystemId(friendSystemId);

            var exists = await ExistsFriendshipAsync(session, normalizedSystemId, normalizedFriendSystemId);
            if (!exists)
            {
                return false;
            }

            await session.ExecuteAsync(new SimpleStatement(
                "UPDATE global.friendships SET level = ? WHERE user_id = ? AND friend_id = ?",
                trusted ? TrustedFriendLevel : FriendLevel,
                normalizedSystemId,
                normalizedFriendSystemId));

            return true;
        }, _options, cancellationToken);
    }

    public async Task<FriendRequestIndexReadModel> GetFriendRequestsAsync(string systemId, CancellationToken cancellationToken = default)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var normalizedSystemId = _keyspaceResolver.NormalizeSystemId(systemId);

            var incomingRows = await session.ExecuteAsync(new SimpleStatement(
                "SELECT from_id, date_sent FROM global.friend_requests WHERE to_id = ?",
                normalizedSystemId));

            var outgoingRows = await session.ExecuteAsync(new SimpleStatement(
                "SELECT to_id, date_sent FROM global.friend_requests WHERE from_id = ?",
                normalizedSystemId));

            var incoming = new List<FriendRequestReadModel>();
            foreach (var row in incomingRows)
            {
                var sourceSystemId = row.GetValue<string>("from_id");
                var profile = await GetFriendProfileAsync(session, sourceSystemId);
                incoming.Add(new FriendRequestReadModel(profile, row.GetValue<DateTimeOffset?>("date_sent") ?? DateTimeOffset.UtcNow));
            }

            var outgoing = new List<FriendRequestReadModel>();
            foreach (var row in outgoingRows)
            {
                var targetSystemId = row.GetValue<string>("to_id");
                var profile = await GetFriendProfileAsync(session, targetSystemId);
                outgoing.Add(new FriendRequestReadModel(profile, row.GetValue<DateTimeOffset?>("date_sent") ?? DateTimeOffset.UtcNow));
            }

            return new FriendRequestIndexReadModel(
                incoming.OrderByDescending(x => x.DateSent).ToList(),
                outgoing.OrderByDescending(x => x.DateSent).ToList());
        }, _options, cancellationToken);
    }

    public async Task<SendFriendRequestOutcome> SendRequestAsync(string systemId, string targetSystemId, CancellationToken cancellationToken = default)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var normalizedSystemId = _keyspaceResolver.NormalizeSystemId(systemId);
            var normalizedTargetSystemId = _keyspaceResolver.NormalizeSystemId(targetSystemId);

            if (!await SystemExistsAsync(session, normalizedTargetSystemId))
            {
                return SendFriendRequestOutcome.NoUser;
            }

            if (await ExistsFriendshipAsync(session, normalizedSystemId, normalizedTargetSystemId))
            {
                return SendFriendRequestOutcome.AlreadyFriends;
            }

            if (await ExistsRequestAsync(session, normalizedSystemId, normalizedTargetSystemId))
            {
                return SendFriendRequestOutcome.AlreadySent;
            }

            if (await ExistsRequestAsync(session, normalizedTargetSystemId, normalizedSystemId))
            {
                await LinkFriendsAsync(session, normalizedSystemId, normalizedTargetSystemId);
                await DeleteRequestAsync(session, normalizedTargetSystemId, normalizedSystemId);
                await DeleteRequestAsync(session, normalizedSystemId, normalizedTargetSystemId);
                return SendFriendRequestOutcome.Accepted;
            }

            await CreateRequestAsync(session, normalizedSystemId, normalizedTargetSystemId);
            return SendFriendRequestOutcome.Sent;
        }, _options, cancellationToken);
    }

    public async Task<FriendRequestMutationOutcome> AcceptRequestAsync(string systemId, string sourceSystemId, CancellationToken cancellationToken = default)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var normalizedSystemId = _keyspaceResolver.NormalizeSystemId(systemId);
            var normalizedSourceSystemId = _keyspaceResolver.NormalizeSystemId(sourceSystemId);

            if (!await SystemExistsAsync(session, normalizedSourceSystemId))
            {
                return FriendRequestMutationOutcome.NoUser;
            }

            if (await ExistsFriendshipAsync(session, normalizedSystemId, normalizedSourceSystemId))
            {
                return FriendRequestMutationOutcome.AlreadyFriends;
            }

            if (!await ExistsRequestAsync(session, normalizedSourceSystemId, normalizedSystemId))
            {
                return FriendRequestMutationOutcome.NotRequested;
            }

            await LinkFriendsAsync(session, normalizedSystemId, normalizedSourceSystemId);
            await DeleteRequestAsync(session, normalizedSourceSystemId, normalizedSystemId);
            await DeleteRequestAsync(session, normalizedSystemId, normalizedSourceSystemId);
            return FriendRequestMutationOutcome.Ok;
        }, _options, cancellationToken);
    }

    public async Task<FriendRequestMutationOutcome> RejectRequestAsync(string systemId, string sourceSystemId, CancellationToken cancellationToken = default)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var normalizedSystemId = _keyspaceResolver.NormalizeSystemId(systemId);
            var normalizedSourceSystemId = _keyspaceResolver.NormalizeSystemId(sourceSystemId);

            if (!await SystemExistsAsync(session, normalizedSourceSystemId))
            {
                return FriendRequestMutationOutcome.NoUser;
            }

            if (await ExistsFriendshipAsync(session, normalizedSystemId, normalizedSourceSystemId))
            {
                return FriendRequestMutationOutcome.AlreadyFriends;
            }

            if (!await ExistsRequestAsync(session, normalizedSourceSystemId, normalizedSystemId))
            {
                return FriendRequestMutationOutcome.NotRequested;
            }

            await DeleteRequestAsync(session, normalizedSourceSystemId, normalizedSystemId);
            return FriendRequestMutationOutcome.Ok;
        }, _options, cancellationToken);
    }

    public async Task<FriendRequestMutationOutcome> CancelRequestAsync(string systemId, string targetSystemId, CancellationToken cancellationToken = default)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var normalizedSystemId = _keyspaceResolver.NormalizeSystemId(systemId);
            var normalizedTargetSystemId = _keyspaceResolver.NormalizeSystemId(targetSystemId);

            if (!await SystemExistsAsync(session, normalizedTargetSystemId))
            {
                return FriendRequestMutationOutcome.NoUser;
            }

            if (await ExistsFriendshipAsync(session, normalizedSystemId, normalizedTargetSystemId))
            {
                return FriendRequestMutationOutcome.AlreadyFriends;
            }

            if (!await ExistsRequestAsync(session, normalizedSystemId, normalizedTargetSystemId))
            {
                return FriendRequestMutationOutcome.NotRequested;
            }

            await DeleteRequestAsync(session, normalizedSystemId, normalizedTargetSystemId);
            return FriendRequestMutationOutcome.Ok;
        }, _options, cancellationToken);
    }

    internal static string ToDomainLevel(short level) => level == TrustedFriendLevel ? "trusted_friend" : "friend";

    private static async Task<bool> ExistsFriendshipAsync(ISession session, string userId, string friendId)
    {
        var query = new SimpleStatement(
            "SELECT friend_id FROM global.friendships WHERE user_id = ? AND friend_id = ? LIMIT 1",
            userId,
            friendId);

        return (await session.ExecuteAsync(query)).Any();
    }

    private static async Task<bool> SystemExistsAsync(ISession session, string userId)
    {
        var query = new SimpleStatement(
            "SELECT id FROM global.users WHERE id = ? LIMIT 1",
            userId);

        return (await session.ExecuteAsync(query)).Any();
    }

    private static async Task<bool> ExistsRequestAsync(ISession session, string fromId, string toId)
    {
        var query = new SimpleStatement(
            "SELECT to_id FROM global.friend_requests WHERE from_id = ? AND to_id = ? LIMIT 1",
            fromId,
            toId);

        return (await session.ExecuteAsync(query)).Any();
    }

    private static async Task CreateRequestAsync(ISession session, string fromId, string toId)
    {
        await session.ExecuteAsync(new SimpleStatement(
            "INSERT INTO global.friend_requests (from_id, to_id, date_sent, inserted_at, updated_at) VALUES (?, ?, toTimestamp(now()), toTimestamp(now()), toTimestamp(now()))",
            fromId,
            toId));
    }

    private static async Task DeleteRequestAsync(ISession session, string fromId, string toId)
    {
        await session.ExecuteAsync(new SimpleStatement(
            "DELETE FROM global.friend_requests WHERE from_id = ? AND to_id = ?",
            fromId,
            toId));
    }

    private static async Task LinkFriendsAsync(ISession session, string leftSystemId, string rightSystemId)
    {
        await session.ExecuteAsync(new SimpleStatement(
            "INSERT INTO global.friendships (user_id, friend_id, level, since, inserted_at, updated_at) VALUES (?, ?, ?, toTimestamp(now()), toTimestamp(now()), toTimestamp(now()))",
            leftSystemId,
            rightSystemId,
            FriendLevel));

        await session.ExecuteAsync(new SimpleStatement(
            "INSERT INTO global.friendships (user_id, friend_id, level, since, inserted_at, updated_at) VALUES (?, ?, ?, toTimestamp(now()), toTimestamp(now()), toTimestamp(now()))",
            rightSystemId,
            leftSystemId,
            FriendLevel));
    }

    private async Task<FriendProfileReadModel> GetFriendProfileAsync(ISession session, string friendSystemId)
    {
        var profileQuery = new SimpleStatement(
            "SELECT username, avatar_url, description, discord_id FROM global.users WHERE id = ? LIMIT 1",
            friendSystemId);

        var profileRow = (await session.ExecuteAsync(profileQuery)).FirstOrDefault();

        return new FriendProfileReadModel(
            friendSystemId,
            profileRow?.GetValue<string?>("username"),
            profileRow?.GetValue<string?>("avatar_url"),
            profileRow?.GetValue<string?>("description"),
            profileRow?.GetValue<string?>("discord_id"));
    }

    private async Task<IReadOnlyList<FriendFrontingReadModel>> GetFrontingAsync(ISession session, string friendSystemId)
    {
        var regionalKeyspace = _keyspaceResolver.ResolveRegionalKeyspace(friendSystemId);

        var activeRows = await session.ExecuteAsync(new SimpleStatement(
            $"SELECT alter_id, comment FROM {regionalKeyspace}.current_fronts WHERE user_id = ?",
            friendSystemId));

        var primaryRow = (await session.ExecuteAsync(new SimpleStatement(
            "SELECT primary_front FROM global.users WHERE id = ? LIMIT 1",
            friendSystemId))).FirstOrDefault();

        var primaryAlterId = primaryRow?.GetValue<int?>("primary_front");

        var alterRows = await session.ExecuteAsync(new SimpleStatement(
            $"SELECT id, name, alias FROM {regionalKeyspace}.alters WHERE user_id = ?",
            friendSystemId));

        var alterMap = alterRows.ToDictionary(
            row => row.GetValue<short>("id"),
            row => (Name: row.GetValue<string?>("name"), Alias: row.GetValue<string?>("alias")));

        return activeRows
            .Select(row =>
            {
                var alterId = row.GetValue<short>("alter_id");
                alterMap.TryGetValue(alterId, out var alter);
                return new FriendFrontingReadModel(
                    alterId,
                    alter.Name,
                    alter.Alias,
                    row.GetValue<string?>("comment"),
                    primaryAlterId == alterId);
            })
            .OrderBy(x => x.AlterId)
            .ToList();
    }
}
