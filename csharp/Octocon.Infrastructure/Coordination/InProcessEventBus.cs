using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Octocon.Domain.Abstractions;

namespace Octocon.Infrastructure.Coordination;

/// <summary>
/// In-process implementation of <see cref="IClusterEventBus"/> backed by
/// <see cref="System.Threading.Channels"/>.
/// <para>
/// Each event type gets its own unbounded channel.  Multiple concurrent subscribers
/// each receive their own reader so every subscriber sees every event (fan-out).
/// This is the single-node equivalent of <c>Phoenix.PubSub</c> from the legacy runtime.
/// </para>
/// </summary>
public sealed class InProcessEventBus : IClusterEventBus, IDisposable
{
    // Non-generic interface so _topics can store typed bags without casting gymnastics.
    private interface ITopicBag
    {
        void CompleteAll();
    }

    private sealed class TopicBag<TEvent> : ITopicBag where TEvent : class
    {
        public readonly ConcurrentBag<ChannelWriter<TEvent>> Writers = new();

        public void CompleteAll()
        {
            foreach (var w in Writers) w.TryComplete();
        }
    }

    private readonly ConcurrentDictionary<Type, ITopicBag> _topics = new();

    private TopicBag<TEvent> GetOrCreateBag<TEvent>() where TEvent : class
        => (TopicBag<TEvent>)_topics.GetOrAdd(typeof(TEvent), _ => new TopicBag<TEvent>());

    public async ValueTask PublishAsync<TEvent>(TEvent evt, CancellationToken ct = default)
        where TEvent : class
    {
        if (!_topics.TryGetValue(typeof(TEvent), out var bag))
            return; // no subscribers — drop the event

        foreach (var writer in ((TopicBag<TEvent>)bag).Writers)
            await writer.WriteAsync(evt, ct).ConfigureAwait(false);
    }

    public async IAsyncEnumerable<TEvent> SubscribeAsync<TEvent>(
        [EnumeratorCancellation] CancellationToken ct = default)
        where TEvent : class
    {
        var channel = Channel.CreateUnbounded<TEvent>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });

        GetOrCreateBag<TEvent>().Writers.Add(channel.Writer);

        try
        {
            await foreach (var item in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                yield return item;
        }
        finally
        {
            // Complete this writer; we can't remove it from the bag but completed writers
            // silently swallow future writes instead of blocking.
            channel.Writer.TryComplete();
        }
    }

    public void Dispose()
    {
        foreach (var bag in _topics.Values)
            bag.CompleteAll();

        _topics.Clear();
    }
}
