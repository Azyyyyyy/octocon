using Interfold.Contracts.Enums;
using Interfold.Contracts.Models.Read;

namespace Interfold.Domain.Abstractions.Repository;

public interface IAccountRepository
{
    Task<bool> UpdateUsernameAsync(string systemId, string username, CancellationToken cancellationToken = default);

    Task<bool> UpdateDescriptionAsync(string systemId, string description, CancellationToken cancellationToken = default);

    Task<bool> UpdateAvatarAsync(string systemId, string avatarUrl, AvatarSource source, CancellationToken cancellationToken = default);

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

    Task<bool> UnlinkDiscordAsync(string systemId, CancellationToken cancellationToken = default);

    Task<bool> UnlinkEmailAsync(string systemId, CancellationToken cancellationToken = default);

    Task<bool> UnlinkAppleAsync(string systemId, CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(string systemId, CancellationToken cancellationToken = default);

    Task<AccountPublicProfileReadModel?> GetPublicProfileAsync(string systemId, CancellationToken cancellationToken = default);
}
