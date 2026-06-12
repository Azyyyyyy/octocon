using System.Collections.Concurrent;
using System.Threading.Channels;
using Interfold.Domain.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Interfold.Infrastructure.Coordination;

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
    private readonly ILogger<InProcessEventBus> _logger;

    public InProcessEventBus(ILogger<InProcessEventBus>? logger = null)
    {
        _logger = logger ?? NullLogger<InProcessEventBus>.Instance;
    }

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
        {
            _logger.LogInformation("[evtbus-diag] PublishAsync<{EventType}> — no topic bag (event dropped).", typeof(TEvent).Name);
            return; // no subscribers — drop the event
        }

        var topicBag = (TopicBag<TEvent>)bag;
        var writerCount = topicBag.Writers.Count;
        _logger.LogInformation("[evtbus-diag] PublishAsync<{EventType}> — writers={Count}", typeof(TEvent).Name, writerCount);

        var delivered = 0;
        foreach (var writer in topicBag.Writers.Keys)
        {
            try
            {
                await writer.WriteAsync(evt, ct).ConfigureAwait(false);
                delivered++;
            }
            catch (ChannelClosedException)
            {
                topicBag.Writers.TryRemove(writer, out _);
                _logger.LogWarning("[evtbus-diag] PublishAsync<{EventType}> — writer closed, removed.", typeof(TEvent).Name);
            }
        }

        _logger.LogInformation("[evtbus-diag] PublishAsync<{EventType}> — delivered={Delivered}/{Total}", typeof(TEvent).Name, delivered, writerCount);
    }

    public IAsyncEnumerable<TEvent> SubscribeAsync<TEvent>(
        CancellationToken ct = default)
        where TEvent : class
    {
        var channel = Channel.CreateUnbounded<TEvent>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = true
        });

        var topicBag = GetOrCreateBag<TEvent>();
        topicBag.Writers.TryAdd(channel.Writer, 0);

        _logger.LogInformation("[evtbus-diag] SubscribeAsync<{EventType}> — total writers now={Count}", typeof(TEvent).Name, topicBag.Writers.Count);

        return new ChannelAsyncEnumerable<TEvent>(channel, topicBag, ct);
    }

    private sealed class ChannelAsyncEnumerable<TEvent> : IAsyncEnumerable<TEvent>
        where TEvent : class
    {
        private readonly Channel<TEvent> _channel;
        private readonly TopicBag<TEvent> _topicBag;
        private readonly CancellationToken _ct;

        public ChannelAsyncEnumerable(Channel<TEvent> channel, TopicBag<TEvent> topicBag, CancellationToken ct)
        {
            _channel = channel;
            _topicBag = topicBag;
            _ct = ct;
        }

        public IAsyncEnumerator<TEvent> GetAsyncEnumerator(CancellationToken cancellationToken = default)
        {
            // Combine the provided cancellation token with the one from construction
            var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_ct, cancellationToken);
            return new ChannelAsyncEnumerator(_channel, _topicBag, linkedCts);
        }

        private sealed class ChannelAsyncEnumerator : IAsyncEnumerator<TEvent>, IAsyncDisposable
        {
            private readonly Channel<TEvent> _channel;
            private readonly TopicBag<TEvent> _topicBag;
            private readonly ChannelReader<TEvent> _reader;
            private readonly ChannelWriter<TEvent> _writer;
            private readonly CancellationTokenSource _cts;
            private IAsyncEnumerator<TEvent>? _enumerator;
            private bool _disposed;

            public ChannelAsyncEnumerator(Channel<TEvent> channel, TopicBag<TEvent> topicBag, CancellationTokenSource cts)
            {
                _channel = channel;
                _topicBag = topicBag;
                _reader = channel.Reader;
                _writer = channel.Writer;
                _cts = cts;
                _enumerator = _reader.ReadAllAsync(_cts.Token).GetAsyncEnumerator();
            }

            public TEvent Current => _enumerator!.Current;

            public async ValueTask<bool> MoveNextAsync()
            {
                if (_disposed) return false;
                try
                {
                    return await _enumerator!.MoveNextAsync().ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return false;
                }
            }

            public async ValueTask DisposeAsync()
            {
                if (_disposed) return;
                _disposed = true;
                _topicBag.Writers.TryRemove(_writer, out _);
                _writer.TryComplete();
                if (_enumerator != null)
                {
                    await _enumerator.DisposeAsync().ConfigureAwait(false);
                    _enumerator = null;
                }
                _cts.Cancel();
                _cts.Dispose();
            }
        }
    }

    public void Dispose()
    {
        foreach (var bag in _topics.Values)
            bag.CompleteAll();

        _topics.Clear();
    }
}
