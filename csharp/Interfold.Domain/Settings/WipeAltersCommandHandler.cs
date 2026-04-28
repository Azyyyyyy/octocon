using Interfold.Contracts;
using Interfold.Contracts.Events;
using Interfold.Contracts.Models;
using Interfold.Contracts.Models.Commands;
using Interfold.Contracts.Operations;
using Interfold.Domain.Abstractions;
using Interfold.Domain.Abstractions.Repository;

namespace Interfold.Domain.Settings;

public sealed class WipeAltersCommandHandler : ICommandHandler<WipeAltersCommand, SettingsCommandResult>
{
    private readonly IIdempotencyStore _idempotencyStore;
    private readonly IClusterEventBus _eventBus;
    private readonly IAlterRepository _alterRepository;

    public WipeAltersCommandHandler(
        IIdempotencyStore idempotencyStore,
        IClusterEventBus eventBus,
        IAlterRepository alterRepository)
    {
        _idempotencyStore = idempotencyStore;
        _eventBus = eventBus;
        _alterRepository = alterRepository;
    }

    public async Task<CommandExecutionResult<SettingsCommandResult>> HandleAsync(CommandEnvelope<WipeAltersCommand> command, CancellationToken cancellationToken = default)
    {
        var result = await SettingsCommandHelper.ExecuteAsync(command, "alters_wiped", "settings:alters:wipe", _idempotencyStore, async ct =>
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

        if (result is { Accepted: true, Result.Replay: false })
        {
            await _eventBus.PublishAsync(new SettingsAltersWipedSignalEvent(command.PrincipalId), cancellationToken);
        }

        return result;
    }
}
