using Microsoft.AspNetCore.Mvc;
using Octocon.Contracts.Operations;
using Octocon.Domain.Accounts;
using Octocon.Domain.Settings;

namespace Octocon.Api.Controllers;

[Route("api/settings")]
public sealed class SettingsController : OctoconControllerBase
{
    private readonly UpdateUsernameCommandHandler _usernameHandler;
    private readonly UpdateDescriptionCommandHandler _descriptionHandler;
    private readonly AddPushTokenCommandHandler _addPushTokenHandler;
    private readonly RemovePushTokenCommandHandler _removePushTokenHandler;
    private readonly SetupEncryptionCommandHandler _setupEncryptionHandler;
    private readonly RecoverEncryptionCommandHandler _recoverEncryptionHandler;
    private readonly ResetEncryptionCommandHandler _resetEncryptionHandler;

    public SettingsController(
        ApiSettings settings,
        UpdateUsernameCommandHandler usernameHandler,
        UpdateDescriptionCommandHandler descriptionHandler,
        AddPushTokenCommandHandler addPushTokenHandler,
        RemovePushTokenCommandHandler removePushTokenHandler,
        SetupEncryptionCommandHandler setupEncryptionHandler,
        RecoverEncryptionCommandHandler recoverEncryptionHandler,
        ResetEncryptionCommandHandler resetEncryptionHandler)
        : base(settings)
    {
        _usernameHandler = usernameHandler;
        _descriptionHandler = descriptionHandler;
        _addPushTokenHandler = addPushTokenHandler;
        _removePushTokenHandler = removePushTokenHandler;
        _setupEncryptionHandler = setupEncryptionHandler;
        _recoverEncryptionHandler = recoverEncryptionHandler;
        _resetEncryptionHandler = resetEncryptionHandler;
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

        var envelope = new CommandEnvelope<AddPushTokenCommand>(
            OperationIds.SettingsPushTokenAdd,
            Guid.NewGuid(),
            PrincipalId: principal,
            IdempotencyKey: GetIdempotencyKey(req.IdempotencyKey),
            ExpectedVersion: req.ExpectedVersion,
            OccurredAt: DateTimeOffset.UtcNow,
            Payload: new AddPushTokenCommand(req.PushToken)
        );

        var result = ToHttpResult(await _addPushTokenHandler.HandleAsync(envelope, ct));
        return result is OkObjectResult ? NoContent() : result;
    }

    [HttpDelete("push-token")]
    public async Task<IActionResult> RemovePushToken([FromBody] SettingsPushTokenRequest req, CancellationToken ct)
    {
        var principal = GetPrincipalId();
        if (principal is null) return Unauthorized();

        var envelope = new CommandEnvelope<RemovePushTokenCommand>(
            OperationIds.SettingsPushTokenRemove,
            Guid.NewGuid(),
            PrincipalId: principal,
            IdempotencyKey: GetIdempotencyKey(req.IdempotencyKey),
            ExpectedVersion: req.ExpectedVersion,
            OccurredAt: DateTimeOffset.UtcNow,
            Payload: new RemovePushTokenCommand(req.PushToken)
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
    string PushToken,
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
