using Interfold.Contracts.Models.Read;

namespace Interfold.Domain.Abstractions.Repository;

public interface ISettingsFieldRepository
{
    Task<IReadOnlyList<SettingsFieldReadModel>> ListAsync(string systemId, CancellationToken cancellationToken = default);

    Task<string?> CreateAsync(
        string systemId,
        string name,
        string type,
        string securityLevel,
        bool locked,
        DateTime insertedAtUtc,
        CancellationToken cancellationToken = default);

    Task<bool> UpdateAsync(
        string systemId,
        string fieldId,
        string? name,
        string? securityLevel,
        bool? locked,
        CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(string systemId, string fieldId, CancellationToken cancellationToken = default);

    Task<bool> RelocateAsync(string systemId, string fieldId, int index, CancellationToken cancellationToken = default);
}
