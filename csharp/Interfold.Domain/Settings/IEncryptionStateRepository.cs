namespace Interfold.Domain.Settings;

public sealed record EncryptionState(bool Initialized, string? KeyChecksum);

public interface IEncryptionStateRepository
{
    Task<EncryptionState?> GetAsync(string systemId, CancellationToken cancellationToken = default);

    Task<bool> UpsertAsync(string systemId, bool initialized, string? keyChecksum, CancellationToken cancellationToken = default);
}
