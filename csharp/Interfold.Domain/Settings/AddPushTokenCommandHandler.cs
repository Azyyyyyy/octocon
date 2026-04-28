using Interfold.Contracts;
using Interfold.Contracts.Models;
using Interfold.Contracts.Models.Commands;
using Interfold.Contracts.Operations;
using Interfold.Domain.Abstractions;
using Interfold.Domain.Abstractions.Repository;

namespace Interfold.Domain.Settings;

public sealed class AddPushTokenCommandHandler : ICommandHandler<AddPushTokenCommand, SettingsCommandResult>
{
    private readonly INotificationTokenRepository _repository;
    private readonly IIdempotencyStore _idempotencyStore;

    public AddPushTokenCommandHandler(
        INotificationTokenRepository repository,
        IIdempotencyStore idempotencyStore)
    {
        _repository = repository;
        _idempotencyStore = idempotencyStore;
    }

    public async Task<CommandExecutionResult<SettingsCommandResult>> HandleAsync(
        CommandEnvelope<AddPushTokenCommand> command,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(command.Payload.Token))
            return RejectInvariant(command, "settings:push_token_invalid");

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
                return RejectDuplicate(command, "settings:push_token:add");

            var replay = CommandSerialization.Deserialize<SettingsCommandResult>(previous.OutcomePayload);
            if (replay is not null)
                return CommandExecutionResult<SettingsCommandResult>.Success(replay with { Replay = true });
        }
        
        var persisted = await _repository.AddAsync(command.PrincipalId, command.Payload.Token.Trim(), cancellationToken);
        if (!persisted)
            return RejectInvariant(command, "settings:push_token_add_failed");

        var result = new SettingsCommandResult(command.PrincipalId, "push_token_added", Replay: false);
        var resultJson = CommandSerialization.Serialize(result);

        await _idempotencyStore.SaveAsync(
            command.PrincipalId,
            command.OperationId,
            command.IdempotencyKey,
            payloadHash,
            CommandSerialization.Hash(resultJson),
            resultJson,
            cancellationToken);

        return CommandExecutionResult<SettingsCommandResult>.Success(result);
    }

    private static CommandExecutionResult<SettingsCommandResult> RejectDuplicate(
        CommandEnvelope<AddPushTokenCommand> command,
        string entityRef) =>
        CommandExecutionResult<SettingsCommandResult>.Rejected(
            new ConflictResult(ConflictCode.ConflictDuplicate, command.OperationId, entityRef, "no_retry"));

    private static CommandExecutionResult<SettingsCommandResult> RejectInvariant(
        CommandEnvelope<AddPushTokenCommand> command,
        string entityRef) =>
        CommandExecutionResult<SettingsCommandResult>.Rejected(
            new ConflictResult(ConflictCode.ConflictInvariant, command.OperationId, entityRef, "manual_merge_required"));
}
