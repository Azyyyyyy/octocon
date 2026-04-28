using Interfold.Contracts;
using Interfold.Contracts.Events;
using Interfold.Contracts.Models;
using Interfold.Contracts.Models.Commands;
using Interfold.Contracts.Operations;
using Interfold.Domain.Abstractions;
using Interfold.Domain.Abstractions.Repository;

namespace Interfold.Domain.Alters;

public sealed class UpdateAlterCommandHandler : ICommandHandler<UpdateAlterCommand, AlterCommandResult>
{
    private readonly IAlterRepository _alterRepository;
    private readonly IIdempotencyStore _idempotencyStore;
    private readonly IClusterEventBus _eventBus;

    public UpdateAlterCommandHandler(
        IAlterRepository alterRepository,
        IIdempotencyStore idempotencyStore,
        IClusterEventBus eventBus
    )
    {
        _alterRepository = alterRepository;
        _idempotencyStore = idempotencyStore;
        _eventBus = eventBus;
    }

    public async Task<CommandExecutionResult<AlterCommandResult>> HandleAsync(
        CommandEnvelope<UpdateAlterCommand> command,
        CancellationToken cancellationToken = default
    )
    {
        if (command.Payload.AlterId is < 1 or > 32_767)
        {
            return RejectInvariant(command, "alter:id");
        }

        if (!HasAnyMutableField(command.Payload))
        {
            return RejectInvariant(command, "alter:update:no_fields");
        }

        var payloadJson = CommandSerialization.Serialize(command.Payload);
        var payloadHash = CommandSerialization.Hash(payloadJson);

        var previous = await _idempotencyStore.FindAsync(
            command.PrincipalId,
            command.OperationId,
            command.IdempotencyKey,
            cancellationToken
        );

        if (previous is not null)
        {
            if (!string.Equals(previous.PayloadHash, payloadHash, StringComparison.Ordinal))
            {
                return RejectDuplicate(command, "alter:update");
            }

            var replay = CommandSerialization.Deserialize<AlterCommandResult>(previous.OutcomePayload);
            if (replay is not null)
            {
                return CommandExecutionResult<AlterCommandResult>.Success(replay with { Replay = true });
            }
        }

        var exists = await _alterRepository.ExistsAsync(command.PrincipalId, command.Payload.AlterId, cancellationToken);
        if (!exists)
        {
            return RejectInvariant(command, "alter:not_found");
        }

        if (!string.IsNullOrWhiteSpace(command.Payload.Alias))
        {
            var aliasTaken = await _alterRepository.AliasTakenByOtherAsync(
                command.PrincipalId,
                command.Payload.AlterId,
                command.Payload.Alias,
                cancellationToken
            );

            if (aliasTaken)
            {
                return RejectInvariant(command, "alter:alias_taken");
            }
        }

        var updated = await _alterRepository.UpdateAsync(command.PrincipalId, command.Payload, cancellationToken);
        if (!updated)
        {
            return RejectInvariant(command, "alter:update_failed");
        }

        var result = new AlterCommandResult(command.PrincipalId, command.Payload.AlterId, Replay: false);
        var resultJson = CommandSerialization.Serialize(result);
        await _idempotencyStore.SaveAsync(
            command.PrincipalId,
            command.OperationId,
            command.IdempotencyKey,
            payloadHash,
            CommandSerialization.Hash(resultJson),
            resultJson,
            cancellationToken
        );

        await _eventBus.PublishAsync(
            new AlterUpdatedEvent(command.PrincipalId, command.Payload.AlterId),
            cancellationToken);

        return CommandExecutionResult<AlterCommandResult>.Success(result);
    }

    private static bool HasAnyMutableField(UpdateAlterCommand payload) =>
        payload.Name is not null ||
        payload.Description is not null ||
        payload.AvatarUrl is not null ||
        payload.ClearAvatar ||
        payload.Color is not null ||
        payload.Pronouns is not null ||
        payload.SecurityLevel is not null ||
        payload.Fields is not null ||
        payload.ProxyName is not null ||
        payload.Alias is not null ||
        payload.Untracked is not null ||
        payload.Archived is not null ||
        payload.Pinned is not null;

    private static CommandExecutionResult<AlterCommandResult> RejectDuplicate(
        CommandEnvelope<UpdateAlterCommand> command,
        string entityRef
    ) =>
        CommandExecutionResult<AlterCommandResult>.Rejected(
            new ConflictResult(
                ConflictCode.ConflictDuplicate,
                command.OperationId,
                entityRef,
                "no_retry"
            )
        );

    private static CommandExecutionResult<AlterCommandResult> RejectInvariant(
        CommandEnvelope<UpdateAlterCommand> command,
        string entityRef
    ) =>
        CommandExecutionResult<AlterCommandResult>.Rejected(
            new ConflictResult(
                ConflictCode.ConflictInvariant,
                command.OperationId,
                entityRef,
                "manual_merge_required"
            )
        );
}