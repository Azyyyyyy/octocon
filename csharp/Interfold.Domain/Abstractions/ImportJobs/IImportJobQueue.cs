namespace Interfold.Domain.Abstractions.ImportJobs;

/// <summary>
/// In-process FIFO queue of import jobs. Handlers <see cref="EnqueueAsync"/> work after
/// successfully claiming a slot in <c>IImportOperationRepository</c>;
/// <c>ImportJobBackgroundService</c> consumes it with <see cref="ReadAllAsync"/>.
///
/// <para>
/// <b>Why in-process is enough.</b> Cross-replica serialisation of imports is already
/// guaranteed by the per-system LWT mutex (<c>active_import_by_system</c>). If two
/// replicas race to dispatch the same system, only one wins the LWT and only that
/// replica enqueues — the queue is a pure local concern. The deepest queue in practice
/// is one item per active SP/PK click, which is human-paced and easily fits a bounded
/// channel.
/// </para>
/// </summary>
public interface IImportJobQueue
{
    /// <summary>
    /// Adds an item to the queue. Returns when the item has been buffered (typically
    /// immediately — the underlying channel is bounded only to provide back-pressure
    /// against a runaway producer, not to throttle normal use).
    /// </summary>
    ValueTask EnqueueAsync(ImportJobItem item, CancellationToken cancellationToken = default);

    /// <summary>
    /// Async sequence of every item enqueued, in FIFO order. The sequence ends when the
    /// channel is completed (typically on host shutdown) or when
    /// <paramref name="cancellationToken"/> fires. Consumers MUST be single-threaded per
    /// implementation choice (one <see cref="IImportJobQueue"/> = one consuming worker).
    /// </summary>
    IAsyncEnumerable<ImportJobItem> ReadAllAsync(CancellationToken cancellationToken = default);
}
