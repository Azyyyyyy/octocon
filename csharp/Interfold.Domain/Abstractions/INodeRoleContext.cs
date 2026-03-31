namespace Interfold.Domain.Abstractions;

/// <summary>
/// Exposes the role of the currently-running node.
/// Resolved once at startup from <c>FLY_PROCESS_GROUP</c> or <c>OCTOCON_NODE_GROUP</c>.
/// Mirrors <c>Octocon.RPC.NodeTracker.current_group/0</c> from the legacy Elixir runtime.
/// </summary>
public interface INodeRoleContext
{
    /// <summary>The resolved role for this node.</summary>
    NodeGroup Role { get; }

    /// <summary>True when this node is a primary (owns singletons, runs background jobs).</summary>
    bool IsPrimary => Role == NodeGroup.Primary;

    /// <summary>True when this node is an auxiliary HTTP-only node.</summary>
    bool IsAuxiliary => Role == NodeGroup.Auxiliary;

    /// <summary>True when this node is a sidecar CPU-task node.</summary>
    bool IsSidecar => Role == NodeGroup.Sidecar;
}
