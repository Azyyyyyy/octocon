using Microsoft.AspNetCore.Mvc;
using Interfold.Contracts.Operations;
using Interfold.Domain.Friendships;

namespace Interfold.Api.Controllers;

//TODO: To ensure route works as expected
[Route("api/friends")]
public sealed class FriendsController : InterfoldControllerBase
{
    private readonly IFriendshipRepository _repository;
    private readonly RemoveFriendshipCommandHandler _remove;
    private readonly SetFriendTrustCommandHandler _setTrust;

    public FriendsController(
        ApiSettings settings,
        IFriendshipRepository repository,
        RemoveFriendshipCommandHandler remove,
        SetFriendTrustCommandHandler setTrust)
        : base(settings)
    {
        _repository = repository;
        _remove = remove;
        _setTrust = setTrust;
    }

    [HttpGet]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var principal = GetPrincipalId();
        if (principal is null) return Unauthorized();

        var friendships = await _repository.ListFriendshipsAsync(principal, ct);
        return Ok(new { data = friendships });
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> Show(string id, CancellationToken ct)
    {
        var principal = GetPrincipalId();
        if (principal is null) return Unauthorized();

        if (string.Equals(principal, id, StringComparison.Ordinal))
        {
            return BadRequest(new
            {
                error = "I'm pretty sure you don't count as your own friend. (Cannot view friendship status for self.)",
                code = "cannot_view_own_friendship"
            });
        }

        var friendship = await _repository.GetFriendshipAsync(principal, id, ct);
        return friendship is null
            ? NotFound(new { error = "You are not friends with that system.", code = "friendship_not_found" })
            : Ok(new { data = friendship });
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(string id, [FromBody] FriendActionRequest? req, CancellationToken ct)
    {
        var principal = GetPrincipalId();
        if (principal is null) return Unauthorized();

        if (string.Equals(principal, id, StringComparison.Ordinal))
        {
            return BadRequest(new
            {
                error = "I'm pretty sure you don't count as your own friend. (Cannot delete friendship with self.)",
                code = "cannot_delete_own_friendship"
            });
        }

        var envelope = new CommandEnvelope<RemoveFriendshipCommand>(
            OperationIds.FriendDelete,
            Guid.NewGuid(),
            PrincipalId: principal,
            IdempotencyKey: GetIdempotencyKey(req?.IdempotencyKey),
            ExpectedVersion: req?.ExpectedVersion,
            OccurredAt: DateTimeOffset.UtcNow,
            Payload: new RemoveFriendshipCommand(id));

        var result = ToHttpResult(await _remove.HandleAsync(envelope, ct));
        return result is OkObjectResult ? NoContent() : result;
    }

    [HttpPost("{id}/trust")]
    public async Task<IActionResult> Trust(string id, [FromBody] FriendActionRequest? req, CancellationToken ct)
        => await SetTrustInternal(id, true, OperationIds.FriendTrust, "cannot_trust_self", req, ct);

    [HttpPost("{id}/untrust")]
    public async Task<IActionResult> Untrust(string id, [FromBody] FriendActionRequest? req, CancellationToken ct)
        => await SetTrustInternal(id, false, OperationIds.FriendUntrust, "cannot_untrust_self", req, ct);

    private async Task<IActionResult> SetTrustInternal(
        string id,
        bool trusted,
        string operationId,
        string selfErrorCode,
        FriendActionRequest? req,
        CancellationToken ct)
    {
        var principal = GetPrincipalId();
        if (principal is null) return Unauthorized();

        if (string.Equals(principal, id, StringComparison.Ordinal))
        {
            return BadRequest(new { code = selfErrorCode });
        }

        var envelope = new CommandEnvelope<SetFriendTrustCommand>(
            operationId,
            Guid.NewGuid(),
            PrincipalId: principal,
            IdempotencyKey: GetIdempotencyKey(req?.IdempotencyKey),
            ExpectedVersion: req?.ExpectedVersion,
            OccurredAt: DateTimeOffset.UtcNow,
            Payload: new SetFriendTrustCommand(id, trusted));

        var result = ToHttpResult(await _setTrust.HandleAsync(envelope, ct));
        return result is OkObjectResult ? NoContent() : result;
    }
}

public sealed record FriendActionRequest(
    string? IdempotencyKey = null,
    long? ExpectedVersion = null);
