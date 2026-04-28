namespace Interfold.Contracts.Models.Read;

public sealed record FriendProfileReadModel(
    string Id,
    string? Username,
    string? AvatarUrl,
    string? Description,
    string? DiscordId
);

public sealed record FriendFrontingAlterReadModel(
    int Id,
    string? Name,
    string? Pronouns,
    string? Description,
    IReadOnlyList<object> Fields,
    string? AvatarUrl,
    IReadOnlyList<string> ExtraImages,
    string? Color
);

public sealed record FriendFrontingFrontReadModel(
    int AlterId,
    string? Comment
);

public sealed record FriendFrontingReadModel(
    FriendFrontingAlterReadModel Alter,
    FriendFrontingFrontReadModel Front,
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
