namespace Octocon.Domain.Fronting;

/// <summary>
/// Published to <c>IClusterEventBus</c> whenever the fronting state for a system changes
/// (start, end, set, or bulk update).
/// <para>
/// Consumed on primary nodes by <c>FrontNotifierBackgroundService</c>, which batches
/// changes and will flush push notifications to friends via FCM (Phase N).
/// Mirrors the role of <c>Octocon.Global.FrontNotifier</c> in the legacy Elixir runtime.
/// </para>
/// </summary>
public sealed record FrontingStateChangedEvent(string SystemId);

/// <summary>
/// Published whenever a front entry is permanently deleted from history.
/// </summary>
public sealed record FrontDeletedEvent(string SystemId, string FrontId);
