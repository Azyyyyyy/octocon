using Interfold.Contracts.Operations;
using Interfold.Domain.Abstractions;

namespace Interfold.Domain.Accounts;

public sealed class UpdateUsernameCommandHandler : ICommandHandler<UpdateUsernameCommand, AccountCommandResult>
{
    private readonly IAccountRepository _accountRepository;
    private readonly IIdempotencyStore _idempotencyStore;
    private readonly IClusterEventBus _eventBus;

    public UpdateUsernameCommandHandler(
        IAccountRepository accountRepository,
        IIdempotencyStore idempotencyStore,
        IClusterEventBus eventBus
    )
    {
        _accountRepository = accountRepository;
        _idempotencyStore = idempotencyStore;
        _eventBus = eventBus;
    }

    public async Task<CommandExecutionResult<AccountCommandResult>> HandleAsync(
        CommandEnvelope<UpdateUsernameCommand> command,
        CancellationToken cancellationToken = default
    )
    {
        if (string.IsNullOrWhiteSpace(command.Payload.Username))
        {
            return RejectInvariant(command, "account:username_invalid");
        }

        if (command.Payload.Username.Length > 64)
        {
            return RejectInvariant(command, "account:username_too_long");
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
                return RejectDuplicate(command, "account:username_update");
            }

            var replay = CommandSerialization.Deserialize<AccountCommandResult>(previous.OutcomePayload);
            if (replay is not null)
            {
                return CommandExecutionResult<AccountCommandResult>.Success(replay with { Replay = true });
            }
        }

        var persisted = await _accountRepository.UpdateUsernameAsync(
            command.PrincipalId,
            command.Payload.Username,
            cancellationToken
        );

        if (!persisted)
        {
            return RejectInvariant(command, "account:username_update_failed");
        }

        var result = new AccountCommandResult(command.PrincipalId, command.Payload.Username, Replay: false);
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

        await _eventBus.PublishAsync(new SettingsProfileUpdatedEvent(command.PrincipalId, true), cancellationToken);
        return CommandExecutionResult<AccountCommandResult>.Success(result);
    }

    private static CommandExecutionResult<AccountCommandResult> RejectDuplicate(
        CommandEnvelope<UpdateUsernameCommand> command,
        string entityRef
    ) =>
        CommandExecutionResult<AccountCommandResult>.Rejected(
            new ConflictResult(
                ConflictCode.ConflictDuplicate,
                command.OperationId,
                entityRef,
                "no_retry"
            )
        );

    private static CommandExecutionResult<AccountCommandResult> RejectInvariant(
        CommandEnvelope<UpdateUsernameCommand> command,
        string entityRef
    ) =>
        CommandExecutionResult<AccountCommandResult>.Rejected(
            new ConflictResult(
                ConflictCode.ConflictInvariant,
                command.OperationId,
                entityRef,
                "manual_merge_required"
            )
        );
}
