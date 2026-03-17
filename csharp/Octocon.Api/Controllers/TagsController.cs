using Microsoft.AspNetCore.Mvc;
using Octocon.Contracts.Operations;
using Octocon.Domain.Tags;

namespace Octocon.Api.Controllers;

[Route("api/systems/me/tags")]
public sealed class TagsController : OctoconControllerBase
{
   private readonly CreateTagCommandHandler _create;
   private readonly UpdateTagCommandHandler _update;
   private readonly DeleteTagCommandHandler _delete;
   private readonly AttachAlterToTagCommandHandler _attachAlter;
   private readonly DetachAlterFromTagCommandHandler _detachAlter;
   private readonly SetParentTagCommandHandler _setParent;
   private readonly RemoveParentTagCommandHandler _removeParent;

   public TagsController(
       ApiSettings settings,
       CreateTagCommandHandler create,
       UpdateTagCommandHandler update,
       DeleteTagCommandHandler delete,
       AttachAlterToTagCommandHandler attachAlter,
       DetachAlterFromTagCommandHandler detachAlter,
       SetParentTagCommandHandler setParent,
       RemoveParentTagCommandHandler removeParent)
        : base(settings)
    {
       _create = create;
       _update = update;
       _delete = delete;
       _attachAlter = attachAlter;
       _detachAlter = detachAlter;
       _setParent = setParent;
       _removeParent = removeParent;
    }

    [HttpPost]
    public async Task<IActionResult> CreateTag(
        [FromBody] CreateTagRequest body,
        CancellationToken cancellationToken
    )
    {
        var principal = GetPrincipalId();
        if (principal is null) return Unauthorized();

        var command = new CommandEnvelope<CreateTagCommand>(
            OperationId: OperationIds.TagCreate,
            CommandId: Guid.NewGuid(),
            PrincipalId: principal,
            IdempotencyKey: GetIdempotencyKey(body.IdempotencyKey),
            ExpectedVersion: body.ExpectedVersion,
            OccurredAt: DateTimeOffset.UtcNow,
            Payload: new CreateTagCommand(body.Name, body.ParentTagId)
        );

       return ToHttpResult(await _create.HandleAsync(command, cancellationToken));
    }

   [HttpPatch("{id}")]
   public async Task<IActionResult> UpdateTag(string id, [FromBody] UpdateTagRequest body, CancellationToken ct)
   {
       var principal = GetPrincipalId();
       if (principal is null) return Unauthorized();

       var command = new CommandEnvelope<UpdateTagCommand>(
           OperationId: OperationIds.TagUpdate,
           CommandId: Guid.NewGuid(),
           PrincipalId: principal,
           IdempotencyKey: GetIdempotencyKey(body.IdempotencyKey),
           ExpectedVersion: body.ExpectedVersion,
           OccurredAt: DateTimeOffset.UtcNow,
           Payload: new UpdateTagCommand(id, body.Name, body.Color, body.Description, body.SecurityLevel)
       );

       return ToHttpResult(await _update.HandleAsync(command, ct));
   }

   [HttpDelete("{id}")]
   public async Task<IActionResult> DeleteTag(string id, [FromBody] TagIdempotencyRequest? body, CancellationToken ct)
   {
       var principal = GetPrincipalId();
       if (principal is null) return Unauthorized();

       var command = new CommandEnvelope<DeleteTagCommand>(
           OperationId: OperationIds.TagDelete,
           CommandId: Guid.NewGuid(),
           PrincipalId: principal,
           IdempotencyKey: GetIdempotencyKey(body?.IdempotencyKey),
           ExpectedVersion: body?.ExpectedVersion,
           OccurredAt: DateTimeOffset.UtcNow,
           Payload: new DeleteTagCommand(id)
       );

       return ToHttpResult(await _delete.HandleAsync(command, ct));
   }

   [HttpPost("{id}/alter")]
   public async Task<IActionResult> AttachAlter(string id, [FromBody] TagAlterRequest body, CancellationToken ct)
   {
       var principal = GetPrincipalId();
       if (principal is null) return Unauthorized();

       var command = new CommandEnvelope<AttachAlterToTagCommand>(
           OperationId: OperationIds.TagAttachAlter,
           CommandId: Guid.NewGuid(),
           PrincipalId: principal,
           IdempotencyKey: GetIdempotencyKey(body.IdempotencyKey),
           ExpectedVersion: body.ExpectedVersion,
           OccurredAt: DateTimeOffset.UtcNow,
           Payload: new AttachAlterToTagCommand(id, body.AlterId)
       );

       return ToHttpResult(await _attachAlter.HandleAsync(command, ct));
   }

   [HttpDelete("{id}/alter")]
   public async Task<IActionResult> DetachAlter(string id, [FromBody] TagAlterRequest body, CancellationToken ct)
   {
       var principal = GetPrincipalId();
       if (principal is null) return Unauthorized();

       var command = new CommandEnvelope<DetachAlterFromTagCommand>(
           OperationId: OperationIds.TagDetachAlter,
           CommandId: Guid.NewGuid(),
           PrincipalId: principal,
           IdempotencyKey: GetIdempotencyKey(body.IdempotencyKey),
           ExpectedVersion: body.ExpectedVersion,
           OccurredAt: DateTimeOffset.UtcNow,
           Payload: new DetachAlterFromTagCommand(id, body.AlterId)
       );

       return ToHttpResult(await _detachAlter.HandleAsync(command, ct));
   }

   [HttpPost("{id}/parent")]
   public async Task<IActionResult> SetParent(string id, [FromBody] SetParentRequest body, CancellationToken ct)
   {
       var principal = GetPrincipalId();
       if (principal is null) return Unauthorized();

       var command = new CommandEnvelope<SetParentTagCommand>(
           OperationId: OperationIds.TagSetParent,
           CommandId: Guid.NewGuid(),
           PrincipalId: principal,
           IdempotencyKey: GetIdempotencyKey(body.IdempotencyKey),
           ExpectedVersion: body.ExpectedVersion,
           OccurredAt: DateTimeOffset.UtcNow,
           Payload: new SetParentTagCommand(id, body.ParentTagId)
       );

       return ToHttpResult(await _setParent.HandleAsync(command, ct));
   }

   [HttpDelete("{id}/parent")]
   public async Task<IActionResult> RemoveParent(string id, [FromBody] TagIdempotencyRequest body, CancellationToken ct)
   {
       var principal = GetPrincipalId();
       if (principal is null) return Unauthorized();

       var command = new CommandEnvelope<RemoveParentTagCommand>(
           OperationId: OperationIds.TagRemoveParent,
           CommandId: Guid.NewGuid(),
           PrincipalId: principal,
           IdempotencyKey: GetIdempotencyKey(body.IdempotencyKey),
           ExpectedVersion: body.ExpectedVersion,
           OccurredAt: DateTimeOffset.UtcNow,
           Payload: new RemoveParentTagCommand(id)
       );

       return ToHttpResult(await _removeParent.HandleAsync(command, ct));
   }
}

public sealed record CreateTagRequest(
    string Name,
    string? ParentTagId,
    string? IdempotencyKey,
    long? ExpectedVersion
);

public sealed record UpdateTagRequest(
    string? Name = null,
    string? Color = null,
    string? Description = null,
    string? SecurityLevel = null,
    string? IdempotencyKey = null,
    long? ExpectedVersion = null
);

public sealed record TagIdempotencyRequest(
    string? IdempotencyKey = null,
    long? ExpectedVersion = null
);

public sealed record TagAlterRequest(
    int AlterId,
    string? IdempotencyKey = null,
    long? ExpectedVersion = null
);

public sealed record SetParentRequest(
    string ParentTagId,
    string? IdempotencyKey = null,
    long? ExpectedVersion = null
);
