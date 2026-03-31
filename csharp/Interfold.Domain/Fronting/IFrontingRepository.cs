namespace Interfold.Domain.Fronting;

public interface IFrontingRepository
{
    Task<bool> IsFrontingAsync(string systemId, int alterId, CancellationToken cancellationToken = default);

    Task<string?> StartAsync(string systemId, int alterId, string? comment, CancellationToken cancellationToken = default);

    Task<bool> EndAsync(string systemId, int alterId, CancellationToken cancellationToken = default);

    Task<bool> SetPrimaryAsync(string systemId, int? alterId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<FrontActiveReadModel>> ListActiveAsync(string systemId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<FrontActiveReadModel>> ListActiveGuardedAsync(
        string systemId,
        string? viewerSystemId,
        CancellationToken cancellationToken = default
    );

    Task<IReadOnlyList<FrontHistoryReadModel>> ListHistoryBetweenAsync(
        string systemId,
        DateTimeOffset startInclusive,
        DateTimeOffset endInclusive,
        CancellationToken cancellationToken = default);

    Task<FrontActiveReadModel?> GetActiveByFrontIdAsync(string systemId, string frontId, CancellationToken cancellationToken = default);

    Task<FrontHistoryReadModel?> GetHistoryEntryByFrontIdAsync(string systemId, string frontId, CancellationToken cancellationToken = default);

    Task<bool> EndByFrontIdAsync(string systemId, string frontId, CancellationToken cancellationToken = default);

    Task<bool> DeleteFrontByIdAsync(string systemId, string frontId, CancellationToken cancellationToken = default);

    Task<bool> UpdateCommentByFrontIdAsync(string systemId, string frontId, string comment, CancellationToken cancellationToken = default);
}