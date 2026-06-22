using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;
using System.Text;
using Interfold.Api.Helpers;
using Interfold.Api.Models;
using Interfold.Api.Services;
using Interfold.Contracts.Enums;
using Interfold.Contracts.Models;
using Interfold.Contracts.Models.Commands;
using Interfold.Contracts.Models.Read;
using Interfold.Contracts.Operations;
using Interfold.Domain.Abstractions.Repository;
using Interfold.Domain.Alters;
using Interfold.Api.Controllers.Base;

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
        await CheckAlterId(alterId);
        var alter = await _alterRepository.GetAsync(PrincipalId, alterId, ct);
        if (alter is null)
        {
            return NotFound(new { error = "Alter not found.", code = "alter_not_found" });
        }

        alter.AvatarUrl = QualifyAvatar(alter.AvatarUrl, alter.AvatarSource);
        return Ok(new { data = alter });
    }

    [HttpPost]
    public async Task<Response<AlterReadModel>> Create([FromBody] CreateAlterRequest req, CancellationToken ct)
    {
        var principal = PrincipalId;
        var envelope = new CommandEnvelope<CreateAlterCommand>(
            OperationIds.AlterCreate, Guid.NewGuid(),
            PrincipalId: principal,
            IdempotencyKey: GetIdempotencyKey(req.IdempotencyKey),
            OccurredAt: DateTimeOffset.UtcNow,
            Payload: new CreateAlterCommand(req.Name, DateTimeOffset.UtcNow)
        );

        var execution = await _createHandler.HandleAsync(envelope, ct);
        if (!execution.Accepted)
        {
            return ConflictToError(execution.Conflict!);
        }

        var alter = await _alterRepository.GetAsync(principal, execution.Result!.AlterId, ct);
        if (alter is null)
            return new ErrorResponse("An unknown error occurred.", "unknown_error", System.Net.HttpStatusCode.InternalServerError);

        Response.Headers.Location = $"/api/systems/me/alters/{execution.Result.AlterId}";
        return new SuccessResponse<AlterReadModel>(alter, System.Net.HttpStatusCode.Created, execution.Result.Replay);
    }

    [HttpPatch("{alterId:int}")]
    public async Task<Response> Update(int alterId, [FromBody] UpdateAlterRequest req, CancellationToken ct)
    {
        await CheckAlterId(alterId);
        var fields = req.Fields?.Select(f => new AlterFieldCommand(f.Id, f.Value)).ToList();

        // PATCH does not currently expose AvatarUrl as a mutable field — avatar updates go
        // through the dedicated PUT .../avatar endpoints (multipart for Local, JSON for External)
        // so we never observe a half-set (url-without-source) here.
        var payload = new UpdateAlterCommand(
            AlterId: alterId,
            Name: req.Name,
            Description: req.Description,
            AvatarUrl: null,
            AvatarSource: null,
            Color: req.Color,
            Pronouns: req.Pronouns,
            SecurityLevel: req.SecurityLevel,
            Fields: fields,
            ProxyName: req.ProxyName,
            Alias: req.Alias,
            Untracked: req.Untracked,
            Archived: req.Archived,
            Pinned: req.Pinned,
            UpdatedAt: DateTimeOffset.UtcNow
        );

        var envelope = new CommandEnvelope<UpdateAlterCommand>(
            OperationIds.AlterUpdate, Guid.NewGuid(),
            PrincipalId: PrincipalId,
            IdempotencyKey: GetIdempotencyKey(req.IdempotencyKey),
            OccurredAt: DateTimeOffset.UtcNow,
            Payload: payload
        );

        return CommandNoContent(await _updateHandler.HandleAsync(envelope, ct));
    }

    //TODO: To ensure route works as expected - check if we delete alter journal entries, unattach from gobal journals when an alter is deleted and delete them from polls
    [HttpDelete("{alterId:int}")]
    public async Task<Response> Delete(int alterId, [FromBody] BaseRequest? req, CancellationToken ct)
    {
        await CheckAlterId(alterId);
        var envelope = new CommandEnvelope<DeleteAlterCommand>(
            OperationIds.AlterDelete, Guid.NewGuid(),
            PrincipalId: PrincipalId,
            IdempotencyKey: GetIdempotencyKey(req?.IdempotencyKey),
            OccurredAt: DateTimeOffset.UtcNow,
            Payload: new DeleteAlterCommand(alterId)
        );
        return CommandNoContent(await _deleteHandler.HandleAsync(envelope, ct));
    }

    [HttpPut("{alterId:int}/avatar")]
    [Consumes("multipart/form-data")]
    public async Task<Response> UploadAvatarMultipart(int alterId, CancellationToken ct)
    {
        await CheckAlterId(alterId);
        var principal = PrincipalId;

        var upload = await ResolveMultipartUploadAsync(ct);
        var avatarStream = upload.Stream;
        if (avatarStream is null)
        {
            if (upload.EmptyFilePart)
                return new ErrorResponse("Avatar file is empty.", "avatar_file_empty", System.Net.HttpStatusCode.BadRequest);

            return new ErrorResponse("No avatar file provided.", "avatar_file_required", System.Net.HttpStatusCode.BadRequest);
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
            return new ErrorResponse("An error occurred while uploading the file.", "unknown_error", System.Net.HttpStatusCode.InternalServerError);
        }

        string? currentAvatarUrl = null;
        AvatarSource? currentAvatarSource = null;
        try
        {
            var existingAlter = await _alterRepository.GetAsync(principal, alterId, ct);
            currentAvatarUrl = existingAlter?.AvatarUrl;
            currentAvatarSource = existingAlter?.AvatarSource;
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
            AvatarSource: AvatarSource.Local,
            Color: null,
            Pronouns: null,
            SecurityLevel: null,
            Fields: null,
            ProxyName: null,
            Alias: null,
            Untracked: null,
            Archived: null,
            Pinned: null,
            UpdatedAt: DateTimeOffset.UtcNow
        );

        var envelope = new CommandEnvelope<UpdateAlterCommand>(
            OperationIds.AlterAvatarUpload,
            Guid.NewGuid(),
            PrincipalId: principal,
            IdempotencyKey: GetIdempotencyKey(upload.IdempotencyKey),
            OccurredAt: DateTimeOffset.UtcNow,
            Payload: payload
        );

        var result = CommandNoContent(await _updateHandler.HandleAsync(envelope, ct));
        if (!result.IsT0) 
        {
            return result;
        }

        // Only the local storage owns the previous bytes; an external URL was never ours
        // to delete.
        if (currentAvatarSource == AvatarSource.Local)
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

        return result;
    }

    /// <summary>
    /// JSON sibling of <see cref="UploadAvatarMultipart"/>: stores the supplied URL on
    /// <c>avatar_url</c> with <c>avatar_source = External</c> without fetching the bytes.
    /// </summary>
    [HttpPut("{alterId:int}/avatar")]
    [Consumes("application/json")]
    public async Task<Response> UploadAvatarByUrl(int alterId, [FromBody] AvatarUrlUploadRequest req, CancellationToken ct)
    {
        await CheckAlterId(alterId);

        if (req is null)
            return new ErrorResponse("Avatar URL payload required.", "avatar_url_invalid", System.Net.HttpStatusCode.BadRequest);

        if (!AvatarUrlValidator.TryNormalize(req.Url, out var url, out var err))
            return new ErrorResponse("Invalid avatar URL.", err, System.Net.HttpStatusCode.BadRequest);

        var principal = PrincipalId;

        string? currentAvatarUrl = null;
        AvatarSource? currentAvatarSource = null;
        try
        {
            var existingAlter = await _alterRepository.GetAsync(principal, alterId, ct);
            currentAvatarUrl = existingAlter?.AvatarUrl;
            currentAvatarSource = existingAlter?.AvatarSource;
        }
        catch
        {
        }

        var payload = new UpdateAlterCommand(
            AlterId: alterId,
            Name: null,
            Description: null,
            AvatarUrl: url,
            AvatarSource: AvatarSource.External,
            Color: null,
            Pronouns: null,
            SecurityLevel: null,
            Fields: null,
            ProxyName: null,
            Alias: null,
            Untracked: null,
            Archived: null,
            Pinned: null,
            UpdatedAt: DateTimeOffset.UtcNow
        );

        var envelope = new CommandEnvelope<UpdateAlterCommand>(
            OperationIds.AlterAvatarUpload,
            Guid.NewGuid(),
            PrincipalId: principal,
            IdempotencyKey: GetIdempotencyKey(req.IdempotencyKey),
            OccurredAt: DateTimeOffset.UtcNow,
            Payload: payload
        );

        var result = CommandNoContent(await _updateHandler.HandleAsync(envelope, ct));
        if (!result.IsT0)
        {
            return result;
        }

        if (currentAvatarSource == AvatarSource.Local)
        {
            try
            {
                await _avatarStorage.DeleteByUrlAsync(currentAvatarUrl, ct);
            }
            catch
            {
            }
        }

        return result;
    }

    private async Task<AvatarUploadPayload> ResolveMultipartUploadAsync(CancellationToken ct)
    {
        string? idempotencyKey = null;
        var emptyFilePart = false;

        if (Request.Body is null)
            return new AvatarUploadPayload(null, idempotencyKey, emptyFilePart);

        Request.EnableBuffering();

        if (Request.Body.CanSeek)
            Request.Body.Position = 0;

        if (!MediaTypeHeaderValue.TryParse(Request.ContentType, out var mediaType)
            || !mediaType.MediaType.HasValue
            || !mediaType.MediaType.Value.StartsWith("multipart/", StringComparison.OrdinalIgnoreCase))
        {
            return new AvatarUploadPayload(null, idempotencyKey, emptyFilePart);
        }

        var boundary = HeaderUtilities.RemoveQuotes(mediaType.Boundary).Value;
        if (string.IsNullOrWhiteSpace(boundary))
            return new AvatarUploadPayload(null, idempotencyKey, emptyFilePart);

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
                    return new AvatarUploadPayload(payload, idempotencyKey, emptyFilePart);
                }

                if (string.IsNullOrWhiteSpace(fieldName))
                    continue;

                using var readerText = new StreamReader(section.Body, Encoding.UTF8, true, 1024, leaveOpen: true);
                var value = (await readerText.ReadToEndAsync()).Trim();
                if (fieldName.Equals("idempotencyKey", StringComparison.OrdinalIgnoreCase))
                    idempotencyKey = string.IsNullOrWhiteSpace(value) ? null : value;
            }
        }
        catch (IOException)
        {
            return new AvatarUploadPayload(null, idempotencyKey, emptyFilePart);
        }

        return new AvatarUploadPayload(null, idempotencyKey, emptyFilePart);
    }

    [HttpDelete("{alterId:int}/avatar")]
    public async Task<Response> DeleteAvatar(int alterId, [FromBody] BaseRequest? req, CancellationToken ct)
    {
        await CheckAlterId(alterId);
        var principal = PrincipalId;

        var existingAlter = await _alterRepository.GetAsync(principal, alterId, ct);
        var currentAvatarUrl = existingAlter?.AvatarUrl;
        var currentAvatarSource = existingAlter?.AvatarSource;

        var payload = new UpdateAlterCommand(
            AlterId: alterId,
            Name: null,
            Description: null,
            AvatarUrl: null,
            AvatarSource: null,
            Color: null,
            Pronouns: null,
            SecurityLevel: null,
            Fields: null,
            ProxyName: null,
            Alias: null,
            Untracked: null,
            Archived: null,
            Pinned: null,
            UpdatedAt: DateTimeOffset.UtcNow,
            ClearAvatar: true
        );

        var envelope = new CommandEnvelope<UpdateAlterCommand>(
            OperationIds.AlterAvatarDelete,
            Guid.NewGuid(),
            PrincipalId: principal,
            IdempotencyKey: GetIdempotencyKey(req?.IdempotencyKey),
            OccurredAt: DateTimeOffset.UtcNow,
            Payload: payload
        );

        var execution = await _updateHandler.HandleAsync(envelope, ct);
        if (execution.Accepted && currentAvatarSource == AvatarSource.Local)
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

        return CommandNoContent(execution);
    }
}