using Interfold.Api.Models;
using Interfold.Contracts.Models.Commands;
using Interfold.Contracts.Models.Read;
using Microsoft.AspNetCore.Mvc;
using Interfold.Contracts.Operations;
using Interfold.Domain.Abstractions.Repository;
using Interfold.Domain.Friendships;
using Interfold.Api.Controllers.Base;

namespace Interfold.Api.Controllers;

[Route("api/friend-requests")]
public sealed class FriendRequestsController : InterfoldControllerBase
{
    private readonly IFriendshipRepository _repository;
    private readonly SendFriendRequestCommandHandler _send;
    private readonly AcceptFriendRequestCommandHandler _accept;
    private readonly RejectFriendRequestCommandHandler _reject;
    private readonly CancelFriendRequestCommandHandler _cancel;

    public FriendRequestsController(
        IFriendshipRepository repository,
        SendFriendRequestCommandHandler send,
        AcceptFriendRequestCommandHandler accept,
        RejectFriendRequestCommandHandler reject,
        CancelFriendRequestCommandHandler cancel)
    {
        _repository = repository;
        _send = send;
        _accept = accept;
        _reject = reject;
        _cancel = cancel;
    }

    [HttpGet]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var requests = await _repository.GetFriendRequestsAsync(PrincipalId, ct);
        var incoming = requests.Incoming
            .Select(x => x with
            {
                System = x.System with { AvatarUrl = QualifyAvatar(x.System.AvatarUrl, x.System.AvatarSource) }
            })
            .ToArray();
        var outgoing = requests.Outgoing
            .Select(x => x with
            {
                System = x.System with { AvatarUrl = QualifyAvatar(x.System.AvatarUrl, x.System.AvatarSource) }
            })
            .ToArray();

        return Ok(new
        {
            data = new
            {
                incoming,
                outgoing
            }
        });
    }

    [HttpPut("{id}")]
    public async Task<Response> Send(string id, [FromBody] BaseRequest? req, CancellationToken ct)
    {
        var principal = PrincipalId;
        if (string.Equals(principal, id, StringComparison.Ordinal))
        {
            return new ErrorResponse(
                "You cannot send a friend request to yourself.",
                "cannot_send_self",
                System.Net.HttpStatusCode.BadRequest);
        }

        var envelope = new CommandEnvelope<SendFriendRequestCommand>(
            OperationIds.FriendRequestSend,
            Guid.NewGuid(),
            PrincipalId: principal,
            IdempotencyKey: GetIdempotencyKey(req?.IdempotencyKey),
            OccurredAt: DateTimeOffset.UtcNow,
            Payload: new SendFriendRequestCommand(id));

        return CommandNoContent(await _send.HandleAsync(envelope, ct));
    }

    [HttpDelete("{id}")]
    public async Task<Response> Cancel(string id, [FromBody] BaseRequest? req, CancellationToken ct)
    {
        var principal = PrincipalId;
        if (string.Equals(principal, id, StringComparison.Ordinal))
        {
            return new ErrorResponse(
                "You cannot cancel a friend request to yourself.",
                "cannot_cancel_self",
                System.Net.HttpStatusCode.BadRequest);
        }

        var envelope = new CommandEnvelope<CancelFriendRequestCommand>(
            OperationIds.FriendRequestCancel,
            Guid.NewGuid(),
            PrincipalId: principal,
            IdempotencyKey: GetIdempotencyKey(req?.IdempotencyKey),
            OccurredAt: DateTimeOffset.UtcNow,
            Payload: new CancelFriendRequestCommand(id));

        return CommandNoContent(await _cancel.HandleAsync(envelope, ct));
    }

    [HttpPost("{id}/accept")]
    public async Task<Response> Accept(string id, [FromBody] BaseRequest? req, CancellationToken ct)
    {
        var principal = PrincipalId;
        if (string.Equals(principal, id, StringComparison.Ordinal))
        {
            return new ErrorResponse(
                "You cannot accept a friend request from yourself.",
                "cannot_accept_self",
                System.Net.HttpStatusCode.BadRequest);
        }

        var envelope = new CommandEnvelope<AcceptFriendRequestCommand>(
            OperationIds.FriendRequestAccept,
            Guid.NewGuid(),
            PrincipalId: principal,
            IdempotencyKey: GetIdempotencyKey(req?.IdempotencyKey),
            OccurredAt: DateTimeOffset.UtcNow,
            Payload: new AcceptFriendRequestCommand(id));

        return CommandNoContent(await _accept.HandleAsync(envelope, ct));
    }

    [HttpPost("{id}/reject")]
    public async Task<Response> Reject(string id, [FromBody] BaseRequest? req, CancellationToken ct)
    {
        var principal = PrincipalId;
        if (string.Equals(principal, id, StringComparison.Ordinal))
        {
            return new ErrorResponse(
                "You cannot reject a friend request from yourself.",
                "cannot_reject_self",
                System.Net.HttpStatusCode.BadRequest);
        }

        var envelope = new CommandEnvelope<RejectFriendRequestCommand>(
            OperationIds.FriendRequestReject,
            Guid.NewGuid(),
            PrincipalId: principal,
            IdempotencyKey: GetIdempotencyKey(req?.IdempotencyKey),
            OccurredAt: DateTimeOffset.UtcNow,
            Payload: new RejectFriendRequestCommand(id));

        return CommandNoContent(await _reject.HandleAsync(envelope, ct));
    }
}
