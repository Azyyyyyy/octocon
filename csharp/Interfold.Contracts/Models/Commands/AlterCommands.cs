namespace Interfold.Contracts.Models.Commands;

public sealed record CreateAlterCommand(string Name);

public sealed record UpdateAlterCommand(
    int AlterId,
    string? Name,
    string? Description,
    string? AvatarUrl,
    string? Color,
    string? Pronouns,
    string? SecurityLevel,
    IReadOnlyList<AlterFieldCommand>? Fields,
    string? ProxyName,
    string? Alias,
    bool? Untracked,
    bool? Archived,
    bool? Pinned,
    bool ClearAvatar = false
);

public sealed record DeleteAlterCommand(int AlterId);

public sealed record AlterFieldCommand(string Id, string? Value);
