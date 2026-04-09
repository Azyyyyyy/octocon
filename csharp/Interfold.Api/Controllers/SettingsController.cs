using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Interfold.Api.Services;
using Interfold.Contracts.Operations;
using Interfold.Domain.Abstractions;
using Interfold.Domain.Accounts;
using Interfold.Domain.Settings;
using Interfold.Domain.Settings.Handlers;

namespace Interfold.Api.Controllers;

[Route("api/settings")]
public sealed class SettingsController : InterfoldControllerBase
{
    private readonly IAccountRepository _accountRepository;
    private readonly ISingletonTaskOwner _singletonTaskOwner;
    private readonly UpdateUsernameCommandHandler _usernameHandler;
    private readonly UpdateDescriptionCommandHandler _descriptionHandler;
    private readonly AddPushTokenCommandHandler _addPushTokenHandler;
    private readonly RemovePushTokenCommandHandler _removePushTokenHandler;
    private readonly SetupEncryptionCommandHandler _setupEncryptionHandler;
    private readonly RecoverEncryptionCommandHandler _recoverEncryptionHandler;
    private readonly ResetEncryptionCommandHandler _resetEncryptionHandler;
    private readonly UploadAvatarCommandHandler _uploadAvatarHandler;
    private readonly IAvatarStorage _avatarStorage;
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
        RelocateFieldCommandHandler relocateFieldHandler)
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
    }

    [HttpGet("link_token")]
    public async Task<IActionResult> GetLinkToken(CancellationToken ct)
    {
        var principal = GetPrincipalId();
        if (principal is null) return Unauthorized();

        // Token creation (write) is gated to primary nodes only — mirrors
        // Octocon.Global.LinkTokenRegistry singleton in the legacy Elixir runtime.
        // On auxiliary/sidecar nodes attempt a read-only lookup; if no token has
        // been provisioned yet, return 503 to inform the caller to retry via a
        // primary node.
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
    public async Task<IActionResult> UpdateUsername([FromBody] SettingsUsernameRequest req, CancellationToken ct)
    {
        var principal = GetPrincipalId();
        if (principal is null) return Unauthorized();

        var envelope = new CommandEnvelope<UpdateUsernameCommand>(
            OperationIds.SettingsUsernameUpdate, Guid.NewGuid(),
            PrincipalId: principal,
            IdempotencyKey: GetIdempotencyKey(req.IdempotencyKey),
            ExpectedVersion: req.ExpectedVersion,
            OccurredAt: DateTimeOffset.UtcNow,
            Payload: new UpdateUsernameCommand(req.Username)
        );

        var result = ToHttpResult(await _usernameHandler.HandleAsync(envelope, ct));
        return result is OkObjectResult ? NoContent() : result;
    }

    [HttpPost("description")]
    public async Task<IActionResult> UpdateDescription([FromBody] SettingsDescriptionRequest req, CancellationToken ct)
    {
        var principal = GetPrincipalId();
        if (principal is null) return Unauthorized();

        var envelope = new CommandEnvelope<UpdateDescriptionCommand>(
            OperationIds.SettingsDescriptionUpdate,
            Guid.NewGuid(),
            PrincipalId: principal,
            IdempotencyKey: GetIdempotencyKey(req.IdempotencyKey),
            ExpectedVersion: req.ExpectedVersion,
            OccurredAt: DateTimeOffset.UtcNow,
            Payload: new UpdateDescriptionCommand(req.Description)
        );

        var result = ToHttpResult(await _descriptionHandler.HandleAsync(envelope, ct));
        return result is OkObjectResult ? NoContent() : result;
    }

    [HttpPost("push-token")]
    public async Task<IActionResult> AddPushToken([FromBody] SettingsPushTokenRequest req, CancellationToken ct)
    {
        var principal = GetPrincipalId();
        if (principal is null) return Unauthorized();

        var pushToken = req.Token;
        if (string.IsNullOrWhiteSpace(pushToken))
            return BadRequest(new { error = "Invalid push token.", code = "invalid_push_token" });

        var envelope = new CommandEnvelope<AddPushTokenCommand>(
            OperationIds.SettingsPushTokenAdd,
            Guid.NewGuid(),
            PrincipalId: principal,
            IdempotencyKey: GetIdempotencyKey(req.IdempotencyKey),
            ExpectedVersion: req.ExpectedVersion,
            OccurredAt: DateTimeOffset.UtcNow,
            Payload: new AddPushTokenCommand(pushToken)
        );

        var result = ToHttpResult(await _addPushTokenHandler.HandleAsync(envelope, ct));
        return result is OkObjectResult ? NoContent() : result;
    }

    [HttpDelete("push-token")]
    public async Task<IActionResult> RemovePushToken([FromBody] SettingsPushTokenRequest req, CancellationToken ct)
    {
        var principal = GetPrincipalId();
        if (principal is null) return Unauthorized();

        var pushToken = req.Token;
        if (string.IsNullOrWhiteSpace(pushToken))
            return BadRequest(new { error = "Invalid push token.", code = "invalid_push_token" });

        var envelope = new CommandEnvelope<RemovePushTokenCommand>(
            OperationIds.SettingsPushTokenRemove,
            Guid.NewGuid(),
            PrincipalId: principal,
            IdempotencyKey: GetIdempotencyKey(req.IdempotencyKey),
            ExpectedVersion: req.ExpectedVersion,
            OccurredAt: DateTimeOffset.UtcNow,
            Payload: new RemovePushTokenCommand(pushToken)
        );

        var result = ToHttpResult(await _removePushTokenHandler.HandleAsync(envelope, ct));
        return result is OkObjectResult ? NoContent() : result;
    }

    [HttpPost("setup-encryption")]
    public async Task<IActionResult> SetupEncryption([FromBody] SettingsEncryptionRequest req, CancellationToken ct)
    {
        var principal = GetPrincipalId();
        if (principal is null) return Unauthorized();

        if (!TryResolveRecoveryCode(req.RecoveryCode, out var recoveryCode, out var decryptionErrorCode))
            return BadRequest(new { error = "Failed to decrypt recovery code.", code = decryptionErrorCode });

        var envelope = new CommandEnvelope<Interfold.Domain.Settings.SetupEncryptionCommand>(
            OperationIds.SettingsEncryptionSetup,
            Guid.NewGuid(),
            PrincipalId: principal,
            IdempotencyKey: GetIdempotencyKey(req.IdempotencyKey),
            ExpectedVersion: req.ExpectedVersion,
            OccurredAt: DateTimeOffset.UtcNow,
            Payload: new Interfold.Domain.Settings.SetupEncryptionCommand(recoveryCode)
        );

        var execution = await _setupEncryptionHandler.HandleAsync(envelope, ct);
        if (execution.Accepted)
            return Ok(new { data = new { key = execution.Result!.Key } });

        return ToHttpResult(execution);
    }

    [HttpPost("recover-encryption")]
    public async Task<IActionResult> RecoverEncryption([FromBody] SettingsEncryptionRequest req, CancellationToken ct)
    {
        var principal = GetPrincipalId();
        if (principal is null) return Unauthorized();

        if (!TryResolveRecoveryCode(req.RecoveryCode, out var recoveryCode, out var decryptionErrorCode))
            return BadRequest(new { error = "Failed to decrypt recovery code.", code = decryptionErrorCode });

        var envelope = new CommandEnvelope<Interfold.Domain.Settings.RecoverEncryptionCommand>(
            OperationIds.SettingsEncryptionRecover,
            Guid.NewGuid(),
            PrincipalId: principal,
            IdempotencyKey: GetIdempotencyKey(req.IdempotencyKey),
            ExpectedVersion: req.ExpectedVersion,
            OccurredAt: DateTimeOffset.UtcNow,
            Payload: new Interfold.Domain.Settings.RecoverEncryptionCommand(recoveryCode)
        );

        var execution = await _recoverEncryptionHandler.HandleAsync(envelope, ct);
        if (execution.Accepted)
            return Ok(new { data = new { key = execution.Result!.Key } });

        //TODO: To ensure route works as expected
        return ToHttpResult(execution);
    }

    //TODO: To ensure route works as expected - does not delete journal entries currently which need adding
    [HttpPost("reset-encryption")]
    public async Task<IActionResult> ResetEncryption([FromBody] SettingsCommandRequest? req, CancellationToken ct)
    {
        var principal = GetPrincipalId();
        if (principal is null) return Unauthorized();

        var envelope = new CommandEnvelope<ResetEncryptionCommand>(
            OperationIds.SettingsEncryptionReset,
            Guid.NewGuid(),
            PrincipalId: principal,
            IdempotencyKey: GetIdempotencyKey(req?.IdempotencyKey),
            ExpectedVersion: req?.ExpectedVersion,
            OccurredAt: DateTimeOffset.UtcNow,
            Payload: new ResetEncryptionCommand()
        );

        var result = ToHttpResult(await _resetEncryptionHandler.HandleAsync(envelope, ct));
        return result is OkObjectResult ? NoContent() : result;
    }

    [HttpPut("avatar")]
    [Consumes("multipart/form-data")]
    public async Task<IActionResult> UploadAvatarMultipart(CancellationToken ct)
    {
        var principal = GetPrincipalId();
        if (principal is null) return Unauthorized();

        var upload = await ResolveMultipartUploadAsync(ct);
        var avatarStream = upload.Stream;
        if (avatarStream is null)
        {
            if (upload.EmptyFilePart)
                return BadRequest(new { error = "Avatar file is empty.", code = "avatar_file_empty" });

            return BadRequest(new { error = "No avatar file provided.", code = "avatar_file_required" });
        }

        string avatarUrl;
        try
        {
            using (avatarStream)
            {
                avatarUrl = await _avatarStorage.SaveSystemAvatarAsync(principal, avatarStream, ct);
            }
        }
        catch
        {
            return StatusCode(500, new { error = "An error occurred while uploading the file.", code = "unknown_error" });
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
            ExpectedVersion: upload.ExpectedVersion,
            OccurredAt: DateTimeOffset.UtcNow,
            Payload: new UploadAvatarCommand(avatarUrl)
        );

        var result = ToHttpResult(await _uploadAvatarHandler.HandleAsync(envelope, ct));

        if (result is not OkObjectResult)
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

        return NoContent();
    }

    [HttpDelete("avatar")]
    public async Task<IActionResult> DeleteAvatar([FromBody] SettingsCommandRequest? req, CancellationToken ct)
    {
        var principal = GetPrincipalId();
        if (principal is null) return Unauthorized();

        var currentProfile = await _accountRepository.GetPublicProfileAsync(principal, ct);
        var currentAvatarUrl = currentProfile?.AvatarUrl;

        var envelope = new CommandEnvelope<DeleteAvatarCommand>(
            OperationIds.SettingsAvatarDelete,
            Guid.NewGuid(),
            PrincipalId: principal,
            IdempotencyKey: GetIdempotencyKey(req?.IdempotencyKey),
            ExpectedVersion: req?.ExpectedVersion,
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

        var result = ToHttpResult(execution);
        return result is OkObjectResult ? NoContent() : result;
    }

    //TODO: To ensure route works as expected
    [HttpPost("import-pk")]
    public async Task<IActionResult> ImportPk([FromBody] SettingsImportRequest req, CancellationToken ct)
    {
        var principal = GetPrincipalId();
        if (principal is null) return Unauthorized();

        var envelope = new CommandEnvelope<ImportPkCommand>(
            OperationIds.SettingsImportPk,
            Guid.NewGuid(),
            PrincipalId: principal,
            IdempotencyKey: GetIdempotencyKey(req.IdempotencyKey),
            ExpectedVersion: req.ExpectedVersion,
            OccurredAt: DateTimeOffset.UtcNow,
            Payload: new ImportPkCommand(req.Token)
        );

        var result = ToHttpResult(await _importPkHandler.HandleAsync(envelope, ct));
        return result is OkObjectResult ? NoContent() : result;
    }

    //TODO: To ensure route works as expected
    [HttpPost("import-sp")]
    public async Task<IActionResult> ImportSp([FromBody] SettingsImportRequest req, CancellationToken ct)
    {
        var principal = GetPrincipalId();
        if (principal is null) return Unauthorized();

        var envelope = new CommandEnvelope<ImportSpCommand>(
            OperationIds.SettingsImportSp,
            Guid.NewGuid(),
            PrincipalId: principal,
            IdempotencyKey: GetIdempotencyKey(req.IdempotencyKey),
            ExpectedVersion: req.ExpectedVersion,
            OccurredAt: DateTimeOffset.UtcNow,
            Payload: new ImportSpCommand(req.Token)
        );

        var result = ToHttpResult(await _importSpHandler.HandleAsync(envelope, ct));
        return result is OkObjectResult ? NoContent() : result;
    }

    [HttpPost("unlink_discord")]
    public async Task<IActionResult> UnlinkDiscord([FromBody] SettingsCommandRequest? req, CancellationToken ct)
    {
        var principal = GetPrincipalId();
        if (principal is null) return Unauthorized();

        var envelope = new CommandEnvelope<UnlinkDiscordCommand>(
            OperationIds.SettingsAuthUnlinkDiscord,
            Guid.NewGuid(),
            PrincipalId: principal,
            IdempotencyKey: GetIdempotencyKey(req?.IdempotencyKey),
            ExpectedVersion: req?.ExpectedVersion,
            OccurredAt: DateTimeOffset.UtcNow,
            Payload: new UnlinkDiscordCommand()
        );

        var result = ToHttpResult(await _unlinkDiscordHandler.HandleAsync(envelope, ct));
        return result is OkObjectResult ? NoContent() : result;
    }

    [HttpPost("unlink_email")]
    public async Task<IActionResult> UnlinkEmail([FromBody] SettingsCommandRequest? req, CancellationToken ct)
    {
        var principal = GetPrincipalId();
        if (principal is null) return Unauthorized();

        var envelope = new CommandEnvelope<UnlinkEmailCommand>(
            OperationIds.SettingsAuthUnlinkEmail,
            Guid.NewGuid(),
            PrincipalId: principal,
            IdempotencyKey: GetIdempotencyKey(req?.IdempotencyKey),
            ExpectedVersion: req?.ExpectedVersion,
            OccurredAt: DateTimeOffset.UtcNow,
            Payload: new UnlinkEmailCommand()
        );

        var result = ToHttpResult(await _unlinkEmailHandler.HandleAsync(envelope, ct));
        return result is OkObjectResult ? NoContent() : result;
    }

    //TODO: To ensure route works as expected - other ones work but this one needs testing to ensure the command handler is correctly implemented
    [HttpPost("unlink_apple")]
    public async Task<IActionResult> UnlinkApple([FromBody] SettingsCommandRequest? req, CancellationToken ct)
    {
        var principal = GetPrincipalId();
        if (principal is null) return Unauthorized();

        var envelope = new CommandEnvelope<UnlinkAppleCommand>(
            OperationIds.SettingsAuthUnlinkApple,
            Guid.NewGuid(),
            PrincipalId: principal,
            IdempotencyKey: GetIdempotencyKey(req?.IdempotencyKey),
            ExpectedVersion: req?.ExpectedVersion,
            OccurredAt: DateTimeOffset.UtcNow,
            Payload: new UnlinkAppleCommand()
        );

        var result = ToHttpResult(await _unlinkAppleHandler.HandleAsync(envelope, ct));
        return result is OkObjectResult ? NoContent() : result;
    }

    //TODO: To ensure route works as expected
    [HttpPost("delete-account")]
    public async Task<IActionResult> DeleteAccount([FromBody] SettingsCommandRequest? req, CancellationToken ct)
    {
        var principal = GetPrincipalId();
        if (principal is null) return Unauthorized();

        var envelope = new CommandEnvelope<DeleteAccountCommand>(
            OperationIds.SettingsAccountDelete,
            Guid.NewGuid(),
            PrincipalId: principal,
            IdempotencyKey: GetIdempotencyKey(req?.IdempotencyKey),
            ExpectedVersion: req?.ExpectedVersion,
            OccurredAt: DateTimeOffset.UtcNow,
            Payload: new DeleteAccountCommand()
        );

        var result = ToHttpResult(await _deleteAccountHandler.HandleAsync(envelope, ct));
        return result is OkObjectResult ? NoContent() : result;
    }

    //TODO: To ensure route works as expected
    [HttpPost("wipe-alters")]
    public async Task<IActionResult> WipeAlters([FromBody] SettingsCommandRequest? req, CancellationToken ct)
    {
        var principal = GetPrincipalId();
        if (principal is null) return Unauthorized();

        var envelope = new CommandEnvelope<WipeAltersCommand>(
            OperationIds.SettingsAltersWipe,
            Guid.NewGuid(),
            PrincipalId: principal,
            IdempotencyKey: GetIdempotencyKey(req?.IdempotencyKey),
            ExpectedVersion: req?.ExpectedVersion,
            OccurredAt: DateTimeOffset.UtcNow,
            Payload: new WipeAltersCommand()
        );

        var result = ToHttpResult(await _wipeAltersHandler.HandleAsync(envelope, ct));
        return result is OkObjectResult ? NoContent() : result;
    }

    [HttpPost("fields")]
    public async Task<IActionResult> CreateField([FromBody] SettingsCreateFieldRequest req, CancellationToken ct)
    {
        var principal = GetPrincipalId();
        if (principal is null) return Unauthorized();

        var envelope = new CommandEnvelope<CreateFieldCommand>(
            OperationIds.SettingsFieldCreate,
            Guid.NewGuid(),
            PrincipalId: principal,
            IdempotencyKey: GetIdempotencyKey(req.IdempotencyKey),
            ExpectedVersion: req.ExpectedVersion,
            OccurredAt: DateTimeOffset.UtcNow,
            Payload: new CreateFieldCommand(req.Name, req.Type, req.SecurityLevel ?? "private", req.Locked ?? false)
        );

        var result = ToHttpResult(await _createFieldHandler.HandleAsync(envelope, ct));
        return result is OkObjectResult ? NoContent() : result;
    }

    [HttpPatch("fields/{id}")]
    public async Task<IActionResult> UpdateField([FromRoute] string id, [FromBody] SettingsUpdateFieldRequest req, CancellationToken ct)
    {
        var principal = GetPrincipalId();
        if (principal is null) return Unauthorized();

        var envelope = new CommandEnvelope<UpdateFieldCommand>(
            OperationIds.SettingsFieldUpdate,
            Guid.NewGuid(),
            PrincipalId: principal,
            IdempotencyKey: GetIdempotencyKey(req.IdempotencyKey),
            ExpectedVersion: req.ExpectedVersion,
            OccurredAt: DateTimeOffset.UtcNow,
            Payload: new UpdateFieldCommand(id, req.Name, req.SecurityLevel, req.Locked)
        );

        var result = ToHttpResult(await _updateFieldHandler.HandleAsync(envelope, ct));
        return result is OkObjectResult ? NoContent() : result;
    }

    [HttpDelete("fields/{id}")]
    public async Task<IActionResult> DeleteField([FromRoute] string id, [FromBody] SettingsCommandRequest? req, CancellationToken ct)
    {
        var principal = GetPrincipalId();
        if (principal is null) return Unauthorized();

        var envelope = new CommandEnvelope<DeleteFieldCommand>(
            OperationIds.SettingsFieldDelete,
            Guid.NewGuid(),
            PrincipalId: principal,
            IdempotencyKey: GetIdempotencyKey(req?.IdempotencyKey),
            ExpectedVersion: req?.ExpectedVersion,
            OccurredAt: DateTimeOffset.UtcNow,
            Payload: new DeleteFieldCommand(id)
        );

        var result = ToHttpResult(await _deleteFieldHandler.HandleAsync(envelope, ct));
        return result is OkObjectResult ? NoContent() : result;
    }

    [HttpPost("fields/{id}/relocate")]
    public async Task<IActionResult> RelocateField([FromRoute] string id, [FromBody] SettingsRelocateFieldRequest req, CancellationToken ct)
    {
        var principal = GetPrincipalId();
        if (principal is null) return Unauthorized();

        var envelope = new CommandEnvelope<RelocateFieldCommand>(
            OperationIds.SettingsFieldRelocate,
            Guid.NewGuid(),
            PrincipalId: principal,
            IdempotencyKey: GetIdempotencyKey(req.IdempotencyKey),
            ExpectedVersion: req.ExpectedVersion,
            OccurredAt: DateTimeOffset.UtcNow,
            Payload: new RelocateFieldCommand(id, req.Index)
        );

        var result = ToHttpResult(await _relocateFieldHandler.HandleAsync(envelope, ct));
        return result is OkObjectResult ? NoContent() : result;
    }

    private async Task<AvatarUploadPayload> ResolveMultipartUploadAsync(CancellationToken ct)
    {
        string? idempotencyKey = null;
        long? expectedVersion = null;
        var emptyFilePart = false;

        if (Request.Body is null)
            return new AvatarUploadPayload(null, idempotencyKey, expectedVersion, emptyFilePart);

        Request.EnableBuffering();

        if (Request.Body.CanSeek)
            Request.Body.Position = 0;

        if (!MediaTypeHeaderValue.TryParse(Request.ContentType, out var mediaType)
            || !mediaType.MediaType.HasValue
            || !mediaType.MediaType.Value.StartsWith("multipart/", StringComparison.OrdinalIgnoreCase))
        {
            return new AvatarUploadPayload(null, idempotencyKey, expectedVersion, emptyFilePart);
        }

        var boundary = HeaderUtilities.RemoveQuotes(mediaType.Boundary).Value;
        if (string.IsNullOrWhiteSpace(boundary))
            return new AvatarUploadPayload(null, idempotencyKey, expectedVersion, emptyFilePart);

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
                    return new AvatarUploadPayload(payload, idempotencyKey, expectedVersion, emptyFilePart);
                }

                if (string.IsNullOrWhiteSpace(fieldName))
                    continue;

                using var readerText = new StreamReader(section.Body, Encoding.UTF8, true, 1024, leaveOpen: true);
                var value = (await readerText.ReadToEndAsync()).Trim();
                if (fieldName.Equals("idempotencyKey", StringComparison.OrdinalIgnoreCase))
                    idempotencyKey = string.IsNullOrWhiteSpace(value) ? null : value;
                else if (fieldName.Equals("expectedVersion", StringComparison.OrdinalIgnoreCase)
                         && long.TryParse(value, out var parsed))
                    expectedVersion = parsed;
            }
        }
        catch (IOException)
        {
            return new AvatarUploadPayload(null, idempotencyKey, expectedVersion, emptyFilePart);
        }

        return new AvatarUploadPayload(null, idempotencyKey, expectedVersion, emptyFilePart);
    }

    private sealed record AvatarUploadPayload(Stream? Stream, string? IdempotencyKey, long? ExpectedVersion, bool EmptyFilePart = false);

    private static bool TryResolveRecoveryCode(string candidate, out string recoveryCode, out string errorCode)
    {
        recoveryCode = string.Empty;
        errorCode = "decryption_error";

        if (string.IsNullOrWhiteSpace(candidate))
        {
            errorCode = "recovery_code_invalid";
            return false;
        }

        if (!LooksLikeCompactJwe(candidate))
        {
            recoveryCode = candidate;
            return true;
        }

        if (!TryLoadEncryptionPrivateKey(out var privateKeyPem))
            return false;

        if (!TryDecryptRecoveryCodeJwe(candidate, privateKeyPem, out recoveryCode))
            return false;

        return !string.IsNullOrWhiteSpace(recoveryCode);
    }

    private static bool LooksLikeCompactJwe(string token) => token.Count(ch => ch == '.') == 4;

    private static bool TryLoadEncryptionPrivateKey(out string privateKeyPem)
    {
        privateKeyPem = string.Empty;
        var raw = Environment.GetEnvironmentVariable("ENCRYPTION_PRIVATE_KEY");

        if (string.IsNullOrWhiteSpace(raw))
            return false;

        var normalized = raw.Trim();
        if (normalized.Contains("BEGIN", StringComparison.Ordinal))
        {
            privateKeyPem = normalized.Replace("\\n", "\n", StringComparison.Ordinal);
            return true;
        }

        try
        {
            var bytes = Convert.FromBase64String(normalized);
            privateKeyPem = Encoding.UTF8.GetString(bytes);
            return privateKeyPem.Contains("BEGIN", StringComparison.Ordinal);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static bool TryDecryptRecoveryCodeJwe(string compactJwe, string privateKeyPem, out string recoveryCode)
    {
        recoveryCode = string.Empty;

        try
        {
            var parts = compactJwe.Split('.');
            if (parts.Length != 5)
                return false;

            var headerJson = Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(parts[0]));
            using var header = JsonDocument.Parse(headerJson);
            var alg = header.RootElement.TryGetProperty("alg", out var algProp) ? algProp.GetString() : null;
            var enc = header.RootElement.TryGetProperty("enc", out var encProp) ? encProp.GetString() : null;

            if (!string.Equals(alg, "RSA-OAEP-256", StringComparison.Ordinal)
                || !string.Equals(enc, "A256GCM", StringComparison.Ordinal))
            {
                return false;
            }

            var encryptedKey = WebEncoders.Base64UrlDecode(parts[1]);
            var iv = WebEncoders.Base64UrlDecode(parts[2]);
            var ciphertext = WebEncoders.Base64UrlDecode(parts[3]);
            var tag = WebEncoders.Base64UrlDecode(parts[4]);
            var aad = Encoding.ASCII.GetBytes(parts[0]);

            using var rsa = RSA.Create();
            rsa.ImportFromPem(privateKeyPem);
            var cek = rsa.Decrypt(encryptedKey, RSAEncryptionPadding.OaepSHA256);

            var plaintext = new byte[ciphertext.Length];
            using var aes = new AesGcm(cek, tag.Length);
            aes.Decrypt(iv, ciphertext, tag, plaintext, aad);

            recoveryCode = Encoding.UTF8.GetString(plaintext);
            return !string.IsNullOrWhiteSpace(recoveryCode);
        }
        catch (Exception)
        {
            return false;
        }
    }
}

public sealed record SettingsUsernameRequest(
    string Username,
    string? IdempotencyKey = null,
    long? ExpectedVersion = null
);

public sealed record SettingsDescriptionRequest(
    string Description,
    string? IdempotencyKey = null,
    long? ExpectedVersion = null
);

public sealed record SettingsPushTokenRequest(
    string? Token = null,
    string? IdempotencyKey = null,
    long? ExpectedVersion = null
);

public sealed record SettingsEncryptionRequest(
    string RecoveryCode,
    string? IdempotencyKey = null,
    long? ExpectedVersion = null
);

public sealed record SettingsCommandRequest(
    string? IdempotencyKey = null,
    long? ExpectedVersion = null
);

public sealed record SettingsAvatarMultipartRequest(
    IFormFile File,
    string? IdempotencyKey = null,
    long? ExpectedVersion = null
);

public sealed record SettingsImportRequest(
    string Token,
    string? IdempotencyKey = null,
    long? ExpectedVersion = null
);

public sealed record SettingsCreateFieldRequest(
    string Name,
    string Type,
    string? SecurityLevel,
    bool? Locked,
    string? IdempotencyKey = null,
    long? ExpectedVersion = null
);

public sealed record SettingsUpdateFieldRequest(
    string? Name,
    string? SecurityLevel,
    bool? Locked,
    string? IdempotencyKey = null,
    long? ExpectedVersion = null
);

public sealed record SettingsRelocateFieldRequest(
    int Index,
    string? IdempotencyKey = null,
    long? ExpectedVersion = null
);
