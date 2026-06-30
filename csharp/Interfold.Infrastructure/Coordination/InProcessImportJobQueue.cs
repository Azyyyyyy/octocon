using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Interfold.Domain.Abstractions.ImportJobs;

namespace Interfold.Infrastructure.Coordination;

/// <summary>
/// Single-channel in-process implementation of <see cref="IImportJobQueue"/>. Holds at
/// most <see cref="DefaultCapacity"/> items; new enqueues await available space when full
/// (back-pressure rather than drop). One <see cref="ImportJobBackgroundService"/>
/// instance per process consumes via <see cref="ReadAllAsync"/>.
///
/// <para>
/// <b>Why a single global channel.</b> Per-system serialisation is owned by the LWT
/// mutex on <c>active_import_by_system</c> — by the time an item reaches the queue, the
/// repository has already guaranteed no other in-flight job exists for the same
/// (system, kind). So the queue's only job is to buffer the worker, not to enforce a
/// fairness or isolation contract. One channel + one worker is the simplest shape that
/// satisfies that and matches the observed workload (single human dispatching at a
/// time).
/// </para>
///
/// <para>
/// If/when concurrent imports across different systems become a measured bottleneck, the
/// shape to grow into is "channel per system" with a worker pool; the
/// <see cref="IImportJobQueue"/> abstraction is deliberately small enough to allow that
/// swap without touching handlers or controllers.
/// </para>
/// </summary>
public sealed class InProcessImportJobQueue : IImportJobQueue, IAsyncDisposable
{
    private const int DefaultCapacity = 256;

    private readonly Channel<ImportJobItem> _channel;

    public InProcessImportJobQueue()
    {
        _channel = Channel.CreateBounded<ImportJobItem>(new BoundedChannelOptions(DefaultCapacity)
        {
            // SingleReader = true keeps the consumer side optimised: only the
            // background service drains the channel, so the channel can skip locking
            // around the read path. Wait on a full channel rather than dropping —
            // dropping silently would mean a claimed operation row with no
            // corresponding worker run.
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
        });
    }

    public ValueTask EnqueueAsync(ImportJobItem item, CancellationToken cancellationToken = default)
    {
        return _channel.Writer.WriteAsync(item, cancellationToken);
    }

    public async IAsyncEnumerable<ImportJobItem> ReadAllAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // ReadAllAsync exposes the standard System.Threading.Channels iteration pattern
        // which already honours cancellation via the supplied token.
        await foreach (var item in _channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return item;
        }
    }

    public ValueTask DisposeAsync()
    {
        _channel.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }
}
