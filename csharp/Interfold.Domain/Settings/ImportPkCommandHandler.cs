using Interfold.Contracts;
using Interfold.Contracts.Events;
using Interfold.Contracts.Models;
using Interfold.Contracts.Models.Commands;
using Interfold.Contracts.Operations;
using Interfold.Domain.Abstractions;

namespace Interfold.Domain.Settings;

public sealed class ImportPkCommandHandler : ICommandHandler<ImportPkCommand, SettingsCommandResult>
{
    private readonly IIdempotencyStore _idempotencyStore;
    private readonly IClusterEventBus _eventBus;

    public ImportPkCommandHandler(IIdempotencyStore idempotencyStore, IClusterEventBus eventBus)
    {
        _idempotencyStore = idempotencyStore;
        _eventBus = eventBus;
    }

    public Task<CommandExecutionResult<SettingsCommandResult>> HandleAsync(CommandEnvelope<ImportPkCommand> command, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(command.Payload.Token))
        {
            return Task.FromResult(CommandExecutionResult<SettingsCommandResult>.Rejected(
                new ConflictResult(ConflictCode.ConflictInvariant, command.OperationId, "settings:import_pk_invalid", "manual_merge_required")));
        }

        return ExecuteAndPublishAsync(command, cancellationToken);
    }

    private async Task<CommandExecutionResult<SettingsCommandResult>> ExecuteAndPublishAsync(
        CommandEnvelope<ImportPkCommand> command,
        CancellationToken cancellationToken)
    {
        // The PK importer itself is still a TODO (mirrors Octocon.Workers.PluralKitImportWorker
        // in the legacy stack). The socket lifecycle wiring lives here so the broadcast
        // contract — pk_import_complete carrying alter_count, pk_import_failed signal — is
        // ready as soon as the apply delegate starts performing real work. Capture the
        // imported count out of the apply scope the same way ImportSpCommandHandler does.
        int importedAlterCount = 0;
        bool importApplied = false;

        CommandExecutionResult<SettingsCommandResult> result;
        try
        {
            result = await SettingsCommandHelper.ExecuteAsync(
                command,
                "pk_imported",
                "settings:import:pk",
                _idempotencyStore,
                _ =>
                {
                    importApplied = true;
                    return Task.FromResult(true);
                },
                cancellationToken);
        }
        catch
        {
            await _eventBus.PublishAsync(new PluralKitImportFailedEvent(command.PrincipalId), cancellationToken);
            throw;
        }

        if (result is { Accepted: true, Result.Replay: false })
        {
            await _eventBus.PublishAsync(new SettingsProfileUpdatedEvent(command.PrincipalId, false), cancellationToken);
            await _eventBus.PublishAsync(
                new PluralKitImportCompletedEvent(command.PrincipalId, importedAlterCount),
                cancellationToken);
        }
        else if (importApplied && result is { Accepted: false })
        {
            // Only fires once the real PK importer is wired up and starts returning false
            // for graceful failures; replay short-circuits before this branch and is a no-op.
            await _eventBus.PublishAsync(new PluralKitImportFailedEvent(command.PrincipalId), cancellationToken);
        }

        return result;
    }
}