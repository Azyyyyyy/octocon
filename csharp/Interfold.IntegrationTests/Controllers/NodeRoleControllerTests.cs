using System.Net;
using Interfold.IntegrationTests.TestServices;

namespace Interfold.IntegrationTests.Controllers;

[ClassDataSource<InMemoryWebFactoryFixture>(Shared = SharedType.PerTestSession)]
[ClassDataSource<ScyllaWebFactoryFixture>(Shared = SharedType.PerTestSession)]
[ClassDataSource<CassandraWebFactoryFixture>(Shared = SharedType.PerTestSession)]
public class NodeRoleControllerTests(IWebFactoryFixture fixture) : BaseEndpointTest
{
    [Test]
    public async Task NodeRole_DefaultsToAuxiliary_WhenNoEnvVarSet()
    {
        using var client = fixture.Factory.CreateClient();

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

    [Test]
    public async Task NodeRole_Endpoint_IsAnonymous()
    {
        using var client = fixture.Factory.CreateClient();

        // Un-authenticated request — must not return 401.
        var response = await client.GetAsync("/health/node-role");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }
}