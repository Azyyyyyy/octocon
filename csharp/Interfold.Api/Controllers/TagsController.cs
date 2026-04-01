using Microsoft.AspNetCore.Mvc;
using Interfold.Contracts.Operations;
using Interfold.Domain.Tags;

namespace Interfold.Api.Controllers;

[Route("api/systems/me/tags")]
public sealed class TagsController : InterfoldControllerBase
{
    private readonly ITagRepository _tagRepository;
   private readonly CreateTagCommandHandler _create;
   private readonly UpdateTagCommandHandler _update;
   private readonly DeleteTagCommandHandler _delete;
   private readonly AttachAlterToTagCommandHandler _attachAlter;
   private readonly DetachAlterFromTagCommandHandler _detachAlter;
   private readonly SetParentTagCommandHandler _setParent;
   private readonly RemoveParentTagCommandHandler _removeParent;

   public TagsController(
        ITagRepository tagRepository,
       CreateTagCommandHandler create,
       UpdateTagCommandHandler update,
       DeleteTagCommandHandler delete,
       AttachAlterToTagCommandHandler attachAlter,
       DetachAlterFromTagCommandHandler detachAlter,
       SetParentTagCommandHandler setParent,
       RemoveParentTagCommandHandler removeParent)
    {
       _tagRepository = tagRepository;
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

       var execution = await _create.HandleAsync(command, cancellationToken);
       if (!execution.Accepted)
       {
           return ToHttpResult(execution);
       }

       var tag = await _tagRepository.GetAsync(principal, execution.Result!.TagId, cancellationToken);
       object data = tag is not null ? tag : execution.Result;
       return StatusCode(StatusCodes.Status201Created, new { data, replay = execution.Result.Replay });
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

    var result = ToHttpResult(await _update.HandleAsync(command, ct));
    return result is OkObjectResult ? NoContent() : result;
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

    var result = ToHttpResult(await _delete.HandleAsync(command, ct));
    return result is OkObjectResult ? NoContent() : result;
   }

   [HttpPost("{id}/alter")]
   public async Task<IActionResult> AttachAlter(string id, [FromBody] TagAlterRequest body, CancellationToken ct)
   {
       var principal = GetPrincipalId();
       if (principal is null) return Unauthorized();

       var alterId = body.AlterId ?? 0;
       if (alterId <= 0)
           return BadRequest(new { error = "Invalid alter ID.", code = "invalid_alter_id" });

       var command = new CommandEnvelope<AttachAlterToTagCommand>(
           OperationId: OperationIds.TagAttachAlter,
           CommandId: Guid.NewGuid(),
           PrincipalId: principal,
           IdempotencyKey: GetIdempotencyKey(body.IdempotencyKey),
           ExpectedVersion: body.ExpectedVersion,
           OccurredAt: DateTimeOffset.UtcNow,
           Payload: new AttachAlterToTagCommand(id, alterId)
       );

    var result = ToHttpResult(await _attachAlter.HandleAsync(command, ct));
    return result is OkObjectResult ? NoContent() : result;
   }

   [HttpDelete("{id}/alter")]
   public async Task<IActionResult> DetachAlter(string id, [FromBody] TagAlterRequest body, CancellationToken ct)
   {
       var principal = GetPrincipalId();
       if (principal is null) return Unauthorized();

       var alterId = body.AlterId ?? 0;
       if (alterId <= 0)
           return BadRequest(new { error = "Invalid alter ID.", code = "invalid_alter_id" });

       var command = new CommandEnvelope<DetachAlterFromTagCommand>(
           OperationId: OperationIds.TagDetachAlter,
           CommandId: Guid.NewGuid(),
           PrincipalId: principal,
           IdempotencyKey: GetIdempotencyKey(body.IdempotencyKey),
           ExpectedVersion: body.ExpectedVersion,
           OccurredAt: DateTimeOffset.UtcNow,
           Payload: new DetachAlterFromTagCommand(id, alterId)
       );

    var result = ToHttpResult(await _detachAlter.HandleAsync(command, ct));
    return result is OkObjectResult ? NoContent() : result;
   }

   [HttpPost("{id}/parent")]
   public async Task<IActionResult> SetParent(string id, [FromBody] SetParentRequest body, CancellationToken ct)
   {
       var principal = GetPrincipalId();
       if (principal is null) return Unauthorized();

       var parentTagId = body.ParentTagId;
       if (string.IsNullOrWhiteSpace(parentTagId))
           return BadRequest(new { error = "Invalid parent tag ID.", code = "invalid_parent_tag_id" });

       var command = new CommandEnvelope<SetParentTagCommand>(
           OperationId: OperationIds.TagSetParent,
           CommandId: Guid.NewGuid(),
           PrincipalId: principal,
           IdempotencyKey: GetIdempotencyKey(body.IdempotencyKey),
           ExpectedVersion: body.ExpectedVersion,
           OccurredAt: DateTimeOffset.UtcNow,
           Payload: new SetParentTagCommand(id, parentTagId)
       );

    var result = ToHttpResult(await _setParent.HandleAsync(command, ct));
    return result is OkObjectResult ? NoContent() : result;
   }

   [HttpDelete("{id}/parent")]
   public async Task<IActionResult> RemoveParent(string id, [FromBody] TagIdempotencyRequest? body, CancellationToken ct)
   {
       var principal = GetPrincipalId();
       if (principal is null) return Unauthorized();

       var command = new CommandEnvelope<RemoveParentTagCommand>(
           OperationId: OperationIds.TagRemoveParent,
           CommandId: Guid.NewGuid(),
           PrincipalId: principal,
           IdempotencyKey: GetIdempotencyKey(body?.IdempotencyKey),
           ExpectedVersion: body?.ExpectedVersion,
           OccurredAt: DateTimeOffset.UtcNow,
           Payload: new RemoveParentTagCommand(id)
       );

       var result = ToHttpResult(await _removeParent.HandleAsync(command, ct));
       return result is OkObjectResult ? NoContent() : result;
   }
}

public sealed record CreateTagRequest(
    string Name,
    string? ParentTagId,
    string? IdempotencyKey = null,
    long? ExpectedVersion = null
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
    int? AlterId,
    string? IdempotencyKey = null,
    long? ExpectedVersion = null
);

public sealed record SetParentRequest(
    string? ParentTagId,
    string? IdempotencyKey = null,
    long? ExpectedVersion = null
);
