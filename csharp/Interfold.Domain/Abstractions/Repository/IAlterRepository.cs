using Interfold.Contracts.Models;
using Interfold.Contracts.Models.Commands;

namespace Interfold.Domain.Abstractions.Repository;

public interface IAlterRepository
{
    Task<int?> CreateAsync(string systemId, CreateAlterCommand command, CancellationToken cancellationToken = default);

    Task<bool> ExistsAsync(string systemId, int alterId, CancellationToken cancellationToken = default);

    Task<bool> UpdateAsync(string systemId, UpdateAlterCommand command, CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(string systemId, int alterId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AlterReadModel>> ListAsync(string systemId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<BareAlter>> ListGuardedAsync(
        string systemId,
        string? viewerSystemId,
        CancellationToken cancellationToken = default
    );

    Task<AlterReadModel?> GetAsync(string systemId, int alterId, CancellationToken cancellationToken = default);

    Task<BareAlter?> GetGuardedAsync(
        string systemId,
        int alterId,
        string? viewerSystemId,
        CancellationToken cancellationToken = default
    );

    Task<bool> AliasTakenByOtherAsync(
        string systemId,
        int alterId,
        string alias,
        CancellationToken cancellationToken = default
    );
}