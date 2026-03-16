using Octocon.Domain.Abstractions;

namespace Octocon.Infrastructure.Coordination;

/// <summary>
/// Resolves the <see cref="NodeGroup"/> from environment variables, mirroring the detection
/// order used by the legacy Elixir runtime:
/// <list type="number">
///   <item><c>FLY_PROCESS_GROUP</c> (set automatically by fly.io)</item>
///   <item><c>OCTOCON_NODE_GROUP</c> (manual override)</item>
///   <item>Default: <see cref="NodeGroup.Auxiliary"/></item>
/// </list>
/// </summary>
public static class NodeGroupResolver
{
    public static NodeGroup Resolve()
    {
        var raw = Environment.GetEnvironmentVariable("FLY_PROCESS_GROUP")
               ?? Environment.GetEnvironmentVariable("OCTOCON_NODE_GROUP");

        return raw?.Trim().ToLowerInvariant() switch
        {
            "primary"   => NodeGroup.Primary,
            "auxiliary" => NodeGroup.Auxiliary,
            "sidecar"   => NodeGroup.Sidecar,
            null        => NodeGroup.Auxiliary,
            var unknown => throw new InvalidOperationException(
                $"Unrecognised node group '{unknown}'. Valid values: primary, auxiliary, sidecar.")
        };
    }
}
