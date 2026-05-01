namespace Interfold.Contracts.Models.Read;

public sealed record AlterJournalReadModel(
    string Id,
    string UserId,
    int AlterId,
    string Title,
    string? Content,
    string? Color,
    bool Locked,
    bool Pinned,
    DateTime InsertedAt,
    DateTime UpdatedAt
);

public sealed record CreateAlterJournalRequest(
    string Title,
    string? IdempotencyKey = null
) : BaseRequest(IdempotencyKey);

public sealed record UpdateAlterJournalRequest(
    string? Title = null,
    string? Content = null,
    string? Color = null,
    string? IdempotencyKey = null
) : BaseRequest(IdempotencyKey);

public sealed record CreateAlterRequest(
    string Name,
    string? IdempotencyKey = null
) : BaseRequest(IdempotencyKey);

public sealed record UpdateAlterRequest(
    string? Name = null,
    string? Description = null,
    string? AvatarUrl = null,
    string? Color = null,
    string? Pronouns = null,
    string? SecurityLevel = null,
    string? ProxyName = null,
    string? Alias = null,
    bool? Untracked = null,
    bool? Archived = null,
    bool? Pinned = null,
    IReadOnlyList<UpdateAlterFieldRequest>? Fields = null,
    string? IdempotencyKey = null
) : BaseRequest(IdempotencyKey);

public sealed record UpdateAlterFieldRequest(
    string Id,
    string? Value
);


public sealed record AlterJournalRef(string EntryId, int AlterId);