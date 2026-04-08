using Interfold.Domain.Abstractions;
using Interfold.Domain.Friendships;
using Interfold.Api.Services;

namespace Interfold.Api.Socket.Handlers;

public static class FriendshipSocketEventHandlers
{
    public static async Task HandleAsync(FriendshipAddedEvent evt, SocketPushContext context, IFriendshipRepository friendshipRepository)
    {
        if (!context.TryGetSystemTopic(evt.TargetSystemId, out var topic, out var joinRef, out var asArray))
        {
            return;
        }

        var friendship = await GetFriendshipWithRetryAsync(friendshipRepository, evt.TargetSystemId, evt.SystemId, context.CancellationToken);
        var qualified = friendship is null
            ? new FriendshipReadModel(
                new FriendProfileReadModel(evt.SystemId, string.Empty, string.Empty, string.Empty, string.Empty),
                new FriendshipModel("friend", DateTimeOffset.UtcNow),
                [])
            : QualifyFriendship(friendship, context);
        
        var payloadJson = WebSocketEvents.SerializeSocketJson(qualified);
        await context.SendAsync(topic, joinRef, asArray, SocketEventNames.Friendships.Added, payloadJson);
    }

    public static Task HandleAsync(FriendshipRemovedEvent evt, SocketPushContext context)
        => SendAsync(evt.TargetSystemId, SocketEventNames.Friendships.Removed, "friend_id", evt.SystemId, context);

    public static Task HandleAsync(FriendshipTrustedEvent evt, SocketPushContext context)
        => SendAsync(evt.TargetSystemId, SocketEventNames.Friendships.Trusted, "friend_id", evt.SystemId, context);

    public static Task HandleAsync(FriendshipUntrustedEvent evt, SocketPushContext context)
        => SendAsync(evt.TargetSystemId, SocketEventNames.Friendships.Untrusted, "friend_id", evt.SystemId, context);

    public static async Task HandleAsync(FriendRequestSentEvent evt, SocketPushContext context, IFriendshipRepository friendshipRepository)
    {
        await SendRequestPayloadAsync(
            evt.TargetSystemId,
            evt.ToSystemId,
            SocketEventNames.Friendships.RequestSent,
            context,
            friendshipRepository,
            outgoing: true);
    }

    public static async Task HandleAsync(FriendRequestReceivedEvent evt, SocketPushContext context, IFriendshipRepository friendshipRepository)
    {
        await SendRequestPayloadAsync(
            evt.TargetSystemId,
            evt.FromSystemId,
            SocketEventNames.Friendships.RequestReceived,
            context,
            friendshipRepository,
            outgoing: false);
    }

    public static Task HandleAsync(FriendRequestRemovedFromEvent evt, SocketPushContext context)
        => SendAsync(evt.TargetSystemId, SocketEventNames.Friendships.RequestRemoved, "system_id", evt.FromSystemId, context);

    public static Task HandleAsync(FriendRequestRemovedToEvent evt, SocketPushContext context)
        => SendAsync(evt.TargetSystemId, SocketEventNames.Friendships.RequestRemoved, "system_id", evt.ToSystemId, context);

    private static async Task SendRequestPayloadAsync(
        string targetSystemId,
        string otherSystemId,
        string eventName,
        SocketPushContext context,
        IFriendshipRepository friendshipRepository,
        bool outgoing)
    {
        if (!context.TryGetSystemTopic(targetSystemId, out var topic, out var joinRef, out var asArray))
        {
            return;
        }

        var matched = await GetFriendRequestWithRetryAsync(
            friendshipRepository,
            targetSystemId,
            otherSystemId,
            outgoing,
            context.CancellationToken);

        var payloadJson = matched is null
            ? WebSocketEvents.SerializeSocketJson(new
            {
                request = new FriendshipRequestModel(DateTimeOffset.UtcNow),
                system = new FriendProfileReadModel(otherSystemId, string.Empty, string.Empty, string.Empty, string.Empty)
            })
            : WebSocketEvents.SerializeSocketJson(new
            {
                request = matched.Request,
                system = matched.System with { AvatarUrl = AvatarUrlQualifier.Qualify(matched.System.AvatarUrl, context.RequestOrigin) }
            });

        await context.SendAsync(topic, joinRef, asArray, eventName, payloadJson);
    }

    private static async Task<FriendshipReadModel?> GetFriendshipWithRetryAsync(
        IFriendshipRepository friendshipRepository,
        string targetSystemId,
        string friendSystemId,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var friendship = await friendshipRepository.GetFriendshipAsync(targetSystemId, friendSystemId, cancellationToken);
            if (friendship is not null)
            {
                return friendship;
            }

            if (attempt < 2)
            {
                await Task.Delay(50, cancellationToken);
            }
        }

        return null;
    }

    private static async Task<FriendRequestReadModel?> GetFriendRequestWithRetryAsync(
        IFriendshipRepository friendshipRepository,
        string targetSystemId,
        string otherSystemId,
        bool outgoing,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var index = await friendshipRepository.GetFriendRequestsAsync(targetSystemId, cancellationToken);
            var matched = (outgoing ? index.Outgoing : index.Incoming)
                .FirstOrDefault(r => IdMatches(r.System?.Id, otherSystemId));

            if (matched is not null)
            {
                return matched;
            }

            if (attempt < 9)
            {
                await Task.Delay(100, cancellationToken);
            }
        }

        return null;
    }

    private static bool IdMatches(string? left, string right)
    {
        if (string.Equals(left, right, StringComparison.Ordinal))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(left))
        {
            return false;
        }

        var leftSuffix = left.Contains(':', StringComparison.Ordinal)
            ? left[(left.IndexOf(':', StringComparison.Ordinal) + 1)..]
            : left;
        var rightSuffix = right.Contains(':', StringComparison.Ordinal)
            ? right[(right.IndexOf(':', StringComparison.Ordinal) + 1)..]
            : right;

        return string.Equals(leftSuffix, rightSuffix, StringComparison.Ordinal);
    }

    private static async Task SendAsync(string targetSystemId, string eventName, string payloadKey, string payloadValue, SocketPushContext context)
    {
        if (!context.TryGetSystemTopic(targetSystemId, out var topic, out var joinRef, out var asArray))
        {
            return;
        }

        var payloadJson = WebSocketEvents.SerializeSocketJson(new Dictionary<string, object?>
        {
            [payloadKey] = payloadValue
        });

        await context.SendAsync(topic, joinRef, asArray, eventName, payloadJson);
    }

    private static FriendshipReadModel QualifyFriendship(FriendshipReadModel friendship, SocketPushContext context)
    {
        var qualifyUrl = (string? url) => AvatarUrlQualifier.Qualify(url, context.RequestOrigin);
        return friendship with
        {
            Friend = friendship.Friend with { AvatarUrl = qualifyUrl(friendship.Friend.AvatarUrl) },
            Fronting = friendship.Fronting
                .Select(f => f with { Alter = f.Alter with { AvatarUrl = qualifyUrl(f.Alter.AvatarUrl) } })
                .ToArray()
        };
    }
}
