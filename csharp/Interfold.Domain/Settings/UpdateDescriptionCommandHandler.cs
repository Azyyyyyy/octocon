using Interfold.Contracts;
using Interfold.Contracts.Events;
using Interfold.Contracts.Models;
using Interfold.Contracts.Models.Commands;
using Interfold.Contracts.Operations;
using Interfold.Domain.Abstractions;
using Interfold.Domain.Abstractions.Repository;

namespace Interfold.Domain.Settings;

public sealed class UpdateDescriptionCommandHandler : ICommandHandler<UpdateDescriptionCommand, SettingsCommandResult>
{
    private readonly IAccountRepository _accountRepository;
    private readonly IIdempotencyStore _idempotencyStore;
    private readonly IClusterEventBus _eventBus;

    public UpdateDescriptionCommandHandler(
        IAccountRepository accountRepository,
        IIdempotencyStore idempotencyStore,
        IClusterEventBus eventBus)
    {
        _accountRepository = accountRepository;
        _idempotencyStore = idempotencyStore;
        _eventBus = eventBus;
    }

    public async Task<CommandExecutionResult<SettingsCommandResult>> HandleAsync(
        CommandEnvelope<UpdateDescriptionCommand> command,
        CancellationToken cancellationToken = default)
    {
        if (command.Payload.Description.Length > 3000)
            return RejectInvariant(command, "settings:description_invalid");

        var payloadJson = CommandSerialization.Serialize(command.Payload);
        var payloadHash = CommandSerialization.Hash(payloadJson);

        var previous = await _idempotencyStore.FindAsync(
            command.PrincipalId,
            command.OperationId,
            command.IdempotencyKey,
            cancellationToken);

        if (previous is not null)
        {
            if (!string.Equals(previous.PayloadHash, payloadHash, StringComparison.Ordinal))
                return RejectDuplicate(command, "settings:description:update");

            var replay = CommandSerialization.Deserialize<SettingsCommandResult>(previous.OutcomePayload);
            if (replay is not null)
                return CommandExecutionResult<SettingsCommandResult>.Success(replay with { Replay = true });
        }

        var persisted = await _accountRepository.UpdateDescriptionAsync(
            command.PrincipalId,
            command.Payload.Description,
            cancellationToken);

        if (!persisted)
            return RejectInvariant(command, "settings:description_update_failed");

        var result = new SettingsCommandResult(command.PrincipalId, "description_updated", Replay: false);
        var resultJson = CommandSerialization.Serialize(result);

        await _idempotencyStore.SaveAsync(
            command.PrincipalId,
            command.OperationId,
            command.IdempotencyKey,
            payloadHash,
            CommandSerialization.Hash(resultJson),
            resultJson,
            cancellationToken);

        await _eventBus.PublishAsync(new SettingsProfileUpdatedEvent(command.PrincipalId, false), cancellationToken);
        return CommandExecutionResult<SettingsCommandResult>.Success(result);
    }

    private static CommandExecutionResult<SettingsCommandResult> RejectDuplicate(
        CommandEnvelope<UpdateDescriptionCommand> command,
        string entityRef) =>
        CommandExecutionResult<SettingsCommandResult>.Rejected(
            new ConflictResult(ConflictCode.ConflictDuplicate, command.OperationId, entityRef, "no_retry"));

    private static CommandExecutionResult<SettingsCommandResult> RejectInvariant(
        CommandEnvelope<UpdateDescriptionCommand> command,
        string entityRef) =>
        CommandExecutionResult<SettingsCommandResult>.Rejected(
            new ConflictResult(ConflictCode.ConflictInvariant, command.OperationId, entityRef, "manual_merge_required"));
}
