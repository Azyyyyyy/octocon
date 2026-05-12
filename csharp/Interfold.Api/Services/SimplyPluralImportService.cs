using System.Security.Cryptography;
using System.Text.Json;
using System.Text;
using Interfold.Api.Services.SimplyPlural;
using Interfold.Contracts.Models.Commands;
using Interfold.Domain.Abstractions;
using Interfold.Domain.Abstractions.Repository;

namespace Interfold.Api.Services;

/*
  TODO: We currently don't import the following SP data
  * Privacy Buckets (we don't have this concept within Interfold, and they don't always 1:1 to our security levels)
  * Message Boards - Alter (we don't have this concept)
    * Receive board messages on switch
  * Prevent notification - On alter front
  * Chat - Can maybe map to global journals?
  * App Reminders
  * Friend Settings
    * Shared Members
    * See who is fronting
    * Global front change notifications (them and us)
    * Privacy Buckets
*/

public sealed class SimplyPluralImportService : ISimplyPluralImportService
{
    private const string SpApiBase = "https://api.apparyllis.com/v1";
    private const string SpCdnBase = "https://spaces.apparyllis.com";
    private const int MaxJournalContentLength = 30_000;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IAlterRepository _alterRepository;
    private readonly ITagRepository _tagRepository;
    private readonly IFrontingRepository _frontingRepository;
    private readonly ISettingsFieldRepository _fieldRepository;
    private readonly IAccountRepository _accountRepository;
    private readonly IPollRepository _pollRepository;
    private readonly IJournalRepository _journalRepository;
    private readonly IAvatarStorage _avatarStorage;
    private readonly ILogger<SimplyPluralImportService> _logger;

    public SimplyPluralImportService(
        IHttpClientFactory httpClientFactory,
        IAlterRepository alterRepository,
        ITagRepository tagRepository,
        IFrontingRepository frontingRepository,
        ISettingsFieldRepository fieldRepository,
        IAccountRepository accountRepository,
        IPollRepository pollRepository,
        IJournalRepository journalRepository,
        IAvatarStorage avatarStorage,
        ILogger<SimplyPluralImportService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _alterRepository = alterRepository;
        _tagRepository = tagRepository;
        _frontingRepository = frontingRepository;
        _fieldRepository = fieldRepository;
        _accountRepository = accountRepository;
        _pollRepository = pollRepository;
        _journalRepository = journalRepository;
        _avatarStorage = avatarStorage;
        _logger = logger;
    }

    public async Task<SpImportResult> ImportAsync(
        string systemId,
        string spToken,
        string? encryptionKey,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Starting Simply Plural import for system {SystemId}", systemId);

        if (!string.IsNullOrWhiteSpace(encryptionKey))
            encryptionKey = encryptionKey.Trim();

        using var httpClient = _httpClientFactory.CreateClient("SimplyPlural");
        httpClient.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", spToken); // SP uses non-standard "Authorization: {token}" header
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Interfold/spimport");

        // 1. Fetch system data
        var systemData = await FetchAsync<SpEntity<SpSystemContent>>(httpClient, $"{SpApiBase}/me", cancellationToken);
        if (systemData is null)
            return new SpImportResult(false, 0, "Failed to fetch system data from Simply Plural.");

        var spSystemId = systemData.Id;
        var description = systemData.Content.Desc;

        // 2. Fetch custom fields
        var (fieldMapping, createdFieldIds) = await ImportCustomFieldsAsync(httpClient, spSystemId, systemId, cancellationToken);

        // 3. Fetch members and custom fronts
        var (alterCount, alterAssociations, avatarDownloads) =
            await ImportAltersAsync(httpClient, spSystemId, systemId, fieldMapping, cancellationToken);

        // 4. Fetch groups -> tags
        var tagAssociations = await ImportTagsAsync(httpClient, spSystemId, systemId, alterAssociations, cancellationToken);

        // 5. Fetch front history
        await ImportFrontsAsync(httpClient, spSystemId, systemId, alterAssociations, cancellationToken);

        // 6. Import polls
        await ImportPollsAsync(httpClient, spSystemId, systemId, alterAssociations, cancellationToken);

        // (optional) 7. Import notes per alter as alter journals
        if (!string.IsNullOrWhiteSpace(encryptionKey))
        {
            await ImportNotesAsync(httpClient, spSystemId, systemId, alterAssociations, encryptionKey, cancellationToken);
        }

        // 8. Update account description if available
        if (!string.IsNullOrWhiteSpace(description))
        {
            var truncated = description.Length > 3000 ? description[..3000] : description;
            await _accountRepository.UpdateDescriptionAsync(systemId, truncated, cancellationToken);
        }

        // 9. Download and attach avatars in background
        _ = Task.Run(async () => await DownloadAvatarsAsync(systemId, avatarDownloads), CancellationToken.None);

        _logger.LogInformation("Simply Plural import complete for system {SystemId}: {AlterCount} alters imported", systemId, alterCount);
        return new SpImportResult(true, alterCount);
    }

    private async Task<(Dictionary<string, string> FieldMapping, List<string> CreatedFieldIds)> ImportCustomFieldsAsync(
        HttpClient httpClient, string spSystemId, string systemId, CancellationToken ct)
    {
        var fieldMapping = new Dictionary<string, string>(); // SP field ID -> our field ID
        var createdFieldIds = new List<string>();

        var customFields = await FetchAsync<List<SpEntity<SpCustomFieldContent>>>(httpClient, $"{SpApiBase}/customFields/{spSystemId}", ct);
        if (customFields is null)
            return (fieldMapping, createdFieldIds);

        foreach (var field in customFields)
        {
            var spFieldId = field.Id;
            var name = field.Content.Name ?? "Unnamed field";
            if (name.Length > 100) name = name[..100];

            var fieldType = MapFieldType(field.Content.Type, field.Content.SupportMarkdown);
            var securityLevel = MapSecurityLevel(field.Content.Private, field.Content.PreventTrusted);
            var createdId = await _fieldRepository.CreateAsync(systemId, name, fieldType, securityLevel, false, ct);
            if (createdId is not null)
            {
                fieldMapping[spFieldId] = createdId;
                createdFieldIds.Add(createdId);
            }
        }

        return (fieldMapping, createdFieldIds);
    }

    private async Task<(int AlterCount, Dictionary<string, int> AlterAssociations, List<AvatarDownload> AvatarDownloads)> ImportAltersAsync(
        HttpClient httpClient, string spSystemId, string systemId,
        Dictionary<string, string> fieldMapping, CancellationToken ct)
    {
        var alterAssociations = new Dictionary<string, int>(); // SP UUID -> our alter ID
        var avatarDownloads = new List<AvatarDownload>();
        var alterCount = 0;

        // Fetch members
        var members = await FetchAsync<List<SpEntity<SpMemberContent>>>(httpClient, $"{SpApiBase}/members/{spSystemId}", ct);
        // Fetch custom fronts
        var customFronts = await FetchAsync<List<SpEntity<SpMemberContent>>>(httpClient, $"{SpApiBase}/customFronts/{spSystemId}", ct);

        var allEntries = new List<(SpMemberContent Content, string Uuid, bool IsCustomFront)>();

        if (members is not null)
        {
            foreach (var member in members)
                allEntries.Add((member.Content, member.Id, false));
        }

        if (customFronts is not null)
        {
            foreach (var cf in customFronts)
                allEntries.Add((cf.Content, cf.Id, true));
        }

        foreach (var (content, uuid, isCustomFront) in allEntries)
        {
            var name = content.Name ?? "Unnamed alter";
            if (name.Length > 80) name = name[..80];
            if (string.IsNullOrWhiteSpace(name)) name = "Unnamed alter";

            var createdAt = UnixTimestampToDateTimeOffset(content.Date);
            var updatedAt = UnixTimestampToDateTimeOffset(content.LastOperationTime);

            var alterId = await _alterRepository.CreateAsync(systemId, new CreateAlterCommand(name, createdAt), ct);
            if (alterId is null)
                continue;

            alterAssociations[uuid] = alterId.Value;
            alterCount++;

            // Build update command with SP data
            var pronouns = content.Pronouns;
            if (pronouns?.Length > 50) pronouns = pronouns[..50];

            var desc = content.Desc;
            if (desc?.Length > 3000) desc = desc[..3000];

            var color = ParseColor(content.Color);

            // Parse fields
            List<AlterFieldCommand>? fields = null;
            if (content.Info is { Count: > 0 })
            {
                fields = new List<AlterFieldCommand>();
                foreach (var (spFieldId, value) in content.Info)
                {
                    if (fieldMapping.TryGetValue(spFieldId, out var ourFieldId) && !string.IsNullOrWhiteSpace(value))
                        fields.Add(new AlterFieldCommand(ourFieldId, value));
                }

                if (fields.Count == 0) fields = null;
            }

            var securityLevel = MapSecurityLevel(content.Private, content.PreventTrusted);

            var updateCommand = new UpdateAlterCommand(
                AlterId: alterId.Value,
                Name: null, // already set via CreateAsync
                Description: desc,
                AvatarUrl: null,
                Color: color,
                Pronouns: pronouns,
                SecurityLevel: securityLevel,
                Fields: fields,
                ProxyName: null,
                Alias: null,
                Untracked: isCustomFront,
                Archived: content.Archived,
                Pinned: false,
                UpdatedAt: updatedAt
            );

            await _alterRepository.UpdateAsync(systemId, updateCommand, ct);

            // Check for avatar
            var avatarUuid = content.AvatarUuid;
            var avatarUrl = content.AvatarUrl;
            var uid = content.Uid;
            if (!string.IsNullOrWhiteSpace(avatarUuid) && !string.IsNullOrWhiteSpace(uid))
            {
                avatarUrl = $"{SpCdnBase}/avatars/{uid}/{avatarUuid}";
            }

            if (!string.IsNullOrWhiteSpace(avatarUrl) && Uri.IsWellFormedUriString(avatarUrl, UriKind.Absolute))
            {
                avatarDownloads.Add(new AvatarDownload(avatarUrl, systemId, alterId.Value));
            }
        }

        return (alterCount, alterAssociations, avatarDownloads);
    }

    private async Task<Dictionary<string, string>> ImportTagsAsync(
        HttpClient httpClient, string spSystemId, string systemId,
        Dictionary<string, int> alterAssociations, CancellationToken ct)
    {
        var tagAssociations = new Dictionary<string, string>(); // SP group ID -> our tag ID

        var groups = await FetchAsync<List<SpEntity<SpGroupContent>>>(httpClient, $"{SpApiBase}/groups/{spSystemId}", ct);
        if (groups is null)
            return tagAssociations;

        // First pass: create all tags (without parent relationships)
        var tagEntries = new List<(string SpId, SpGroupContent Content)>();
        foreach (var group in groups)
        {
            tagEntries.Add((group.Id, group.Content));
        }

        // Create tags
        foreach (var (spId, content) in tagEntries)
        {
            var name = content.Name ?? "Unnamed tag";
            if (name.Length > 100) name = name[..100];
            if (string.IsNullOrWhiteSpace(name)) name = "Unnamed tag";

            var createdTagId = await _tagRepository.CreateAsync(systemId, new CreateTagCommand(name, null), ct);
            if (createdTagId is not null)
            {
                tagAssociations[spId] = createdTagId;

                // Update tag with description and color from SP
                var tagDesc = content.Desc;
                if (tagDesc?.Length > 1000) tagDesc = tagDesc[..1000];
                var tagColor = ParseColor(content.Color);

                var tagSecurityLevel = MapSecurityLevel(content.Private, content.PreventTrusted);

                if (!string.IsNullOrWhiteSpace(tagDesc) || !string.IsNullOrWhiteSpace(tagColor) || tagSecurityLevel != "private")
                {
                    await _tagRepository.UpdateAsync(systemId, new UpdateTagCommand(
                        TagId: createdTagId,
                        Name: null,
                        Color: tagColor,
                        Description: tagDesc,
                        SecurityLevel: tagSecurityLevel
                    ), ct);
                }
            }
        }

        // Second pass: set parent relationships
        foreach (var (spId, content) in tagEntries)
        {
            if (!tagAssociations.TryGetValue(spId, out var ourTagId))
                continue;

            var parent = content.Parent;
            if (string.IsNullOrWhiteSpace(parent) || parent == "root")
                continue;

            if (tagAssociations.TryGetValue(parent, out var parentTagId))
            {
                await _tagRepository.SetParentAsync(systemId, ourTagId, parentTagId, ct);
            }
        }

        // Third pass: attach alters to tags
        foreach (var (spId, content) in tagEntries)
        {
            if (!tagAssociations.TryGetValue(spId, out var ourTagId))
                continue;

            if (content.Members is null)
                continue;

            foreach (var memberUuid in content.Members)
            {
                if (alterAssociations.TryGetValue(memberUuid, out var alterId))
                {
                    await _tagRepository.AttachAlterAsync(systemId, ourTagId, alterId, ct);
                }
            }
        }

        return tagAssociations;
    }

    private async Task ImportFrontsAsync(
        HttpClient httpClient, string spSystemId, string systemId,
        Dictionary<string, int> alterAssociations, CancellationToken ct)
    {
        //TODO: There is a small issue with some being put into todays date
        // Fetch front history in chunks (SP epoch: Jan 1, 2015)
        const long startEpoch = 1_420_070_400_000;
        const int monthInterval = 6;
        var chunkSizeMs = (long)monthInterval * 30 * 24 * 60 * 60 * 1000;
        var endTimeMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var numberOfChunks = (int)Math.Ceiling((double)(endTimeMs - startEpoch) / chunkSizeMs);

        var seenFrontIds = new HashSet<string>();

        for (var i = 0; i <= numberOfChunks; i++)
        {
            var chunkStart = startEpoch + i * chunkSizeMs;
            var chunkEnd = Math.Min(startEpoch + (i + 1) * chunkSizeMs, endTimeMs);

            var fronts = await FetchAsync<List<SpEntity<SpFrontContent>>>(httpClient,
                $"{SpApiBase}/frontHistory/{spSystemId}?startTime={chunkStart}&endTime={chunkEnd}", ct);

            if (fronts is not null)
            {
                foreach (var front in fronts)
                {
                    if (!seenFrontIds.Add(front.Id))
                        continue;

                    var memberId = front.Content.Member;
                    if (memberId is null || !alterAssociations.TryGetValue(memberId, out var alterId))
                        continue;

                    if (front.Content.StartTime <= 0 || front.Content.EndTime <= 0)
                        continue;

                    //TODO: Add comments on top of the custom status
                    var comment = front.Content.CustomStatus;
                    if (comment?.Length > 50) comment = comment[..50];

                    var spStart = DateTimeOffset.FromUnixTimeMilliseconds(front.Content.StartTime);
                    var spEnd = DateTimeOffset.FromUnixTimeMilliseconds(front.Content.EndTime);

                    await _frontingRepository.StartAsync(systemId, alterId, comment, spStart, ct);
                    await _frontingRepository.EndAsync(systemId, alterId, spEnd, ct);
                }
            }

            // Rate limit courtesy delay
            await Task.Delay(200, ct);
        }

        // Import current fronters
        var currentFronters = await FetchAsync<List<SpEntity<SpFrontContent>>>(httpClient, $"{SpApiBase}/fronters/", ct);
        if (currentFronters is not null)
        {
            foreach (var fronter in currentFronters)
            {
                var memberId = fronter.Content.Member;
                if (memberId is null || !alterAssociations.TryGetValue(memberId, out var alterId))
                    continue;

                //TODO: Add comments on top of the custom status
                var comment = fronter.Content.CustomStatus;
                if (comment?.Length > 50) comment = comment[..50];

                var spStart = fronter.Content.StartTime > 0
                    ? DateTimeOffset.FromUnixTimeMilliseconds(fronter.Content.StartTime)
                    : DateTimeOffset.UtcNow;

                await _frontingRepository.StartAsync(systemId, alterId, comment, spStart, ct);
            }
        }
    }

    //TODO: Test, not use if this actually works lmao
    private async Task ImportPollsAsync(
        HttpClient httpClient, string spSystemId, string systemId,
        Dictionary<string, int> alterAssociations, CancellationToken ct)
    {
        var polls = await FetchAsync<List<SpEntity<SpPollContent>>>(httpClient, $"{SpApiBase}/polls/{spSystemId}", ct);
        if (polls is null)
            return;

        foreach (var poll in polls)
        {
            var title = poll.Content.Name ?? "Unnamed poll";
            if (title.Length > 200) title = title[..200];
            if (string.IsNullOrWhiteSpace(title)) title = "Unnamed poll";

            var desc = poll.Content.Desc;
            if (desc?.Length > 3000) desc = desc[..3000];

            // SP custom=false → "vote" (yes/no/abstain/veto), custom=true → "choice" (multiple options)
            var type = poll.Content.Custom ? "choice" : "vote";

            DateTime? timeEnd = poll.Content.EndTime > 0
                ? DateTimeOffset.FromUnixTimeMilliseconds(poll.Content.EndTime).UtcDateTime
                : null;

            var pollId = await _pollRepository.CreateAsync(systemId, new CreatePollCommand(title, desc, type, timeEnd), ct);
            if (pollId is null)
                continue;

            // Build the data payload with votes and (for custom polls) options, remapping SP member IDs to our alter IDs
            var data = BuildPollData(poll.Content, alterAssociations);
            if (data.ValueKind != JsonValueKind.Undefined)
            {
                await _pollRepository.UpdateAsync(systemId, new UpdatePollCommand(
                    Id: pollId,
                    Title: null,
                    Description: null,
                    TimeEnd: null,
                    HasTimeEnd: false,
                    Data: data
                ), ct);
            }
        }
    }

    private async Task ImportNotesAsync(
        HttpClient httpClient, string spSystemId, string systemId,
        Dictionary<string, int> alterAssociations, string encryptionKey, CancellationToken ct)
    {
        foreach (var (spMemberId, alterId) in alterAssociations)
        {
            var notes = await FetchAsync<List<SpEntity<SpNoteContent>>>(httpClient, $"{SpApiBase}/notes/{spSystemId}/{spMemberId}", ct);
            if (notes is null || notes.Count == 0)
                continue;

            foreach (var note in notes)
            {
                var title = note.Content.Title ?? "Imported note";
                if (title.Length > 100) title = title[..100];
                if (string.IsNullOrWhiteSpace(title)) title = "Imported note";

                // Convert SP timestamps (Unix milliseconds) to DateTimeOffset for audit preservation
                var createdAt = UnixTimestampToDateTimeOffset(note.Content.Date);
                var updatedAt = UnixTimestampToDateTimeOffset(note.Content.LastOperationTime);

                var entryId = await _journalRepository.CreateAlterAsync(systemId, new CreateAlterJournalEntryCommand(
                    AlterId: alterId,
                    Title: title,
                    CreatedAt: createdAt
                ), ct);

                if (entryId is null)
                    continue;

                var content = PrepareNote(note.Content.Note, encryptionKey, out var successful);
                var color = ParseColor(note.Content.Color);

                if (!successful && color is null)
                    continue;

                await _journalRepository.UpdateAlterAsync(systemId, new UpdateAlterJournalEntryCommand(
                    EntryId: entryId,
                    Title: null,
                    Content: content,
                    Color: color,
                    UpdatedAt: updatedAt
                ), ct);
            }
        }
    }

    private static string? PrepareNote(string? content, string encryptionKey, out bool successful)
    {
        if (string.IsNullOrEmpty(content))
        {
            successful = true;
            return content;
        }

        content = content.Length > MaxJournalContentLength
            ? content[..MaxJournalContentLength]
            : content;

        var encrypted = TryEncryptForClient(content, encryptionKey);
        if (encrypted is null)
        {
            successful = false;
            return null;
        }

        successful = true;
        return encrypted;
    }

    // Mirrors client encryptData: real AES-256-GCM with random IV, ciphertext and tag stored separately
    private static string? TryEncryptForClient(string plaintext, string base64Key)
    {
        try
        {
            var key = Convert.FromBase64String(base64Key);
            if (key.Length != 32)
                return null;

            var iv = new byte[12];
            RandomNumberGenerator.Fill(iv);

            var plainBytes = Encoding.UTF8.GetBytes(plaintext);
            var ciphertext = new byte[plainBytes.Length];
            var tag = new byte[16];

            using var aes = new AesGcm(key, tag.Length);
            aes.Encrypt(iv, plainBytes, ciphertext, tag);

            return $"enc|{Convert.ToBase64String(iv)}|{Convert.ToBase64String(ciphertext)}|{Convert.ToBase64String(tag)}";
        }
        catch
        {
            return null;
        }
    }

    private static JsonElement BuildPollData(SpPollContent poll, Dictionary<string, int> alterAssociations)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();

            // Write options for custom polls
            if (poll.Custom && poll.Options is { Count: > 0 })
            {
                writer.WriteStartArray("options");
                foreach (var option in poll.Options)
                {
                    writer.WriteStartObject();
                    writer.WriteString("name", option.Name ?? "");
                    if (option.Color is not null)
                        writer.WriteString("color", option.Color);
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
            }

            // Write votes, remapping SP member IDs to our alter IDs
            if (poll.Votes is { Count: > 0 })
            {
                writer.WriteStartArray("votes");
                foreach (var vote in poll.Votes)
                {
                    if (vote.Id is null)
                        continue;

                    // Remap SP member ID to our alter ID
                    var alterId = alterAssociations.TryGetValue(vote.Id, out var id) ? id.ToString() : vote.Id;

                    writer.WriteStartObject();
                    writer.WriteString("id", alterId);
                    writer.WriteString("vote", vote.Vote ?? "");
                    if (vote.Comment is not null)
                        writer.WriteString("comment", vote.Comment);
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
            }

            writer.WriteEndObject();
        }

        return JsonDocument.Parse(stream.ToArray()).RootElement.Clone();
    }

    private async Task DownloadAvatarsAsync(string systemId, List<AvatarDownload> downloads)
    {
        using var httpClient = _httpClientFactory.CreateClient("SimplyPlural");

        foreach (var download in downloads)
        {
            try
            {
                using var response = await httpClient.GetAsync(download.Url, HttpCompletionOption.ResponseHeadersRead);
                if (!response.IsSuccessStatusCode)
                    continue;

                await using var stream = await response.Content.ReadAsStreamAsync();
                //TODO: We should ideally be coverting it to webp for consistency of other images
                var avatarUrl = await _avatarStorage.SaveAlterAvatarAsync(download.SystemId, download.AlterId, stream);

                await _alterRepository.UpdateAsync(systemId, new UpdateAlterCommand(
                    AlterId: download.AlterId,
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
                    Pinned: null,
                    UpdatedAt: DateTimeOffset.UtcNow
                ));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to download avatar for alter {AlterId} in system {SystemId}", download.AlterId, download.SystemId);
            }
        }
    }


    private static async Task<T?> FetchAsync<T>(HttpClient httpClient, string url, CancellationToken ct)
    {
        try
        {
            return await httpClient.GetFromJsonAsync<T>(url, ct);
        }
        catch
        {
            return default;
        }
    }

    // 0 = string/text, 1 = color, 2 = date, 3 = month, 4 = year, 5 = month+year, 6 = timestamp, 7 = month+day
    private static string MapFieldType(int spType, bool supportMarkdown) => spType switch
    {
        0 => supportMarkdown ? "text" : "plaintext",
        1 => "colour",
        2 => "date",
        3 => "month",
        4 => "year",
        5 => "month_year",
        6 => "timestamp",
        7 => "month_day",
        _ => throw new ArgumentOutOfRangeException(nameof(spType), spType, "Unknown SP field type")
    };

    private static string MapSecurityLevel(bool isPrivate, bool preventTrusted) => (isPrivate, preventTrusted) switch
    {
        (false, _) => "public",
        (true, false) => "trusted_only",
        (true, true) => "private",
    };

    private static DateTimeOffset UnixTimestampToDateTimeOffset(long unixMilliseconds)
    {
        // Convert Unix epoch milliseconds to DateTimeOffset
        var epochTime = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var dateTime = epochTime.AddMilliseconds(unixMilliseconds);
        return new DateTimeOffset(dateTime, TimeSpan.Zero);
    }

    private static string? ParseColor(string? color)
    {
        if (string.IsNullOrWhiteSpace(color))
            return null;
        if (color.StartsWith('#') && color.Length == 7)
            return color;
        if (color.Length == 6 && !color.StartsWith('#'))
            return color;
        return null;
    }

    private sealed record AvatarDownload(string Url, string SystemId, int AlterId);
}
