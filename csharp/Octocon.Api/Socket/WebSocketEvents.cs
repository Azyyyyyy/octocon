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

public static async Task PumpFrontingStartedPushesAsync(
    WebSocket socket,
    IClusterEventBus eventBus,
    IFrontingRepository frontingRepository,
    System.Collections.Concurrent.ConcurrentDictionary<string, byte> joinedTopics,
    System.Collections.Concurrent.ConcurrentDictionary<string, string?> topicJoinReference,
    System.Collections.Concurrent.ConcurrentDictionary<string, bool> topicReplyAsArrayFrame,
    SemaphoreSlim sendGate,
    CancellationToken cancellationToken)
{
    await foreach (var evt in eventBus.SubscribeAsync<FrontingStartedEvent>(cancellationToken).ConfigureAwait(false))
    {
        var topic = $"system:{evt.SystemId}";
        if (!joinedTopics.ContainsKey(topic)) continue;

        var front = await frontingRepository.GetActiveByFrontIdAsync(evt.SystemId, evt.FrontId, cancellationToken).ConfigureAwait(false);
        if (front is null) continue;

        var payloadJson = SerializeSocketJson(new { front });
        topicJoinReference.TryGetValue(topic, out var joinRef);
        topicReplyAsArrayFrame.TryGetValue(topic, out var asArray);

        await SendPhoenixPushAsync(socket, topic, joinRef, eventName: "fronting_started", payloadJson, asArray, cancellationToken, sendGate);
    }
}

public static async Task PumpFrontingEndedPushesAsync(
    WebSocket socket,
    IClusterEventBus eventBus,
    System.Collections.Concurrent.ConcurrentDictionary<string, byte> joinedTopics,
    System.Collections.Concurrent.ConcurrentDictionary<string, string?> topicJoinReference,
    System.Collections.Concurrent.ConcurrentDictionary<string, bool> topicReplyAsArrayFrame,
    SemaphoreSlim sendGate,
    CancellationToken cancellationToken)
{
    await foreach (var evt in eventBus.SubscribeAsync<FrontingEndedEvent>(cancellationToken).ConfigureAwait(false))
    {
        var topic = $"system:{evt.SystemId}";
        if (!joinedTopics.ContainsKey(topic)) continue;

        var payloadJson = SerializeSocketJson(new { alter_id = evt.AlterId });
        topicJoinReference.TryGetValue(topic, out var joinRef);
        topicReplyAsArrayFrame.TryGetValue(topic, out var asArray);

        await SendPhoenixPushAsync(socket, topic, joinRef, eventName: "fronting_ended", payloadJson, asArray, cancellationToken, sendGate);
    }
}

public static async Task PumpFrontingSetPushesAsync(
    WebSocket socket,
    IClusterEventBus eventBus,
    IFrontingRepository frontingRepository,
    System.Collections.Concurrent.ConcurrentDictionary<string, byte> joinedTopics,
    System.Collections.Concurrent.ConcurrentDictionary<string, string?> topicJoinReference,
    System.Collections.Concurrent.ConcurrentDictionary<string, bool> topicReplyAsArrayFrame,
    SemaphoreSlim sendGate,
    CancellationToken cancellationToken)
{
    await foreach (var evt in eventBus.SubscribeAsync<FrontingSetEvent>(cancellationToken).ConfigureAwait(false))
    {
        var topic = $"system:{evt.SystemId}";
        if (!joinedTopics.ContainsKey(topic)) continue;

        var front = await frontingRepository.GetActiveByFrontIdAsync(evt.SystemId, evt.FrontId, cancellationToken).ConfigureAwait(false);
        if (front is null) continue;

        var payloadJson = SerializeSocketJson(new { front });
        topicJoinReference.TryGetValue(topic, out var joinRef);
        topicReplyAsArrayFrame.TryGetValue(topic, out var asArray);

        await SendPhoenixPushAsync(socket, topic, joinRef, eventName: "fronting_set", payloadJson, asArray, cancellationToken, sendGate);
    }
}

public static async Task PumpFrontingBulkUpdatedPushesAsync(
    WebSocket socket,
    IClusterEventBus eventBus,
    IFrontingRepository frontingRepository,
    System.Collections.Concurrent.ConcurrentDictionary<string, byte> joinedTopics,
    System.Collections.Concurrent.ConcurrentDictionary<string, string?> topicJoinReference,
    System.Collections.Concurrent.ConcurrentDictionary<string, bool> topicReplyAsArrayFrame,
    SemaphoreSlim sendGate,
    CancellationToken cancellationToken)
{
    await foreach (var evt in eventBus.SubscribeAsync<FrontingBulkUpdatedEvent>(cancellationToken).ConfigureAwait(false))
    {
        var topic = $"system:{evt.SystemId}";
        if (!joinedTopics.ContainsKey(topic)) continue;

        var fronts = await frontingRepository.ListActiveAsync(evt.SystemId, cancellationToken).ConfigureAwait(false);
        var payloadJson = SerializeSocketJson(new { fronts });

        topicJoinReference.TryGetValue(topic, out var joinRef);
        topicReplyAsArrayFrame.TryGetValue(topic, out var asArray);

        await SendPhoenixPushAsync(socket, topic, joinRef, eventName: "fronting_bulk", payloadJson, asArray, cancellationToken, sendGate);
    }
}

public static async Task PumpFrontCommentUpdatedPushesAsync(
    WebSocket socket,
    IClusterEventBus eventBus,
    IFrontingRepository frontingRepository,
    System.Collections.Concurrent.ConcurrentDictionary<string, byte> joinedTopics,
    System.Collections.Concurrent.ConcurrentDictionary<string, string?> topicJoinReference,
    System.Collections.Concurrent.ConcurrentDictionary<string, bool> topicReplyAsArrayFrame,
    SemaphoreSlim sendGate,
    CancellationToken cancellationToken)
{
    await foreach (var evt in eventBus.SubscribeAsync<FrontCommentUpdatedEvent>(cancellationToken).ConfigureAwait(false))
    {
        var topic = $"system:{evt.SystemId}";
        if (!joinedTopics.ContainsKey(topic)) continue;

        var front = await frontingRepository.GetActiveByFrontIdAsync(evt.SystemId, evt.FrontId, cancellationToken).ConfigureAwait(false);
        if (front is null) continue;

        var payloadJson = SerializeSocketJson(new { front });
        topicJoinReference.TryGetValue(topic, out var joinRef);
        topicReplyAsArrayFrame.TryGetValue(topic, out var asArray);

        await SendPhoenixPushAsync(socket, topic, joinRef, eventName: "front_updated", payloadJson, asArray, cancellationToken, sendGate);
    }
}

public static async Task PumpFrontingPrimaryChangedPushesAsync(
    WebSocket socket,
    IClusterEventBus eventBus,
    System.Collections.Concurrent.ConcurrentDictionary<string, byte> joinedTopics,
    System.Collections.Concurrent.ConcurrentDictionary<string, string?> topicJoinReference,
    System.Collections.Concurrent.ConcurrentDictionary<string, bool> topicReplyAsArrayFrame,
    SemaphoreSlim sendGate,
    CancellationToken cancellationToken)
{
    await foreach (var evt in eventBus.SubscribeAsync<FrontingPrimaryChangedEvent>(cancellationToken).ConfigureAwait(false))
    {
        var topic = $"system:{evt.SystemId}";
        if (!joinedTopics.ContainsKey(topic))
        {
            continue;
        }

        var payloadJson = SerializeSocketJson(new { alter_id = evt.AlterId });
        topicJoinReference.TryGetValue(topic, out var joinRef);
        topicReplyAsArrayFrame.TryGetValue(topic, out var asArray);

        await SendPhoenixPushAsync(
            socket,
            topic,
            joinRef,
            eventName: "primary_front",
            payloadJson,
            asArray,
            cancellationToken,
            sendGate);
    }
}

public static async Task PumpFrontDeletedPushesAsync(
    WebSocket socket,
    IClusterEventBus eventBus,
    System.Collections.Concurrent.ConcurrentDictionary<string, byte> joinedTopics,
    System.Collections.Concurrent.ConcurrentDictionary<string, string?> topicJoinReference,
    System.Collections.Concurrent.ConcurrentDictionary<string, bool> topicReplyAsArrayFrame,
    SemaphoreSlim sendGate,
    CancellationToken cancellationToken)
{
    await foreach (var evt in eventBus.SubscribeAsync<FrontDeletedEvent>(cancellationToken).ConfigureAwait(false))
    {
        var topic = $"system:{evt.SystemId}";
        if (!joinedTopics.ContainsKey(topic))
        {
            continue;
        }

        var payloadJson = SerializeSocketJson(new { front_id = evt.FrontId });

        topicJoinReference.TryGetValue(topic, out var joinRef);
        topicReplyAsArrayFrame.TryGetValue(topic, out var asArray);

        await SendPhoenixPushAsync(
            socket,
            topic,
            joinRef,
            eventName: "front_deleted",
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

public static async Task PumpSettingsProfilePushesAsync(
    WebSocket socket,
    IClusterEventBus eventBus,
    IAccountRepository accountRepository,
    System.Collections.Concurrent.ConcurrentDictionary<string, byte> joinedTopics,
    System.Collections.Concurrent.ConcurrentDictionary<string, string?> topicJoinReference,
    System.Collections.Concurrent.ConcurrentDictionary<string, bool> topicReplyAsArrayFrame,
    SemaphoreSlim sendGate,
    CancellationToken cancellationToken)
{
    await foreach (var evt in eventBus.SubscribeAsync<SettingsProfileUpdatedEvent>(cancellationToken).ConfigureAwait(false))
    {
        var topic = $"system:{evt.SystemId}";
        if (!joinedTopics.ContainsKey(topic))
        {
            continue;
        }

        var profile = await accountRepository.GetPublicProfileAsync(evt.SystemId, cancellationToken).ConfigureAwait(false);

        topicJoinReference.TryGetValue(topic, out var joinRef);
        topicReplyAsArrayFrame.TryGetValue(topic, out var asArray);

        if (evt.EmitUsernameUpdated && profile?.Username is not null)
        {
            var usernamePayloadJson = SerializeSocketJson(new { username = profile.Username });
            await SendPhoenixPushAsync(
                socket,
                topic,
                joinRef,
                eventName: "username_updated",
                usernamePayloadJson,
                asArray,
                cancellationToken,
                sendGate);
        }

        var selfData = new Dictionary<string, object?>
        {
            ["id"] = profile?.SystemId ?? evt.SystemId,
            ["username"] = profile?.Username,
            ["description"] = profile?.Description,
            ["avatar_url"] = profile?.AvatarUrl,
            ["autoproxy_mode"] = "off",
            ["show_system_tag"] = false
        };

        var payloadJson = SerializeSocketJson(new { data = selfData });
        await SendPhoenixPushAsync(
            socket,
            topic,
            joinRef,
            eventName: "self_updated",
            payloadJson,
            asArray,
            cancellationToken,
            sendGate);
    }
}

public static async Task PumpSettingsSignalPushesAsync(
    WebSocket socket,
    IClusterEventBus eventBus,
    System.Collections.Concurrent.ConcurrentDictionary<string, byte> joinedTopics,
    System.Collections.Concurrent.ConcurrentDictionary<string, string?> topicJoinReference,
    System.Collections.Concurrent.ConcurrentDictionary<string, bool> topicReplyAsArrayFrame,
    SemaphoreSlim sendGate,
    CancellationToken cancellationToken)
{
    await foreach (var evt in eventBus.SubscribeAsync<SettingsSocketSignalEvent>(cancellationToken).ConfigureAwait(false))
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
            payloadJson: "{}",
            asArray,
            cancellationToken,
            sendGate);
    }
}

public static async Task PumpSettingsAccountLinkedPushesAsync(
    WebSocket socket,
    IClusterEventBus eventBus,
    System.Collections.Concurrent.ConcurrentDictionary<string, byte> joinedTopics,
    System.Collections.Concurrent.ConcurrentDictionary<string, string?> topicJoinReference,
    System.Collections.Concurrent.ConcurrentDictionary<string, bool> topicReplyAsArrayFrame,
    SemaphoreSlim sendGate,
    CancellationToken cancellationToken)
{
    await foreach (var evt in eventBus.SubscribeAsync<SettingsAccountLinkedEvent>(cancellationToken).ConfigureAwait(false))
    {
        var topic = $"system:{evt.SystemId}";
        if (!joinedTopics.ContainsKey(topic))
        {
            continue;
        }

        topicJoinReference.TryGetValue(topic, out var joinRef);
        topicReplyAsArrayFrame.TryGetValue(topic, out var asArray);

        var eventName = evt.ProviderKey switch
        {
            "discord" => "discord_account_linked",
            "google" => "google_account_linked",
            "apple" => "apple_account_linked",
            _ => "account_linked"
        };

        var payloadJson = evt.ProviderKey switch
        {
            "discord" => SerializeSocketJson(new { discord_id = evt.Identity }),
            "google" => SerializeSocketJson(new { email = evt.Identity }),
            "apple" => SerializeSocketJson(new { apple_id = evt.Identity }),
            _ => "{}"
        };

        await SendPhoenixPushAsync(
            socket,
            topic,
            joinRef,
            eventName,
            payloadJson,
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

public static async Task PumpPollPushesAsync(
    WebSocket socket,
    IClusterEventBus eventBus,
    IPollRepository pollRepository,
    System.Collections.Concurrent.ConcurrentDictionary<string, byte> joinedTopics,
    System.Collections.Concurrent.ConcurrentDictionary<string, string?> topicJoinReference,
    System.Collections.Concurrent.ConcurrentDictionary<string, bool> topicReplyAsArrayFrame,
    SemaphoreSlim sendGate,
    CancellationToken cancellationToken)
{
    await foreach (var evt in eventBus.SubscribeAsync<PollChangedEvent>(cancellationToken).ConfigureAwait(false))
    {
        var topic = $"system:{evt.SystemId}";
        if (!joinedTopics.ContainsKey(topic))
        {
            continue;
        }

        topicJoinReference.TryGetValue(topic, out var joinRef);
        topicReplyAsArrayFrame.TryGetValue(topic, out var asArray);

        string payloadJson;
        if (string.Equals(evt.EventName, "poll_deleted", StringComparison.Ordinal))
        {
            payloadJson = SerializeSocketJson(new Dictionary<string, object?> { ["poll_id"] = evt.PollId });
        }
        else
        {
            var poll = await pollRepository.GetAsync(evt.SystemId, evt.PollId, cancellationToken).ConfigureAwait(false);
            if (poll is null)
            {
                continue;
            }

            payloadJson = SerializeSocketJson(new { poll });
        }

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

public static async Task PumpGlobalJournalPushesAsync(
    WebSocket socket,
    IClusterEventBus eventBus,
    IJournalRepository journalRepository,
    System.Collections.Concurrent.ConcurrentDictionary<string, byte> joinedTopics,
    System.Collections.Concurrent.ConcurrentDictionary<string, string?> topicJoinReference,
    System.Collections.Concurrent.ConcurrentDictionary<string, bool> topicReplyAsArrayFrame,
    SemaphoreSlim sendGate,
    CancellationToken cancellationToken)
{
    await foreach (var evt in eventBus.SubscribeAsync<GlobalJournalChangedEvent>(cancellationToken).ConfigureAwait(false))
    {
        var topic = $"system:{evt.SystemId}";
        if (!joinedTopics.ContainsKey(topic))
        {
            continue;
        }

        topicJoinReference.TryGetValue(topic, out var joinRef);
        topicReplyAsArrayFrame.TryGetValue(topic, out var asArray);

        string payloadJson;
        if (string.Equals(evt.EventName, "global_journal_entry_deleted", StringComparison.Ordinal))
        {
            payloadJson = SerializeSocketJson(new { entry_id = evt.EntryId });
        }
        else
        {
            var entry = await journalRepository.GetGlobalAsync(evt.SystemId, evt.EntryId, cancellationToken).ConfigureAwait(false);
            if (entry is null)
            {
                continue;
            }

            payloadJson = SerializeSocketJson(new { entry });
        }

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

public static async Task PumpAlterJournalPushesAsync(
    WebSocket socket,
    IClusterEventBus eventBus,
    IJournalRepository journalRepository,
    System.Collections.Concurrent.ConcurrentDictionary<string, byte> joinedTopics,
    System.Collections.Concurrent.ConcurrentDictionary<string, string?> topicJoinReference,
    System.Collections.Concurrent.ConcurrentDictionary<string, bool> topicReplyAsArrayFrame,
    SemaphoreSlim sendGate,
    CancellationToken cancellationToken)
{
    await foreach (var evt in eventBus.SubscribeAsync<AlterJournalChangedEvent>(cancellationToken).ConfigureAwait(false))
    {
        var topic = $"system:{evt.SystemId}";
        if (!joinedTopics.ContainsKey(topic))
        {
            continue;
        }

        topicJoinReference.TryGetValue(topic, out var joinRef);
        topicReplyAsArrayFrame.TryGetValue(topic, out var asArray);

        string payloadJson;
        if (string.Equals(evt.EventName, "alter_journal_entry_deleted", StringComparison.Ordinal))
        {
            payloadJson = SerializeSocketJson(new { entry_id = evt.EntryId });
        }
        else
        {
            var entry = await journalRepository.GetAlterAsync(evt.SystemId, evt.EntryId, cancellationToken).ConfigureAwait(false);
            if (entry is null)
            {
                continue;
            }

            payloadJson = SerializeSocketJson(new { entry });
        }

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

public static string SerializeSocketJson(this object value)
{
    var opts = new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };
    return JsonSerializer.Serialize(value, opts);
}

}