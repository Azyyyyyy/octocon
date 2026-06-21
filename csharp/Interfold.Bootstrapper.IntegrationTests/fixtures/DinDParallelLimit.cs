using TUnit.Core.Interfaces;

namespace Interfold.Bootstrapper.IntegrationTests.Fixtures;

/// <summary>
/// Caps how many bootstrapper integration tests run concurrently across the whole assembly.
/// Each test does several <c>docker exec</c> calls into the shared DinD container's inner
/// daemon plus a self-contained Aspire publish; running too many in parallel pushes the inner
/// dockerd into <c>BadGateway</c> responses and 5xx exec failures.
/// </summary>
public sealed class DinDParallelLimit : IParallelLimit
{
    // Empirically, the inner dockerd handles ~3 concurrent exec sessions cleanly on a developer
    // box. CI typically has more headroom but the bottleneck is still the inner daemon, so we
    // keep the same cap everywhere.
    public int Limit => 3;
}
