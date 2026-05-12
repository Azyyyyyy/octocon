using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;
using Microsoft.Extensions.Options;
using Interfold.Contracts.Configuration;
using Interfold.Contracts;
using Interfold.Contracts.Events;
using Interfold.Contracts.Models;
using Interfold.Contracts.Models.Commands;
using Interfold.Contracts.Operations;
using Interfold.Domain.Abstractions;
using Interfold.Domain.Abstractions.Repository;

namespace Interfold.Domain.Settings;

public sealed class SetupEncryptionCommandHandler : ICommandHandler<SetupEncryptionCommand, EncryptionCommandResult>
{
    private readonly IEncryptionStateRepository _repository;
    private readonly IIdempotencyStore _idempotencyStore;
    private readonly IClusterEventBus _eventBus;
    private readonly IOptionsMonitor<AuthenticationConfiguration> _authOptions;

    public SetupEncryptionCommandHandler(
        IEncryptionStateRepository repository,
        IIdempotencyStore idempotencyStore,
        IClusterEventBus eventBus,
        IOptionsMonitor<AuthenticationConfiguration> authOptions)
    {
        _repository = repository;
        _idempotencyStore = idempotencyStore;
        _eventBus = eventBus;
        _authOptions = authOptions;
    }

    public async Task<CommandExecutionResult<EncryptionCommandResult>> HandleAsync(
        CommandEnvelope<SetupEncryptionCommand> command,
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
                return RejectDuplicate(command, "settings:encryption:setup");

            var replay = CommandSerialization.Deserialize<EncryptionCommandResult>(previous.OutcomePayload);
            if (replay is not null)
                return CommandExecutionResult<EncryptionCommandResult>.Success(replay with { Replay = true });
        }
        
        var pepper = _authOptions.CurrentValue.EncryptionPepper;

        var existing = await _repository.GetAsync(command.PrincipalId, cancellationToken);
        if (string.IsNullOrWhiteSpace(existing?.Salt))
        {
            throw new InterfoldException("Salt must be provided.", "encryption_salt_required");
        }

        string salt = existing.Salt;
        var key = DeriveKey(pepper, command.PrincipalId, command.Payload.RecoveryCode, salt);
        var checksum = DeriveChecksum(key);

        var persisted = await _repository.UpsertAsync(command.PrincipalId, true, checksum, salt, cancellationToken);
        if (!persisted)
            return RejectInvariant(command, "settings:encryption_setup_failed");

        var result = new EncryptionCommandResult(command.PrincipalId, "encryption_setup", key, Replay: false);
        var resultJson = CommandSerialization.Serialize(result);

        await _idempotencyStore.SaveAsync(
            command.PrincipalId,
            command.OperationId,
            command.IdempotencyKey,
            payloadHash,
            CommandSerialization.Hash(resultJson),
            resultJson,
            cancellationToken);

        await _eventBus.PublishAsync(new SettingsProfileUpdatedEvent(command.PrincipalId, false), cancellationToken);
        return CommandExecutionResult<EncryptionCommandResult>.Success(result);
    }

    private static string DeriveKey(string pepper, string systemId, string recoveryCode, string salt)
    {
        // Step 1: SHA256(pepper + user_id + recovery_code)
        var hashInput = Encoding.UTF8.GetBytes(pepper + systemId + recoveryCode);
        var sha256Hash = SHA256.HashData(hashInput);

        // Step 2: Argon2id(sha256_hash, salt, t_cost=12, m_cost=65536, parallelism=1, hash_len=32)
        var saltBytes = Convert.FromBase64String(salt);
        using var argon2 = new Argon2id(sha256Hash);
        argon2.Salt = saltBytes;
        argon2.DegreeOfParallelism = 1;
        argon2.MemorySize = 65536; // KB
        argon2.Iterations = 12;

        var keyBytes = argon2.GetBytes(32);
        return Convert.ToBase64String(keyBytes);
    }

    private static string DeriveChecksum(string key)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return Convert.ToBase64String(hash)[..9];
    }

    private static CommandExecutionResult<EncryptionCommandResult> RejectDuplicate(
        CommandEnvelope<SetupEncryptionCommand> command,
        string entityRef) =>
        CommandExecutionResult<EncryptionCommandResult>.Rejected(
            new ConflictResult(ConflictCode.ConflictDuplicate, command.OperationId, entityRef, "no_retry"));

    private static CommandExecutionResult<EncryptionCommandResult> RejectInvariant(
        CommandEnvelope<SetupEncryptionCommand> command,
        string entityRef) =>
        CommandExecutionResult<EncryptionCommandResult>.Rejected(
            new ConflictResult(ConflictCode.ConflictInvariant, command.OperationId, entityRef, "manual_merge_required"));
}
