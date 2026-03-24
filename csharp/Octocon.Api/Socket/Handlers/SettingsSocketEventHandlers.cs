using Octocon.Domain.Abstractions;
using Octocon.Domain.Accounts;
using Octocon.Domain.Settings;

namespace Octocon.Api.Socket.Handlers;

public static class SettingsSocketEventHandlers
{
    public static async Task HandleAsync(SettingsFieldsChangedEvent evt, SocketPushContext context, ISettingsFieldRepository fieldRepository)
    {
        if (!context.TryGetSystemTopic(evt.SystemId, out var topic, out var joinRef, out var asArray))
        {
            return;
        }

        var fields = await fieldRepository.ListAsync(evt.SystemId, context.CancellationToken).ConfigureAwait(false);
        var payloadJson = WebSocketEvents.SerializeSocketJson(new { fields });
        await context.SendAsync(topic, joinRef, asArray, "fields_updated", payloadJson);
    }

    public static async Task HandleAsync(SettingsProfileUpdatedEvent evt, SocketPushContext context, IAccountRepository accountRepository)
    {
        if (!context.TryGetSystemTopic(evt.SystemId, out var topic, out var joinRef, out var asArray))
        {
            return;
        }

        var profile = await accountRepository.GetPublicProfileAsync(evt.SystemId, context.CancellationToken).ConfigureAwait(false);

        if (evt.EmitUsernameUpdated && profile?.Username is not null)
        {
            var usernamePayloadJson = WebSocketEvents.SerializeSocketJson(new { username = profile.Username });
            await context.SendAsync(topic, joinRef, asArray, "username_updated", usernamePayloadJson);
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

        var payloadJson = WebSocketEvents.SerializeSocketJson(new { data = selfData });
        await context.SendAsync(topic, joinRef, asArray, "self_updated", payloadJson);
    }

    public static async Task HandleAsync(SettingsSocketSignalEvent evt, SocketPushContext context)
    {
        if (!context.TryGetSystemTopic(evt.SystemId, out var topic, out var joinRef, out var asArray))
        {
            return;
        }

        await context.SendAsync(topic, joinRef, asArray, evt.EventName, "{}");
    }

    public static async Task HandleAsync(SettingsAccountLinkedEvent evt, SocketPushContext context)
    {
        if (!context.TryGetSystemTopic(evt.SystemId, out var topic, out var joinRef, out var asArray))
        {
            return;
        }

        var eventName = evt.ProviderKey switch
        {
            "discord" => "discord_account_linked",
            "google" => "google_account_linked",
            "apple" => "apple_account_linked",
            _ => "account_linked"
        };

        var payloadJson = evt.ProviderKey switch
        {
            "discord" => WebSocketEvents.SerializeSocketJson(new { discord_id = evt.Identity }),
            "google" => WebSocketEvents.SerializeSocketJson(new { email = evt.Identity }),
            "apple" => WebSocketEvents.SerializeSocketJson(new { apple_id = evt.Identity }),
            _ => "{}"
        };

        await context.SendAsync(topic, joinRef, asArray, eventName, payloadJson);
    }
}
