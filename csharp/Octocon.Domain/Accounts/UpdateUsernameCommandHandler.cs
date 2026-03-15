using Octocon.Contracts.Operations;
using Octocon.Domain.Abstractions;

namespace Octocon.Domain.Accounts;

public sealed class UpdateUsernameCommandHandler : ICommandHandler<UpdateUsernameCommand, AccountCommandResult>
{
    private const string AggregateType = "accounts";

    private readonly IAccountRepository _accountRepository;
    private readonly IIdempotencyStore _idempotencyStore;
    private readonly IAggregateVersionStore _versionStore;

    public UpdateUsernameCommandHandler(
        IAccountRepository accountRepository,
        IIdempotencyStore idempotencyStore,
        IAggregateVersionStore versionStore
    )
    {
        _accountRepository = accountRepository;
        _idempotencyStore = idempotencyStore;
        _versionStore = versionStore;
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

        var versionAdvanced = await _versionStore.TryAdvanceVersionAsync(
            AggregateType,
            command.PrincipalId,
            command.ExpectedVersion,
            cancellationToken
        );

        if (!versionAdvanced)
        {
            return await RejectStaleVersion(command, cancellationToken);
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
                null,
                "no_retry",
                null
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
                null,
                "manual_merge_required",
                null
            )
        );

    private async Task<CommandExecutionResult<AccountCommandResult>> RejectStaleVersion(
        CommandEnvelope<UpdateUsernameCommand> command,
        CancellationToken cancellationToken
    )
    {
        var current = await _versionStore.GetVersionAsync(AggregateType, command.PrincipalId, cancellationToken);
        return CommandExecutionResult<AccountCommandResult>.Rejected(
            new ConflictResult(
                ConflictCode.ConflictStaleVersion,
                command.OperationId,
                $"{AggregateType}:{command.PrincipalId}",
                current,
                "refresh_and_retry",
                null
            )
        );
    }
}
