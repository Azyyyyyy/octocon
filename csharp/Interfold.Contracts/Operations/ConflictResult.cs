namespace Interfold.Contracts.Operations;

public enum ConflictCode
{
    ConflictDuplicate,
    ConflictInvariant
}

public sealed record ConflictResult(
    ConflictCode Code,
    string OperationId,
    string EntityRef,
    string ResolutionHint
);
