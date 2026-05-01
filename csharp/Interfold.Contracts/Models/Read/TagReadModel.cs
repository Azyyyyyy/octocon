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

public sealed record CreateTagRequest(
    string Name,
    string? ParentTagId,
    string? IdempotencyKey = null
) : BaseRequest(IdempotencyKey);

public sealed record UpdateTagRequest(
    string? Name = null,
    string? Color = null,
    string? Description = null,
    string? SecurityLevel = null,
    string? IdempotencyKey = null
) : BaseRequest(IdempotencyKey);

public sealed record TagAlterRequest(
    int? AlterId,
    string? IdempotencyKey = null
) : BaseRequest(IdempotencyKey);

public sealed record SetParentRequest(
    string? ParentTagId,
    string? IdempotencyKey = null
) : BaseRequest(IdempotencyKey);
