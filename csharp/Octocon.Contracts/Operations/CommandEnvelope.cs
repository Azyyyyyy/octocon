namespace Octocon.Contracts.Operations;

public sealed record CommandEnvelope<TPayload>(
    string OperationId,
    Guid CommandId,
    string PrincipalId,
    string IdempotencyKey,
    long? ExpectedVersion,
    DateTimeOffset? OccurredAt,
    TPayload Payload
);
