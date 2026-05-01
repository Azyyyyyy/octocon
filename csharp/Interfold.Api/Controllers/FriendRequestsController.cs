using Interfold.Contracts.Models.Commands;
using Interfold.Contracts.Models.Read;
using Microsoft.AspNetCore.Mvc;
using Interfold.Contracts.Operations;
using Interfold.Domain.Abstractions.Repository;
using Interfold.Domain.Friendships;

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
                System = x.System with { AvatarUrl = QualifyUrl(x.System.AvatarUrl) }
            })
            .ToArray();
        var outgoing = requests.Outgoing
            .Select(x => x with
            {
                System = x.System with { AvatarUrl = QualifyUrl(x.System.AvatarUrl) }
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
    public async Task<IActionResult> Send(string id, [FromBody] BaseRequest? req, CancellationToken ct)
    {
        var principal = PrincipalId;
        if (string.Equals(principal, id, StringComparison.Ordinal))
        {
            return BadRequest(new
            {
                error = "You cannot send a friend request to yourself.",
                code = "cannot_send_self"
            });
        }

        var envelope = new CommandEnvelope<SendFriendRequestCommand>(
            OperationIds.FriendRequestSend,
            Guid.NewGuid(),
            PrincipalId: principal,
            IdempotencyKey: GetIdempotencyKey(req?.IdempotencyKey),
            OccurredAt: DateTimeOffset.UtcNow,
            Payload: new SendFriendRequestCommand(id));

        var result = ToHttpResult(await _send.HandleAsync(envelope, ct));
        return result is OkObjectResult ? NoContent() : result;
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Cancel(string id, [FromBody] BaseRequest? req, CancellationToken ct)
    {
        var principal = PrincipalId;
        if (string.Equals(principal, id, StringComparison.Ordinal))
        {
            return BadRequest(new
            {
                error = "You cannot cancel a friend request to yourself.",
                code = "cannot_cancel_self"
            });
        }

        var envelope = new CommandEnvelope<CancelFriendRequestCommand>(
            OperationIds.FriendRequestCancel,
            Guid.NewGuid(),
            PrincipalId: principal,
            IdempotencyKey: GetIdempotencyKey(req?.IdempotencyKey),
            OccurredAt: DateTimeOffset.UtcNow,
            Payload: new CancelFriendRequestCommand(id));

        var result = ToHttpResult(await _cancel.HandleAsync(envelope, ct));
        return result is OkObjectResult ? NoContent() : result;
    }

    [HttpPost("{id}/accept")]
    public async Task<IActionResult> Accept(string id, [FromBody] BaseRequest? req, CancellationToken ct)
    {
        var principal = PrincipalId;
        if (string.Equals(principal, id, StringComparison.Ordinal))
        {
            return BadRequest(new
            {
                error = "You cannot accept a friend request from yourself.",
                code = "cannot_accept_self"
            });
        }

        var envelope = new CommandEnvelope<AcceptFriendRequestCommand>(
            OperationIds.FriendRequestAccept,
            Guid.NewGuid(),
            PrincipalId: principal,
            IdempotencyKey: GetIdempotencyKey(req?.IdempotencyKey),
            OccurredAt: DateTimeOffset.UtcNow,
            Payload: new AcceptFriendRequestCommand(id));

        var result = ToHttpResult(await _accept.HandleAsync(envelope, ct));
        return result is OkObjectResult ? NoContent() : result;
    }

    [HttpPost("{id}/reject")]
    public async Task<IActionResult> Reject(string id, [FromBody] BaseRequest? req, CancellationToken ct)
    {
        var principal = PrincipalId;
        if (string.Equals(principal, id, StringComparison.Ordinal))
        {
            return BadRequest(new
            {
                error = "You cannot reject a friend request from yourself.",
                code = "cannot_reject_self"
            });
        }

        var envelope = new CommandEnvelope<RejectFriendRequestCommand>(
            OperationIds.FriendRequestReject,
            Guid.NewGuid(),
            PrincipalId: principal,
            IdempotencyKey: GetIdempotencyKey(req?.IdempotencyKey),
            OccurredAt: DateTimeOffset.UtcNow,
            Payload: new RejectFriendRequestCommand(id));

        var result = ToHttpResult(await _reject.HandleAsync(envelope, ct));
        return result is OkObjectResult ? NoContent() : result;
    }
}
