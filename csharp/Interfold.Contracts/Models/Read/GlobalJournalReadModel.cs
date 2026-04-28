namespace Interfold.Contracts.Models.Read;

public sealed record GlobalJournalReadModel(
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
