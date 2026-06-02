using Interfold.Api.Models;
using Interfold.Contracts.Models.Commands;
using Interfold.Contracts.Models.Read;
using Microsoft.AspNetCore.Mvc;
using Interfold.Contracts.Operations;
using Interfold.Domain.Abstractions.Repository;
using Interfold.Domain.Tags;
using Interfold.Api.Controllers.Base;

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
    public async Task<Response<TagReadModel>> CreateTag(
        [FromBody] CreateTagRequest body,
        CancellationToken cancellationToken)
    {
        var principal = PrincipalId;
        var command = new CommandEnvelope<CreateTagCommand>(
            OperationId: OperationIds.TagCreate,
            CommandId: Guid.NewGuid(),
            PrincipalId: principal,
            IdempotencyKey: GetIdempotencyKey(body.IdempotencyKey),
            OccurredAt: DateTimeOffset.UtcNow,
            Payload: new CreateTagCommand(body.Name, body.ParentTagId)
        );

        var execution = await _create.HandleAsync(command, cancellationToken);
        if (!execution.Accepted)
        {
            return ConflictToError(execution.Conflict!);
        }

        var tag = await _tagRepository.GetAsync(principal, execution.Result!.TagId, cancellationToken);
        if (tag is null)
            return new ErrorResponse("An unknown error occurred.", "unknown_error", System.Net.HttpStatusCode.InternalServerError);

        return new SuccessResponse<TagReadModel>(tag, System.Net.HttpStatusCode.Created, execution.Result.Replay);
    }

    [HttpPatch("{id}")]
    public async Task<Response> UpdateTag(string id, [FromBody] UpdateTagRequest body, CancellationToken ct)
    {
        var command = new CommandEnvelope<UpdateTagCommand>(
            OperationId: OperationIds.TagUpdate,
            CommandId: Guid.NewGuid(),
            PrincipalId: PrincipalId,
            IdempotencyKey: GetIdempotencyKey(body.IdempotencyKey),
            OccurredAt: DateTimeOffset.UtcNow,
            Payload: new UpdateTagCommand(id, body.Name, body.Color, body.Description, body.SecurityLevel)
        );

        return CommandNoContent(await _update.HandleAsync(command, ct));
    }

    //TODO: To ensure route works as expected - check if we unattach alters and remove parent tag relationships when a tag is deleted
    [HttpDelete("{id}")]
    public async Task<Response> DeleteTag(string id, [FromBody] BaseRequest? body, CancellationToken ct)
    {
        var command = new CommandEnvelope<DeleteTagCommand>(
            OperationId: OperationIds.TagDelete,
            CommandId: Guid.NewGuid(),
            PrincipalId: PrincipalId,
            IdempotencyKey: GetIdempotencyKey(body?.IdempotencyKey),
            OccurredAt: DateTimeOffset.UtcNow,
            Payload: new DeleteTagCommand(id)
        );

        return CommandNoContent(await _delete.HandleAsync(command, ct));
    }

    [HttpPost("{id}/alter")]
    public async Task<Response> AttachAlter(string id, [FromBody] TagAlterRequest body, CancellationToken ct)
    {
        var alterId = body.AlterId ?? 0;
        await CheckAlterId(alterId);

        var command = new CommandEnvelope<AttachAlterToTagCommand>(
            OperationId: OperationIds.TagAttachAlter,
            CommandId: Guid.NewGuid(),
            PrincipalId: PrincipalId,
            IdempotencyKey: GetIdempotencyKey(body.IdempotencyKey),
            OccurredAt: DateTimeOffset.UtcNow,
            Payload: new AttachAlterToTagCommand(id, alterId)
        );

        return CommandNoContent(await _attachAlter.HandleAsync(command, ct));
    }

    [HttpDelete("{id}/alter")]
    public async Task<Response> DetachAlter(string id, [FromBody] TagAlterRequest body, CancellationToken ct)
    {
        var alterId = body.AlterId ?? 0;
        await CheckAlterId(alterId);

        var command = new CommandEnvelope<DetachAlterFromTagCommand>(
            OperationId: OperationIds.TagDetachAlter,
            CommandId: Guid.NewGuid(),
            PrincipalId: PrincipalId,
            IdempotencyKey: GetIdempotencyKey(body.IdempotencyKey),
            OccurredAt: DateTimeOffset.UtcNow,
            Payload: new DetachAlterFromTagCommand(id, alterId)
        );

        return CommandNoContent(await _detachAlter.HandleAsync(command, ct));
    }

    [HttpPost("{id}/parent")]
    public async Task<Response> SetParent(string id, [FromBody] SetParentRequest body, CancellationToken ct)
    {
        var parentTagId = body.ParentTagId;
        if (string.IsNullOrWhiteSpace(parentTagId))
            return new ErrorResponse("Invalid parent tag ID.", "invalid_parent_tag_id", System.Net.HttpStatusCode.BadRequest);

        var command = new CommandEnvelope<SetParentTagCommand>(
            OperationId: OperationIds.TagSetParent,
            CommandId: Guid.NewGuid(),
            PrincipalId: PrincipalId,
            IdempotencyKey: GetIdempotencyKey(body.IdempotencyKey),
            OccurredAt: DateTimeOffset.UtcNow,
            Payload: new SetParentTagCommand(id, parentTagId)
        );

        return CommandNoContent(await _setParent.HandleAsync(command, ct));
    }

    [HttpDelete("{id}/parent")]
    public async Task<Response> RemoveParent(string id, [FromBody] BaseRequest? body, CancellationToken ct)
    {
        var command = new CommandEnvelope<RemoveParentTagCommand>(
            OperationId: OperationIds.TagRemoveParent,
            CommandId: Guid.NewGuid(),
            PrincipalId: PrincipalId,
            IdempotencyKey: GetIdempotencyKey(body?.IdempotencyKey),
            OccurredAt: DateTimeOffset.UtcNow,
            Payload: new RemoveParentTagCommand(id)
        );

        return CommandNoContent(await _removeParent.HandleAsync(command, ct));
    }
}