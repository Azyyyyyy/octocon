using Interfold.Contracts.Models.ImportOperations;
using Interfold.Domain.Abstractions;
using Interfold.Domain.Abstractions.ImportJobs;

namespace Interfold.Api.Services.ImportJobs;

/// <summary>
/// Bridges the generic <see cref="IImportJobRunner"/> contract to the concrete Simply
/// Plural importer (<see cref="ISimplyPluralImportService"/>). One per-process registration
/// (singleton). The background worker resolves this instance when it dequeues an item
/// with <see cref="ImportOperationKinds.SimplyPlural"/>.
/// </summary>
public sealed class SpImportJobRunner : IImportJobRunner
{
    private readonly ISimplyPluralImportService _importService;

    public SpImportJobRunner(ISimplyPluralImportService importService)
    {
        _importService = importService;
    }

    public string Kind => ImportOperationKinds.SimplyPlural;

    public async Task<ImportJobOutcome> RunAsync(ImportJobItem item, CancellationToken cancellationToken = default)
    {
        // The service returns Success=false for graceful failures (auth, decryption, etc.)
        // and throws only on transport or programming errors — let those bubble so the
        // worker classifies them as exception-failed.
        var result = await _importService.ImportAsync(
            item.SystemId,
            item.Token,
            item.RecoveryCode,
            cancellationToken).ConfigureAwait(false);

        if (result.Success)
        {
            return new ImportJobOutcome(Success: true, AlterCount: result.AlterCount);
        }

        // Map the service-level error string onto the persisted error_code. We keep the
        // raw service message in error_message for operators; the code is the stable
        // machine-readable handle that frontends or future automated retries can branch
        // on. Default "sp_import_failed" mirrors the legacy Elixir worker's terminal
        // socket frame so the client UX is unchanged.
        return new ImportJobOutcome(
            Success: false,
            AlterCount: 0,
            ErrorCode: "sp_import_failed",
            ErrorMessage: result.Error);
    }
}
