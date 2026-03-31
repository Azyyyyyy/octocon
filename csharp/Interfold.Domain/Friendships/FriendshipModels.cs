namespace Interfold.Domain.Friendships;

public sealed record FriendProfileReadModel(
    string Id,
    string? Username,
    string? AvatarUrl,
    string? Description,
    string? DiscordId
);

public sealed record FriendFrontingReadModel(
    int AlterId,
    string? AlterName,
    string? AlterAlias,
    string? Comment,
    bool Primary
);

public record FriendshipModel(
    string Level,
    DateTimeOffset Since
);

public sealed record FriendshipReadModel(
    FriendProfileReadModel Friend,
    FriendshipModel Friendship,
    IReadOnlyList<FriendFrontingReadModel> Fronting
);

public sealed record FriendRequestReadModel(
    FriendProfileReadModel System,
    FriendshipRequestModel Request
);

public sealed record FriendshipRequestModel(
    DateTimeOffset DateSent
);

public sealed record FriendRequestIndexReadModel(
    IReadOnlyList<FriendRequestReadModel> Incoming,
    IReadOnlyList<FriendRequestReadModel> Outgoing
);

public enum SendFriendRequestOutcome
{
    Sent,
    Accepted,
    AlreadyFriends,
    AlreadySent,
    NoUser
}

public enum FriendRequestMutationOutcome
{
    Ok,
    AlreadyFriends,
    NotRequested,
    NoUser
}
