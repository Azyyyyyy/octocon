using Interfold.Contracts;
using Interfold.Contracts.Events;
using Interfold.Contracts.Models;
using Interfold.Contracts.Models.Commands;
using Interfold.Contracts.Operations;
using Interfold.Domain.Abstractions;

namespace Interfold.Domain.Settings;

public sealed class ImportSpCommandHandler : ICommandHandler<ImportSpCommand, SettingsCommandResult>
{
    private readonly IIdempotencyStore _idempotencyStore;
    private readonly IClusterEventBus _eventBus;
    private readonly ISimplyPluralImportService _importService;

    public ImportSpCommandHandler(IIdempotencyStore idempotencyStore, IClusterEventBus eventBus, ISimplyPluralImportService importService)
    {
        _idempotencyStore = idempotencyStore;
        _eventBus = eventBus;
        _importService = importService;
    }

    public Task<CommandExecutionResult<SettingsCommandResult>> HandleAsync(CommandEnvelope<ImportSpCommand> command, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(command.Payload.Token))
        {
            return Task.FromResult(CommandExecutionResult<SettingsCommandResult>.Rejected(
                new ConflictResult(ConflictCode.ConflictInvariant, command.OperationId, "settings:import_sp_invalid", "manual_merge_required")));
        }

        return ExecuteAndPublishAsync(command, cancellationToken);
    }

    private async Task<CommandExecutionResult<SettingsCommandResult>> ExecuteAndPublishAsync(
        CommandEnvelope<ImportSpCommand> command,
        CancellationToken cancellationToken)
    {
        // Captured from inside the apply delegate so the outer handler can broadcast the
        // matching socket lifecycle event (sp_import_complete includes alter_count, mirroring
        // the legacy Elixir Octocon.Workers.SimplyPluralImportWorker contract).
        SpImportResult? capturedResult = null;

        CommandExecutionResult<SettingsCommandResult> result;
        try
        {
            result = await SettingsCommandHelper.ExecuteAsync(
                command,
                "sp_imported",
                "settings:import:sp",
                _idempotencyStore,
                async ct =>
                {
                    capturedResult = await _importService.ImportAsync(
                        command.PrincipalId,
                        command.Payload.Token,
                        command.Payload.RecoveryCode,
                        ct);
                    return capturedResult.Success;
                },
                cancellationToken);
        }
        catch
        {
            // Mirror the legacy worker's rescue-clause: emit sp_import_failed even for
            // thrown exceptions (HTTP/transport failures, decryption errors, etc.) before
            // letting the exception propagate to the controller.
            await _eventBus.PublishAsync(new SimplyPluralImportFailedEvent(command.PrincipalId), cancellationToken);
            throw;
        }

        if (result is { Accepted: true, Result.Replay: false })
        {
            await _eventBus.PublishAsync(new SettingsProfileUpdatedEvent(command.PrincipalId, false), cancellationToken);
            await _eventBus.PublishAsync(
                new SimplyPluralImportCompletedEvent(command.PrincipalId, capturedResult?.AlterCount ?? 0),
                cancellationToken);
        }
        else if (capturedResult is { Success: false })
        {
            // Graceful import failure (auth, encryption mismatch, fetch errors that the
            // service swallows into a `Success=false` result) still warrants the socket
            // signal that the legacy contract sent.
            await _eventBus.PublishAsync(new SimplyPluralImportFailedEvent(command.PrincipalId), cancellationToken);
        }

        return result;
    }
}
