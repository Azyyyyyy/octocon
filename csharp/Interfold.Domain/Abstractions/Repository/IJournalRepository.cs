using Interfold.Contracts.Models.Commands;
using Interfold.Contracts.Models.Read;

namespace Interfold.Domain.Abstractions.Repository;

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

    /// <summary>
    /// Cascade cleanup for <see cref="Interfold.Domain.Alters.DeleteAlterCommandHandler"/>:
    /// wipes every per-alter journal entry the alter owned (both view tables in the Scylla
    /// repository) and detaches the alter from any global journals it was attached to.
    /// <para>
    /// Global journal rows themselves are left intact - they may be attached to other
    /// alters, and we do not want deleting one alter to silently delete a shared group
    /// journal. The alter-id rows in the <c>global_journal_alters</c> join table are
    /// removed so the deleted alter does not appear on any global journal's attached-alters
    /// list after this returns.
    /// </para>
    /// <para>
    /// Idempotent: returns gracefully when the alter has no journal entries and no global
    /// journal attachments. Returns the number of per-alter journal entries that were
    /// removed (the global-journal detach count is logged at the repository level but not
    /// surfaced; the caller only needs the alter-journal count for telemetry).
    /// </para>
    /// </summary>
    Task<int> DeleteAllForAlterAsync(string systemId, int alterId, CancellationToken cancellationToken = default);

    Task<bool> SetAlterLockedAsync(string systemId, string entryId, bool locked, CancellationToken cancellationToken = default);

    Task<bool> SetAlterPinnedAsync(string systemId, string entryId, bool pinned, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AlterJournalReadModel>> ListAlterAsync(string systemId, int alterId, CancellationToken cancellationToken = default);

    Task<AlterJournalReadModel?> GetAlterAsync(string systemId, string entryId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<JournalReadModel>> ListGlobalAsync(string systemId, CancellationToken cancellationToken = default);

    Task<JournalReadModel?> GetGlobalAsync(string systemId, string entryId, CancellationToken cancellationToken = default);
}