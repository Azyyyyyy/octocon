using Interfold.Contracts.Operations;
using Interfold.Domain.Abstractions;

namespace Interfold.Domain.Settings.Handlers;

internal static class SettingsCommandHelper
{
    public static async Task<CommandExecutionResult<SettingsCommandResult>> ExecuteAsync<TCommand>(
        CommandEnvelope<TCommand> command,
        string action,
        string duplicateEntityRef,
        IIdempotencyStore idempotencyStore,
        Func<CancellationToken, Task<bool>> apply,
        CancellationToken cancellationToken = default)
    {
        var payloadJson = CommandSerialization.Serialize(command.Payload);
        var payloadHash = CommandSerialization.Hash(payloadJson);

        var previous = await idempotencyStore.FindAsync(
            command.PrincipalId,
            command.OperationId,
            command.IdempotencyKey,
            cancellationToken);

        if (previous is not null)
        {
            if (!string.Equals(previous.PayloadHash, payloadHash, StringComparison.Ordinal))
            {
                return CommandExecutionResult<SettingsCommandResult>.Rejected(
                    new ConflictResult(ConflictCode.ConflictDuplicate, command.OperationId, duplicateEntityRef, "no_retry"));
            }

            var replay = CommandSerialization.Deserialize<SettingsCommandResult>(previous.OutcomePayload);
            if (replay is not null)
                return CommandExecutionResult<SettingsCommandResult>.Success(replay with { Replay = true });
        }

        var applied = await apply(cancellationToken);
        if (!applied)
        {
            return CommandExecutionResult<SettingsCommandResult>.Rejected(
                new ConflictResult(ConflictCode.ConflictInvariant, command.OperationId, $"settings:{action}_failed", "manual_merge_required"));
        }

        var result = new SettingsCommandResult(command.PrincipalId, action, Replay: false);
        var resultJson = CommandSerialization.Serialize(result);

        await idempotencyStore.SaveAsync(
            command.PrincipalId,
            command.OperationId,
            command.IdempotencyKey,
            payloadHash,
            CommandSerialization.Hash(resultJson),
            resultJson,
            cancellationToken);

        return CommandExecutionResult<SettingsCommandResult>.Success(result);
    }
}
