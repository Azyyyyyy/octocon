namespace Octocon.Contracts.Operations;

public enum ConflictCode
{
    ConflictStaleVersion,
    ConflictDuplicate,
    ConflictInvariant
}

public sealed record ConflictResult(
    ConflictCode Code,
    string OperationId,
    string EntityRef,
    long? CurrentVersion,
    string ResolutionHint,
    object? ServerSnapshot
);
