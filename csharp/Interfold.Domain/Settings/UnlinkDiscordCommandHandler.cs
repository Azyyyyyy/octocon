using Interfold.Contracts;
using Interfold.Contracts.Events;
using Interfold.Contracts.Models;
using Interfold.Contracts.Models.Commands;
using Interfold.Contracts.Operations;
using Interfold.Domain.Abstractions;
using Interfold.Domain.Abstractions.Repository;

namespace Interfold.Domain.Settings;

public sealed class UnlinkDiscordCommandHandler : ICommandHandler<UnlinkDiscordCommand, SettingsCommandResult>
{
    private readonly IAccountRepository _accountRepository;
    private readonly IIdempotencyStore _idempotencyStore;
    private readonly IClusterEventBus _eventBus;

    public UnlinkDiscordCommandHandler(
        IAccountRepository accountRepository, 
        IIdempotencyStore idempotencyStore, 
        IClusterEventBus eventBus)
    {
        _accountRepository = accountRepository;
        _idempotencyStore = idempotencyStore;
        _eventBus = eventBus;
    }

    public async Task<CommandExecutionResult<SettingsCommandResult>> HandleAsync(CommandEnvelope<UnlinkDiscordCommand> command, CancellationToken cancellationToken = default)
    {
        var result = await SettingsCommandHelper.ExecuteAsync(
            command,
            "discord_unlinked",
            "settings:unlink:discord",
            _idempotencyStore,
            ct => _accountRepository.UnlinkDiscordAsync(command.PrincipalId, ct),
            cancellationToken);
        if (result is { Accepted: true, Result.Replay: false })
        {
            await _eventBus.PublishAsync(new SettingsDiscordAccountUnlinkedSignalEvent(command.PrincipalId), cancellationToken);
        }

        return result;
    }
}