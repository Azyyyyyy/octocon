using System.Net;
using Interfold.IntegrationTests.Attributes;
using Interfold.IntegrationTests.TestServices;

namespace Interfold.IntegrationTests.Controllers;

public class NodeRoleControllerTests : BaseEndpointTest
{
    [Test, ApiIntegration]
    public async Task NodeRole_DefaultsToAuxiliary_WhenNoEnvVarSet()
    {
        await using var factory = new InterfoldWebApplicationFactory("inmemory");
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
    public async Task NodeRole_ReturnsPrimary_WhenOctoconNodeGroupIsPrimary()
    {
        await using var factory = new InterfoldWebApplicationFactory("inmemory")
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
    public async Task NodeRole_ReturnsPrimary_WhenFlyProcessGroupIsPrimary()
    {
        // FLY_PROCESS_GROUP takes precedence; OCTOCON_NODE_GROUP is not set.
        await using var factory = new InterfoldWebApplicationFactory("inmemory")
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
    public async Task NodeRole_FlyProcessGroup_TakesPrecedenceOver_OctoconNodeGroup()
    {
        // FLY_PROCESS_GROUP=sidecar should win over OCTOCON_NODE_GROUP=primary.
        await using var factory = new InterfoldWebApplicationFactory("inmemory")
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
    public async Task NodeRole_Endpoint_IsAnonymous()
    {
        await using var factory = new InterfoldWebApplicationFactory("inmemory");
        using var client = factory.CreateClient();

        // Un-authenticated request — must not return 401.
        var response = await client.GetAsync("/health/node-role");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }
}