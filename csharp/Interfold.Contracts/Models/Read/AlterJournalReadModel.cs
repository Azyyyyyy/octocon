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

public sealed record AlterJournalRef(string EntryId, int AlterId);