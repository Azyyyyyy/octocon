using Interfold.Contracts;
using Interfold.Contracts.Events;

namespace Interfold.Api.Socket.Handlers;

public static class ImportSocketEventHandlers
{
    public static Task HandleAsync(SimplyPluralImportCompletedEvent evt, SocketPushContext context)
        => SendCompletedAsync(evt.TargetSystemId, SocketEventNames.Imports.SpComplete, evt.AlterCount, context);

    public static Task HandleAsync(SimplyPluralImportFailedEvent evt, SocketPushContext context)
        => SendFailedAsync(evt.TargetSystemId, SocketEventNames.Imports.SpFailed, context);

    public static Task HandleAsync(PluralKitImportCompletedEvent evt, SocketPushContext context)
        => SendCompletedAsync(evt.TargetSystemId, SocketEventNames.Imports.PkComplete, evt.AlterCount, context);

    public static Task HandleAsync(PluralKitImportFailedEvent evt, SocketPushContext context)
        => SendFailedAsync(evt.TargetSystemId, SocketEventNames.Imports.PkFailed, context);

    private static async Task SendCompletedAsync(string systemId, string eventName, int alterCount, SocketPushContext context)
    {
        if (!context.TryGetSystemTopic(systemId, out var topic, out var joinRef, out var asArray))
        {
            return;
        }

        await context.SendAsync(topic, joinRef, asArray, eventName, new ImportCompletedSocketPayload(alterCount));
    }

    private static async Task SendFailedAsync(string systemId, string eventName, SocketPushContext context)
    {
        if (!context.TryGetSystemTopic(systemId, out var topic, out var joinRef, out var asArray))
        {
            return;
        }

        await context.SendAsync(topic, joinRef, asArray, eventName, new EmptyPayload());
    }
}
