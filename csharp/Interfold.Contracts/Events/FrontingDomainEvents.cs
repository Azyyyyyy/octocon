namespace Interfold.Contracts.Events;

/// <summary>
/// Published to <c>IClusterEventBus</c> whenever the fronting state for a system changes
/// (start, end, set, or bulk update).
/// <para>
/// Consumed on primary nodes by <c>FrontNotifierBackgroundService</c>, which batches
/// changes and will flush push notifications to friends via FCM (Phase N).
/// Mirrors the role of <c>Octocon.Global.FrontNotifier</c> in the legacy Elixir runtime.
/// </para>
/// </summary>
public sealed record FrontingStateChangedEvent(string TargetSystemId) : ITargetedClusterEvent;

/// <summary>
/// Published whenever a front entry is permanently deleted from history.
/// </summary>
public sealed record FrontDeletedEvent(string TargetSystemId, string FrontId) : ITargetedClusterEvent;

/// <summary>
/// Published when a front is started. Consumed by socket pumps to emit "fronting_started" events.
/// </summary>
public sealed record FrontingStartedEvent(string TargetSystemId, string FrontId) : ITargetedClusterEvent;

/// <summary>
/// Published when a front is ended. Consumed by socket pumps to emit "fronting_ended" events.
/// </summary>
public sealed record FrontingEndedEvent(string TargetSystemId, int AlterId) : ITargetedClusterEvent;

/// <summary>
/// Published when fronting is set to a single front. Consumed by socket pumps to emit "fronting_set" events.
/// </summary>
public sealed record FrontingSetEvent(string TargetSystemId, string FrontId) : ITargetedClusterEvent;

/// <summary>
/// Published when fronts are bulk updated. Consumed by socket pumps to emit "fronting_bulk" events.
/// </summary>
public sealed record FrontingBulkUpdatedEvent(string TargetSystemId) : ITargetedClusterEvent;

/// <summary>
/// Published when a front's comment is updated. Consumed by socket pumps to emit "front_updated" events.
/// </summary>
public sealed record FrontCommentUpdatedEvent(string TargetSystemId, string FrontId) : ITargetedClusterEvent;

/// <summary>
/// Published when a system's primary front changes. Consumed by socket pumps to emit "primary_front" events.
/// </summary>
public sealed record FrontingPrimaryChangedEvent(string TargetSystemId, int? AlterId) : ITargetedClusterEvent;
