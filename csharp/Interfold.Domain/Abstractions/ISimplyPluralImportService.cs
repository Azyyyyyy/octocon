namespace Interfold.Domain.Abstractions;

/// <summary>
/// Performs a full data import from Simply Plural for a given system.
/// </summary>
public interface ISimplyPluralImportService
{
    Task<SpImportResult> ImportAsync(string systemId, string spToken, CancellationToken cancellationToken = default);
}

public sealed record SpImportResult(bool Success, int AlterCount, string? Error = null);
