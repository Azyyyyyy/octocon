using Interfold.Contracts.Operations;
using Interfold.Domain.Abstractions;
using Interfold.Domain.Accounts;

namespace Interfold.Domain.Settings;

internal static class SettingsPhaseOCommandHelper
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

public sealed class UploadAvatarCommandHandler : ICommandHandler<UploadAvatarCommand, SettingsCommandResult>
{
    private const string AggregateType = "settings";
    private readonly IAccountRepository _accountRepository;
    private readonly IIdempotencyStore _idempotencyStore;
    private readonly IAggregateVersionStore _versionStore;
    private readonly IClusterEventBus _eventBus;

    public UploadAvatarCommandHandler(IAccountRepository accountRepository, IIdempotencyStore idempotencyStore, IAggregateVersionStore versionStore, IClusterEventBus eventBus)
    {
        _accountRepository = accountRepository;
        _idempotencyStore = idempotencyStore;
        _versionStore = versionStore;
        _eventBus = eventBus;
    }

    public Task<CommandExecutionResult<SettingsCommandResult>> HandleAsync(CommandEnvelope<UploadAvatarCommand> command, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(command.Payload.AvatarUrl))
        {
            return Task.FromResult(CommandExecutionResult<SettingsCommandResult>.Rejected(
                new ConflictResult(ConflictCode.ConflictInvariant, command.OperationId, "settings:avatar_invalid", null, "manual_merge_required", null)));
        }

        return ExecuteAndPublishAsync(command, cancellationToken);
    }

    private async Task<CommandExecutionResult<SettingsCommandResult>> ExecuteAndPublishAsync(
        CommandEnvelope<UploadAvatarCommand> command,
        CancellationToken cancellationToken)
    {
        var result = await SettingsPhaseOCommandHelper.ExecuteAsync(
            command,
            AggregateType,
            "avatar_uploaded",
            "settings:avatar:upload",
            _idempotencyStore,
            _versionStore,
            ct => _accountRepository.UpdateAvatarAsync(command.PrincipalId, command.Payload.AvatarUrl, ct),
            cancellationToken);

        if (result.Accepted && result.Result is { Replay: false })
        {
            await _eventBus.PublishAsync(new SettingsProfileUpdatedEvent(command.PrincipalId, false), cancellationToken);
        }

        return result;
    }
}

public sealed class DeleteAvatarCommandHandler : ICommandHandler<DeleteAvatarCommand, SettingsCommandResult>
{
    private const string AggregateType = "settings";
    private readonly IAccountRepository _accountRepository;
    private readonly IIdempotencyStore _idempotencyStore;
    private readonly IAggregateVersionStore _versionStore;
    private readonly IClusterEventBus _eventBus;

    public DeleteAvatarCommandHandler(IAccountRepository accountRepository, IIdempotencyStore idempotencyStore, IAggregateVersionStore versionStore, IClusterEventBus eventBus)
    {
        _accountRepository = accountRepository;
        _idempotencyStore = idempotencyStore;
        _versionStore = versionStore;
        _eventBus = eventBus;
    }

    public async Task<CommandExecutionResult<SettingsCommandResult>> HandleAsync(CommandEnvelope<DeleteAvatarCommand> command, CancellationToken cancellationToken = default)
    {
        var result = await SettingsPhaseOCommandHelper.ExecuteAsync(
            command,
            AggregateType,
            "avatar_deleted",
            "settings:avatar:delete",
            _idempotencyStore,
            _versionStore,
            ct => _accountRepository.ClearAvatarAsync(command.PrincipalId, ct),
            cancellationToken);

        if (result.Accepted && result.Result is { Replay: false })
        {
            await _eventBus.PublishAsync(new SettingsProfileUpdatedEvent(command.PrincipalId, false), cancellationToken);
        }

        return result;
    }
}

public sealed class ImportPkCommandHandler : ICommandHandler<ImportPkCommand, SettingsCommandResult>
{
    private const string AggregateType = "settings";
    private readonly IIdempotencyStore _idempotencyStore;
    private readonly IAggregateVersionStore _versionStore;
    private readonly IClusterEventBus _eventBus;

    public ImportPkCommandHandler(IIdempotencyStore idempotencyStore, IAggregateVersionStore versionStore, IClusterEventBus eventBus)
    {
        _idempotencyStore = idempotencyStore;
        _versionStore = versionStore;
        _eventBus = eventBus;
    }

    public Task<CommandExecutionResult<SettingsCommandResult>> HandleAsync(CommandEnvelope<ImportPkCommand> command, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(command.Payload.Token))
        {
            return Task.FromResult(CommandExecutionResult<SettingsCommandResult>.Rejected(
                new ConflictResult(ConflictCode.ConflictInvariant, command.OperationId, "settings:import_pk_invalid", null, "manual_merge_required", null)));
        }

        return ExecuteAndPublishAsync(command, cancellationToken);
    }

    private async Task<CommandExecutionResult<SettingsCommandResult>> ExecuteAndPublishAsync(
        CommandEnvelope<ImportPkCommand> command,
        CancellationToken cancellationToken)
    {
        var result = await SettingsPhaseOCommandHelper.ExecuteAsync(
            command,
            AggregateType,
            "pk_imported",
            "settings:import:pk",
            _idempotencyStore,
            _versionStore,
            _ => Task.FromResult(true),
            cancellationToken);

        if (result.Accepted && result.Result is { Replay: false })
        {
            await _eventBus.PublishAsync(new SettingsProfileUpdatedEvent(command.PrincipalId, false), cancellationToken);
        }

        return result;
    }
}

public sealed class ImportSpCommandHandler : ICommandHandler<ImportSpCommand, SettingsCommandResult>
{
    private const string AggregateType = "settings";
    private readonly IIdempotencyStore _idempotencyStore;
    private readonly IAggregateVersionStore _versionStore;
    private readonly IClusterEventBus _eventBus;

    public ImportSpCommandHandler(IIdempotencyStore idempotencyStore, IAggregateVersionStore versionStore, IClusterEventBus eventBus)
    {
        _idempotencyStore = idempotencyStore;
        _versionStore = versionStore;
        _eventBus = eventBus;
    }

    public Task<CommandExecutionResult<SettingsCommandResult>> HandleAsync(CommandEnvelope<ImportSpCommand> command, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(command.Payload.Token))
        {
            return Task.FromResult(CommandExecutionResult<SettingsCommandResult>.Rejected(
                new ConflictResult(ConflictCode.ConflictInvariant, command.OperationId, "settings:import_sp_invalid", null, "manual_merge_required", null)));
        }

        return ExecuteAndPublishAsync(command, cancellationToken);
    }

    private async Task<CommandExecutionResult<SettingsCommandResult>> ExecuteAndPublishAsync(
        CommandEnvelope<ImportSpCommand> command,
        CancellationToken cancellationToken)
    {
        var result = await SettingsPhaseOCommandHelper.ExecuteAsync(
            command,
            AggregateType,
            "sp_imported",
            "settings:import:sp",
            _idempotencyStore,
            _versionStore,
            _ => Task.FromResult(true),
            cancellationToken);

        if (result.Accepted && result.Result is { Replay: false })
        {
            await _eventBus.PublishAsync(new SettingsProfileUpdatedEvent(command.PrincipalId, false), cancellationToken);
        }

        return result;
    }
}

public sealed class UnlinkDiscordCommandHandler : ICommandHandler<UnlinkDiscordCommand, SettingsCommandResult>
{
    private const string AggregateType = "settings";
    private readonly IIdempotencyStore _idempotencyStore;
    private readonly IAggregateVersionStore _versionStore;
    private readonly IClusterEventBus _eventBus;

    public UnlinkDiscordCommandHandler(IIdempotencyStore idempotencyStore, IAggregateVersionStore versionStore, IClusterEventBus eventBus)
    {
        _idempotencyStore = idempotencyStore;
        _versionStore = versionStore;
        _eventBus = eventBus;
    }

    public async Task<CommandExecutionResult<SettingsCommandResult>> HandleAsync(CommandEnvelope<UnlinkDiscordCommand> command, CancellationToken cancellationToken = default)
    {
        var result = await SettingsPhaseOCommandHelper.ExecuteAsync(command, AggregateType, "discord_unlinked", "settings:unlink:discord", _idempotencyStore, _versionStore, _ => Task.FromResult(true), cancellationToken);
        if (result.Accepted && result.Result is { Replay: false })
        {
            await _eventBus.PublishAsync(new SettingsDiscordAccountUnlinkedSignalEvent(command.PrincipalId), cancellationToken);
        }

        return result;
    }
}

public sealed class UnlinkEmailCommandHandler : ICommandHandler<UnlinkEmailCommand, SettingsCommandResult>
{
    private const string AggregateType = "settings";
    private readonly IIdempotencyStore _idempotencyStore;
    private readonly IAggregateVersionStore _versionStore;

    public UnlinkEmailCommandHandler(IIdempotencyStore idempotencyStore, IAggregateVersionStore versionStore)
    {
        _idempotencyStore = idempotencyStore;
        _versionStore = versionStore;
    }

    public Task<CommandExecutionResult<SettingsCommandResult>> HandleAsync(CommandEnvelope<UnlinkEmailCommand> command, CancellationToken cancellationToken = default) =>
        SettingsPhaseOCommandHelper.ExecuteAsync(command, AggregateType, "email_unlinked", "settings:unlink:email", _idempotencyStore, _versionStore, _ => Task.FromResult(true), cancellationToken);
}

public sealed class UnlinkAppleCommandHandler : ICommandHandler<UnlinkAppleCommand, SettingsCommandResult>
{
    private const string AggregateType = "settings";
    private readonly IIdempotencyStore _idempotencyStore;
    private readonly IAggregateVersionStore _versionStore;
    private readonly IClusterEventBus _eventBus;

    public UnlinkAppleCommandHandler(IIdempotencyStore idempotencyStore, IAggregateVersionStore versionStore, IClusterEventBus eventBus)
    {
        _idempotencyStore = idempotencyStore;
        _versionStore = versionStore;
        _eventBus = eventBus;
    }

    public async Task<CommandExecutionResult<SettingsCommandResult>> HandleAsync(CommandEnvelope<UnlinkAppleCommand> command, CancellationToken cancellationToken = default)
    {
        var result = await SettingsPhaseOCommandHelper.ExecuteAsync(command, AggregateType, "apple_unlinked", "settings:unlink:apple", _idempotencyStore, _versionStore, _ => Task.FromResult(true), cancellationToken);
        if (result.Accepted && result.Result is { Replay: false })
        {
            await _eventBus.PublishAsync(new SettingsAppleAccountUnlinkedSignalEvent(command.PrincipalId), cancellationToken);
        }

        return result;
    }
}

public sealed class DeleteAccountCommandHandler : ICommandHandler<DeleteAccountCommand, SettingsCommandResult>
{
    private const string AggregateType = "settings";
    private readonly IIdempotencyStore _idempotencyStore;
    private readonly IAggregateVersionStore _versionStore;
    private readonly IClusterEventBus _eventBus;

    public DeleteAccountCommandHandler(IIdempotencyStore idempotencyStore, IAggregateVersionStore versionStore, IClusterEventBus eventBus)
    {
        _idempotencyStore = idempotencyStore;
        _versionStore = versionStore;
        _eventBus = eventBus;
    }

    public async Task<CommandExecutionResult<SettingsCommandResult>> HandleAsync(CommandEnvelope<DeleteAccountCommand> command, CancellationToken cancellationToken = default)
    {
        var result = await SettingsPhaseOCommandHelper.ExecuteAsync(command, AggregateType, "account_deleted", "settings:account:delete", _idempotencyStore, _versionStore, _ => Task.FromResult(true), cancellationToken);
        if (result.Accepted && result.Result is { Replay: false })
        {
            await _eventBus.PublishAsync(new SettingsAccountDeletedSignalEvent(command.PrincipalId), cancellationToken);
        }

        return result;
    }
}

public sealed class WipeAltersCommandHandler : ICommandHandler<WipeAltersCommand, SettingsCommandResult>
{
    private const string AggregateType = "settings";
    private readonly IIdempotencyStore _idempotencyStore;
    private readonly IAggregateVersionStore _versionStore;
    private readonly IClusterEventBus _eventBus;

    public WipeAltersCommandHandler(IIdempotencyStore idempotencyStore, IAggregateVersionStore versionStore, IClusterEventBus eventBus)
    {
        _idempotencyStore = idempotencyStore;
        _versionStore = versionStore;
        _eventBus = eventBus;
    }

    public async Task<CommandExecutionResult<SettingsCommandResult>> HandleAsync(CommandEnvelope<WipeAltersCommand> command, CancellationToken cancellationToken = default)
    {
        var result = await SettingsPhaseOCommandHelper.ExecuteAsync(command, AggregateType, "alters_wiped", "settings:alters:wipe", _idempotencyStore, _versionStore, _ => Task.FromResult(true), cancellationToken);
        if (result.Accepted && result.Result is { Replay: false })
        {
            await _eventBus.PublishAsync(new SettingsAltersWipedSignalEvent(command.PrincipalId), cancellationToken);
        }

        return result;
    }
}

public sealed class CreateFieldCommandHandler : ICommandHandler<CreateFieldCommand, SettingsCommandResult>
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

    public Task<CommandExecutionResult<SettingsCommandResult>> HandleAsync(CommandEnvelope<CreateFieldCommand> command, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(command.Payload.Name))
        {
            return Task.FromResult(CommandExecutionResult<SettingsCommandResult>.Rejected(
                new ConflictResult(ConflictCode.ConflictInvariant, command.OperationId, "settings:field_name_required", null, "manual_merge_required", null)));
        }

        var normalizedType = NormalizeType(command.Payload.Type);

        if (!AllowedSecurityLevels.Contains(command.Payload.SecurityLevel))
        {
            return Task.FromResult(CommandExecutionResult<SettingsCommandResult>.Rejected(
                new ConflictResult(ConflictCode.ConflictInvariant, command.OperationId, "settings:field_security_level_invalid", null, "manual_merge_required", null)));
        }

        return ExecuteAndPublishAsync(command, normalizedType, cancellationToken);
    }

    private async Task<CommandExecutionResult<SettingsCommandResult>> ExecuteAndPublishAsync(
        CommandEnvelope<CreateFieldCommand> command,
        string normalizedType,
        CancellationToken cancellationToken)
    {
        var result = await SettingsPhaseOCommandHelper.ExecuteAsync(
            command,
            AggregateType,
            "field_created",
            "settings:field:create",
            _idempotencyStore,
            _versionStore,
            async ct => (await _fieldRepository.CreateAsync(
                command.PrincipalId,
                command.Payload.Name,
                normalizedType,
                command.Payload.SecurityLevel,
                command.Payload.Locked,
                ct)) is not null,
            cancellationToken);

        if (result.Accepted && result.Result is { Replay: false })
        {
            await _eventBus.PublishAsync(new SettingsFieldsChangedEvent(command.PrincipalId), cancellationToken);
        }

        return result;
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
        var result = await SettingsPhaseOCommandHelper.ExecuteAsync(
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
        var result = await SettingsPhaseOCommandHelper.ExecuteAsync(
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
        var result = await SettingsPhaseOCommandHelper.ExecuteAsync(
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
