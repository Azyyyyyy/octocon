using Interfold.Contracts;
using Interfold.Contracts.Events;
using Interfold.Contracts.Models;
using Interfold.Contracts.Models.Commands;
using Interfold.Contracts.Operations;
using Interfold.Domain.Abstractions;
using Interfold.Domain.Abstractions.Repository;

namespace Interfold.Domain.Alters;

public sealed class CreateAlterCommandHandler : ICommandHandler<CreateAlterCommand, AlterCommandResult>
{
    private readonly IAlterRepository _alterRepository;
    private readonly IIdempotencyStore _idempotencyStore;
    private readonly IClusterEventBus _eventBus;

    public CreateAlterCommandHandler(
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
        CommandEnvelope<CreateAlterCommand> command,
        CancellationToken cancellationToken = default
    )
    {
        if (string.IsNullOrWhiteSpace(command.Payload.Name))
        {
            return RejectInvariant(command, "alter:name");
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
                return RejectDuplicate(command, "alter:create");
            }

            var replay = CommandSerialization.Deserialize<AlterCommandResult>(previous.OutcomePayload);
            if (replay is not null)
            {
                return CommandExecutionResult<AlterCommandResult>.Success(replay with { Replay = true });
            }
        }
        
        var alterId = await _alterRepository.CreateAsync(command.PrincipalId, command.Payload, cancellationToken);
        if (alterId is null)
        {
            return RejectInvariant(command, "alter:create");
        }

        var result = new AlterCommandResult(command.PrincipalId, alterId.Value, Replay: false);
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
            new AlterCreatedEvent(command.PrincipalId, alterId.Value),
            cancellationToken);

        return CommandExecutionResult<AlterCommandResult>.Success(result);
    }

    private static CommandExecutionResult<AlterCommandResult> RejectDuplicate(
        CommandEnvelope<CreateAlterCommand> command,
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
        CommandEnvelope<CreateAlterCommand> command,
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