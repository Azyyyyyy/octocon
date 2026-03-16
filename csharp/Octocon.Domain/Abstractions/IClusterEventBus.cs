namespace Octocon.Domain.Abstractions;

/// <summary>
/// In-process (and eventually cluster-wide) publish/subscribe bus for domain events.
/// <para>
/// Mirrors <c>Phoenix.PubSub</c> used in the legacy Elixir runtime for cache
/// invalidation, fronting flush triggers, and other cross-component signals.
/// </para>
/// </summary>
public interface IClusterEventBus
{
    /// <summary>
    /// Publishes <paramref name="evt"/> to all current subscribers of <typeparamref name="TEvent"/>.
    /// </summary>
    ValueTask PublishAsync<TEvent>(TEvent evt, CancellationToken ct = default)
        where TEvent : class;

    /// <summary>
    /// Returns an async stream that yields each event of <typeparamref name="TEvent"/>
    /// published after the subscription is established.
    /// The stream completes when <paramref name="ct"/> is cancelled.
    /// </summary>
    IAsyncEnumerable<TEvent> SubscribeAsync<TEvent>(CancellationToken ct = default)
        where TEvent : class;
}
