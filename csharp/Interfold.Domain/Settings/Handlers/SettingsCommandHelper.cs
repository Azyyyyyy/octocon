using Interfold.Contracts.Operations;
using Interfold.Domain.Abstractions;

namespace Interfold.Domain.Settings.Handlers;

internal static class SettingsCommandHelper
{
    public static async Task<CommandExecutionResult<SettingsCommandResult>> ExecuteAsync<TCommand>(
        CommandEnvelope<TCommand> command,
        string aggregateType,
        string action,
        string duplicateEntityRef,
        IIdempotencyStore idempotencyStore,
        IAggregateVersionStore versionStore,
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
                    new ConflictResult(ConflictCode.ConflictDuplicate, command.OperationId, duplicateEntityRef, null, "no_retry", null));
            }

            var replay = CommandSerialization.Deserialize<SettingsCommandResult>(previous.OutcomePayload);
            if (replay is not null)
                return CommandExecutionResult<SettingsCommandResult>.Success(replay with { Replay = true });
        }

        var versionAdvanced = await versionStore.TryAdvanceVersionAsync(
            aggregateType,
            command.PrincipalId,
            command.ExpectedVersion,
            cancellationToken);

        if (!versionAdvanced)
        {
            var current = await versionStore.GetVersionAsync(aggregateType, command.PrincipalId, cancellationToken);
            return CommandExecutionResult<SettingsCommandResult>.Rejected(
                new ConflictResult(ConflictCode.ConflictStaleVersion, command.OperationId, $"{aggregateType}:{command.PrincipalId}", current, "refresh_and_retry", null));
        }

        var applied = await apply(cancellationToken);
        if (!applied)
        {
            return CommandExecutionResult<SettingsCommandResult>.Rejected(
                new ConflictResult(ConflictCode.ConflictInvariant, command.OperationId, $"settings:{action}_failed", null, "manual_merge_required", null));
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
