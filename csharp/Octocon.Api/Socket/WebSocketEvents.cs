using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Octocon.Domain.Abstractions;
using Octocon.Domain.Accounts;
using Octocon.Domain.Alters;
using Octocon.Domain.Fronting;
using Octocon.Domain.Journals;
using Octocon.Domain.Polls;
using Octocon.Domain.Settings;
using Octocon.Domain.Tags;

namespace Octocon.Api.Socket;

public static class WebSocketEvents
{
    public static async Task PumpFrontingPushesAsync(
    WebSocket socket,
    IClusterEventBus eventBus,
    IFrontingRepository frontingRepository,
    System.Collections.Concurrent.ConcurrentDictionary<string, byte> joinedTopics,
    System.Collections.Concurrent.ConcurrentDictionary<string, string?> topicJoinReference,
    System.Collections.Concurrent.ConcurrentDictionary<string, bool> topicReplyAsArrayFrame,
    SemaphoreSlim sendGate,
    CancellationToken cancellationToken)
{
    await foreach (var evt in eventBus.SubscribeAsync<FrontingStateChangedEvent>(cancellationToken).ConfigureAwait(false))
    {
        var topic = $"system:{evt.SystemId}";
        if (!joinedTopics.ContainsKey(topic))
        {
            continue;
        }

        var fronts = await frontingRepository.ListActiveAsync(evt.SystemId, cancellationToken).ConfigureAwait(false);
        var payloadJson = SerializeSocketJson(new { fronts });

        topicJoinReference.TryGetValue(topic, out var joinRef);
        topicReplyAsArrayFrame.TryGetValue(topic, out var asArray);

        await SendPhoenixPushAsync(
            socket,
            topic,
            joinRef,
            eventName: "fronting_changed",
            payloadJson,
            asArray,
            cancellationToken,
            sendGate);
    }
}

public static async Task PumpAlterPushesAsync(
    WebSocket socket,
    IClusterEventBus eventBus,
    IAlterRepository alterRepository,
    System.Collections.Concurrent.ConcurrentDictionary<string, byte> joinedTopics,
    System.Collections.Concurrent.ConcurrentDictionary<string, string?> topicJoinReference,
    System.Collections.Concurrent.ConcurrentDictionary<string, bool> topicReplyAsArrayFrame,
    SemaphoreSlim sendGate,
    CancellationToken cancellationToken)
{
    await foreach (var evt in eventBus.SubscribeAsync<AlterChangedEvent>(cancellationToken).ConfigureAwait(false))
    {
        var topic = $"system:{evt.SystemId}";
        if (!joinedTopics.ContainsKey(topic))
        {
            continue;
        }

        string payloadJson;
        if (string.Equals(evt.EventName, "alter_deleted", StringComparison.Ordinal) && evt.AlterId.HasValue)
        {
            payloadJson = SerializeSocketJson(new Dictionary<string, object?> { ["alter_id"] = evt.AlterId.Value });
        }
        else
        {
            if (!evt.AlterId.HasValue)
            {
                continue;
            }

            var alter = await alterRepository.GetAsync(evt.SystemId, evt.AlterId.Value, cancellationToken).ConfigureAwait(false);
            if (alter is null)
            {
                continue;
            }

            payloadJson = SerializeSocketJson(new { alter });
        }

        topicJoinReference.TryGetValue(topic, out var joinRef);
        topicReplyAsArrayFrame.TryGetValue(topic, out var asArray);

        await SendPhoenixPushAsync(
            socket,
            topic,
            joinRef,
            eventName: evt.EventName,
            payloadJson,
            asArray,
            cancellationToken,
            sendGate);
    }
}

public static async Task PumpTagPushesAsync(
    WebSocket socket,
    IClusterEventBus eventBus,
    ITagRepository tagRepository,
    System.Collections.Concurrent.ConcurrentDictionary<string, byte> joinedTopics,
    System.Collections.Concurrent.ConcurrentDictionary<string, string?> topicJoinReference,
    System.Collections.Concurrent.ConcurrentDictionary<string, bool> topicReplyAsArrayFrame,
    SemaphoreSlim sendGate,
    CancellationToken cancellationToken)
{
    await foreach (var evt in eventBus.SubscribeAsync<TagChangedEvent>(cancellationToken).ConfigureAwait(false))
    {
        var topic = $"system:{evt.SystemId}";
        if (!joinedTopics.ContainsKey(topic))
        {
            continue;
        }

        string payloadJson;
        if (string.Equals(evt.EventName, "tag_deleted", StringComparison.Ordinal))
        {
            payloadJson = SerializeSocketJson(new Dictionary<string, object?> { ["tag_id"] = evt.TagId });
        }
        else
        {
            var tag = await tagRepository.GetAsync(evt.SystemId, evt.TagId, cancellationToken).ConfigureAwait(false);
            if (tag is null)
            {
                continue;
            }

            payloadJson = SerializeSocketJson(new { tag });
        }

        topicJoinReference.TryGetValue(topic, out var joinRef);
        topicReplyAsArrayFrame.TryGetValue(topic, out var asArray);

        await SendPhoenixPushAsync(
            socket,
            topic,
            joinRef,
            eventName: evt.EventName,
            payloadJson,
            asArray,
            cancellationToken,
            sendGate);
    }
}

public static async Task PumpSettingsFieldsPushesAsync(
    WebSocket socket,
    IClusterEventBus eventBus,
    ISettingsFieldRepository fieldRepository,
    System.Collections.Concurrent.ConcurrentDictionary<string, byte> joinedTopics,
    System.Collections.Concurrent.ConcurrentDictionary<string, string?> topicJoinReference,
    System.Collections.Concurrent.ConcurrentDictionary<string, bool> topicReplyAsArrayFrame,
    SemaphoreSlim sendGate,
    CancellationToken cancellationToken)
{
    await foreach (var evt in eventBus.SubscribeAsync<SettingsFieldsChangedEvent>(cancellationToken).ConfigureAwait(false))
    {
        var topic = $"system:{evt.SystemId}";
        if (!joinedTopics.ContainsKey(topic))
        {
            continue;
        }

        var fields = await fieldRepository.ListAsync(evt.SystemId, cancellationToken).ConfigureAwait(false);
        var payloadJson = SerializeSocketJson(new { fields });

        topicJoinReference.TryGetValue(topic, out var joinRef);
        topicReplyAsArrayFrame.TryGetValue(topic, out var asArray);

        await SendPhoenixPushAsync(
            socket,
            topic,
            joinRef,
            eventName: "fields_updated",
            payloadJson,
            asArray,
            cancellationToken,
            sendGate);
    }
}

public static async Task PumpRawPushesAsync(
    WebSocket socket,
    IClusterEventBus eventBus,
    System.Collections.Concurrent.ConcurrentDictionary<string, byte> joinedTopics,
    System.Collections.Concurrent.ConcurrentDictionary<string, string?> topicJoinReference,
    System.Collections.Concurrent.ConcurrentDictionary<string, bool> topicReplyAsArrayFrame,
    SemaphoreSlim sendGate,
    CancellationToken cancellationToken)
{
    await foreach (var evt in eventBus.SubscribeAsync<SocketRawPushEvent>(cancellationToken).ConfigureAwait(false))
    {
        var topic = $"system:{evt.SystemId}";
        if (!joinedTopics.ContainsKey(topic))
        {
            continue;
        }

        topicJoinReference.TryGetValue(topic, out var joinRef);
        topicReplyAsArrayFrame.TryGetValue(topic, out var asArray);

        await SendPhoenixPushAsync(
            socket,
            topic,
            joinRef,
            eventName: evt.EventName,
            evt.PayloadJson,
            asArray,
            cancellationToken,
            sendGate);
    }
}

public static async Task PumpFriendshipPushesAsync(
    WebSocket socket,
    IClusterEventBus eventBus,
    System.Collections.Concurrent.ConcurrentDictionary<string, byte> joinedTopics,
    System.Collections.Concurrent.ConcurrentDictionary<string, string?> topicJoinReference,
    System.Collections.Concurrent.ConcurrentDictionary<string, bool> topicReplyAsArrayFrame,
    SemaphoreSlim sendGate,
    CancellationToken cancellationToken)
{
    await foreach (var evt in eventBus.SubscribeAsync<FriendshipSocketEvent>(cancellationToken).ConfigureAwait(false))
    {
        var topic = $"system:{evt.TargetSystemId}";
        if (!joinedTopics.ContainsKey(topic))
        {
            continue;
        }

        topicJoinReference.TryGetValue(topic, out var joinRef);
        topicReplyAsArrayFrame.TryGetValue(topic, out var asArray);

        var payloadJson = SerializeSocketJson(new Dictionary<string, object?>
        {
            [evt.PayloadKey] = evt.PayloadValue
        });

        await SendPhoenixPushAsync(
            socket,
            topic,
            joinRef,
            evt.EventName,
            payloadJson,
            asArray,
            cancellationToken,
            sendGate);
    }
}

public static async Task PublishRelayDomainEventsAsync(
    HttpContext context,
    string? systemId,
    string method,
    string path,
    int statusCode,
    string responseBody,
    string? requestBodyJson,
    CancellationToken ct)
{
    if (string.IsNullOrWhiteSpace(systemId)
        || statusCode is < 200 or >= 300
        || string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase)
        || string.Equals(method, "HEAD", StringComparison.OrdinalIgnoreCase))
    {
        return;
    }

    var eventBus = context.RequestServices.GetRequiredService<IClusterEventBus>();

    // ── Alters ────────────────────────────────────────────────────────────
    if (path.StartsWith("/api/systems/me/alters", StringComparison.OrdinalIgnoreCase))
    {
        // Alter journals live under /api/systems/me/alters/{id}/journals – handle first
        if (path.Contains("/journals", StringComparison.OrdinalIgnoreCase))
        {
            await HandleAlterJournalRelayAsync(eventBus, context, systemId, method, path, responseBody, ct);
            return;
        }
        // Non-journal alter events are published by domain command handlers.
        return;
    }

    // ── Tags ──────────────────────────────────────────────────────────────
    if (path.StartsWith("/api/systems/me/tags", StringComparison.OrdinalIgnoreCase))
    {
        // Tag events are published by domain command handlers.
        return;
    }

    // ── Settings: custom fields ───────────────────────────────────────────
    if (path.StartsWith("/api/settings/fields", StringComparison.OrdinalIgnoreCase))
    {
        // settings field events are published by domain command handlers.
        return;
    }

    // ── Settings: account management ──────────────────────────────────────
    if (path.StartsWith("/api/settings", StringComparison.OrdinalIgnoreCase))
    {
        if (string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase))
        {
            if (path.EndsWith("/delete-account", StringComparison.OrdinalIgnoreCase))
            {
                await eventBus.PublishAsync(new SocketRawPushEvent(systemId, "account_deleted", "{}"), ct);
                return;
            }
            if (path.EndsWith("/wipe-alters", StringComparison.OrdinalIgnoreCase))
            {
                await eventBus.PublishAsync(new SocketRawPushEvent(systemId, "alters_wiped", "{}"), ct);
                return;
            }
            if (path.EndsWith("/reset-encryption", StringComparison.OrdinalIgnoreCase))
            {
                await eventBus.PublishAsync(new SocketRawPushEvent(systemId, "encrypted_data_wiped", "{}"), ct);
                return;
            }
            if (path.EndsWith("/unlink_discord", StringComparison.OrdinalIgnoreCase))
            {
                await eventBus.PublishAsync(new SocketRawPushEvent(systemId, "discord_account_unlinked", "{}"), ct);
                return;
            }
            if (path.EndsWith("/unlink_apple", StringComparison.OrdinalIgnoreCase))
            {
                await eventBus.PublishAsync(new SocketRawPushEvent(systemId, "apple_account_unlinked", "{}"), ct);
                return;
            }
        }

        // Profile-mutating operations → self_updated (and username_updated when applicable)
        var isAvatarWrite = path.EndsWith("/avatar", StringComparison.OrdinalIgnoreCase);
        var isPost = string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase);
        var emitsProfileUpdate =
            isAvatarWrite ||
            (isPost && (
                path.EndsWith("/username", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith("/description", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith("/import-pk", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith("/import-sp", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith("/setup-encryption", StringComparison.OrdinalIgnoreCase)));

        if (emitsProfileUpdate)
        {
            var accountRepo = context.RequestServices.GetRequiredService<IAccountRepository>();
            var profile = await accountRepo.GetPublicProfileAsync(systemId, ct);
            if (isPost && path.EndsWith("/username", StringComparison.OrdinalIgnoreCase) && profile?.Username != null)
            {
                var usernamePayload = SerializeSocketJson(new { username = profile.Username });
                await eventBus.PublishAsync(new SocketRawPushEvent(systemId, "username_updated", usernamePayload), ct);
            }
            var selfData = new Dictionary<string, object?>
            {
                ["id"] = profile?.SystemId ?? systemId,
                ["username"] = profile?.Username,
                ["description"] = profile?.Description,
                ["avatar_url"] = profile?.AvatarUrl,
                ["autoproxy_mode"] = "off",
                ["show_system_tag"] = false
            };
            await eventBus.PublishAsync(new SocketRawPushEvent(systemId, "self_updated", SerializeSocketJson(new { data = selfData })), ct);
        }

        return;
    }

    // ── Fronting ──────────────────────────────────────────────────────────
    if (path.StartsWith("/api/systems/me/front", StringComparison.OrdinalIgnoreCase))
    {
        // primary_front: SetPrimaryFrontCommandHandler already publishes FrontingStateChangedEvent
        // but we also want to emit the specific primary_front event
        if (path.EndsWith("/primary", StringComparison.OrdinalIgnoreCase)
            && string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase))
        {
            int? alterIdForPrimary = null;
            if (!string.IsNullOrEmpty(requestBodyJson))
            {
                try
                {
                    using var reqDoc = JsonDocument.Parse(requestBodyJson);
                    if (reqDoc.RootElement.TryGetProperty("alter_id", out var aidProp) && aidProp.TryGetInt32(out var aid))
                        alterIdForPrimary = aid;
                    else if (reqDoc.RootElement.TryGetProperty("id", out var idProp) && idProp.TryGetInt32(out var id2))
                        alterIdForPrimary = id2;
                }
                catch (JsonException) { }
            }
            await eventBus.PublishAsync(new SocketRawPushEvent(systemId, "primary_front",
                SerializeSocketJson(new { alter_id = alterIdForPrimary })), ct);

            // Also emit self_updated since set_primary_front mutates the user record
            var accountRepo = context.RequestServices.GetRequiredService<IAccountRepository>();
            var profile = await accountRepo.GetPublicProfileAsync(systemId, ct);
            var selfData = new Dictionary<string, object?>
            {
                ["id"] = profile?.SystemId ?? systemId,
                ["username"] = profile?.Username,
                ["description"] = profile?.Description,
                ["avatar_url"] = profile?.AvatarUrl,
                ["autoproxy_mode"] = "off",
                ["show_system_tag"] = false
            };
            await eventBus.PublishAsync(new SocketRawPushEvent(systemId, "self_updated", SerializeSocketJson(new { data = selfData })), ct);
            return;
        }

        if (string.Equals(method, "DELETE", StringComparison.OrdinalIgnoreCase)
            && TryExtractTerminalString(path, "/api/systems/me/front", out var deletedFrontId)
            && !string.IsNullOrWhiteSpace(deletedFrontId)
            && char.IsDigit(deletedFrontId[0]))
        {
            // front_deleted with the front ID
            await eventBus.PublishAsync(new SocketRawPushEvent(systemId, "front_deleted",
                SerializeSocketJson(new { front_id = deletedFrontId })), ct);
            // FrontingStateChangedEvent already fired by DeleteFrontByIdCommandHandler (added in todo 9)
            return;
        }

        // POST /api/systems/me/front/{id}/comment → front_updated
        if (string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase)
            && path.EndsWith("/comment", StringComparison.OrdinalIgnoreCase))
        {
            var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            // segments: [api, systems, me, front, {id}, comment]
            if (segments.Length >= 2 && int.TryParse(segments[^2], out var frontIdForComment))
            {
                var frontingRepo = context.RequestServices.GetRequiredService<IFrontingRepository>();
                var front = await frontingRepo.GetActiveByFrontIdAsync(systemId, frontIdForComment.ToString(), ct);
                if (front is not null)
                {
                    await eventBus.PublishAsync(new SocketRawPushEvent(systemId, "front_updated",
                        SerializeSocketJson(new { front })), ct);
                }
            }
            return;
        }

        // bulk update (POST /api/systems/me/front) and set (POST /api/systems/me/front/set)
        // FrontingStateChangedEvent is published by the command handlers (added in todo 9)
        return;
    }

    // ── Polls ─────────────────────────────────────────────────────────────
    if (path.StartsWith("/api/polls", StringComparison.OrdinalIgnoreCase))
    {
        var pollRepo = context.RequestServices.GetRequiredService<IPollRepository>();

        if (statusCode is 201
            && string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase)
            && string.Equals(path, "/api/polls", StringComparison.OrdinalIgnoreCase))
        {
            var poll = TryExtractEntityFromResponse(responseBody);
            if (poll is not null)
                await eventBus.PublishAsync(new SocketRawPushEvent(systemId, "poll_created", "{\"poll\":" + poll + "}"), ct);
            return;
        }

        if (TryExtractTerminalString(path, "/api/polls", out var pollId))
        {
            if (string.Equals(method, "PATCH", StringComparison.OrdinalIgnoreCase))
            {
                var poll = await pollRepo.GetAsync(systemId, pollId, ct);
                if (poll is not null)
                    await eventBus.PublishAsync(new SocketRawPushEvent(systemId, "poll_updated",
                        SerializeSocketJson(new { poll })), ct);
                return;
            }
            if (string.Equals(method, "DELETE", StringComparison.OrdinalIgnoreCase))
            {
                await eventBus.PublishAsync(new SocketRawPushEvent(systemId, "poll_deleted",
                    SerializeSocketJson(new { poll_id = pollId })), ct);
                return;
            }
        }

        return;
    }

    // ── Global journals ───────────────────────────────────────────────────
    if (path.StartsWith("/api/journals", StringComparison.OrdinalIgnoreCase))
    {
        await HandleGlobalJournalRelayAsync(eventBus, context, systemId, method, path, responseBody, ct);
        return;
    }

    // ── Friends ───────────────────────────────────────────────────────────
    if (path.StartsWith("/api/friends", StringComparison.OrdinalIgnoreCase))
    {
        // Friend events are published by domain command handlers.
        return;
    }

    // ── Friend requests ───────────────────────────────────────────────────
    if (path.StartsWith("/api/friend-requests", StringComparison.OrdinalIgnoreCase))
    {
        // Friend request events are published by domain command handlers.
        return;
    }
}

// Helper: journal fan-out for global journals
static async Task HandleGlobalJournalRelayAsync(
    IClusterEventBus eventBus,
    HttpContext context,
    string systemId,
    string method,
    string path,
    string responseBody,
    CancellationToken ct)
{
    var journalRepo = context.RequestServices.GetRequiredService<IJournalRepository>();

    // POST /api/journals → create
    if (string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase)
        && string.Equals(path, "/api/journals", StringComparison.OrdinalIgnoreCase))
    {
        var entryRaw = TryExtractEntityFromResponse(responseBody);
        if (entryRaw is not null)
            await eventBus.PublishAsync(new SocketRawPushEvent(systemId, "global_journal_entry_created",
                "{\"entry\":" + entryRaw + "}"), ct);
        return;
    }

    if (TryExtractTerminalString(path, "/api/journals", out var entryId))
    {
        if (string.Equals(method, "DELETE", StringComparison.OrdinalIgnoreCase))
        {
            await eventBus.PublishAsync(new SocketRawPushEvent(systemId, "global_journal_entry_deleted",
                SerializeSocketJson(new { entry_id = entryId })), ct);
            return;
        }

        // PATCH or POST sub-operations (alters, locked, pinned) → entry updated
        var entry = await journalRepo.GetGlobalAsync(systemId, entryId, ct);
        if (entry is not null)
            await eventBus.PublishAsync(new SocketRawPushEvent(systemId, "global_journal_entry_updated",
                SerializeSocketJson(new { entry })), ct);
    }
}

// Helper: journal fan-out for alter journals (/api/systems/me/alters/{alterId}/journals/...)
static async Task HandleAlterJournalRelayAsync(
    IClusterEventBus eventBus,
    HttpContext context,
    string systemId,
    string method,
    string path,
    string responseBody,
    CancellationToken ct)
{
    var journalRepo = context.RequestServices.GetRequiredService<IJournalRepository>();

    // Find the journals segment index to extract entry ID
    var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
    // segments: [api, systems, me, alters, {alterId}, journals[, {entryId}]]
    var journalsIdx = Array.FindIndex(segments, s => string.Equals(s, "journals", StringComparison.OrdinalIgnoreCase));
    if (journalsIdx < 0) return;

    var isCreate = string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase)
        && journalsIdx == segments.Length - 1;

    if (isCreate)
    {
        var entryRaw = TryExtractEntityFromResponse(responseBody);
        if (entryRaw is not null)
            await eventBus.PublishAsync(new SocketRawPushEvent(systemId, "alter_journal_entry_created",
                "{\"entry\":" + entryRaw + "}"), ct);
        return;
    }

    if (journalsIdx + 1 < segments.Length)
    {
        var entryId = segments[journalsIdx + 1];
        if (string.Equals(method, "DELETE", StringComparison.OrdinalIgnoreCase))
        {
            await eventBus.PublishAsync(new SocketRawPushEvent(systemId, "alter_journal_entry_deleted",
                SerializeSocketJson(new { entry_id = entryId })), ct);
            return;
        }

        var entry = await journalRepo.GetAlterAsync(systemId, entryId, ct);
        if (entry is not null)
            await eventBus.PublishAsync(new SocketRawPushEvent(systemId, "alter_journal_entry_updated",
                SerializeSocketJson(new { entry })), ct);
    }
}

static bool TryExtractTerminalString(string path, string prefix, out string segment)
{
    segment = string.Empty;
    if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
    {
        return false;
    }

    var tail = path[prefix.Length..].Trim('/');
    if (string.IsNullOrWhiteSpace(tail))
    {
        return false;
    }

    var first = tail.Split('/', StringSplitOptions.RemoveEmptyEntries)[0];
    if (string.IsNullOrWhiteSpace(first))
    {
        return false;
    }

    segment = first;
    return true;
}

public static async Task SendPhoenixPushAsync(
    WebSocket socket,
    string topic,
    string? joinReference,
    string eventName,
    string payloadJson,
    bool replyAsArrayFrame,
    CancellationToken cancellationToken,
    SemaphoreSlim? sendGate = null)
{
    var escapedTopic = JsonSerializer.Serialize(topic);
    var escapedJoinRef = joinReference is null ? "null" : JsonSerializer.Serialize(joinReference);
    var escapedEvent = JsonSerializer.Serialize(eventName);

    var frame = replyAsArrayFrame
        ?
        "[" +
        escapedJoinRef + "," +
        "null," +
        escapedTopic + "," +
        escapedEvent + "," +
        payloadJson +
        "]"
        :
        "{" +
        "\"topic\":" + escapedTopic + "," +
        "\"event\":" + escapedEvent + "," +
        "\"payload\":" + payloadJson + "," +
        "\"ref\":null," +
        "\"join_ref\":" + escapedJoinRef +
        "}";

    var bytes = Encoding.UTF8.GetBytes(frame);
    if (sendGate is not null)
    {
        await sendGate.WaitAsync(cancellationToken);
        try
        {
            await socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, cancellationToken);
        }
        finally
        {
            sendGate.Release();
        }
    }
    else
    {
        await socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, cancellationToken);
    }
}

// Helper: extract the raw JSON of the "data" field from a response body
static string? TryExtractEntityFromResponse(string responseBody)
{
    try
    {
        using var doc = JsonDocument.Parse(responseBody);
        return doc.RootElement.TryGetProperty("data", out var data)
            ? data.GetRawText()
            : null;
    }
    catch (JsonException)
    {
        return null;
    }
}

public static string SerializeSocketJson(this object value)
{
    var opts = new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };
    return JsonSerializer.Serialize(value, opts);
}

}