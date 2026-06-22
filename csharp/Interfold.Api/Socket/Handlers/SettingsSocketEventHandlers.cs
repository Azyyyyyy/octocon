using Interfold.Contracts;
using Interfold.Contracts.Events;
using Interfold.Domain.Abstractions.Repository;

namespace Interfold.Api.Socket.Handlers;

public static class SettingsSocketEventHandlers
{
    public static async Task HandleAsync(SettingsFieldsChangedEvent evt, SocketPushContext context, ISettingsFieldRepository fieldRepository)
    {
        if (!context.TryGetSystemTopic(evt.TargetSystemId, out var topic, out var joinRef, out var asArray))
        {
            return;
        }

        var fields = await fieldRepository.ListAsync(evt.TargetSystemId, context.CancellationToken).ConfigureAwait(false);
        await context.SendAsync(topic, joinRef, asArray, SocketEventNames.Settings.FieldsUpdated, new SettingsFieldsUpdatedPayload(fields));
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
        if (!context.TryGetSystemTopic(evt.TargetSystemId, out var topic, out var joinRef, out var asArray))
        {
            return;
        }

        var profileTask = accountRepository.GetPublicProfileAsync(evt.TargetSystemId, context.CancellationToken);
        var altersTask = alterRepository.ListAsync(evt.TargetSystemId, context.CancellationToken);
        var frontsTask = frontingRepository.ListActiveAsync(evt.TargetSystemId, context.CancellationToken);
        var fieldsTask = settingsFieldRepository.ListAsync(evt.TargetSystemId, context.CancellationToken);
        var encryptionTask = encryptionStateRepository.GetAsync(evt.TargetSystemId, context.CancellationToken);
        await Task.WhenAll(profileTask, altersTask, frontsTask, fieldsTask, encryptionTask).ConfigureAwait(false);

        var profile = profileTask.Result;

        if (evt.EmitUsernameUpdated && profile?.Username is not null)
        {
            await context.SendAsync(topic, joinRef, asArray, SocketEventNames.Settings.UsernameUpdated, new SettingsUsernameUpdatedPayload(profile.Username));
        }

        var selfData = WebSocketInitialization.BuildSelfReadModel(
            evt.TargetSystemId,
            profile,
            altersTask.Result,
            frontsTask.Result,
            fieldsTask.Result,
            encryptionTask.Result,
            context.RequestOrigin);

        await context.SendAsync(topic, joinRef, asArray, SocketEventNames.Settings.SelfUpdated, new SettingsSelfUpdatedPayload(selfData));
    }

    public static Task HandleAsync(SettingsAccountDeletedSignalEvent evt, SocketPushContext context)
        => HandleSignalAsync(evt.TargetSystemId, SocketEventNames.Settings.AccountDeleted, context);

    public static Task HandleAsync(SettingsAltersWipedSignalEvent evt, SocketPushContext context)
        => HandleSignalAsync(evt.TargetSystemId, SocketEventNames.Settings.AltersWiped, context);

    public static Task HandleAsync(SettingsEncryptedDataWipedSignalEvent evt, SocketPushContext context)
        => HandleSignalAsync(evt.TargetSystemId, SocketEventNames.Settings.EncryptedDataWiped, context);

    public static Task HandleAsync(SettingsDiscordAccountUnlinkedSignalEvent evt, SocketPushContext context)
        => HandleSignalAsync(evt.TargetSystemId, SocketEventNames.Settings.DiscordAccountUnlinked, context);

    public static Task HandleAsync(SettingsAppleAccountUnlinkedSignalEvent evt, SocketPushContext context)
        => HandleSignalAsync(evt.TargetSystemId, SocketEventNames.Settings.AppleAccountUnlinked, context);

    public static Task HandleAsync(SettingsGoogleAccountUnlinkedSignalEvent evt, SocketPushContext context)
        => HandleSignalAsync(evt.TargetSystemId, SocketEventNames.Settings.GoogleAccountUnlinked, context);

    public static Task HandleAsync(SettingsTagsWipedSignalEvent evt, SocketPushContext context)
        => HandleSignalAsync(evt.TargetSystemId, SocketEventNames.Settings.TagsWiped, context);

    private static async Task HandleSignalAsync(string systemId, string eventName, SocketPushContext context)
    {
        if (!context.TryGetSystemTopic(systemId, out var topic, out var joinRef, out var asArray))
        {
            return;
        }

        await context.SendAsync(topic, joinRef, asArray, eventName, new EmptyPayload());
    }

    public static async Task HandleAsync(SettingsAccountLinkedEvent evt, SocketPushContext context)
    {
        if (!context.TryGetSystemTopic(evt.TargetSystemId, out var topic, out var joinRef, out var asArray))
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

        ISocketPayload payload = evt.ProviderKey switch
        {
            "discord" => new DiscordAccountLinkedPayload(evt.Identity),
            "google" => new GoogleAccountLinkedPayload(evt.Identity),
            "apple" => new AppleAccountLinkedPayload(evt.Identity),
            _ => throw new InvalidOperationException("Unrecognized provider key") //Should never hit due to the earlier check, but satisfies the compiler
        };

        await context.SendAsync(topic, joinRef, asArray, eventName, payload);
    }
}
