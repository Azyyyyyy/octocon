using Interfold.Contracts.Models;

namespace Interfold.Domain.Abstractions.Repository;

public interface IEncryptionStateRepository
{
    Task<EncryptionState?> GetAsync(string systemId, CancellationToken cancellationToken = default);

    Task<bool> UpsertAsync(string systemId, bool initialized, string? keyChecksum, CancellationToken cancellationToken = default);
}
