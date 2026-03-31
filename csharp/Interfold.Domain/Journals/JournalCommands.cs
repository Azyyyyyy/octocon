namespace Interfold.Domain.Journals;

public sealed record CreateGlobalJournalEntryCommand(string Title);

public sealed record UpdateGlobalJournalEntryCommand(
    string EntryId,
    string? Title,
    string? Content,
    string? Color
);

public sealed record DeleteGlobalJournalEntryCommand(string EntryId);

public sealed record SetGlobalJournalLockedCommand(string EntryId, bool Locked);

public sealed record SetGlobalJournalPinnedCommand(string EntryId, bool Pinned);

public sealed record AttachAlterToGlobalJournalCommand(string EntryId, int AlterId);

public sealed record DetachAlterFromGlobalJournalCommand(string EntryId, int AlterId);

public sealed record CreateAlterJournalEntryCommand(int AlterId, string Title);

public sealed record UpdateAlterJournalEntryCommand(
    string EntryId,
    string? Title,
    string? Content,
    string? Color
);

public sealed record DeleteAlterJournalEntryCommand(string EntryId);

public sealed record SetAlterJournalLockedCommand(string EntryId, bool Locked);

public sealed record SetAlterJournalPinnedCommand(string EntryId, bool Pinned);
