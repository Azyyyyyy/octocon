using Interfold.Contracts.Models.Commands;
using Interfold.Contracts.Models.Read;

namespace Interfold.Domain.Abstractions.Repository;

public interface IPollRepository
{
    Task<IReadOnlyList<PollReadModel>> ListAsync(string systemId, CancellationToken cancellationToken = default);

    Task<PollReadModel?> GetAsync(string systemId, string pollId, CancellationToken cancellationToken = default);

    Task<string?> CreateAsync(string systemId, CreatePollCommand command, CancellationToken cancellationToken = default);

    Task<bool> ExistsAsync(string systemId, string pollId, CancellationToken cancellationToken = default);

    Task<bool> UpdateAsync(string systemId, UpdatePollCommand command, CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(string systemId, string pollId, CancellationToken cancellationToken = default);
    Task RemoveAlterFromPollsAsync(string systemId, int alterId, CancellationToken cancellationToken = default);
}
