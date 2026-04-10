using Interfold.Contracts.Operations;
using Interfold.Domain.Abstractions;
using Interfold.Domain.Accounts;
using Interfold.Domain.Alters;
using Interfold.Domain.Fronting;
using Interfold.Domain.Journals;
using Interfold.Domain.Polls;
using Interfold.Domain.Tags;
using Interfold.Domain.Settings;
using Interfold.Domain.Friendships;

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
    private readonly IAccountRepository _accountRepository;
    private readonly IAlterRepository _alterRepository;
    private readonly ITagRepository _tagRepository;
    private readonly IPollRepository _pollRepository;
    private readonly ISettingsFieldRepository _fieldRepository;
    private readonly IJournalRepository _journalRepository;
    private readonly IFrontingRepository _frontingRepository;
    private readonly IFriendshipRepository _friendshipRepository;

    public DeleteAccountCommandHandler(
        IIdempotencyStore idempotencyStore,
        IAggregateVersionStore versionStore,
        IClusterEventBus eventBus,
        IAccountRepository accountRepository,
        IAlterRepository alterRepository,
        ITagRepository tagRepository,
        IPollRepository pollRepository,
        ISettingsFieldRepository fieldRepository,
        IJournalRepository journalRepository,
        IFrontingRepository frontingRepository,
        IFriendshipRepository friendshipRepository)
    {
        _idempotencyStore = idempotencyStore;
        _versionStore = versionStore;
        _eventBus = eventBus;
        _accountRepository = accountRepository;
        _alterRepository = alterRepository;
        _tagRepository = tagRepository;
        _pollRepository = pollRepository;
        _fieldRepository = fieldRepository;
        _journalRepository = journalRepository;
        _frontingRepository = frontingRepository;
        _friendshipRepository = friendshipRepository;
    }

    public async Task<CommandExecutionResult<SettingsCommandResult>> HandleAsync(CommandEnvelope<DeleteAccountCommand> command, CancellationToken cancellationToken = default)
    {
        var unfriendedIds = new List<string>();
        var result = await SettingsCommandHelper.ExecuteAsync(command, AggregateType, "account_deleted", "settings:account:delete", _idempotencyStore, _versionStore, async ct =>
        {
            var systemId = command.PrincipalId;

            // Delete Alters
            var alters = await _alterRepository.ListAsync(systemId, ct);
            foreach (var alter in alters)
            {
                await _alterRepository.DeleteAsync(systemId, alter.Id, ct);
                //TODO: Delete alter image if it exists
            }

            // Delete Tags
            var tags = await _tagRepository.ListAsync(systemId, ct);
            foreach (var tag in tags)
            {
                await _tagRepository.DeleteAsync(systemId, tag.Id, ct);
            }

            // Delete Polls
            var polls = await _pollRepository.ListAsync(systemId, ct);
            foreach (var poll in polls)
            {
                await _pollRepository.DeleteAsync(systemId, poll.Id, ct);
            }

            // Delete Settings Fields
            var fields = await _fieldRepository.ListAsync(systemId, ct);
            foreach (var field in fields)
            {
                await _fieldRepository.DeleteAsync(systemId, field.Id, ct);
            }

            // Delete Journal Entries (Global)
            var globalEntries = await _journalRepository.ListGlobalAsync(systemId, ct);
            foreach (var entry in globalEntries)
            {
                await _journalRepository.DeleteGlobalAsync(systemId, entry.Id, ct);
            }

            // Fronting records are tied to alters so no need to delete them here
            
            // Delete Friendships and Friend Requests
            var deletedIds = await _friendshipRepository.DeleteAllForSystemAsync(systemId, ct);
            unfriendedIds.AddRange(deletedIds);

            // Delete Account
            return await _accountRepository.DeleteAsync(systemId, ct);

            //TODO: Delete account image if it exists
        }, cancellationToken);

        if (result.Accepted && result.Result is { Replay: false })
        {
            await _eventBus.PublishAsync(new SettingsAccountDeletedSignalEvent(command.PrincipalId), cancellationToken);

            foreach (var friendId in unfriendedIds)
            {
                await _eventBus.PublishAsync(new FriendshipRemovedEvent(friendId, command.PrincipalId), cancellationToken);
            }
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
    private readonly IAlterRepository _alterRepository;

    public WipeAltersCommandHandler(
        IIdempotencyStore idempotencyStore,
        IAggregateVersionStore versionStore,
        IClusterEventBus eventBus,
        IAlterRepository alterRepository)
    {
        _idempotencyStore = idempotencyStore;
        _versionStore = versionStore;
        _eventBus = eventBus;
        _alterRepository = alterRepository;
    }

    public async Task<CommandExecutionResult<SettingsCommandResult>> HandleAsync(CommandEnvelope<WipeAltersCommand> command, CancellationToken cancellationToken = default)
    {
        var result = await SettingsCommandHelper.ExecuteAsync(command, AggregateType, "alters_wiped", "settings:alters:wipe", _idempotencyStore, _versionStore, async ct =>
        {
            var systemId = command.PrincipalId;
            var alters = await _alterRepository.ListAsync(systemId, ct);
            foreach (var alter in alters)
            {
                await _alterRepository.DeleteAsync(systemId, alter.Id, ct);
                //TODO: Delete alter image if it exists
            }

            return true;
        }, cancellationToken);

        if (result.Accepted && result.Result is { Replay: false })
        {
            await _eventBus.PublishAsync(new SettingsAltersWipedSignalEvent(command.PrincipalId), cancellationToken);
        }

        return result;
    }
}
