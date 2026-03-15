using Microsoft.AspNetCore.Mvc;
using Octocon.Contracts.Operations;
using Octocon.Domain.Alters;

namespace Octocon.Api.Controllers;

[Route("api/systems/me/alters")]
public sealed class AltersController : OctoconControllerBase
{
    private readonly CreateAlterCommandHandler _createHandler;
    private readonly UpdateAlterCommandHandler _updateHandler;
    private readonly DeleteAlterCommandHandler _deleteHandler;

    public AltersController(
        ApiSettings settings,
        CreateAlterCommandHandler createHandler,
        UpdateAlterCommandHandler updateHandler,
        DeleteAlterCommandHandler deleteHandler)
        : base(settings)
    {
        _createHandler = createHandler;
        _updateHandler = updateHandler;
        _deleteHandler = deleteHandler;
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
        return ToHttpResult(await _createHandler.HandleAsync(envelope, ct));
    }

    [HttpPatch("{id:int}")]
    public async Task<IActionResult> Update(int id, [FromBody] UpdateAlterRequest req, CancellationToken ct)
    {
        var principal = GetPrincipalId();
        if (principal is null) return Unauthorized();

        var payload = new UpdateAlterCommand(
            AlterId: id,
            Name: req.Name,
            Description: req.Description,
            AvatarUrl: req.AvatarUrl,
            Color: req.Color,
            Pronouns: req.Pronouns,
            SecurityLevel: req.SecurityLevel,
            Fields: null,
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
        return ToHttpResult(await _updateHandler.HandleAsync(envelope, ct));
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id, [FromBody] DeleteAlterRequest? req, CancellationToken ct)
    {
        var principal = GetPrincipalId();
        if (principal is null) return Unauthorized();

        var envelope = new CommandEnvelope<DeleteAlterCommand>(
            OperationIds.AlterDelete, Guid.NewGuid(),
            PrincipalId: principal,
            IdempotencyKey: GetIdempotencyKey(req?.IdempotencyKey),
            ExpectedVersion: req?.ExpectedVersion,
            OccurredAt: DateTimeOffset.UtcNow,
            Payload: new DeleteAlterCommand(id)
        );
        return ToHttpResult(await _deleteHandler.HandleAsync(envelope, ct));
    }

    [HttpPut("{id:int}/avatar")]
    public async Task<IActionResult> UploadAvatar(int id, [FromBody] AlterAvatarRequest req, CancellationToken ct)
    {
        var principal = GetPrincipalId();
        if (principal is null) return Unauthorized();

        var payload = new UpdateAlterCommand(
            AlterId: id,
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

        return ToHttpResult(await _updateHandler.HandleAsync(envelope, ct));
    }

    [HttpDelete("{id:int}/avatar")]
    public async Task<IActionResult> DeleteAvatar(int id, [FromBody] DeleteAlterRequest? req, CancellationToken ct)
    {
        var principal = GetPrincipalId();
        if (principal is null) return Unauthorized();

        var payload = new UpdateAlterCommand(
            AlterId: id,
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

        return ToHttpResult(await _updateHandler.HandleAsync(envelope, ct));
    }
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
    string? IdempotencyKey = null,
    long? ExpectedVersion = null
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
