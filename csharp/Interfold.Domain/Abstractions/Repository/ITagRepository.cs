using Interfold.Contracts.Models.Commands;
using Interfold.Contracts.Models.Read;

namespace Interfold.Domain.Abstractions.Repository;

public interface ITagRepository
{
    /// <summary>Returns the new tag's ID on success, null if the parent was not found.</summary>
    Task<string?> CreateAsync(string systemId, CreateTagCommand command, CancellationToken cancellationToken = default);

    Task<bool> ExistsAsync(string systemId, string tagId, CancellationToken cancellationToken = default);

    /// <summary>Returns true if the tag was found and updated, false if not found.</summary>
    Task<bool> UpdateAsync(string systemId, UpdateTagCommand command, CancellationToken cancellationToken = default);

    /// <summary>Returns true if the tag was found and deleted, false if not found.</summary>
    Task<bool> DeleteAsync(string systemId, string tagId, CancellationToken cancellationToken = default);

    /// <summary>Returns true if the alter was attached, false if the tag does not exist.</summary>
    Task<bool> AttachAlterAsync(string systemId, string tagId, int alterId, CancellationToken cancellationToken = default);

    /// <summary>Returns true if the alter was detached, false if the tag/alter combo does not exist.</summary>
    Task<bool> DetachAlterAsync(string systemId, string tagId, int alterId, CancellationToken cancellationToken = default);

    /// <summary>Returns the immediate parent tag ID, or null if none or tag does not exist.</summary>
    Task<string?> GetParentIdAsync(string systemId, string tagId, CancellationToken cancellationToken = default);

    /// <summary>Returns true if the parent was set, false if the tag or parent tag does not exist.</summary>
    Task<bool> SetParentAsync(string systemId, string tagId, string parentTagId, CancellationToken cancellationToken = default);

    /// <summary>Returns true if the parent was removed, false if the tag does not exist.</summary>
    Task<bool> RemoveParentAsync(string systemId, string tagId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TagReadModel>> ListAsync(string systemId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TagPublicReadModel>> ListGuardedAsync(
        string systemId,
        string? viewerSystemId,
        CancellationToken cancellationToken = default
    );

    Task<TagReadModel?> GetAsync(string systemId, string tagId, CancellationToken cancellationToken = default);

    Task<TagPublicReadModel?> GetGuardedAsync(
        string systemId,
        string tagId,
        string? viewerSystemId,
        CancellationToken cancellationToken = default
    );
}
