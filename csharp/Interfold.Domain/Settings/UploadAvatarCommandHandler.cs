using Interfold.Contracts;
using Interfold.Contracts.Events;
using Interfold.Contracts.Models;
using Interfold.Contracts.Models.Commands;
using Interfold.Contracts.Operations;
using Interfold.Domain.Abstractions;
using Interfold.Domain.Abstractions.Repository;

namespace Interfold.Domain.Settings;

public sealed class UploadAvatarCommandHandler : ICommandHandler<UploadAvatarCommand, SettingsCommandResult>
{
    private readonly IAccountRepository _accountRepository;
    private readonly IIdempotencyStore _idempotencyStore;
    private readonly IClusterEventBus _eventBus;

    public UploadAvatarCommandHandler(IAccountRepository accountRepository, IIdempotencyStore idempotencyStore, IClusterEventBus eventBus)
    {
        _accountRepository = accountRepository;
        _idempotencyStore = idempotencyStore;
        _eventBus = eventBus;
    }

    public Task<CommandExecutionResult<SettingsCommandResult>> HandleAsync(CommandEnvelope<UploadAvatarCommand> command, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(command.Payload.AvatarUrl))
        {
            return Task.FromResult(CommandExecutionResult<SettingsCommandResult>.Rejected(
                new ConflictResult(ConflictCode.ConflictInvariant, command.OperationId, "settings:avatar_invalid", "manual_merge_required")));
        }

        return ExecuteAndPublishAsync(command, cancellationToken);
    }

    private async Task<CommandExecutionResult<SettingsCommandResult>> ExecuteAndPublishAsync(
        CommandEnvelope<UploadAvatarCommand> command,
        CancellationToken cancellationToken)
    {
        var result = await SettingsCommandHelper.ExecuteAsync(
            command,
            "avatar_uploaded",
            "settings:avatar:upload",
            _idempotencyStore,
            ct => _accountRepository.UpdateAvatarAsync(command.PrincipalId, command.Payload.AvatarUrl, ct),
            cancellationToken);

        if (result is { Accepted: true, Result.Replay: false })
        {
            await _eventBus.PublishAsync(new SettingsProfileUpdatedEvent(command.PrincipalId, false), cancellationToken);
        }

        return result;
    }
}