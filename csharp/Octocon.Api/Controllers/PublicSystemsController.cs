using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Octocon.Domain.Accounts;
using Octocon.Domain.Alters;
using Octocon.Domain.Fronting;
using Octocon.Domain.Tags;

namespace Octocon.Api.Controllers;

[AllowAnonymous]
[Route("api/systems")]
public sealed class PublicSystemsController : OctoconControllerBase
{
    private readonly IAccountRepository _accounts;
    private readonly IAlterRepository _alters;
    private readonly ITagRepository _tags;
    private readonly IFrontingRepository _fronting;

    public PublicSystemsController(
        ApiSettings settings,
        IAccountRepository accounts,
        IAlterRepository alters,
        ITagRepository tags,
        IFrontingRepository fronting) : base(settings)
    {
        _accounts = accounts;
        _alters = alters;
        _tags = tags;
        _fronting = fronting;
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
                avatar_url = profile.AvatarUrl,
                username = profile.Username,
                description = profile.Description
            }
        });
    }

    [HttpGet("{systemId}/alters")]
    public async Task<IActionResult> ListAlters([FromRoute] string systemId, CancellationToken ct)
    {
        if (!await SystemExistsAsync(systemId, ct))
        {
            return NotFound(new { error = "System not found.", code = "system_not_found" });
        }

        var alters = await _alters.ListAsync(systemId, ct);
        return Ok(new { data = alters });
    }

    [HttpGet("{systemId}/alters/{id}")]
    public async Task<IActionResult> ShowAlter([FromRoute] string systemId, [FromRoute] string id, CancellationToken ct)
    {
        if (!int.TryParse(id, out var alterId) || alterId <= 0)
        {
            return BadRequest(new { error = "Invalid alter ID.", code = "invalid_alter_id" });
        }

        if (!await SystemExistsAsync(systemId, ct))
        {
            return NotFound(new { error = "System not found.", code = "system_not_found" });
        }

        var alter = await _alters.GetAsync(systemId, alterId, ct);
        return alter is null
            ? NotFound(new { error = "Alter not found.", code = "alter_not_found" })
            : Ok(new { data = alter });
    }

    [HttpGet("{systemId}/tags")]
    public async Task<IActionResult> ListTags([FromRoute] string systemId, CancellationToken ct)
    {
        if (!await SystemExistsAsync(systemId, ct))
        {
            return NotFound(new { error = "System not found.", code = "system_not_found" });
        }

        var tags = await _tags.ListAsync(systemId, ct);
        return Ok(new { data = tags });
    }

    [HttpGet("{systemId}/tags/{id}")]
    public async Task<IActionResult> ShowTag([FromRoute] string systemId, [FromRoute] string id, CancellationToken ct)
    {
        if (!await SystemExistsAsync(systemId, ct))
        {
            return NotFound(new { error = "System not found.", code = "system_not_found" });
        }

        var tag = await _tags.GetAsync(systemId, id, ct);
        return tag is null
            ? NotFound(new { error = "Tag not found.", code = "tag_not_found" })
            : Ok(new { data = tag });
    }

    [HttpGet("{systemId}/fronting")]
    public async Task<IActionResult> ListFronting([FromRoute] string systemId, CancellationToken ct)
    {
        if (!await SystemExistsAsync(systemId, ct))
        {
            return NotFound(new { error = "System not found.", code = "system_not_found" });
        }

        var fronts = await _fronting.ListActiveAsync(systemId, ct);
        return Ok(new { data = fronts });
    }

    [HttpGet("{systemId}/batch")]
    public async Task<IActionResult> Batch([FromRoute] string systemId, CancellationToken ct)
    {
        if (!await SystemExistsAsync(systemId, ct))
        {
            return NotFound(new { error = "System not found.", code = "system_not_found" });
        }

        var altersTask = _alters.ListAsync(systemId, ct);
        var tagsTask = _tags.ListAsync(systemId, ct);
        await Task.WhenAll(altersTask, tagsTask);

        return Ok(new
        {
            data = new
            {
                friendship = (object?)null,
                tags = tagsTask.Result,
                alters = altersTask.Result
            }
        });
    }

    private async Task<bool> SystemExistsAsync(string systemId, CancellationToken ct)
    {
        var profile = await _accounts.GetPublicProfileAsync(systemId, ct);
        return profile is not null;
    }
}
