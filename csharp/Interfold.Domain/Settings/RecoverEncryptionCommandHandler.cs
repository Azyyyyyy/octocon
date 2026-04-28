using System.Security.Cryptography;
using System.Text;
using Interfold.Contracts;
using Interfold.Contracts.Models;
using Interfold.Contracts.Models.Commands;
using Interfold.Contracts.Operations;
using Interfold.Domain.Abstractions;
using Interfold.Domain.Abstractions.Repository;

namespace Interfold.Domain.Settings;

public sealed class RecoverEncryptionCommandHandler : ICommandHandler<RecoverEncryptionCommand, EncryptionCommandResult>
{
    private readonly IEncryptionStateRepository _repository;
    private readonly IIdempotencyStore _idempotencyStore;

    public RecoverEncryptionCommandHandler(
        IEncryptionStateRepository repository,
        IIdempotencyStore idempotencyStore)
    {
        _repository = repository;
        _idempotencyStore = idempotencyStore;
    }

    public async Task<CommandExecutionResult<EncryptionCommandResult>> HandleAsync(
        CommandEnvelope<RecoverEncryptionCommand> command,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(command.Payload.RecoveryCode))
            return RejectInvariant(command, "settings:recovery_code_invalid");

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
                return RejectDuplicate(command, "settings:encryption:recover");

            var replay = CommandSerialization.Deserialize<EncryptionCommandResult>(previous.OutcomePayload);
            if (replay is not null)
                return CommandExecutionResult<EncryptionCommandResult>.Success(replay with { Replay = true });
        }

        var state = await _repository.GetAsync(command.PrincipalId, cancellationToken);
        if (state is null || !state.Initialized || string.IsNullOrWhiteSpace(state.KeyChecksum))
            return RejectInvariant(command, "settings:encryption_not_initialized");

        var key = DeriveKey(command.PrincipalId, command.Payload.RecoveryCode);
        var checksum = DeriveChecksum(key);

        if (!string.Equals(checksum, state.KeyChecksum, StringComparison.Ordinal))
            return RejectInvariant(command, "settings:invalid_recovery_code");

        var result = new EncryptionCommandResult(command.PrincipalId, "encryption_recovered", key, Replay: false);
        var resultJson = CommandSerialization.Serialize(result);

        await _idempotencyStore.SaveAsync(
            command.PrincipalId,
            command.OperationId,
            command.IdempotencyKey,
            payloadHash,
            CommandSerialization.Hash(resultJson),
            resultJson,
            cancellationToken);

        return CommandExecutionResult<EncryptionCommandResult>.Success(result);
    }

    private static string DeriveKey(string systemId, string recoveryCode)
    {
        var input = Encoding.UTF8.GetBytes($"{systemId}:{recoveryCode}");
        return Convert.ToBase64String(SHA256.HashData(input));
    }

    private static string DeriveChecksum(string key)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return Convert.ToBase64String(hash)[..9];
    }

    private static CommandExecutionResult<EncryptionCommandResult> RejectDuplicate(
        CommandEnvelope<RecoverEncryptionCommand> command,
        string entityRef) =>
        CommandExecutionResult<EncryptionCommandResult>.Rejected(
            new ConflictResult(ConflictCode.ConflictDuplicate, command.OperationId, entityRef, "no_retry"));

    private static CommandExecutionResult<EncryptionCommandResult> RejectInvariant(
        CommandEnvelope<RecoverEncryptionCommand> command,
        string entityRef) =>
        CommandExecutionResult<EncryptionCommandResult>.Rejected(
            new ConflictResult(ConflictCode.ConflictInvariant, command.OperationId, entityRef, "manual_merge_required"));
}
