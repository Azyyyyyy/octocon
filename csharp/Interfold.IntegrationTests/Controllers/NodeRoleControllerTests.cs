using System.Net;
using Interfold.IntegrationTests.Attributes;
using Interfold.IntegrationTests.TestServices;

namespace Interfold.IntegrationTests.Controllers;

public class NodeRoleControllerTests : BaseEndpointTest
{
    [Test, ApiIntegration]
    [CombinedDataSources]
    public async Task NodeRole_DefaultsToAuxiliary_WhenNoEnvVarSet([InterfoldFactoryGenerator] InterfoldWebApplicationFactory factory)
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health/node-role");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        var body = await response.Content.ReadAsStringAsync();
        var role = ReadStringField(body, "role");
        var ownsSingletons = ReadBoolField(body, "owns_singletons");

        using (Assert.Multiple())
        {
            await Assert.That(role).IsEqualTo("auxiliary");
            await Assert.That(ownsSingletons).IsFalse();
        }
    }

    [Test, ApiIntegration]
    [CombinedDataSources]
    public async Task NodeRole_ReturnsPrimary_WhenOctoconNodeGroupIsPrimary([InterfoldFactoryGenerator] InterfoldWebApplicationFactory factory)
    {
        factory
            .WithConfiguration("OCTOCON_NODE_GROUP", "primary");
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health/node-role");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        var body = await response.Content.ReadAsStringAsync();
        var role = ReadStringField(body, "role");
        var ownsSingletons = ReadBoolField(body, "owns_singletons");

        using (Assert.Multiple())
        {
            await Assert.That(role).IsEqualTo("primary");
            await Assert.That(ownsSingletons).IsTrue();
        }
    }

    [Test, ApiIntegration]
    [CombinedDataSources]
    public async Task NodeRole_ReturnsPrimary_WhenFlyProcessGroupIsPrimary([InterfoldFactoryGenerator] InterfoldWebApplicationFactory factory)
    {
        // FLY_PROCESS_GROUP takes precedence; OCTOCON_NODE_GROUP is not set.
        factory
            .WithConfiguration("FLY_PROCESS_GROUP", "primary");
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health/node-role");

        var body = await response.Content.ReadAsStringAsync();
        var role = ReadStringField(body, "role");
        using (Assert.Multiple())
        {
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(role).IsEqualTo("primary");
        }
    }

    [Test, ApiIntegration]
    [CombinedDataSources]
    public async Task NodeRole_FlyProcessGroup_TakesPrecedenceOver_OctoconNodeGroup([InterfoldFactoryGenerator] InterfoldWebApplicationFactory factory)
    {
        // FLY_PROCESS_GROUP=sidecar should win over OCTOCON_NODE_GROUP=primary.
        factory
            .WithConfiguration("OCTOCON_NODE_GROUP", "primary")
            .WithConfiguration("FLY_PROCESS_GROUP", "sidecar");
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health/node-role");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        var body = await response.Content.ReadAsStringAsync();
        var role = ReadStringField(body, "role");
        var ownsSingletons = ReadBoolField(body, "owns_singletons");

        using (Assert.Multiple())
        {
            await Assert.That(role).IsEqualTo("sidecar");
            await Assert.That(ownsSingletons).IsFalse();
        }
    }

    [Test, ApiIntegration]
    [CombinedDataSources]
    public async Task NodeRole_Endpoint_IsAnonymous([InterfoldFactoryGenerator] InterfoldWebApplicationFactory factory)
    {
        using var client = factory.CreateClient();

        // Un-authenticated request — must not return 401.
        var response = await client.GetAsync("/health/node-role");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }
}