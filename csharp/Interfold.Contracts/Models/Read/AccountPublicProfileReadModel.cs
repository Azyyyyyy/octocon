namespace Interfold.Contracts.Models.Read;

public sealed record AccountPublicProfileReadModel(
    string SystemId,
    string? Username,
    string? Description,
    string? AvatarUrl,
    string? DiscordId,
    string? Email,
    string? AppleId
);

public enum AccountLinkResult
{
    Success,
    AlreadyLinked,
    UserExists,
    UserNotFound
}