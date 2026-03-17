namespace Octocon.Domain.Alters;

public sealed record AlterPublicFieldReadModel(string Id, string Name, string Type, string? Value);

public sealed record AlterPublicReadModel(
    int AlterId,
    string Name,
    string? Alias,
    IReadOnlyList<AlterPublicFieldReadModel>? Fields = null
);

public enum VisibilityLevel
{
    Public = 0,
    FriendsOnly = 1,
    TrustedOnly = 2,
    Private = 3
}

public interface IAlterRepository
{
    Task<int?> CreateAsync(string systemId, CreateAlterCommand command, CancellationToken cancellationToken = default);

    Task<bool> ExistsAsync(string systemId, int alterId, CancellationToken cancellationToken = default);

    Task<bool> UpdateAsync(string systemId, UpdateAlterCommand command, CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(string systemId, int alterId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AlterPublicReadModel>> ListAsync(string systemId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AlterPublicReadModel>> ListGuardedAsync(
        string systemId,
        string? viewerSystemId,
        CancellationToken cancellationToken = default
    );

    Task<AlterPublicReadModel?> GetAsync(string systemId, int alterId, CancellationToken cancellationToken = default);

    Task<AlterPublicReadModel?> GetGuardedAsync(
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