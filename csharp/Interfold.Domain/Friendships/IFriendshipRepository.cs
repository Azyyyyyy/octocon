namespace Interfold.Domain.Friendships;

public interface IFriendshipRepository
{
    Task<string?> ResolveUserIdAsync(string userNameOrId, CancellationToken cancellationToken = default);

    Task<string?> GetFriendshipLevelAsync(string systemId, string? viewerSystemId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<FriendshipReadModel>> ListFriendshipsAsync(string systemId, CancellationToken cancellationToken = default);

    Task<FriendshipReadModel?> GetFriendshipAsync(string systemId, string friendSystemId, CancellationToken cancellationToken = default);

    Task<bool> RemoveFriendshipAsync(string systemId, string friendSystemId, CancellationToken cancellationToken = default);

    Task<bool> SetTrustedAsync(string systemId, string friendSystemId, bool trusted, CancellationToken cancellationToken = default);

    Task<FriendRequestIndexReadModel> GetFriendRequestsAsync(string systemId, CancellationToken cancellationToken = default);

    Task<SendFriendRequestOutcome> SendRequestAsync(string systemId, string targetSystemId, CancellationToken cancellationToken = default);

    Task<FriendRequestMutationOutcome> AcceptRequestAsync(string systemId, string sourceSystemId, CancellationToken cancellationToken = default);

    Task<FriendRequestMutationOutcome> RejectRequestAsync(string systemId, string sourceSystemId, CancellationToken cancellationToken = default);

    Task<FriendRequestMutationOutcome> CancelRequestAsync(string systemId, string targetSystemId, CancellationToken cancellationToken = default);
    
    Task<IReadOnlyList<string>> DeleteAllForSystemAsync(string systemId, CancellationToken cancellationToken = default);
}
