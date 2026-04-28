using Interfold.Domain.Abstractions.Repository;
using Microsoft.AspNetCore.Mvc;

namespace Interfold.Api.Controllers;

[Route("api/systems")]
public sealed class PublicSystemsController : InterfoldControllerBase
{
    private readonly IAccountRepository _accounts;
    private readonly IAlterRepository _alters;
    private readonly ITagRepository _tags;
    private readonly IFrontingRepository _fronting;
    private readonly IFriendshipRepository _friendships;

    public PublicSystemsController(
        IAccountRepository accounts,
        IAlterRepository alters,
        ITagRepository tags,
        IFrontingRepository fronting,
        IFriendshipRepository friendships)
    {
        _accounts = accounts;
        _alters = alters;
        _tags = tags;
        _fronting = fronting;
        _friendships = friendships;
    }

    [HttpGet("{systemId}")]
    public async Task<IActionResult> Show([FromRoute] string systemId, CancellationToken ct)
    {
        var profile = await _accounts.GetPublicProfileAsync(systemId, ct);
        if (profile is null)
        {
            return NotFound(new { error = "System not found.", code = "system_not_found" });
        }

        return Ok(new
        {
            data = new
            {
                id = profile.SystemId,
                avatar_url = QualifyUrl(profile.AvatarUrl),
                username = profile.Username,
                description = profile.Description
            }
        });
    }

    //TODO: To ensure route works as expected
    [HttpGet("{systemId}/alters")]
    public async Task<IActionResult> ListAlters([FromRoute] string systemId, CancellationToken ct)
    {
        if (!await SystemExistsAsync(systemId, ct))
        {
            return NotFound(new { error = "System not found.", code = "system_not_found" });
        }

        var alters = await _alters.ListGuardedAsync(systemId, PrincipalId, ct);
        foreach (var a in alters) a.AvatarUrl = QualifyUrl(a.AvatarUrl);
        return Ok(new { data = alters });
    }

    [HttpGet("{systemId}/alters/{alterId}")]
    public async Task<IActionResult> ShowAlter([FromRoute] string systemId, [FromRoute] int alterId, CancellationToken ct)
    {
        await CheckAlterId(alterId);
        if (!await SystemExistsAsync(systemId, ct))
        {
            return NotFound(new { error = "System not found.", code = "system_not_found" });
        }

        var alter = await _alters.GetGuardedAsync(systemId, alterId, PrincipalId, ct);
        if (alter is not null) alter.AvatarUrl = QualifyUrl(alter.AvatarUrl);
        return alter is null
            ? NotFound(new { error = "Alter not found.", code = "alter_not_found" })
            : Ok(new { data = alter });
    }

    //TODO: To ensure route works as expected
    [HttpGet("{systemId}/tags")]
    public async Task<IActionResult> ListTags([FromRoute] string systemId, CancellationToken ct)
    {
        if (!await SystemExistsAsync(systemId, ct))
        {
            return NotFound(new { error = "System not found.", code = "system_not_found" });
        }

        var tags = await _tags.ListGuardedAsync(systemId, PrincipalId, ct);
        return Ok(new { data = tags });
    }

    [HttpGet("{systemId}/tags/{id}")]
    public async Task<IActionResult> ShowTag([FromRoute] string systemId, [FromRoute] string id, CancellationToken ct)
    {
        if (!await SystemExistsAsync(systemId, ct))
        {
            return NotFound(new { error = "System not found.", code = "system_not_found" });
        }

        var tag = await _tags.GetGuardedAsync(systemId, id, PrincipalId, ct);
        return tag is null
            ? NotFound(new { error = "Tag not found.", code = "tag_not_found" })
            : Ok(new { data = tag });
    }

    //TODO: To ensure route works as expected
    [HttpGet("{systemId}/fronting")]
    public async Task<IActionResult> ListFronting([FromRoute] string systemId, CancellationToken ct)
    {
        if (!await SystemExistsAsync(systemId, ct))
        {
            return NotFound(new { error = "System not found.", code = "system_not_found" });
        }

        var fronts = await _fronting.ListActiveGuardedAsync(systemId, PrincipalId, ct);
        return Ok(new { data = fronts });
    }

    [HttpGet("{systemId}/batch")]
    public async Task<IActionResult> Batch([FromRoute] string systemId, CancellationToken ct)
    {
        if (!await SystemExistsAsync(systemId, ct))
        {
            return NotFound(new { error = "System not found.", code = "system_not_found" });
        }

        var principalId = PrincipalId;
        if (string.Equals(principalId, systemId, StringComparison.Ordinal))
        {
            return StatusCode(StatusCodes.Status403Forbidden, new
            {
                error = "You cannot view your own system through this endpoint.",
                code = "invalid_endpoint"
            });
        }

        var altersTask = _alters.ListGuardedAsync(systemId, principalId, ct);
        var tagsTask = _tags.ListGuardedAsync(systemId, principalId, ct);
        var friendshipTask = _friendships.GetFriendshipAsync(principalId, systemId, ct);

        await Task.WhenAll(altersTask, tagsTask, friendshipTask);

        var batchAlters = altersTask.Result;
        foreach (var a in batchAlters) a.AvatarUrl = QualifyUrl(a.AvatarUrl);

        var friendship = friendshipTask.Result;
        if (friendship is not null)
        {
            friendship = friendship with
            {
                Friend = friendship.Friend with { AvatarUrl = QualifyUrl(friendship.Friend.AvatarUrl) },
                Fronting = friendship.Fronting
                    .Select(f => f with { Alter = f.Alter with { AvatarUrl = QualifyUrl(f.Alter.AvatarUrl) } })
                    .ToList()
            };
        }

        return Ok(new
        {
            data = new
            {
                friendship,
                tags = tagsTask.Result,
                alters = batchAlters
            }
        });
    }

    private async Task<bool> SystemExistsAsync(string systemId, CancellationToken ct)
    {
        var profile = await _accounts.GetPublicProfileAsync(systemId, ct);
        return profile is not null;
    }
}
