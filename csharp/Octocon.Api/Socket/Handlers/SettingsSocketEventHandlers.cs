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
        await context.SendAsync(topic, joinRef, asArray, SocketEventNames.Settings.FieldsUpdated, payloadJson);
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
            await context.SendAsync(topic, joinRef, asArray, SocketEventNames.Settings.UsernameUpdated, usernamePayloadJson);
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
        await context.SendAsync(topic, joinRef, asArray, SocketEventNames.Settings.SelfUpdated, payloadJson);
    }

    public static Task HandleAsync(SettingsAccountDeletedSignalEvent evt, SocketPushContext context)
        => HandleSignalAsync(evt.SystemId, SocketEventNames.Settings.AccountDeleted, context);

    public static Task HandleAsync(SettingsAltersWipedSignalEvent evt, SocketPushContext context)
        => HandleSignalAsync(evt.SystemId, SocketEventNames.Settings.AltersWiped, context);

    public static Task HandleAsync(SettingsEncryptedDataWipedSignalEvent evt, SocketPushContext context)
        => HandleSignalAsync(evt.SystemId, SocketEventNames.Settings.EncryptedDataWiped, context);

    public static Task HandleAsync(SettingsDiscordAccountUnlinkedSignalEvent evt, SocketPushContext context)
        => HandleSignalAsync(evt.SystemId, SocketEventNames.Settings.DiscordAccountUnlinked, context);

    public static Task HandleAsync(SettingsAppleAccountUnlinkedSignalEvent evt, SocketPushContext context)
        => HandleSignalAsync(evt.SystemId, SocketEventNames.Settings.AppleAccountUnlinked, context);

    private static async Task HandleSignalAsync(string systemId, string eventName, SocketPushContext context)
    {
        if (!context.TryGetSystemTopic(systemId, out var topic, out var joinRef, out var asArray))
        {
            return;
        }

        await context.SendAsync(topic, joinRef, asArray, eventName, "{}");
    }

    public static async Task HandleAsync(SettingsAccountLinkedEvent evt, SocketPushContext context)
    {
        if (!context.TryGetSystemTopic(evt.SystemId, out var topic, out var joinRef, out var asArray))
        {
            return;
        }

        var eventName = evt.ProviderKey switch
        {
            "discord" => SocketEventNames.Settings.DiscordAccountLinked,
            "google" => SocketEventNames.Settings.GoogleAccountLinked,
            "apple" => SocketEventNames.Settings.AppleAccountLinked,
            _ => SocketEventNames.Settings.AccountLinked
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
