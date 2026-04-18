using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;
using System.Text;
using Interfold.Api.Services;
using Interfold.Contracts.Operations;
using Interfold.Domain.Alters;

namespace Interfold.Api.Controllers;

[Route("api/systems/me/alters")]
public sealed class AltersController : InterfoldControllerBase
{
    private readonly IAlterRepository _alterRepository;
    private readonly CreateAlterCommandHandler _createHandler;
    private readonly UpdateAlterCommandHandler _updateHandler;
    private readonly DeleteAlterCommandHandler _deleteHandler;
    private readonly IAvatarStorage _avatarStorage;

    public AltersController(
        IAlterRepository alterRepository,
        CreateAlterCommandHandler createHandler,
        UpdateAlterCommandHandler updateHandler,
        DeleteAlterCommandHandler deleteHandler,
        IAvatarStorage avatarStorage)
    {
        _alterRepository = alterRepository;
        _createHandler = createHandler;
        _updateHandler = updateHandler;
        _deleteHandler = deleteHandler;
        _avatarStorage = avatarStorage;
    }

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var alters = await _alterRepository.ListAsync(PrincipalId, ct);
        return Ok(new { data = alters });
    }

    [HttpGet("{alterId:int}")]
    public async Task<IActionResult> Show(int alterId, CancellationToken ct)
    {
        CheckAlterId(alterId);
        var alter = await _alterRepository.GetAsync(PrincipalId, alterId, ct);
        if (alter is null)
        {
            return NotFound(new { error = "Alter not found.", code = "alter_not_found" });
        }

        alter.AvatarUrl = QualifyUrl(alter.AvatarUrl);
        return Ok(new { data = alter });
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateAlterRequest req, CancellationToken ct)
    {
        var principal = PrincipalId;
        var envelope = new CommandEnvelope<CreateAlterCommand>(
            OperationIds.AlterCreate, Guid.NewGuid(),
            PrincipalId: principal,
            IdempotencyKey: GetIdempotencyKey(req.IdempotencyKey),
            ExpectedVersion: req.ExpectedVersion,
            OccurredAt: DateTimeOffset.UtcNow,
            Payload: new CreateAlterCommand(req.Name)
        );

        var execution = await _createHandler.HandleAsync(envelope, ct);
        if (!execution.Accepted)
        {
            return ToHttpResult(execution);
        }

        var alter = await _alterRepository.GetAsync(principal, execution.Result!.AlterId, ct);
        object data = alter is not null ? alter : execution.Result;
        Response.Headers.Location = $"/api/systems/me/alters/{execution.Result.AlterId}";
        return StatusCode(StatusCodes.Status201Created, new { data, replay = execution.Result.Replay });
    }

    [HttpPatch("{alterId:int}")]
    public async Task<IActionResult> Update(int alterId, [FromBody] UpdateAlterRequest req, CancellationToken ct)
    {
        CheckAlterId(alterId);
        var fields = req.Fields?.Select(f => new AlterFieldCommand(f.Id, f.Value)).ToList();
        var payload = new UpdateAlterCommand(
            AlterId: alterId,
            Name: req.Name,
            Description: req.Description,
            AvatarUrl: req.AvatarUrl,
            Color: req.Color,
            Pronouns: req.Pronouns,
            SecurityLevel: req.SecurityLevel,
            Fields: fields,
            ProxyName: req.ProxyName,
            Alias: req.Alias,
            Untracked: req.Untracked,
            Archived: req.Archived,
            Pinned: req.Pinned
        );

        var envelope = new CommandEnvelope<UpdateAlterCommand>(
            OperationIds.AlterUpdate, Guid.NewGuid(),
            PrincipalId: PrincipalId,
            IdempotencyKey: GetIdempotencyKey(req.IdempotencyKey),
            ExpectedVersion: req.ExpectedVersion,
            OccurredAt: DateTimeOffset.UtcNow,
            Payload: payload
        );

        var result = ToHttpResult(await _updateHandler.HandleAsync(envelope, ct));
        return result is OkObjectResult ? NoContent() : result;
    }

    //TODO: To ensure route works as expected - check if we delete alter journal entries, unattach from gobal journals when an alter is deleted and delete them from polls
    [HttpDelete("{alterId:int}")]
    public async Task<IActionResult> Delete(int alterId, [FromBody] DeleteAlterRequest? req, CancellationToken ct)
    {
        CheckAlterId(alterId);
        var envelope = new CommandEnvelope<DeleteAlterCommand>(
            OperationIds.AlterDelete, Guid.NewGuid(),
            PrincipalId: PrincipalId,
            IdempotencyKey: GetIdempotencyKey(req?.IdempotencyKey),
            ExpectedVersion: req?.ExpectedVersion,
            OccurredAt: DateTimeOffset.UtcNow,
            Payload: new DeleteAlterCommand(alterId)
        );
        var result = ToHttpResult(await _deleteHandler.HandleAsync(envelope, ct));
        return result is OkObjectResult ? NoContent() : result;
    }

    [HttpPut("{alterId:int}/avatar")]
    [Consumes("multipart/form-data")]
    public async Task<IActionResult> UploadAvatarMultipart(int alterId, CancellationToken ct)
    {
        CheckAlterId(alterId);
        var principal = PrincipalId;

        var upload = await ResolveMultipartUploadAsync(ct);
        var avatarStream = upload.Stream;
        if (avatarStream is null)
        {
            if (upload.EmptyFilePart)
                return BadRequest(new { error = "Avatar file is empty.", code = "avatar_file_empty" });

            return BadRequest(new { error = "No avatar file provided.", code = "avatar_file_required" });
        }

        string avatarUrl;
        try
        {
            await using (avatarStream)
            {
                avatarUrl = await _avatarStorage.SaveAlterAvatarAsync(principal, alterId, avatarStream, ct);
            }
        }
        catch
        {
            return StatusCode(500, new { error = "An error occurred while uploading the file.", code = "unknown_error" });
        }

        string? currentAvatarUrl = null;
        try
        {
            var existingAlter = await _alterRepository.GetAsync(principal, alterId, ct);
            currentAvatarUrl = existingAlter?.AvatarUrl;
        }
        catch
        {
            // Best effort to clean up old avatar; this isn't critical but ideal for costs/storage
        }

        var payload = new UpdateAlterCommand(
            AlterId: alterId,
            Name: null,
            Description: null,
            AvatarUrl: avatarUrl,
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
            IdempotencyKey: GetIdempotencyKey(upload.IdempotencyKey),
            ExpectedVersion: upload.ExpectedVersion,
            OccurredAt: DateTimeOffset.UtcNow,
            Payload: payload
        );

        var result = ToHttpResult(await _updateHandler.HandleAsync(envelope, ct));
        if (result is not OkObjectResult) 
        {
            return result;
        }

        try
        {
            await _avatarStorage.DeleteByUrlAsync(currentAvatarUrl, ct);
        }
        catch
        {
            // Alter metadata update succeeded; tolerate storage cleanup failures.
        }

        return NoContent();
    }

    private async Task<AvatarUploadPayload> ResolveMultipartUploadAsync(CancellationToken ct)
    {
        string? idempotencyKey = null;
        long? expectedVersion = null;
        var emptyFilePart = false;

        if (Request.Body is null)
            return new AvatarUploadPayload(null, idempotencyKey, expectedVersion, emptyFilePart);

        Request.EnableBuffering();

        if (Request.Body.CanSeek)
            Request.Body.Position = 0;

        if (!MediaTypeHeaderValue.TryParse(Request.ContentType, out var mediaType)
            || !mediaType.MediaType.HasValue
            || !mediaType.MediaType.Value.StartsWith("multipart/", StringComparison.OrdinalIgnoreCase))
        {
            return new AvatarUploadPayload(null, idempotencyKey, expectedVersion, emptyFilePart);
        }

        var boundary = HeaderUtilities.RemoveQuotes(mediaType.Boundary).Value;
        if (string.IsNullOrWhiteSpace(boundary))
            return new AvatarUploadPayload(null, idempotencyKey, expectedVersion, emptyFilePart);

        try
        {
            var reader = new MultipartReader(boundary, Request.Body);
            MultipartSection? section;

            while ((section = await reader.ReadNextSectionAsync(ct)) is not null)
            {
                if (!ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out var disposition))
                    continue;

                var fieldName = HeaderUtilities.RemoveQuotes(disposition.Name).Value;
                var fileName = HeaderUtilities.RemoveQuotes(disposition.FileNameStar).Value
                               ?? HeaderUtilities.RemoveQuotes(disposition.FileName).Value;

                if (!string.IsNullOrWhiteSpace(fileName))
                {
                    var payload = new MemoryStream();
                    await section.Body.CopyToAsync(payload, ct);
                    if (payload.Length <= 0)
                    {
                        emptyFilePart = true;
                        await payload.DisposeAsync();
                        continue;
                    }

                    payload.Position = 0;
                    return new AvatarUploadPayload(payload, idempotencyKey, expectedVersion, emptyFilePart);
                }

                if (string.IsNullOrWhiteSpace(fieldName))
                    continue;

                using var readerText = new StreamReader(section.Body, Encoding.UTF8, true, 1024, leaveOpen: true);
                var value = (await readerText.ReadToEndAsync()).Trim();
                if (fieldName.Equals("idempotencyKey", StringComparison.OrdinalIgnoreCase))
                    idempotencyKey = string.IsNullOrWhiteSpace(value) ? null : value;
                else if (fieldName.Equals("expectedVersion", StringComparison.OrdinalIgnoreCase)
                         && long.TryParse(value, out var parsed))
                    expectedVersion = parsed;
            }
        }
        catch (IOException)
        {
            return new AvatarUploadPayload(null, idempotencyKey, expectedVersion, emptyFilePart);
        }

        return new AvatarUploadPayload(null, idempotencyKey, expectedVersion, emptyFilePart);
    }

    private sealed record AvatarUploadPayload(Stream? Stream, string? IdempotencyKey, long? ExpectedVersion, bool EmptyFilePart = false);

    [HttpDelete("{alterId:int}/avatar")]
    public async Task<IActionResult> DeleteAvatar(int alterId, [FromBody] DeleteAlterRequest? req, CancellationToken ct)
    {
        CheckAlterId(alterId);
        var principal = PrincipalId;

        var existingAlter = await _alterRepository.GetAsync(principal, alterId, ct);
        var currentAvatarUrl = existingAlter?.AvatarUrl;

        var payload = new UpdateAlterCommand(
            AlterId: alterId,
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
            Pinned: null,
            ClearAvatar: true
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

        var execution = await _updateHandler.HandleAsync(envelope, ct);
        if (execution.Accepted)
        {
            try
            {
                await _avatarStorage.DeleteByUrlAsync(currentAvatarUrl, ct);
            }
            catch
            {
                // Alter metadata update succeeded; tolerate storage cleanup failures.
            }
        }

        var result = ToHttpResult(execution);
        return result is OkObjectResult ? NoContent() : result;
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
    IReadOnlyList<UpdateAlterFieldRequest>? Fields = null,
    string? IdempotencyKey = null,
    long? ExpectedVersion = null
);

public sealed record UpdateAlterFieldRequest(
    string Id,
    string? Value
);

public sealed record DeleteAlterRequest(
    string? IdempotencyKey = null,
    long? ExpectedVersion = null
);