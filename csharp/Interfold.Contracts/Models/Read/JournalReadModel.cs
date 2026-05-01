namespace Interfold.Contracts.Models.Read;

public sealed record JournalReadModel(
    string Id,
    string UserId,
    string Title,
    string? Content,
    string? Color,
    bool Locked,
    bool Pinned,
    DateTime InsertedAt,
    DateTime UpdatedAt,
    IReadOnlyList<int> Alters
);


public sealed record CreateGlobalJournalRequest(
    string Title,
    string? IdempotencyKey = null,
    long? ExpectedVersion = null
) : BaseRequest(IdempotencyKey);

public sealed record UpdateGlobalJournalRequest(
    string? Title = null,
    string? Content = null,
    string? Color = null,
    string? IdempotencyKey = null,
    long? ExpectedVersion = null
) : BaseRequest(IdempotencyKey);

public sealed record DeleteGlobalJournalRequest(
    string? IdempotencyKey = null,
    long? ExpectedVersion = null
) : BaseRequest(IdempotencyKey);

public sealed record JournalActionRequest(
    string? IdempotencyKey = null,
    long? ExpectedVersion = null
) : BaseRequest(IdempotencyKey);

public sealed record JournalAlterRequest(
    int? AlterId,
    string? IdempotencyKey = null,
    long? ExpectedVersion = null
) : BaseRequest(IdempotencyKey);
