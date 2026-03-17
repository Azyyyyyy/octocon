using Microsoft.AspNetCore.Mvc;
using Octocon.Api.Services;
using Octocon.Contracts.Operations;
using Octocon.Domain.Alters;

namespace Octocon.Api.Controllers;

[Route("api/systems/me/alters")]
public sealed class AltersController : OctoconControllerBase
{
    private readonly IAlterRepository _alterRepository;
    private readonly CreateAlterCommandHandler _createHandler;
    private readonly UpdateAlterCommandHandler _updateHandler;
    private readonly DeleteAlterCommandHandler _deleteHandler;
    private readonly IAvatarStorage _avatarStorage;

    public AltersController(
        ApiSettings settings,
        IAlterRepository alterRepository,
        CreateAlterCommandHandler createHandler,
        UpdateAlterCommandHandler updateHandler,
        DeleteAlterCommandHandler deleteHandler,
        IAvatarStorage avatarStorage)
        : base(settings)
    {
        _alterRepository = alterRepository;
        _createHandler = createHandler;
        _updateHandler = updateHandler;
        _deleteHandler = deleteHandler;
        _avatarStorage = avatarStorage;
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateAlterRequest req, CancellationToken ct)
    {
        var principal = GetPrincipalId();
        if (principal is null) return Unauthorized();

        var envelope = new CommandEnvelope<CreateAlterCommand>(
            OperationIds.AlterCreate, Guid.NewGuid(),
            PrincipalId: principal,
            IdempotencyKey: GetIdempotencyKey(req.IdempotencyKey),
            ExpectedVersion: req.ExpectedVersion,
            OccurredAt: DateTimeOffset.UtcNow,
            Payload: new CreateAlterCommand(req.Name)
        );

        var execution = await _createHandler.HandleAsync(envelope, ct);
        if (!execution.Accepted)
        {
            return ToHttpResult(execution);
        }

        var alter = await _alterRepository.GetAsync(principal, execution.Result!.AlterId, ct);
        object data = alter is not null ? alter : execution.Result;
        Response.Headers.Location = $"/api/systems/me/alters/{execution.Result.AlterId}";
        return StatusCode(StatusCodes.Status201Created, new { data, replay = execution.Result.Replay });
    }

    [HttpPatch("{id}")]
    public async Task<IActionResult> Update(string id, [FromBody] UpdateAlterRequest req, CancellationToken ct)
    {
        var principal = GetPrincipalId();
        if (principal is null) return Unauthorized();

        if (!TryParseAlterId(id, out var alterId))
            return BadRequest(new { error = "Invalid alter ID.", code = "invalid_alter_id" });

        var fields = req.Fields?.Select(f => new AlterFieldCommand(f.Id, f.Value)).ToList();
        var payload = new UpdateAlterCommand(
            AlterId: alterId,
            Name: req.Name,
            Description: req.Description,
            AvatarUrl: req.AvatarUrl,
            Color: req.Color,
            Pronouns: req.Pronouns,
            SecurityLevel: req.SecurityLevel,
            Fields: fields,
            ProxyName: req.ProxyName,
            Alias: req.Alias,
            Untracked: req.Untracked,
            Archived: req.Archived,
            Pinned: req.Pinned
        );
        var envelope = new CommandEnvelope<UpdateAlterCommand>(
            OperationIds.AlterUpdate, Guid.NewGuid(),
            PrincipalId: principal,
            IdempotencyKey: GetIdempotencyKey(req.IdempotencyKey),
            ExpectedVersion: req.ExpectedVersion,
            OccurredAt: DateTimeOffset.UtcNow,
            Payload: payload
        );
        var result = ToHttpResult(await _updateHandler.HandleAsync(envelope, ct));
        return result is OkObjectResult ? NoContent() : result;
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(string id, [FromBody] DeleteAlterRequest? req, CancellationToken ct)
    {
        var principal = GetPrincipalId();
        if (principal is null) return Unauthorized();

        if (!TryParseAlterId(id, out var alterId))
            return BadRequest(new { error = "Invalid alter ID.", code = "invalid_alter_id" });

        var envelope = new CommandEnvelope<DeleteAlterCommand>(
            OperationIds.AlterDelete, Guid.NewGuid(),
            PrincipalId: principal,
            IdempotencyKey: GetIdempotencyKey(req?.IdempotencyKey),
            ExpectedVersion: req?.ExpectedVersion,
            OccurredAt: DateTimeOffset.UtcNow,
            Payload: new DeleteAlterCommand(alterId)
        );
        var result = ToHttpResult(await _deleteHandler.HandleAsync(envelope, ct));
        return result is OkObjectResult ? NoContent() : result;
    }

    [HttpPut("{id}/avatar")]
    public async Task<IActionResult> UploadAvatar(string id, [FromBody] AlterAvatarRequest req, CancellationToken ct)
    {
        var principal = GetPrincipalId();
        if (principal is null) return Unauthorized();

        if (!TryParseAlterId(id, out var alterId))
            return BadRequest(new { error = "Invalid alter ID.", code = "invalid_alter_id" });

        var payload = new UpdateAlterCommand(
            AlterId: alterId,
            Name: null,
            Description: null,
            AvatarUrl: req.AvatarUrl,
            Color: null,
            Pronouns: null,
            SecurityLevel: null,
            Fields: null,
            ProxyName: null,
            Alias: null,
            Untracked: null,
            Archived: null,
            Pinned: null
        );

        var envelope = new CommandEnvelope<UpdateAlterCommand>(
            OperationIds.AlterAvatarUpload,
            Guid.NewGuid(),
            PrincipalId: principal,
            IdempotencyKey: GetIdempotencyKey(req.IdempotencyKey),
            ExpectedVersion: req.ExpectedVersion,
            OccurredAt: DateTimeOffset.UtcNow,
            Payload: payload
        );

        var result = ToHttpResult(await _updateHandler.HandleAsync(envelope, ct));
        return result is OkObjectResult ? NoContent() : result;
    }

    [HttpPut("{id}/avatar")]
    [Consumes("multipart/form-data")]
    public async Task<IActionResult> UploadAvatarMultipart(string id, [FromForm] AlterAvatarMultipartRequest req, CancellationToken ct)
    {
        var principal = GetPrincipalId();
        if (principal is null) return Unauthorized();

        if (!TryParseAlterId(id, out var alterId))
            return BadRequest(new { error = "Invalid alter ID.", code = "invalid_alter_id" });

        if (req.File is null || req.File.Length <= 0)
            return BadRequest(new { error = "No avatar file provided.", code = "avatar_file_required" });

        string avatarUrl;
        try
        {
            avatarUrl = await _avatarStorage.SaveAlterAvatarAsync(principal, alterId, req.File, ct);
        }
        catch
        {
            return StatusCode(500, new { error = "An error occurred while uploading the file.", code = "unknown_error" });
        }

        var payload = new UpdateAlterCommand(
            AlterId: alterId,
            Name: null,
            Description: null,
            AvatarUrl: avatarUrl,
            Color: null,
            Pronouns: null,
            SecurityLevel: null,
            Fields: null,
            ProxyName: null,
            Alias: null,
            Untracked: null,
            Archived: null,
            Pinned: null
        );

        var envelope = new CommandEnvelope<UpdateAlterCommand>(
            OperationIds.AlterAvatarUpload,
            Guid.NewGuid(),
            PrincipalId: principal,
            IdempotencyKey: GetIdempotencyKey(req.IdempotencyKey),
            ExpectedVersion: req.ExpectedVersion,
            OccurredAt: DateTimeOffset.UtcNow,
            Payload: payload
        );

        var result = ToHttpResult(await _updateHandler.HandleAsync(envelope, ct));
        return result is OkObjectResult ? NoContent() : result;
    }

    [HttpDelete("{id}/avatar")]
    public async Task<IActionResult> DeleteAvatar(string id, [FromBody] DeleteAlterRequest? req, CancellationToken ct)
    {
        var principal = GetPrincipalId();
        if (principal is null) return Unauthorized();

        if (!TryParseAlterId(id, out var alterId))
            return BadRequest(new { error = "Invalid alter ID.", code = "invalid_alter_id" });

        var payload = new UpdateAlterCommand(
            AlterId: alterId,
            Name: null,
            Description: null,
            AvatarUrl: null,
            Color: null,
            Pronouns: null,
            SecurityLevel: null,
            Fields: null,
            ProxyName: null,
            Alias: null,
            Untracked: null,
            Archived: null,
            Pinned: null
        );

        var envelope = new CommandEnvelope<UpdateAlterCommand>(
            OperationIds.AlterAvatarDelete,
            Guid.NewGuid(),
            PrincipalId: principal,
            IdempotencyKey: GetIdempotencyKey(req?.IdempotencyKey),
            ExpectedVersion: req?.ExpectedVersion,
            OccurredAt: DateTimeOffset.UtcNow,
            Payload: payload
        );

        var result = ToHttpResult(await _updateHandler.HandleAsync(envelope, ct));
        return result is OkObjectResult ? NoContent() : result;
    }

    private static bool TryParseAlterId(string value, out int alterId)
        => int.TryParse(value, out alterId) && alterId > 0;
}

public sealed record CreateAlterRequest(
    string Name,
    string? IdempotencyKey = null,
    long? ExpectedVersion = null
);

public sealed record UpdateAlterRequest(
    string? Name = null,
    string? Description = null,
    string? AvatarUrl = null,
    string? Color = null,
    string? Pronouns = null,
    string? SecurityLevel = null,
    string? ProxyName = null,
    string? Alias = null,
    bool? Untracked = null,
    bool? Archived = null,
    bool? Pinned = null,
    IReadOnlyList<UpdateAlterFieldRequest>? Fields = null,
    string? IdempotencyKey = null,
    long? ExpectedVersion = null
);

public sealed record UpdateAlterFieldRequest(
    string Id,
    string? Value
);

public sealed record DeleteAlterRequest(
    string? IdempotencyKey = null,
    long? ExpectedVersion = null
);

public sealed record AlterAvatarRequest(
    string AvatarUrl,
    string? IdempotencyKey = null,
    long? ExpectedVersion = null
);

public sealed record AlterAvatarMultipartRequest(
    IFormFile? File,
    string? IdempotencyKey = null,
    long? ExpectedVersion = null
);
