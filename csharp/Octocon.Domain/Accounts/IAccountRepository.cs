namespace Octocon.Domain.Accounts;

public sealed record AccountPublicProfileReadModel(
    string SystemId,
    string? Username,
    string? Description,
    string? AvatarUrl
);

public enum AccountLinkResult
{
    Success,
    AlreadyLinked,
    UserExists,
    UserNotFound
}

public interface IAccountRepository
{
    Task<bool> UpdateUsernameAsync(string systemId, string username, CancellationToken cancellationToken = default);

    Task<bool> UpdateDescriptionAsync(string systemId, string description, CancellationToken cancellationToken = default);

    Task<bool> UpdateAvatarAsync(string systemId, string avatarUrl, CancellationToken cancellationToken = default);

    Task<bool> ClearAvatarAsync(string systemId, CancellationToken cancellationToken = default);

    Task<string> GetOrCreateLinkTokenAsync(string systemId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Read-only lookup: returns the existing link token for <paramref name="systemId"/>,
    /// or <see langword="null"/> if none has been created yet.
    /// Safe to call on non-primary nodes.
    /// </summary>
    Task<string?> GetLinkTokenAsync(string systemId, CancellationToken cancellationToken = default);

    Task<string?> ResolveSystemIdByLinkTokenAsync(string linkToken, CancellationToken cancellationToken = default);

    Task<bool> ClearLinkTokenAsync(string systemId, CancellationToken cancellationToken = default);

    Task<string?> FindSystemIdByDiscordIdAsync(string discordId, CancellationToken cancellationToken = default);

    Task<string?> FindSystemIdByEmailAsync(string email, CancellationToken cancellationToken = default);

    Task<string?> FindSystemIdByAppleIdAsync(string appleId, CancellationToken cancellationToken = default);

    Task<AccountLinkResult> LinkDiscordToUserAsync(string systemId, string discordId, CancellationToken cancellationToken = default);

    Task<AccountLinkResult> LinkEmailToUserAsync(string systemId, string email, CancellationToken cancellationToken = default);

    Task<AccountLinkResult> LinkAppleToUserAsync(string systemId, string appleId, CancellationToken cancellationToken = default);

    Task<AccountPublicProfileReadModel?> GetPublicProfileAsync(string systemId, CancellationToken cancellationToken = default);
}
