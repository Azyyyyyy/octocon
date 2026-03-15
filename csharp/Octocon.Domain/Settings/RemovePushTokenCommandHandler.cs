using Octocon.Contracts.Operations;
using Octocon.Domain.Abstractions;

namespace Octocon.Domain.Settings;

public sealed class RemovePushTokenCommandHandler : ICommandHandler<RemovePushTokenCommand, SettingsCommandResult>
{
    private const string AggregateType = "settings";

    private readonly INotificationTokenRepository _repository;
    private readonly IIdempotencyStore _idempotencyStore;
    private readonly IAggregateVersionStore _versionStore;

    public RemovePushTokenCommandHandler(
        INotificationTokenRepository repository,
        IIdempotencyStore idempotencyStore,
        IAggregateVersionStore versionStore)
    {
        _repository = repository;
        _idempotencyStore = idempotencyStore;
        _versionStore = versionStore;
    }

    public async Task<CommandExecutionResult<SettingsCommandResult>> HandleAsync(
        CommandEnvelope<RemovePushTokenCommand> command,
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
                return RejectDuplicate(command, "settings:push_token:remove");

            var replay = CommandSerialization.Deserialize<SettingsCommandResult>(previous.OutcomePayload);
            if (replay is not null)
                return CommandExecutionResult<SettingsCommandResult>.Success(replay with { Replay = true });
        }

        var versionAdvanced = await _versionStore.TryAdvanceVersionAsync(
            AggregateType,
            command.PrincipalId,
            command.ExpectedVersion,
            cancellationToken);

        if (!versionAdvanced)
            return await RejectStaleVersion(command, cancellationToken);

        // Removal is treated as idempotent for retry safety.
        await _repository.RemoveAsync(command.Payload.Token.Trim(), cancellationToken);

        var result = new SettingsCommandResult(command.PrincipalId, "push_token_removed", Replay: false);
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
        CommandEnvelope<RemovePushTokenCommand> command,
        string entityRef) =>
        CommandExecutionResult<SettingsCommandResult>.Rejected(
            new ConflictResult(ConflictCode.ConflictDuplicate, command.OperationId, entityRef, null, "no_retry", null));

    private static CommandExecutionResult<SettingsCommandResult> RejectInvariant(
        CommandEnvelope<RemovePushTokenCommand> command,
        string entityRef) =>
        CommandExecutionResult<SettingsCommandResult>.Rejected(
            new ConflictResult(ConflictCode.ConflictInvariant, command.OperationId, entityRef, null, "manual_merge_required", null));

    private async Task<CommandExecutionResult<SettingsCommandResult>> RejectStaleVersion(
        CommandEnvelope<RemovePushTokenCommand> command,
        CancellationToken cancellationToken)
    {
        var current = await _versionStore.GetVersionAsync(AggregateType, command.PrincipalId, cancellationToken);
        return CommandExecutionResult<SettingsCommandResult>.Rejected(
            new ConflictResult(
                ConflictCode.ConflictStaleVersion,
                command.OperationId,
                $"{AggregateType}:{command.PrincipalId}",
                current,
                "refresh_and_retry",
                null));
    }
}
