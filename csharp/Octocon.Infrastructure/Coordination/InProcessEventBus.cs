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
        public readonly ConcurrentDictionary<ChannelWriter<TEvent>, byte> Writers = new();

        public void CompleteAll()
        {
            foreach (var w in Writers.Keys)
            {
                w.TryComplete();
            }

            Writers.Clear();
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

        var topicBag = (TopicBag<TEvent>)bag;
        foreach (var writer in topicBag.Writers.Keys)
        {
            try
            {
                await writer.WriteAsync(evt, ct).ConfigureAwait(false);
            }
            catch (ChannelClosedException)
            {
                topicBag.Writers.TryRemove(writer, out _);
            }
        }
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

        var topicBag = GetOrCreateBag<TEvent>();
        topicBag.Writers.TryAdd(channel.Writer, 0);

        try
        {
            await foreach (var item in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                yield return item;
        }
        finally
        {
            // Remove the writer so reconnect churn does not leave stale writers behind.
            topicBag.Writers.TryRemove(channel.Writer, out _);
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
