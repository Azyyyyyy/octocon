using Interfold.Api.Models;
using Interfold.Contracts.Models.Commands;
using Interfold.Contracts.Models.Read;
using Microsoft.AspNetCore.Mvc;
using Interfold.Contracts.Operations;
using Interfold.Domain.Abstractions.Repository;
using Interfold.Domain.Friendships;

namespace Interfold.Api.Controllers;

[Route("api/friends")]
public sealed class FriendsController : InterfoldControllerBase
{
    private readonly IFriendshipRepository _repository;
    private readonly RemoveFriendshipCommandHandler _remove;
    private readonly SetFriendTrustCommandHandler _setTrust;

    public FriendsController(
        IFriendshipRepository repository,
        RemoveFriendshipCommandHandler remove,
        SetFriendTrustCommandHandler setTrust)
    {
        _repository = repository;
        _remove = remove;
        _setTrust = setTrust;
    }

    [HttpGet]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var friendships = await _repository.ListFriendshipsAsync(PrincipalId, ct);
        var qualified = friendships.Select(QualifyFriendship).ToArray();
        return Ok(new { data = qualified });
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> Show(string id, CancellationToken ct)
    {
        var principal = PrincipalId;
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
            : Ok(new { data = QualifyFriendship(friendship) });
    }

    private FriendshipReadModel QualifyFriendship(FriendshipReadModel friendship)
    {
        return friendship with
        {
            Friend = friendship.Friend with { AvatarUrl = QualifyUrl(friendship.Friend.AvatarUrl) },
            Fronting = friendship.Fronting
                .Select(f => f with { Alter = f.Alter with { AvatarUrl = QualifyUrl(f.Alter.AvatarUrl) } })
                .ToArray()
        };
    }

    [HttpDelete("{id}")]
    public async Task<Response> Delete(string id, [FromBody] BaseRequest? req, CancellationToken ct)
    {
        var principal = PrincipalId;
        if (string.Equals(principal, id, StringComparison.Ordinal))
        {
            return new ErrorResponse(
                "I'm pretty sure you don't count as your own friend. (Cannot delete friendship with self.)",
                "cannot_delete_own_friendship",
                System.Net.HttpStatusCode.BadRequest);
        }

        var envelope = new CommandEnvelope<RemoveFriendshipCommand>(
            OperationIds.FriendDelete,
            Guid.NewGuid(),
            PrincipalId: principal,
            IdempotencyKey: GetIdempotencyKey(req?.IdempotencyKey),
            OccurredAt: DateTimeOffset.UtcNow,
            Payload: new RemoveFriendshipCommand(id));

        return CommandNoContent(await _remove.HandleAsync(envelope, ct));
    }

    [HttpPost("{id}/trust")]
    public async Task<Response> Trust(string id, [FromBody] BaseRequest? req, CancellationToken ct)
        => await SetTrustInternal(id, true, OperationIds.FriendTrust, "cannot_trust_self", req, ct);

    [HttpPost("{id}/untrust")]
    public async Task<Response> Untrust(string id, [FromBody] BaseRequest? req, CancellationToken ct)
        => await SetTrustInternal(id, false, OperationIds.FriendUntrust, "cannot_untrust_self", req, ct);

    private async Task<Response> SetTrustInternal(
        string id,
        bool trusted,
        string operationId,
        string selfErrorCode,
        BaseRequest? req,
        CancellationToken ct)
    {
        var principal = PrincipalId;
        if (string.Equals(principal, id, StringComparison.Ordinal))
        {
            return new ErrorResponse("Cannot trust self.", selfErrorCode, System.Net.HttpStatusCode.BadRequest);
        }

        var envelope = new CommandEnvelope<SetFriendTrustCommand>(
            operationId,
            Guid.NewGuid(),
            PrincipalId: principal,
            IdempotencyKey: GetIdempotencyKey(req?.IdempotencyKey),
            OccurredAt: DateTimeOffset.UtcNow,
            Payload: new SetFriendTrustCommand(id, trusted));

        return CommandNoContent(await _setTrust.HandleAsync(envelope, ct));
    }
}