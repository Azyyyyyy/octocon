namespace Octocon.Domain.Journals;

public sealed record AlterJournalReadModel(
    string EntryId,
    int AlterId,
    string Title,
    string? Content,
    string? Color,
    bool Pinned,
    bool Locked
);