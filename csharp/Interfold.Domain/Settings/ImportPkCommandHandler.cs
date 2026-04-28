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
        //TODO: Implement        
        var result = await SettingsCommandHelper.ExecuteAsync(
            command,
            "pk_imported",
            "settings:import:pk",
            _idempotencyStore,
            _ => Task.FromResult(true),
            cancellationToken);

        if (result is { Accepted: true, Result.Replay: false })
        {
            await _eventBus.PublishAsync(new SettingsProfileUpdatedEvent(command.PrincipalId, false), cancellationToken);
        }

        return result;
    }
}