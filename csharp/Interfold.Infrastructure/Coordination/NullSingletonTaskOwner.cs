using Interfold.Domain.Abstractions;

namespace Interfold.Infrastructure.Coordination;

/// <summary>
/// No-op <see cref="ISingletonTaskOwner"/> for <see cref="NodeGroup.Auxiliary"/> and
/// <see cref="NodeGroup.Sidecar"/> nodes.  Never claims ownership; never runs work.
/// </summary>
public sealed class NullSingletonTaskOwner : ISingletonTaskOwner
{
    public bool OwnsTask(string taskName) => false;

    public Task RunIfOwnerAsync(
        string taskName,
        Func<CancellationToken, Task> work,
        CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}
