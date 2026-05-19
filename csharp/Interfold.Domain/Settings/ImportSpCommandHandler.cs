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
        var result = await SettingsCommandHelper.ExecuteAsync(
            command,
            "sp_imported",
            "settings:import:sp",
            _idempotencyStore,
            async ct =>
            {
                var importResult = await _importService.ImportAsync(
                    command.PrincipalId,
                    command.Payload.Token,
                    command.Payload.RecoveryCode,
                    ct);
                return importResult.Success;
            },
            cancellationToken);

        if (result is { Accepted: true, Result.Replay: false })
        {
            await _eventBus.PublishAsync(new SettingsProfileUpdatedEvent(command.PrincipalId, false), cancellationToken);
        }

        return result;
    }
}
