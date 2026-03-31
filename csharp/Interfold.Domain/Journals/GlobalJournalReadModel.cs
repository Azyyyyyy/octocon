namespace Interfold.Domain.Journals;

public sealed record GlobalJournalReadModel(
    string EntryId,
    string Title,
    string? Content,
    string? Color,
    bool Pinned,
    bool Locked,
    IReadOnlyList<int> AlterIds
);
