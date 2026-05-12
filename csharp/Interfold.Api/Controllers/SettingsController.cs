using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Interfold.Api.Models;
using Interfold.Api.Services;
using Interfold.Contracts;
using Interfold.Contracts.Configuration;
using Interfold.Contracts.Models.Commands;
using Interfold.Contracts.Models.Read;
using Interfold.Contracts.Operations;
using Interfold.Domain.Abstractions;
using Interfold.Domain.Abstractions.Repository;
using Interfold.Domain.Accounts;
using Interfold.Domain.Settings;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;

namespace Interfold.Api.Controllers;

[Route("api/settings")]
public sealed class SettingsController : InterfoldControllerBase
{
    private readonly IAccountRepository _accountRepository;
    private readonly ISingletonTaskOwner _singletonTaskOwner;
    private readonly IAvatarStorage _avatarStorage;
    private readonly IOptionsMonitor<AuthenticationConfiguration> _authenticationConfiguration;

    private readonly UpdateUsernameCommandHandler _usernameHandler;
    private readonly UpdateDescriptionCommandHandler _descriptionHandler;
    private readonly AddPushTokenCommandHandler _addPushTokenHandler;
    private readonly RemovePushTokenCommandHandler _removePushTokenHandler;
    private readonly SetupEncryptionCommandHandler _setupEncryptionHandler;
    private readonly RecoverEncryptionCommandHandler _recoverEncryptionHandler;
    private readonly ResetEncryptionCommandHandler _resetEncryptionHandler;
    private readonly UploadAvatarCommandHandler _uploadAvatarHandler;
    private readonly DeleteAvatarCommandHandler _deleteAvatarHandler;
    private readonly ImportPkCommandHandler _importPkHandler;
    private readonly ImportSpCommandHandler _importSpHandler;
    private readonly UnlinkDiscordCommandHandler _unlinkDiscordHandler;
    private readonly UnlinkEmailCommandHandler _unlinkEmailHandler;
    private readonly UnlinkAppleCommandHandler _unlinkAppleHandler;
    private readonly DeleteAccountCommandHandler _deleteAccountHandler;
    private readonly WipeAltersCommandHandler _wipeAltersHandler;
    private readonly CreateFieldCommandHandler _createFieldHandler;
    private readonly UpdateFieldCommandHandler _updateFieldHandler;
    private readonly DeleteFieldCommandHandler _deleteFieldHandler;
    private readonly RelocateFieldCommandHandler _relocateFieldHandler;

    public SettingsController(
        IAccountRepository accountRepository,
        ISingletonTaskOwner singletonTaskOwner,
        UpdateUsernameCommandHandler usernameHandler,
        UpdateDescriptionCommandHandler descriptionHandler,
        AddPushTokenCommandHandler addPushTokenHandler,
        RemovePushTokenCommandHandler removePushTokenHandler,
        SetupEncryptionCommandHandler setupEncryptionHandler,
        RecoverEncryptionCommandHandler recoverEncryptionHandler,
        ResetEncryptionCommandHandler resetEncryptionHandler,
        UploadAvatarCommandHandler uploadAvatarHandler,
        IAvatarStorage avatarStorage,
        DeleteAvatarCommandHandler deleteAvatarHandler,
        ImportPkCommandHandler importPkHandler,
        ImportSpCommandHandler importSpHandler,
        UnlinkDiscordCommandHandler unlinkDiscordHandler,
        UnlinkEmailCommandHandler unlinkEmailHandler,
        UnlinkAppleCommandHandler unlinkAppleHandler,
        DeleteAccountCommandHandler deleteAccountHandler,
        WipeAltersCommandHandler wipeAltersHandler,
        CreateFieldCommandHandler createFieldHandler,
        UpdateFieldCommandHandler updateFieldHandler,
        DeleteFieldCommandHandler deleteFieldHandler,
        RelocateFieldCommandHandler relocateFieldHandler,
        IOptionsMonitor<AuthenticationConfiguration> authenticationConfiguration)
    {
        _accountRepository = accountRepository;
        _singletonTaskOwner = singletonTaskOwner;
        _usernameHandler = usernameHandler;
        _descriptionHandler = descriptionHandler;
        _addPushTokenHandler = addPushTokenHandler;
        _removePushTokenHandler = removePushTokenHandler;
        _setupEncryptionHandler = setupEncryptionHandler;
        _recoverEncryptionHandler = recoverEncryptionHandler;
        _resetEncryptionHandler = resetEncryptionHandler;
        _uploadAvatarHandler = uploadAvatarHandler;
        _avatarStorage = avatarStorage;
        _deleteAvatarHandler = deleteAvatarHandler;
        _importPkHandler = importPkHandler;
        _importSpHandler = importSpHandler;
        _unlinkDiscordHandler = unlinkDiscordHandler;
        _unlinkEmailHandler = unlinkEmailHandler;
        _unlinkAppleHandler = unlinkAppleHandler;
        _deleteAccountHandler = deleteAccountHandler;
        _wipeAltersHandler = wipeAltersHandler;
        _createFieldHandler = createFieldHandler;
        _updateFieldHandler = updateFieldHandler;
        _deleteFieldHandler = deleteFieldHandler;
        _relocateFieldHandler = relocateFieldHandler;
        _authenticationConfiguration = authenticationConfiguration;
    }

    [HttpGet("link_token")]
    public async Task<IActionResult> GetLinkToken(CancellationToken ct)
    {
        // Token creation (write) is gated to primary nodes only — mirrors
        // Octocon.Global.LinkTokenRegistry singleton in the legacy Elixir runtime.
        // On auxiliary/sidecar nodes attempt a read-only lookup; if no token has
        // been provisioned yet, return 503 to inform the caller to retry via a
        // primary node.
        var principal = PrincipalId;
        if (_singletonTaskOwner.OwnsTask(SingletonTaskNames.LinkTokenRegistry))
        {
            var token = await _accountRepository.GetOrCreateLinkTokenAsync(principal, ct);
            return Ok(new { data = new { token } });
        }

        var existing = await _accountRepository.GetLinkTokenAsync(principal, ct);
        if (existing is null)
            return StatusCode(503, new { error = "link_token_unavailable", hint = "Retry on a primary node." });

        return Ok(new { data = new { token = existing } });
    }

    [HttpPost("username")]
    public async Task<Response> UpdateUsername([FromBody] SettingsUsernameRequest req, CancellationToken ct)
    {
        var principal = PrincipalId;
        var envelope = new CommandEnvelope<UpdateUsernameCommand>(
            OperationIds.SettingsUsernameUpdate, Guid.NewGuid(),
            PrincipalId: principal,
            IdempotencyKey: GetIdempotencyKey(req.IdempotencyKey),
            OccurredAt: DateTimeOffset.UtcNow,
            Payload: new UpdateUsernameCommand(req.Username)
        );

        return CommandNoContent(await _usernameHandler.HandleAsync(envelope, ct));
    }

    [HttpPost("description")]
    public async Task<Response> UpdateDescription([FromBody] SettingsDescriptionRequest req, CancellationToken ct)
    {
        var envelope = new CommandEnvelope<UpdateDescriptionCommand>(
            OperationIds.SettingsDescriptionUpdate,
            Guid.NewGuid(),
            PrincipalId: PrincipalId,
            IdempotencyKey: GetIdempotencyKey(req.IdempotencyKey),
            OccurredAt: DateTimeOffset.UtcNow,
            Payload: new UpdateDescriptionCommand(req.Description)
        );

        return CommandNoContent(await _descriptionHandler.HandleAsync(envelope, ct));
    }

    [HttpPost("push-token")]
    public async Task<Response> AddPushToken([FromBody] SettingsPushTokenRequest req, CancellationToken ct)
    {
        var pushToken = req.Token;
        if (string.IsNullOrWhiteSpace(pushToken))
            return new ErrorResponse("Invalid push token.", "invalid_push_token", System.Net.HttpStatusCode.BadRequest);

        var envelope = new CommandEnvelope<AddPushTokenCommand>(
            OperationIds.SettingsPushTokenAdd,
            Guid.NewGuid(),
            PrincipalId: PrincipalId,
            IdempotencyKey: GetIdempotencyKey(req.IdempotencyKey),
            OccurredAt: DateTimeOffset.UtcNow,
            Payload: new AddPushTokenCommand(pushToken)
        );

        return CommandNoContent(await _addPushTokenHandler.HandleAsync(envelope, ct));
    }

    [HttpDelete("push-token")]
    public async Task<Response> RemovePushToken([FromBody] SettingsPushTokenRequest req, CancellationToken ct)
    {
        var pushToken = req.Token;
        if (string.IsNullOrWhiteSpace(pushToken))
            return new ErrorResponse("Invalid push token.", "invalid_push_token", System.Net.HttpStatusCode.BadRequest);

        var envelope = new CommandEnvelope<RemovePushTokenCommand>(
            OperationIds.SettingsPushTokenRemove,
            Guid.NewGuid(),
            PrincipalId: PrincipalId,
            IdempotencyKey: GetIdempotencyKey(req.IdempotencyKey),
            OccurredAt: DateTimeOffset.UtcNow,
            Payload: new RemovePushTokenCommand(pushToken)
        );

        return CommandNoContent(await _removePushTokenHandler.HandleAsync(envelope, ct));
    }

    [HttpPost("setup-encryption")]
    public async Task<Response<EncryptionKeyResponse>> SetupEncryption([FromBody] SettingsEncryptionRequest req, CancellationToken ct)
    {
        if (!TryResolveRecoveryCode(req.RecoveryCode, out var recoveryCode, out var decryptionErrorCode))
            return new ErrorResponse("Failed to decrypt recovery code.", decryptionErrorCode, System.Net.HttpStatusCode.BadRequest);

        var envelope = new CommandEnvelope<SetupEncryptionCommand>(
            OperationIds.SettingsEncryptionSetup,
            Guid.NewGuid(),
            PrincipalId: PrincipalId,
            IdempotencyKey: GetIdempotencyKey(req.IdempotencyKey),
            OccurredAt: DateTimeOffset.UtcNow,
            Payload: new SetupEncryptionCommand(recoveryCode)
        );

        var execution = await _setupEncryptionHandler.HandleAsync(envelope, ct);
        if (execution.Accepted)
            return new SuccessResponse<EncryptionKeyResponse>(new EncryptionKeyResponse(execution.Result!.Key));

        return ConflictToError(execution.Conflict!);
    }

    [HttpPost("recover-encryption")]
    public async Task<Response<EncryptionKeyResponse>> RecoverEncryption([FromBody] SettingsEncryptionRequest req, CancellationToken ct)
    {
        if (!TryResolveRecoveryCode(req.RecoveryCode, out var recoveryCode, out var decryptionErrorCode))
            return new ErrorResponse("Failed to decrypt recovery code.", decryptionErrorCode, System.Net.HttpStatusCode.BadRequest);

        var envelope = new CommandEnvelope<RecoverEncryptionCommand>(
            OperationIds.SettingsEncryptionRecover,
            Guid.NewGuid(),
            PrincipalId: PrincipalId,
            IdempotencyKey: GetIdempotencyKey(req.IdempotencyKey),
            OccurredAt: DateTimeOffset.UtcNow,
            Payload: new RecoverEncryptionCommand(recoveryCode)
        );

        var execution = await _recoverEncryptionHandler.HandleAsync(envelope, ct);
        if (execution.Accepted)
            return new SuccessResponse<EncryptionKeyResponse>(new EncryptionKeyResponse(Convert.ToBase64String(Encoding.UTF8.GetBytes(execution.Result!.Key))));

        //TODO: To ensure route works as expected
        return ConflictToError(execution.Conflict!);
    }

    //TODO: To ensure route works as expected - does not delete journal entries currently which need adding
    [HttpPost("reset-encryption")]
    public async Task<Response> ResetEncryption([FromBody] BaseRequest? req, CancellationToken ct)
    {
        var envelope = new CommandEnvelope<ResetEncryptionCommand>(
            OperationIds.SettingsEncryptionReset,
            Guid.NewGuid(),
            PrincipalId: PrincipalId,
            IdempotencyKey: GetIdempotencyKey(req?.IdempotencyKey),
            OccurredAt: DateTimeOffset.UtcNow,
            Payload: new ResetEncryptionCommand()
        );

        return CommandNoContent(await _resetEncryptionHandler.HandleAsync(envelope, ct));
    }

    [HttpPut("avatar")]
    [Consumes("multipart/form-data")]
    public async Task<Response> UploadAvatarMultipart(CancellationToken ct)
    {
        var principal = PrincipalId;
        var upload = await ResolveMultipartUploadAsync(ct);
        var avatarStream = upload.Stream;
        if (avatarStream is null)
        {
            if (upload.EmptyFilePart)
                return new ErrorResponse("Avatar file is empty.", "avatar_file_empty", System.Net.HttpStatusCode.BadRequest);

            return new ErrorResponse("No avatar file provided.", "avatar_file_required", System.Net.HttpStatusCode.BadRequest);
        }

        string avatarUrl;
        try
        {
            await using (avatarStream)
            {
                avatarUrl = await _avatarStorage.SaveSystemAvatarAsync(principal, avatarStream, ct);
            }
        }
        catch
        {
            return new ErrorResponse("An error occurred while uploading the file.", "unknown_error", System.Net.HttpStatusCode.InternalServerError);
        }

        string? currentAvatarUrl = null;
        try
        {
            var currentProfile = await _accountRepository.GetPublicProfileAsync(principal, ct);
            currentAvatarUrl = currentProfile?.AvatarUrl;
        }
        catch
        {
        }

        var envelope = new CommandEnvelope<UploadAvatarCommand>(
            OperationIds.SettingsAvatarUpload,
            Guid.NewGuid(),
            PrincipalId: principal,
            IdempotencyKey: GetIdempotencyKey(upload.IdempotencyKey),
            OccurredAt: DateTimeOffset.UtcNow,
            Payload: new UploadAvatarCommand(avatarUrl)
        );

        var result = CommandNoContent(await _uploadAvatarHandler.HandleAsync(envelope, ct));

        if (!result.IsT0)
        {
            return result;
        }

        try
        {
            await _avatarStorage.DeleteByUrlAsync(currentAvatarUrl, ct);
        }
        catch
        {
            // Avatar metadata was updated successfully; tolerate storage cleanup failures.
        }

        return result;
    }

    [HttpDelete("avatar")]
    public async Task<Response> DeleteAvatar([FromBody] BaseRequest? req, CancellationToken ct)
    {
        var principal = PrincipalId;
        var currentProfile = await _accountRepository.GetPublicProfileAsync(principal, ct);
        var currentAvatarUrl = currentProfile?.AvatarUrl;

        var envelope = new CommandEnvelope<DeleteAvatarCommand>(
            OperationIds.SettingsAvatarDelete,
            Guid.NewGuid(),
            PrincipalId: principal,
            IdempotencyKey: GetIdempotencyKey(req?.IdempotencyKey),
            OccurredAt: DateTimeOffset.UtcNow,
            Payload: new DeleteAvatarCommand()
        );

        var execution = await _deleteAvatarHandler.HandleAsync(envelope, ct);
        if (execution.Accepted)
        {
            try
            {
                await _avatarStorage.DeleteByUrlAsync(currentAvatarUrl, ct);
            }
            catch
            {
                // Avatar metadata was cleared successfully; tolerate storage cleanup failures.
            }
        }

        return CommandNoContent(execution);
    }

    //TODO: To ensure route works as expected
    [HttpPost("import-pk")]
    public async Task<Response> ImportPk([FromBody] SettingsImportRequest req, CancellationToken ct)
    {
        var envelope = new CommandEnvelope<ImportPkCommand>(
            OperationIds.SettingsImportPk,
            Guid.NewGuid(),
            PrincipalId: PrincipalId,
            IdempotencyKey: GetIdempotencyKey(req.IdempotencyKey),
            OccurredAt: DateTimeOffset.UtcNow,
            Payload: new ImportPkCommand(req.Token)
        );

        return CommandNoContent(await _importPkHandler.HandleAsync(envelope, ct));
    }

    [HttpPost("import-sp")]
    public async Task<Response> ImportSp([FromBody] SettingsImportRequest req, CancellationToken ct)
    {
        var envelope = new CommandEnvelope<ImportSpCommand>(
            OperationIds.SettingsImportSp,
            Guid.NewGuid(),
            PrincipalId: PrincipalId,
            IdempotencyKey: GetIdempotencyKey(req.IdempotencyKey),
            OccurredAt: DateTimeOffset.UtcNow,
            Payload: new ImportSpCommand(req.Token, string.IsNullOrWhiteSpace(req.EncryptionKey) ? null : req.EncryptionKey)
        );

        return CommandNoContent(await _importSpHandler.HandleAsync(envelope, ct));
    }

    [HttpPost("unlink_discord")]
    public async Task<Response> UnlinkDiscord([FromBody] BaseRequest? req, CancellationToken ct)
    {
        var envelope = new CommandEnvelope<UnlinkDiscordCommand>(
            OperationIds.SettingsAuthUnlinkDiscord,
            Guid.NewGuid(),
            PrincipalId: PrincipalId,
            IdempotencyKey: GetIdempotencyKey(req?.IdempotencyKey),
            OccurredAt: DateTimeOffset.UtcNow,
            Payload: new UnlinkDiscordCommand()
        );

        return CommandNoContent(await _unlinkDiscordHandler.HandleAsync(envelope, ct));
    }

    [HttpPost("unlink_email")]
    public async Task<Response> UnlinkEmail([FromBody] BaseRequest? req, CancellationToken ct)
    {
        var envelope = new CommandEnvelope<UnlinkEmailCommand>(
            OperationIds.SettingsAuthUnlinkEmail,
            Guid.NewGuid(),
            PrincipalId: PrincipalId,
            IdempotencyKey: GetIdempotencyKey(req?.IdempotencyKey),
            OccurredAt: DateTimeOffset.UtcNow,
            Payload: new UnlinkEmailCommand()
        );

        return CommandNoContent(await _unlinkEmailHandler.HandleAsync(envelope, ct));
    }

    //TODO: To ensure route works as expected - other ones work but this one needs testing to ensure the command handler is correctly implemented
    [HttpPost("unlink_apple")]
    public async Task<Response> UnlinkApple([FromBody] BaseRequest? req, CancellationToken ct)
    {
        var envelope = new CommandEnvelope<UnlinkAppleCommand>(
            OperationIds.SettingsAuthUnlinkApple,
            Guid.NewGuid(),
            PrincipalId: PrincipalId,
            IdempotencyKey: GetIdempotencyKey(req?.IdempotencyKey),
            OccurredAt: DateTimeOffset.UtcNow,
            Payload: new UnlinkAppleCommand()
        );

        return CommandNoContent(await _unlinkAppleHandler.HandleAsync(envelope, ct));
    }

    //TODO: To ensure route works as expected
    [HttpPost("delete-account")]
    public async Task<Response> DeleteAccount([FromBody] BaseRequest? req, CancellationToken ct)
    {
        var envelope = new CommandEnvelope<DeleteAccountCommand>(
            OperationIds.SettingsAccountDelete,
            Guid.NewGuid(),
            PrincipalId: PrincipalId,
            IdempotencyKey: GetIdempotencyKey(req?.IdempotencyKey),
            OccurredAt: DateTimeOffset.UtcNow,
            Payload: new DeleteAccountCommand()
        );

        return CommandNoContent(await _deleteAccountHandler.HandleAsync(envelope, ct));
    }

    [HttpPost("wipe-alters")]
    public async Task<Response> WipeAlters([FromBody] BaseRequest? req, CancellationToken ct)
    {
        var envelope = new CommandEnvelope<WipeAltersCommand>(
            OperationIds.SettingsAltersWipe,
            Guid.NewGuid(),
            PrincipalId: PrincipalId,
            IdempotencyKey: GetIdempotencyKey(req?.IdempotencyKey),
            OccurredAt: DateTimeOffset.UtcNow,
            Payload: new WipeAltersCommand()
        );

        return CommandNoContent(await _wipeAltersHandler.HandleAsync(envelope, ct));
    }

    [HttpPost("fields")]
    public async Task<Response<FieldCreatedResponse>> CreateField([FromBody] SettingsCreateFieldRequest req, CancellationToken ct)
    {
        var envelope = new CommandEnvelope<CreateFieldCommand>(
            OperationIds.SettingsFieldCreate,
            Guid.NewGuid(),
            PrincipalId: PrincipalId,
            IdempotencyKey: GetIdempotencyKey(req.IdempotencyKey),
            OccurredAt: DateTimeOffset.UtcNow,
            Payload: new CreateFieldCommand(req.Name, req.Type ?? string.Empty, req.SecurityLevel ?? "private", req.Locked ?? false)
        );

        var execution = await _createFieldHandler.HandleAsync(envelope, ct);
        if (!execution.Accepted)
            return ConflictToError(execution.Conflict!);

        return new SuccessResponse<FieldCreatedResponse>(new FieldCreatedResponse(execution.Result!.FieldId), System.Net.HttpStatusCode.Created, execution.Result.Replay);
    }

    [HttpPatch("fields/{id}")]
    public async Task<Response> UpdateField([FromRoute] string id, [FromBody] SettingsUpdateFieldRequest req, CancellationToken ct)
    {
        var envelope = new CommandEnvelope<UpdateFieldCommand>(
            OperationIds.SettingsFieldUpdate,
            Guid.NewGuid(),
            PrincipalId: PrincipalId,
            IdempotencyKey: GetIdempotencyKey(req.IdempotencyKey),
            OccurredAt: DateTimeOffset.UtcNow,
            Payload: new UpdateFieldCommand(id, req.Name, req.SecurityLevel, req.Locked)
        );

        return CommandNoContent(await _updateFieldHandler.HandleAsync(envelope, ct));
    }

    [HttpDelete("fields/{id}")]
    public async Task<Response> DeleteField([FromRoute] string id, [FromBody] BaseRequest? req, CancellationToken ct)
    {
        var envelope = new CommandEnvelope<DeleteFieldCommand>(
            OperationIds.SettingsFieldDelete,
            Guid.NewGuid(),
            PrincipalId: PrincipalId,
            IdempotencyKey: GetIdempotencyKey(req?.IdempotencyKey),
            OccurredAt: DateTimeOffset.UtcNow,
            Payload: new DeleteFieldCommand(id)
        );

        return CommandNoContent(await _deleteFieldHandler.HandleAsync(envelope, ct));
    }

    [HttpPost("fields/{id}/relocate")]
    public async Task<Response> RelocateField([FromRoute] string id, [FromBody] SettingsRelocateFieldRequest req, CancellationToken ct)
    {
        var envelope = new CommandEnvelope<RelocateFieldCommand>(
            OperationIds.SettingsFieldRelocate,
            Guid.NewGuid(),
            PrincipalId: PrincipalId,
            IdempotencyKey: GetIdempotencyKey(req.IdempotencyKey),
            OccurredAt: DateTimeOffset.UtcNow,
            Payload: new RelocateFieldCommand(id, req.Index)
        );

        return CommandNoContent(await _relocateFieldHandler.HandleAsync(envelope, ct));
    }

    /* New endpoints from here! */
    [HttpGet("public-key")]
    [AllowAnonymous]
    public async Task<Response<string>> GetPublicKey(CancellationToken ct)
    {
        var authenticationConfiguration = _authenticationConfiguration.CurrentValue;
        return authenticationConfiguration.Rsa256PublicKey;
    }

    private async Task<AvatarUploadPayload> ResolveMultipartUploadAsync(CancellationToken ct)
    {
        string? idempotencyKey = null;
        var emptyFilePart = false;

        if (Request.Body is null)
            return new AvatarUploadPayload(null, idempotencyKey, emptyFilePart);

        Request.EnableBuffering();

        if (Request.Body.CanSeek)
            Request.Body.Position = 0;

        if (!MediaTypeHeaderValue.TryParse(Request.ContentType, out var mediaType)
            || !mediaType.MediaType.HasValue
            || !mediaType.MediaType.Value.StartsWith("multipart/", StringComparison.OrdinalIgnoreCase))
        {
            return new AvatarUploadPayload(null, idempotencyKey, emptyFilePart);
        }

        var boundary = HeaderUtilities.RemoveQuotes(mediaType.Boundary).Value;
        if (string.IsNullOrWhiteSpace(boundary))
            return new AvatarUploadPayload(null, idempotencyKey, emptyFilePart);

        try
        {
            var reader = new MultipartReader(boundary, Request.Body);
            MultipartSection? section;

            while ((section = await reader.ReadNextSectionAsync(ct)) is not null)
            {
                if (!ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out var disposition))
                    continue;

                var fieldName = HeaderUtilities.RemoveQuotes(disposition.Name).Value;
                var fileName = HeaderUtilities.RemoveQuotes(disposition.FileNameStar).Value
                               ?? HeaderUtilities.RemoveQuotes(disposition.FileName).Value;

                if (!string.IsNullOrWhiteSpace(fileName))
                {
                    var payload = new MemoryStream();
                    await section.Body.CopyToAsync(payload, ct);
                    if (payload.Length <= 0)
                    {
                        emptyFilePart = true;
                        await payload.DisposeAsync();
                        continue;
                    }

                    payload.Position = 0;
                    return new AvatarUploadPayload(payload, idempotencyKey, emptyFilePart);
                }

                if (string.IsNullOrWhiteSpace(fieldName))
                    continue;

                using var readerText = new StreamReader(section.Body, Encoding.UTF8, true, 1024, leaveOpen: true);
                var value = (await readerText.ReadToEndAsync(ct)).Trim();
                if (fieldName.Equals("idempotencyKey", StringComparison.OrdinalIgnoreCase))
                    idempotencyKey = string.IsNullOrWhiteSpace(value) ? null : value;
            }
        }
        catch (IOException)
        {
            return new AvatarUploadPayload(null, idempotencyKey, emptyFilePart);
        }

        return new AvatarUploadPayload(null, idempotencyKey, emptyFilePart);
    }

    private bool TryResolveRecoveryCode(string candidate, out string recoveryCode, out string errorCode)
        => Helpers.RecoveryCodeResolver.TryResolve(candidate, _authenticationConfiguration.CurrentValue.Rsa256PrivateKey, out recoveryCode, out errorCode);
}
