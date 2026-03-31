namespace Interfold.Domain.Journals;

public interface IJournalRepository
{
    Task<string?> CreateGlobalAsync(string systemId, CreateGlobalJournalEntryCommand command, CancellationToken cancellationToken = default);

    Task<bool> ExistsGlobalAsync(string systemId, string entryId, CancellationToken cancellationToken = default);

    Task<bool> UpdateGlobalAsync(string systemId, UpdateGlobalJournalEntryCommand command, CancellationToken cancellationToken = default);

    Task<bool> DeleteGlobalAsync(string systemId, string entryId, CancellationToken cancellationToken = default);

    Task<bool> SetGlobalLockedAsync(string systemId, string entryId, bool locked, CancellationToken cancellationToken = default);

    Task<bool> SetGlobalPinnedAsync(string systemId, string entryId, bool pinned, CancellationToken cancellationToken = default);

    Task<bool> AttachGlobalAlterAsync(string systemId, string entryId, int alterId, CancellationToken cancellationToken = default);

    Task<bool> DetachGlobalAlterAsync(string systemId, string entryId, int alterId, CancellationToken cancellationToken = default);

    Task<string?> CreateAlterAsync(string systemId, CreateAlterJournalEntryCommand command, CancellationToken cancellationToken = default);

    Task<AlterJournalRef?> GetAlterRefAsync(string systemId, string entryId, CancellationToken cancellationToken = default);

    Task<bool> UpdateAlterAsync(string systemId, UpdateAlterJournalEntryCommand command, CancellationToken cancellationToken = default);

    Task<bool> DeleteAlterAsync(string systemId, string entryId, CancellationToken cancellationToken = default);

    Task<bool> SetAlterLockedAsync(string systemId, string entryId, bool locked, CancellationToken cancellationToken = default);

    Task<bool> SetAlterPinnedAsync(string systemId, string entryId, bool pinned, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AlterJournalReadModel>> ListAlterAsync(string systemId, int alterId, CancellationToken cancellationToken = default);

    Task<AlterJournalReadModel?> GetAlterAsync(string systemId, string entryId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<GlobalJournalReadModel>> ListGlobalAsync(string systemId, CancellationToken cancellationToken = default);

    Task<GlobalJournalReadModel?> GetGlobalAsync(string systemId, string entryId, CancellationToken cancellationToken = default);
}

public sealed record AlterJournalRef(string EntryId, int AlterId);
