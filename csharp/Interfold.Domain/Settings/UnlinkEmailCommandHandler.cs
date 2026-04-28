using Interfold.Contracts;
using Interfold.Contracts.Models;
using Interfold.Contracts.Models.Commands;
using Interfold.Contracts.Operations;
using Interfold.Domain.Abstractions;
using Interfold.Domain.Abstractions.Repository;

namespace Interfold.Domain.Settings;

public sealed class UnlinkEmailCommandHandler : ICommandHandler<UnlinkEmailCommand, SettingsCommandResult>
{
    private readonly IAccountRepository _accountRepository;
    private readonly IIdempotencyStore _idempotencyStore;

    public UnlinkEmailCommandHandler(IAccountRepository accountRepository, IIdempotencyStore idempotencyStore)
    {
        _accountRepository = accountRepository;
        _idempotencyStore = idempotencyStore;
    }

    public Task<CommandExecutionResult<SettingsCommandResult>> HandleAsync(CommandEnvelope<UnlinkEmailCommand> command, CancellationToken cancellationToken = default)
        => SettingsCommandHelper.ExecuteAsync(
            command,
            "email_unlinked",
            "settings:unlink:email",
            _idempotencyStore,
            ct => _accountRepository.UnlinkEmailAsync(command.PrincipalId, ct),
            cancellationToken);
}