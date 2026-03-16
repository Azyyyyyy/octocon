namespace Octocon.Domain.Alters;

public sealed record AlterPublicReadModel(int AlterId, string Name, string? Alias);

public interface IAlterRepository
{
    Task<int?> CreateAsync(string systemId, CreateAlterCommand command, CancellationToken cancellationToken = default);

    Task<bool> ExistsAsync(string systemId, int alterId, CancellationToken cancellationToken = default);

    Task<bool> UpdateAsync(string systemId, UpdateAlterCommand command, CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(string systemId, int alterId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AlterPublicReadModel>> ListAsync(string systemId, CancellationToken cancellationToken = default);

    Task<AlterPublicReadModel?> GetAsync(string systemId, int alterId, CancellationToken cancellationToken = default);

    Task<bool> AliasTakenByOtherAsync(
        string systemId,
        int alterId,
        string alias,
        CancellationToken cancellationToken = default
    );
}