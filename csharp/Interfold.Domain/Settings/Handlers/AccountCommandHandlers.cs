using Interfold.Contracts.Operations;
using Interfold.Domain.Abstractions;
using Interfold.Domain.Accounts;

namespace Interfold.Domain.Settings.Handlers;

public sealed class UnlinkDiscordCommandHandler : ICommandHandler<UnlinkDiscordCommand, SettingsCommandResult>
{
    private const string AggregateType = "settings";
    private readonly IAccountRepository _accountRepository;
    private readonly IIdempotencyStore _idempotencyStore;
    private readonly IAggregateVersionStore _versionStore;
    private readonly IClusterEventBus _eventBus;

    public UnlinkDiscordCommandHandler(IAccountRepository accountRepository, IIdempotencyStore idempotencyStore, IAggregateVersionStore versionStore, IClusterEventBus eventBus)
    {
        _accountRepository = accountRepository;
        _idempotencyStore = idempotencyStore;
        _versionStore = versionStore;
        _eventBus = eventBus;
    }

    public async Task<CommandExecutionResult<SettingsCommandResult>> HandleAsync(CommandEnvelope<UnlinkDiscordCommand> command, CancellationToken cancellationToken = default)
    {
        var result = await SettingsCommandHelper.ExecuteAsync(
            command,
            AggregateType,
            "discord_unlinked",
            "settings:unlink:discord",
            _idempotencyStore,
            _versionStore,
            ct => _accountRepository.UnlinkDiscordAsync(command.PrincipalId, ct),
            cancellationToken);
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
    private readonly IAccountRepository _accountRepository;
    private readonly IIdempotencyStore _idempotencyStore;
    private readonly IAggregateVersionStore _versionStore;

    public UnlinkEmailCommandHandler(IAccountRepository accountRepository, IIdempotencyStore idempotencyStore, IAggregateVersionStore versionStore)
    {
        _accountRepository = accountRepository;
        _idempotencyStore = idempotencyStore;
        _versionStore = versionStore;
    }

    public Task<CommandExecutionResult<SettingsCommandResult>> HandleAsync(CommandEnvelope<UnlinkEmailCommand> command, CancellationToken cancellationToken = default)
        => SettingsCommandHelper.ExecuteAsync(
            command,
            AggregateType,
            "email_unlinked",
            "settings:unlink:email",
            _idempotencyStore,
            _versionStore,
            ct => _accountRepository.UnlinkEmailAsync(command.PrincipalId, ct),
            cancellationToken);
}

public sealed class UnlinkAppleCommandHandler : ICommandHandler<UnlinkAppleCommand, SettingsCommandResult>
{
    private const string AggregateType = "settings";
    private readonly IAccountRepository _accountRepository;
    private readonly IIdempotencyStore _idempotencyStore;
    private readonly IAggregateVersionStore _versionStore;
    private readonly IClusterEventBus _eventBus;

    public UnlinkAppleCommandHandler(IAccountRepository accountRepository, IIdempotencyStore idempotencyStore, IAggregateVersionStore versionStore, IClusterEventBus eventBus)
    {
        _accountRepository = accountRepository;
        _idempotencyStore = idempotencyStore;
        _versionStore = versionStore;
        _eventBus = eventBus;
    }

    public async Task<CommandExecutionResult<SettingsCommandResult>> HandleAsync(CommandEnvelope<UnlinkAppleCommand> command, CancellationToken cancellationToken = default)
    {
        var result = await SettingsCommandHelper.ExecuteAsync(
            command,
            AggregateType,
            "apple_unlinked",
            "settings:unlink:apple",
            _idempotencyStore,
            _versionStore,
            ct => _accountRepository.UnlinkAppleAsync(command.PrincipalId, ct),
            cancellationToken);
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

    //TODO: Implement
    public async Task<CommandExecutionResult<SettingsCommandResult>> HandleAsync(CommandEnvelope<DeleteAccountCommand> command, CancellationToken cancellationToken = default)
    {
        var result = await SettingsCommandHelper.ExecuteAsync(command, AggregateType, "account_deleted", "settings:account:delete", _idempotencyStore, _versionStore, _ => Task.FromResult(true), cancellationToken);
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

    //TODO: Implement
    public WipeAltersCommandHandler(IIdempotencyStore idempotencyStore, IAggregateVersionStore versionStore, IClusterEventBus eventBus)
    {
        _idempotencyStore = idempotencyStore;
        _versionStore = versionStore;
        _eventBus = eventBus;
    }

    public async Task<CommandExecutionResult<SettingsCommandResult>> HandleAsync(CommandEnvelope<WipeAltersCommand> command, CancellationToken cancellationToken = default)
    {
        var result = await SettingsCommandHelper.ExecuteAsync(command, AggregateType, "alters_wiped", "settings:alters:wipe", _idempotencyStore, _versionStore, _ => Task.FromResult(true), cancellationToken);
        if (result.Accepted && result.Result is { Replay: false })
        {
            await _eventBus.PublishAsync(new SettingsAltersWipedSignalEvent(command.PrincipalId), cancellationToken);
        }

        return result;
    }
}
