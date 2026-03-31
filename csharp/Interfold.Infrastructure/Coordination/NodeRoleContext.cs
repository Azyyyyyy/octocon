using Interfold.Domain.Abstractions;

namespace Interfold.Infrastructure.Coordination;

/// <summary>
/// Stateless <see cref="INodeRoleContext"/> backed by a value resolved once at startup.
/// </summary>
public sealed class NodeRoleContext(NodeGroup role) : INodeRoleContext
{
    public NodeGroup Role { get; } = role;
}
