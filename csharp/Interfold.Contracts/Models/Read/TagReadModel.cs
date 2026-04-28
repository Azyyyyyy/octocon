namespace Interfold.Contracts.Models.Read;

public sealed record TagReadModel(
    string Id,
    string Name,
    string? Color,
    string? Description,
    string? ParentTagId,
    IReadOnlyList<int> Alters,
    DateTime InsertedAt,
    DateTime UpdatedAt,
    VisibilityLevel SecurityLevel,
    string? UserId
);

public sealed record TagPublicReadModel(
    string Id,
    string Name,
    string? Color,
    string? Description,
    string? ParentTagId,
    IReadOnlyList<BareAlter> Alters,
    DateTime InsertedAt,
    DateTime UpdatedAt,
    VisibilityLevel SecurityLevel,
    string? UserId
);