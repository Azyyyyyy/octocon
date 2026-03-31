namespace Interfold.Domain.Abstractions;

public interface IIdempotencyStore
{
    Task<IdempotencyMatch?> FindAsync(
        string principalId,
        string operationId,
        string idempotencyKey,
        CancellationToken cancellationToken = default
    );

    Task SaveAsync(
        string principalId,
        string operationId,
        string idempotencyKey,
        string payloadHash,
        string outcomeHash,
        string? outcomePayload,
        CancellationToken cancellationToken = default
    );
}

public sealed record IdempotencyMatch(string PayloadHash, string OutcomeHash, string? OutcomePayload);
