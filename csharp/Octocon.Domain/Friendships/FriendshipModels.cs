namespace Octocon.Domain.Friendships;

public sealed record FriendProfileReadModel(
    string SystemId,
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

public sealed record FriendshipReadModel(
    FriendProfileReadModel Friend,
    string Level,
    DateTimeOffset Since,
    IReadOnlyList<FriendFrontingReadModel> Fronting
);

public sealed record FriendRequestReadModel(
    FriendProfileReadModel System,
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
