using Microsoft.Extensions.DependencyInjection;
using Interfold.Domain.Abstractions;
using Interfold.Infrastructure.Coordination;

namespace Interfold.Infrastructure.DependencyInjection;

public static partial class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers node-role and cluster coordination services.
    /// Call this after <see cref="AddInterfoldPersistence"/> in <c>Program.cs</c>.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="role">The resolved node group; pass <see cref="NodeGroupResolver.Resolve()"/>.</param>
    public static IServiceCollection AddInterfoldCluster(
        this IServiceCollection services,
        NodeGroup role)
    {
        // Node-role context — singleton read by any service that needs to know the role.
        services.AddSingleton<INodeRoleContext>(new NodeRoleContext(role));

        // Cluster event bus — in-process for single-node and integration tests.
        services.AddSingleton<IClusterEventBus, InProcessEventBus>();

            // FCM push notification service — NullFCMService until real Firebase integration is configured.
            services.AddSingleton<IFCMService, NullFCMService>();

        // Singleton task owner — primary owns tasks, auxiliary/sidecar do not.
        ISingletonTaskOwner taskOwner = role == NodeGroup.Primary
            ? new PrimaryOnlySingletonTaskOwner()
            : new NullSingletonTaskOwner();

        services.AddSingleton(taskOwner);

        // Background services gated on primary role.
        if (role == NodeGroup.Primary)
        {
            services.AddHostedService<FrontNotifierBackgroundService>();
        }

        return services;
    }
}
