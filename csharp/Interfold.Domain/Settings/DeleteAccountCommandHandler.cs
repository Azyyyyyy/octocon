using Interfold.Contracts;
using Interfold.Contracts.Events;
using Interfold.Contracts.Models;
using Interfold.Contracts.Models.Commands;
using Interfold.Contracts.Operations;
using Interfold.Domain.Abstractions;
using Interfold.Domain.Abstractions.Repository;

namespace Interfold.Domain.Settings;

public sealed class DeleteAccountCommandHandler : ICommandHandler<DeleteAccountCommand, SettingsCommandResult>
{
    private readonly IIdempotencyStore _idempotencyStore;
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
        var result = await SettingsCommandHelper.ExecuteAsync(command, "account_deleted", "settings:account:delete", _idempotencyStore, async ct =>
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

        if (result is { Accepted: true, Result.Replay: false })
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