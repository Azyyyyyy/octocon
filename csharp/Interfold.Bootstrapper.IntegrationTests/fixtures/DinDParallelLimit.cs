using System.Globalization;
using TUnit.Core.Interfaces;

namespace Interfold.Bootstrapper.IntegrationTests.Fixtures;

/// <summary>
/// Caps how many bootstrapper integration tests run concurrently across the whole assembly.
/// Each test does several <c>docker exec</c> calls into the shared DinD container's inner
/// daemon plus a self-contained Aspire publish; running too many in parallel pushes the inner
/// dockerd into <c>BadGateway</c> responses and 5xx exec failures.
/// </summary>
/// <remarks>
/// <para>
/// Host-port collisions on the DinD's network namespace used to be the dominant ceiling and
/// kept this cap at 3. The per-test port allocator in <see cref="DinDFixtureBase.CreateScratchAsync"/>
/// removed that ceiling, so the remaining limit is the inner dockerd's exec throughput and the
/// runner box's CPU/RAM budget. We default to 6 — comfortable on a 4-vCPU GH-hosted Ubuntu
/// runner — and let CI raise it via <c>INTERFOLD_DIND_PARALLEL_LIMIT</c> when running on a
/// fatter machine.
/// </para>
/// </remarks>
public sealed class DinDParallelLimit : IParallelLimit
{
    private const int DefaultLimit = 6;
    private const string EnvVarName = "INTERFOLD_DIND_PARALLEL_LIMIT";

    public int Limit { get; } = ResolveLimit();

    private static int ResolveLimit()
    {
        var raw = Environment.GetEnvironmentVariable(EnvVarName);
        if (!string.IsNullOrWhiteSpace(raw)
            && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            && parsed > 0)
        {
            return parsed;
        }
        return DefaultLimit;
    }
}
