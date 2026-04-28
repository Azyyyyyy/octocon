namespace Interfold.Contracts;

public static class DomainErrors
{
    public const string ValidationError = "validation_error";
    public const string NotFound = "not_found";
    public const string ConflictStaleVersion = "conflict_stale_version";
    public const string ConflictDuplicate = "conflict_duplicate";
    public const string ConflictInvariant = "conflict_invariant";
    public const string UnknownError = "unknown_error";
}
