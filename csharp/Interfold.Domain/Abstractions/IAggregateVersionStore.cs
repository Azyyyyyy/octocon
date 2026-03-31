namespace Interfold.Domain.Abstractions;

public interface IAggregateVersionStore
{
    Task<long?> GetVersionAsync(
        string aggregateType,
        string aggregateId,
        CancellationToken cancellationToken = default
    );

    Task<bool> TryAdvanceVersionAsync(
        string aggregateType,
        string aggregateId,
        long? expectedVersion,
        CancellationToken cancellationToken = default
    );
}
