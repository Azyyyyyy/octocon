using Octocon.Domain.Abstractions;
using Octocon.Domain.Accounts;
using Octocon.Domain.Alters;
using Octocon.Domain.Fronting;
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

    public static async Task HandleAsync(
        SettingsProfileUpdatedEvent evt,
        SocketPushContext context,
        IAccountRepository accountRepository,
        IAlterRepository alterRepository,
        IFrontingRepository frontingRepository,
        ISettingsFieldRepository settingsFieldRepository,
        IEncryptionStateRepository encryptionStateRepository)
    {
        if (!context.TryGetSystemTopic(evt.SystemId, out var topic, out var joinRef, out var asArray))
        {
            return;
        }

        var profileTask = accountRepository.GetPublicProfileAsync(evt.SystemId, context.CancellationToken);
        var altersTask = alterRepository.ListAsync(evt.SystemId, context.CancellationToken);
        var frontsTask = frontingRepository.ListActiveAsync(evt.SystemId, context.CancellationToken);
        var fieldsTask = settingsFieldRepository.ListAsync(evt.SystemId, context.CancellationToken);
        var encryptionTask = encryptionStateRepository.GetAsync(evt.SystemId, context.CancellationToken);
        await Task.WhenAll(profileTask, altersTask, frontsTask, fieldsTask, encryptionTask).ConfigureAwait(false);

        var profile = profileTask.Result;

        var fields = fieldsTask.Result
            .OrderBy(x => x.Index)
            .Select(x => (object)new Dictionary<string, object?>
            {
                ["id"] = x.Id,
                ["name"] = x.Name,
                ["type"] = x.Type,
                ["security_level"] = x.SecurityLevel,
                ["locked"] = x.Locked,
                ["index"] = x.Index
            })
            .ToArray();

        var primaryFront = frontsTask.Result.FirstOrDefault(x => x.Primary)?.Front.AlterId;

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
            ["discord_id"] = null,
            ["google_id"] = null,
            ["apple_id"] = null,
            ["email"] = null,
            ["autoproxy_mode"] = "off",
            ["show_system_tag"] = false,
            ["lifetime_alter_count"] = altersTask.Result.Count,
            ["primary_front"] = primaryFront,
            ["fields"] = fields,
            ["encryption_initialized"] = encryptionTask.Result?.Initialized ?? false
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
            _ => null
        };

        if (eventName is null)
        {
            return;
        }

        var payloadJson = evt.ProviderKey switch
        {
            "discord" => WebSocketEvents.SerializeSocketJson(new { discord_id = evt.Identity }),
            "google" => WebSocketEvents.SerializeSocketJson(new { email = evt.Identity }),
            "apple" => WebSocketEvents.SerializeSocketJson(new { apple_id = evt.Identity }),
            _ => throw new InvalidOperationException("Unrecognized provider key") //Should never hit due to the earlier check, but satisfies the compiler
        };

        await context.SendAsync(topic, joinRef, asArray, eventName, payloadJson);
    }
}
