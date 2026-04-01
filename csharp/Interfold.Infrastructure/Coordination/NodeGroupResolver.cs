using Interfold.Domain.Abstractions;
using Microsoft.Extensions.Configuration;

namespace Interfold.Infrastructure.Coordination;

/// <summary>
/// Resolves the <see cref="NodeGroup"/> from environment variables, mirroring the detection
/// order used by the legacy Elixir runtime:
/// <list type="number">
///   <item><c>FLY_PROCESS_GROUP</c> (set automatically by fly.io)</item>
///   <item><c>OCTOCON_NODE_GROUP</c> (manual override)</item>
///   <item>Default: <see cref="NodeGroup.Auxiliary"/></item>
/// </list>
/// For typed-configuration usage, prefer <see cref="Resolve(string?)"/> with the value
/// from <c>ClusterConfiguration.NodeGroup</c>.
/// </summary>
public static class NodeGroupResolver
{
    /// <summary>Resolves node group from <see cref="IConfiguration"/> directly.</summary>
    public static NodeGroup Resolve(IConfiguration configuration)
    {
        var raw = configuration["FLY_PROCESS_GROUP"]
               ?? configuration["OCTOCON_NODE_GROUP"];

        return ResolveFromRawValue(raw);
    }

    /// <summary>Resolves node group from a pre-bound string value (e.g. from <c>ClusterConfiguration.NodeGroup</c>).</summary>
    public static NodeGroup Resolve(string? rawValue) => ResolveFromRawValue(rawValue);

    private static NodeGroup ResolveFromRawValue(string? raw)
    {
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
