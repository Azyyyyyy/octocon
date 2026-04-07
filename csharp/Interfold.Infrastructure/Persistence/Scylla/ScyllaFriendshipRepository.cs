using Cassandra;
using Interfold.Domain.Friendships;
using Interfold.Infrastructure.Configuration;
using Interfold.Infrastructure.Persistence.Transient;

namespace Interfold.Infrastructure.Persistence.Scylla;

public sealed class ScyllaFriendshipRepository : IFriendshipRepository
{
    private const short FriendLevel = 0;
    private const short TrustedFriendLevel = 1;
    private static readonly string[] CanonicalRegions = [    
        "nam",
        "eur",
        "ocn",
        "eas",
        "sam",
        "sas",
        "gdpr" 
    ];

    private readonly IScyllaSessionProvider _sessionProvider;
    private readonly IScyllaKeyspaceResolver _keyspaceResolver;
    private readonly PersistenceConfiguration _options;

    public ScyllaFriendshipRepository(
        IScyllaSessionProvider sessionProvider,
        IScyllaKeyspaceResolver keyspaceResolver,
        PersistenceConfiguration options)
    {
        _sessionProvider = sessionProvider;
        _keyspaceResolver = keyspaceResolver;
        _options = options;
    }

    public async Task<string?> ResolveUserIdAsync(string userNameOrId, CancellationToken cancellationToken = default)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            return await ResolveUserIdInScyllaAsync(session, _keyspaceResolver.NormalizeSystemId(userNameOrId));
        }, _options, cancellationToken);
    }

    public async Task<string?> GetFriendshipLevelAsync(string systemId, string? viewerSystemId, CancellationToken cancellationToken = default)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            if (string.IsNullOrWhiteSpace(viewerSystemId))
            {
                return null;
            }

            var normalizedSystemId = _keyspaceResolver.NormalizeSystemId(systemId);
            var normalizedViewerSystemId = _keyspaceResolver.NormalizeSystemId(viewerSystemId);

            if (string.Equals(normalizedSystemId, normalizedViewerSystemId, StringComparison.Ordinal))
            {
                return "trusted_friend";
            }

            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var query = new SimpleStatement(
                "SELECT level FROM global.friendships WHERE user_id = ? AND friend_id = ? LIMIT 1",
                normalizedSystemId,
                normalizedViewerSystemId);

            var row = (await session.ExecuteAsync(query)).FirstOrDefault();
            return row is null ? null : ToDomainLevel(row.GetValue<short>("level"));
        }, _options, cancellationToken);
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
                var fronting = await GetFrontingAsync(session, friendId, normalizedSystemId);

                result.Add(new FriendshipReadModel(profile, new FriendshipModel(level, since), fronting));
            }

            return result.OrderByDescending(x => x.Friendship.Since).ToList();
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

            var friendId = row.GetValue<string>("friend_id");
            var since = row.GetValue<DateTimeOffset?>("since") ?? DateTimeOffset.UtcNow;
            var level = ToDomainLevel(row.GetValue<short>("level"));
            var profile = await GetFriendProfileAsync(session, normalizedFriendSystemId);
            var fronting = await GetFrontingAsync(session, normalizedFriendSystemId, normalizedSystemId);

            return new FriendshipReadModel(
                profile,
                new FriendshipModel(level, since),
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
                incoming.Add(new FriendRequestReadModel(profile, new FriendshipRequestModel(row.GetValue<DateTimeOffset?>("date_sent") ?? DateTimeOffset.UtcNow)));
            }

            var outgoing = new List<FriendRequestReadModel>();
            foreach (var row in outgoingRows)
            {
                var targetSystemId = row.GetValue<string>("to_id");
                var profile = await GetFriendProfileAsync(session, targetSystemId);
                outgoing.Add(new FriendRequestReadModel(profile, new FriendshipRequestModel(row.GetValue<DateTimeOffset?>("date_sent") ?? DateTimeOffset.UtcNow)));
            }

            return new FriendRequestIndexReadModel(
                incoming.OrderByDescending(x => x.Request.DateSent).ToList(),
                outgoing.OrderByDescending(x => x.Request.DateSent).ToList());
        }, _options, cancellationToken);
    }

    public async Task<SendFriendRequestOutcome> SendRequestAsync(string systemId, string targetSystemId, CancellationToken cancellationToken = default)
    {
        return await DatabaseTransientRetry.ExecuteScyllaAsync(async () =>
        {
            var session = await _sessionProvider.GetSessionAsync(cancellationToken);
            var normalizedSystemId = _keyspaceResolver.NormalizeSystemId(systemId);
            var normalizedTargetSystemId = _keyspaceResolver.NormalizeSystemId(targetSystemId);

            var resolvedTargetUserId = await ResolveUserIdInScyllaAsync(session, normalizedTargetSystemId);
            if (resolvedTargetUserId is null)
            {
                return SendFriendRequestOutcome.NoUser;
            }
            normalizedTargetSystemId = resolvedTargetUserId;

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

            var resolvedSourceUserId = await ResolveUserIdInScyllaAsync(session, normalizedSourceSystemId);
            if (resolvedSourceUserId is null)
            {
                return FriendRequestMutationOutcome.NoUser;
            }
            normalizedSourceSystemId = resolvedSourceUserId;

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

            var resolvedSourceUserId = await ResolveUserIdInScyllaAsync(session, normalizedSourceSystemId);
            if (resolvedSourceUserId is null)
            {
                return FriendRequestMutationOutcome.NoUser;
            }
            normalizedSourceSystemId = resolvedSourceUserId;

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

            var resolvedTargetUserId = await ResolveUserIdInScyllaAsync(session, normalizedTargetSystemId);
            if (resolvedTargetUserId is null)
            {
                return FriendRequestMutationOutcome.NoUser;
            }
            normalizedTargetSystemId = resolvedTargetUserId;

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
            "SELECT user_id FROM global.user_registry WHERE user_id = ? LIMIT 1",
            userId);

        return (await session.ExecuteAsync(query)).Any();
    }

    private static async Task<string?> ResolveUserIdInScyllaAsync(ISession session, string userNameOrId)
    {
        // First, try direct lookup in user_registry (handles 7-char IDs)
        var directQuery = new SimpleStatement(
            "SELECT user_id FROM global.user_registry WHERE user_id = ? LIMIT 1",
            userNameOrId);
        var directRow = (await session.ExecuteAsync(directQuery)).FirstOrDefault();
        if (directRow != null)
        {
            return directRow.GetValue<string>("user_id");
        }

        // If not found as direct ID, try as username across known regional keyspaces that exist.
        var existingKeyspaces = await GetExistingRegionalKeyspacesAsync(session);

        foreach (var region in existingKeyspaces.Where(CanonicalRegions.Contains))
        {
            try
            {
                var userQuery = new SimpleStatement(
                    $"SELECT id FROM {region}.users WHERE username = ? LIMIT 1",
                    userNameOrId);
                var userRow = (await session.ExecuteAsync(userQuery)).FirstOrDefault();
                if (userRow != null)
                {
                    return userRow.GetValue<string>("id");
                }
            }
            catch (UnavailableException)
            {
                // Region keyspace/table temporarily unavailable; skip and try next region
                continue;
            }
            catch (InvalidQueryException)
            {
                // Table doesn't exist in this keyspace; skip and try next region
                continue;
            }
        }

        return null;
    }

    private static async Task<HashSet<string>> GetExistingRegionalKeyspacesAsync(ISession session)
    {
        var keyspacesQuery = new SimpleStatement("SELECT keyspace_name FROM system_schema.keyspaces");
        var keyspaceRows = await session.ExecuteAsync(keyspacesQuery);

        return keyspaceRows
            .Select(row => row.GetValue<string>("keyspace_name").ToLowerInvariant())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
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
        var region = await ResolveUserRegionAsync(session, friendSystemId);
        if (string.IsNullOrWhiteSpace(region))
        {
            return new FriendProfileReadModel(friendSystemId, null, null, null, null);
        }

        var profileQuery = new SimpleStatement(
            $"SELECT username, avatar_url, description, discord_id FROM {region}.users WHERE id = ? LIMIT 1",
            friendSystemId);

        var profileRow = (await session.ExecuteAsync(profileQuery)).FirstOrDefault();

        return new FriendProfileReadModel(
            friendSystemId,
            profileRow?.GetValue<string?>("username"),
            profileRow?.GetValue<string?>("avatar_url"),
            profileRow?.GetValue<string?>("description"),
            profileRow?.GetValue<string?>("discord_id"));
    }

    private async Task<IReadOnlyList<FriendFrontingReadModel>> GetFrontingAsync(ISession session, string friendSystemId, string viewerSystemId)
    {
        var regionalKeyspace = await ResolveUserRegionAsync(session, friendSystemId);
        if (string.IsNullOrWhiteSpace(regionalKeyspace))
        {
            return [];
        }

        // Friendship level from the friend's perspective (they control their own alter visibility)
        var levelRow = (await session.ExecuteAsync(new SimpleStatement(
            "SELECT level FROM global.friendships WHERE user_id = ? AND friend_id = ? LIMIT 1",
            friendSystemId,
            viewerSystemId))).FirstOrDefault();
        var friendshipLevel = levelRow is null ? null : ToDomainLevel(levelRow.GetValue<short>("level"));
        var activeRows = await session.ExecuteAsync(new SimpleStatement(
            $"SELECT alter_id, comment FROM {regionalKeyspace}.current_fronts WHERE user_id = ?",
            friendSystemId));

        var primaryRow = (await session.ExecuteAsync(new SimpleStatement(
            $"SELECT primary_front FROM {regionalKeyspace}.users WHERE id = ? LIMIT 1",
            friendSystemId))).FirstOrDefault();

        var primaryAlterId = primaryRow?.GetValue<int?>("primary_front");

        var alterRows = await session.ExecuteAsync(new SimpleStatement(
            $"SELECT id, name, avatar_url, pronouns, color, description, extra_images, security_level FROM {regionalKeyspace}.alters WHERE user_id = ?",
            friendSystemId));

        var alterMap = alterRows
            .Where(row => CanViewAlter(friendshipLevel, row.GetValue<short?>("security_level")))
            .ToDictionary(
                row => row.GetValue<short>("id"),
                row => (
                    Name: row.GetValue<string?>("name"),
                    AvatarUrl: row.GetValue<string?>("avatar_url"),
                    Pronouns: row.GetValue<string?>("pronouns"),
                    Color: row.GetValue<string?>("color"),
                    Description: row.GetValue<string?>("description"),
                    ExtraImages: (IReadOnlyList<string>)(row.GetValue<IEnumerable<string>?>("extra_images")?.ToList() ?? [])));

        return activeRows
            .Select(row =>
            {
                var alterId = row.GetValue<short>("alter_id");
                if (!alterMap.TryGetValue(alterId, out var alter))
                {
                    return null;
                }
                return new FriendFrontingReadModel(
                    new FriendFrontingAlterReadModel(
                        alterId,
                        alter.Name,
                        alter.Pronouns,
                        alter.Description,
                        [],
                        alter.AvatarUrl,
                        alter.ExtraImages,
                        alter.Color),
                    new FriendFrontingFrontReadModel(alterId, row.GetValue<string?>("comment")),
                    primaryAlterId == alterId);
            })
            .Where(x => x is not null)
            .OrderBy(x => x!.Alter.Id)
            .ToList()!;
    }

    private static bool CanViewAlter(string? friendshipLevel, short? securityLevel)
    {
        return securityLevel switch
        {
            1 => friendshipLevel is "friend" or "trusted_friend",
            2 => friendshipLevel is "trusted_friend",
            3 => false,
            _ => true
        };
    }

    private static async Task<string?> ResolveUserRegionAsync(ISession session, string userId)
    {
        var regionQuery = new SimpleStatement(
            "SELECT region FROM global.user_registry WHERE user_id = ? LIMIT 1",
            userId);

        var row = (await session.ExecuteAsync(regionQuery)).FirstOrDefault();
        return row?.GetValue<string>("region")?.ToLowerInvariant();
    }
}
