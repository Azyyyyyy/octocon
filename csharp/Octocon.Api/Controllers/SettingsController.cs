using Microsoft.AspNetCore.Mvc;
using System.Text.Json.Serialization;
using Octocon.Api.Services;
using Octocon.Contracts.Operations;
using Octocon.Domain.Abstractions;
using Octocon.Domain.Accounts;
using Octocon.Domain.Settings;

namespace Octocon.Api.Controllers;

[Route("api/settings")]
public sealed class SettingsController : OctoconControllerBase
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
        ApiSettings settings,
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
        : base(settings)
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

        var pushToken = req.ResolvePushToken();
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

        var pushToken = req.ResolvePushToken();
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

        var envelope = new CommandEnvelope<Octocon.Domain.Settings.SetupEncryptionCommand>(
            OperationIds.SettingsEncryptionSetup,
            Guid.NewGuid(),
            PrincipalId: principal,
            IdempotencyKey: GetIdempotencyKey(req.IdempotencyKey),
            ExpectedVersion: req.ExpectedVersion,
            OccurredAt: DateTimeOffset.UtcNow,
            Payload: new Octocon.Domain.Settings.SetupEncryptionCommand(req.RecoveryCode)
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

        var envelope = new CommandEnvelope<Octocon.Domain.Settings.RecoverEncryptionCommand>(
            OperationIds.SettingsEncryptionRecover,
            Guid.NewGuid(),
            PrincipalId: principal,
            IdempotencyKey: GetIdempotencyKey(req.IdempotencyKey),
            ExpectedVersion: req.ExpectedVersion,
            OccurredAt: DateTimeOffset.UtcNow,
            Payload: new Octocon.Domain.Settings.RecoverEncryptionCommand(req.RecoveryCode)
        );

        var execution = await _recoverEncryptionHandler.HandleAsync(envelope, ct);
        if (execution.Accepted)
            return Ok(new { data = new { key = execution.Result!.Key } });

        return ToHttpResult(execution);
    }

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
    public async Task<IActionResult> UploadAvatar([FromBody] SettingsAvatarRequest req, CancellationToken ct)
    {
        var principal = GetPrincipalId();
        if (principal is null) return Unauthorized();

        var envelope = new CommandEnvelope<UploadAvatarCommand>(
            OperationIds.SettingsAvatarUpload,
            Guid.NewGuid(),
            PrincipalId: principal,
            IdempotencyKey: GetIdempotencyKey(req.IdempotencyKey),
            ExpectedVersion: req.ExpectedVersion,
            OccurredAt: DateTimeOffset.UtcNow,
            Payload: new UploadAvatarCommand(req.AvatarUrl)
        );

        var result = ToHttpResult(await _uploadAvatarHandler.HandleAsync(envelope, ct));
        return result is OkObjectResult ? NoContent() : result;
    }

    [HttpPut("avatar")]
    [Consumes("multipart/form-data")]
    public async Task<IActionResult> UploadAvatarMultipart([FromForm] SettingsAvatarMultipartRequest req, CancellationToken ct)
    {
        var principal = GetPrincipalId();
        if (principal is null) return Unauthorized();

        if (req.File is null || req.File.Length <= 0)
        {
            return BadRequest(new { error = "No avatar file provided.", code = "avatar_file_required" });
        }

        string avatarUrl;
        try
        {
            avatarUrl = await _avatarStorage.SaveSystemAvatarAsync(principal, req.File, ct);
        }
        catch
        {
            return StatusCode(500, new { error = "An error occurred while uploading the file.", code = "unknown_error" });
        }

        var envelope = new CommandEnvelope<UploadAvatarCommand>(
            OperationIds.SettingsAvatarUpload,
            Guid.NewGuid(),
            PrincipalId: principal,
            IdempotencyKey: GetIdempotencyKey(req.IdempotencyKey),
            ExpectedVersion: req.ExpectedVersion,
            OccurredAt: DateTimeOffset.UtcNow,
            Payload: new UploadAvatarCommand(avatarUrl)
        );

        var result = ToHttpResult(await _uploadAvatarHandler.HandleAsync(envelope, ct));
        return result is OkObjectResult ? NoContent() : result;
    }

    [HttpDelete("avatar")]
    public async Task<IActionResult> DeleteAvatar([FromBody] SettingsCommandRequest? req, CancellationToken ct)
    {
        var principal = GetPrincipalId();
        if (principal is null) return Unauthorized();

        var envelope = new CommandEnvelope<DeleteAvatarCommand>(
            OperationIds.SettingsAvatarDelete,
            Guid.NewGuid(),
            PrincipalId: principal,
            IdempotencyKey: GetIdempotencyKey(req?.IdempotencyKey),
            ExpectedVersion: req?.ExpectedVersion,
            OccurredAt: DateTimeOffset.UtcNow,
            Payload: new DeleteAvatarCommand()
        );

        var result = ToHttpResult(await _deleteAvatarHandler.HandleAsync(envelope, ct));
        return result is OkObjectResult ? NoContent() : result;
    }

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
    string? PushToken,
    [property: JsonPropertyName("token")] string? Token = null,
    string? IdempotencyKey = null,
    long? ExpectedVersion = null
)
{
    public string? ResolvePushToken() => PushToken ?? Token;
}

public sealed record SettingsEncryptionRequest(
    string RecoveryCode,
    string? IdempotencyKey = null,
    long? ExpectedVersion = null
);

public sealed record SettingsCommandRequest(
    string? IdempotencyKey = null,
    long? ExpectedVersion = null
);

public sealed record SettingsAvatarRequest(
    string AvatarUrl,
    string? IdempotencyKey = null,
    long? ExpectedVersion = null
);

public sealed record SettingsAvatarMultipartRequest(
    IFormFile? File,
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
