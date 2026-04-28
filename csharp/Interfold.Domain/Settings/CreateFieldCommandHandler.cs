using Interfold.Contracts;
using Interfold.Contracts.Events;
using Interfold.Contracts.Models;
using Interfold.Contracts.Models.Commands;
using Interfold.Contracts.Operations;
using Interfold.Domain.Abstractions;
using Interfold.Domain.Abstractions.Repository;

namespace Interfold.Domain.Settings;

public sealed class CreateFieldCommandHandler : ICommandHandler<CreateFieldCommand, SettingsFieldCommandResult>
{
    private static readonly HashSet<string> AllowedTypes = ["text", "number", "boolean"];
    private static readonly HashSet<string> AllowedSecurityLevels = ["public", "friends_only", "trusted_only", "private"];
    private readonly ISettingsFieldRepository _fieldRepository;
    private readonly IIdempotencyStore _idempotencyStore;
    private readonly IClusterEventBus _eventBus;

    public CreateFieldCommandHandler(ISettingsFieldRepository fieldRepository, IIdempotencyStore idempotencyStore, IClusterEventBus eventBus)
    {
        _fieldRepository = fieldRepository;
        _idempotencyStore = idempotencyStore;
        _eventBus = eventBus;
    }

    public async Task<CommandExecutionResult<SettingsFieldCommandResult>> HandleAsync(CommandEnvelope<CreateFieldCommand> command, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(command.Payload.Name))
        {
            return CommandExecutionResult<SettingsFieldCommandResult>.Rejected(
                new ConflictResult(ConflictCode.ConflictInvariant, command.OperationId, "settings:field_name_required", "manual_merge_required"));
        }

        var normalizedType = NormalizeType(command.Payload.Type);

        if (!AllowedSecurityLevels.Contains(command.Payload.SecurityLevel))
        {
            return CommandExecutionResult<SettingsFieldCommandResult>.Rejected(
                new ConflictResult(ConflictCode.ConflictInvariant, command.OperationId, "settings:field_security_level_invalid", "manual_merge_required"));
        }

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
            {
                return CommandExecutionResult<SettingsFieldCommandResult>.Rejected(
                    new ConflictResult(ConflictCode.ConflictDuplicate, command.OperationId, "settings:field:create", "no_retry"));
            }

            var replay = CommandSerialization.Deserialize<SettingsFieldCommandResult>(previous.OutcomePayload);
            if (replay is not null)
                return CommandExecutionResult<SettingsFieldCommandResult>.Success(replay with { Replay = true });
        }

        var fieldId = await _fieldRepository.CreateAsync(
            command.PrincipalId,
            command.Payload.Name,
            normalizedType,
            command.Payload.SecurityLevel,
            command.Payload.Locked,
            cancellationToken);

        if (fieldId is null)
        {
            return CommandExecutionResult<SettingsFieldCommandResult>.Rejected(
                new ConflictResult(ConflictCode.ConflictInvariant, command.OperationId, "settings:field:create_failed", "manual_merge_required"));
        }

        var result = new SettingsFieldCommandResult(command.PrincipalId, "field_created", fieldId, Replay: false);
        var resultJson = CommandSerialization.Serialize(result);

        await _idempotencyStore.SaveAsync(
            command.PrincipalId,
            command.OperationId,
            command.IdempotencyKey,
            payloadHash,
            CommandSerialization.Hash(resultJson),
            resultJson,
            cancellationToken);

        if (!result.Replay)
            await _eventBus.PublishAsync(new SettingsFieldsChangedEvent(command.PrincipalId), cancellationToken);

        return CommandExecutionResult<SettingsFieldCommandResult>.Success(result);
    }

    private static string NormalizeType(string? type)
    {
        if (string.IsNullOrWhiteSpace(type))
        {
            return "text";
        }

        return AllowedTypes.Contains(type) ? type : "text";
    }
}