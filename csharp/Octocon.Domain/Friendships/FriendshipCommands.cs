namespace Octocon.Domain.Friendships;

public sealed record RemoveFriendshipCommand(string FriendSystemId);

public sealed record SetFriendTrustCommand(string FriendSystemId, bool Trusted);

public sealed record SendFriendRequestCommand(string TargetSystemId);

public sealed record AcceptFriendRequestCommand(string SourceSystemId);

public sealed record RejectFriendRequestCommand(string SourceSystemId);

public sealed record CancelFriendRequestCommand(string TargetSystemId);
