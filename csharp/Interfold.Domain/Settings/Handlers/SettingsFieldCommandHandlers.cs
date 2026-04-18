using Interfold.Contracts.Operations;
using Interfold.Domain.Abstractions;

namespace Interfold.Domain.Settings.Handlers;

public sealed class CreateFieldCommandHandler : ICommandHandler<CreateFieldCommand, SettingsFieldCommandResult>
{
    private const string AggregateType = "settings";
    private static readonly HashSet<string> AllowedTypes = ["text", "number", "boolean"];
    private static readonly HashSet<string> AllowedSecurityLevels = ["public", "friends_only", "trusted_only", "private"];
    private readonly ISettingsFieldRepository _fieldRepository;
    private readonly IIdempotencyStore _idempotencyStore;
    private readonly IAggregateVersionStore _versionStore;
    private readonly IClusterEventBus _eventBus;

    public CreateFieldCommandHandler(ISettingsFieldRepository fieldRepository, IIdempotencyStore idempotencyStore, IAggregateVersionStore versionStore, IClusterEventBus eventBus)
    {
        _fieldRepository = fieldRepository;
        _idempotencyStore = idempotencyStore;
        _versionStore = versionStore;
        _eventBus = eventBus;
    }

    public async Task<CommandExecutionResult<SettingsFieldCommandResult>> HandleAsync(CommandEnvelope<CreateFieldCommand> command, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(command.Payload.Name))
        {
            return CommandExecutionResult<SettingsFieldCommandResult>.Rejected(
                new ConflictResult(ConflictCode.ConflictInvariant, command.OperationId, "settings:field_name_required", null, "manual_merge_required", null));
        }

        var normalizedType = NormalizeType(command.Payload.Type);

        if (!AllowedSecurityLevels.Contains(command.Payload.SecurityLevel))
        {
            return CommandExecutionResult<SettingsFieldCommandResult>.Rejected(
                new ConflictResult(ConflictCode.ConflictInvariant, command.OperationId, "settings:field_security_level_invalid", null, "manual_merge_required", null));
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
                    new ConflictResult(ConflictCode.ConflictDuplicate, command.OperationId, "settings:field:create", null, "no_retry", null));
            }

            var replay = CommandSerialization.Deserialize<SettingsFieldCommandResult>(previous.OutcomePayload);
            if (replay is not null)
                return CommandExecutionResult<SettingsFieldCommandResult>.Success(replay with { Replay = true });
        }

        var versionAdvanced = await _versionStore.TryAdvanceVersionAsync(
            AggregateType,
            command.PrincipalId,
            command.ExpectedVersion,
            cancellationToken);

        if (!versionAdvanced)
        {
            var current = await _versionStore.GetVersionAsync(AggregateType, command.PrincipalId, cancellationToken);
            return CommandExecutionResult<SettingsFieldCommandResult>.Rejected(
                new ConflictResult(ConflictCode.ConflictStaleVersion, command.OperationId, $"{AggregateType}:{command.PrincipalId}", current, "refresh_and_retry", null));
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
                new ConflictResult(ConflictCode.ConflictInvariant, command.OperationId, "settings:field:create_failed", null, "manual_merge_required", null));
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

public sealed class UpdateFieldCommandHandler : ICommandHandler<UpdateFieldCommand, SettingsCommandResult>
{
    private const string AggregateType = "settings";
    private readonly ISettingsFieldRepository _fieldRepository;
    private readonly IIdempotencyStore _idempotencyStore;
    private readonly IAggregateVersionStore _versionStore;
    private readonly IClusterEventBus _eventBus;

    public UpdateFieldCommandHandler(ISettingsFieldRepository fieldRepository, IIdempotencyStore idempotencyStore, IAggregateVersionStore versionStore, IClusterEventBus eventBus)
    {
        _fieldRepository = fieldRepository;
        _idempotencyStore = idempotencyStore;
        _versionStore = versionStore;
        _eventBus = eventBus;
    }

    public async Task<CommandExecutionResult<SettingsCommandResult>> HandleAsync(CommandEnvelope<UpdateFieldCommand> command, CancellationToken cancellationToken = default)
    {
        var result = await SettingsCommandHelper.ExecuteAsync(
            command,
            AggregateType,
            "field_updated",
            "settings:field:update",
            _idempotencyStore,
            _versionStore,
            ct => _fieldRepository.UpdateAsync(command.PrincipalId, command.Payload.FieldId, command.Payload.Name, command.Payload.SecurityLevel, command.Payload.Locked, ct),
            cancellationToken);

        if (result.Accepted && result.Result is { Replay: false })
        {
            await _eventBus.PublishAsync(new SettingsFieldsChangedEvent(command.PrincipalId), cancellationToken);
        }

        return result;
    }
}

public sealed class DeleteFieldCommandHandler : ICommandHandler<DeleteFieldCommand, SettingsCommandResult>
{
    private const string AggregateType = "settings";
    private readonly ISettingsFieldRepository _fieldRepository;
    private readonly IIdempotencyStore _idempotencyStore;
    private readonly IAggregateVersionStore _versionStore;
    private readonly IClusterEventBus _eventBus;

    public DeleteFieldCommandHandler(ISettingsFieldRepository fieldRepository, IIdempotencyStore idempotencyStore, IAggregateVersionStore versionStore, IClusterEventBus eventBus)
    {
        _fieldRepository = fieldRepository;
        _idempotencyStore = idempotencyStore;
        _versionStore = versionStore;
        _eventBus = eventBus;
    }

    public async Task<CommandExecutionResult<SettingsCommandResult>> HandleAsync(CommandEnvelope<DeleteFieldCommand> command, CancellationToken cancellationToken = default)
    {
        var result = await SettingsCommandHelper.ExecuteAsync(
            command,
            AggregateType,
            "field_deleted",
            "settings:field:delete",
            _idempotencyStore,
            _versionStore,
            ct => _fieldRepository.DeleteAsync(command.PrincipalId, command.Payload.FieldId, ct),
            cancellationToken);

        if (result.Accepted && result.Result is { Replay: false })
        {
            await _eventBus.PublishAsync(new SettingsFieldsChangedEvent(command.PrincipalId), cancellationToken);
        }

        return result;
    }
}

public sealed class RelocateFieldCommandHandler : ICommandHandler<RelocateFieldCommand, SettingsCommandResult>
{
    private const string AggregateType = "settings";
    private readonly ISettingsFieldRepository _fieldRepository;
    private readonly IIdempotencyStore _idempotencyStore;
    private readonly IAggregateVersionStore _versionStore;
    private readonly IClusterEventBus _eventBus;

    public RelocateFieldCommandHandler(ISettingsFieldRepository fieldRepository, IIdempotencyStore idempotencyStore, IAggregateVersionStore versionStore, IClusterEventBus eventBus)
    {
        _fieldRepository = fieldRepository;
        _idempotencyStore = idempotencyStore;
        _versionStore = versionStore;
        _eventBus = eventBus;
    }

    public async Task<CommandExecutionResult<SettingsCommandResult>> HandleAsync(CommandEnvelope<RelocateFieldCommand> command, CancellationToken cancellationToken = default)
    {
        var result = await SettingsCommandHelper.ExecuteAsync(
            command,
            AggregateType,
            "field_relocated",
            "settings:field:relocate",
            _idempotencyStore,
            _versionStore,
            ct => _fieldRepository.RelocateAsync(command.PrincipalId, command.Payload.FieldId, command.Payload.Index, ct),
            cancellationToken);

        if (result.Accepted && result.Result is { Replay: false })
        {
            await _eventBus.PublishAsync(new SettingsFieldsChangedEvent(command.PrincipalId), cancellationToken);
        }

        return result;
    }
}
