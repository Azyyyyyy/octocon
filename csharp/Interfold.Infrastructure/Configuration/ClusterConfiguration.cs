namespace Interfold.Infrastructure.Configuration;

/// <summary>
/// Cluster and deployment configuration for node role and orchestration.
/// Binds from environment variables:
///   - FLY_PROCESS_GROUP (fly.io automatic)
///   - OCTOCON_NODE_GROUP (manual override, takes precedence)
/// </summary>
public sealed class ClusterConfiguration
{
    public const string SectionName = "Cluster";

    /// <summary>
    /// Resolved node group (Primary, Auxiliary, or Sidecar).
    /// Resolution order: FLY_PROCESS_GROUP → OCTOCON_NODE_GROUP → Auxiliary (default)
    /// </summary>
    public string NodeGroup { get; set; } = "auxiliary";
}
