using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Interfold.IntegrationTests.TestServices;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Interfold.IntegrationTests.Controllers;

[ClassDataSource<InMemoryWebFactoryFixture>(Shared = SharedType.PerTestSession)]
[ClassDataSource<ScyllaWebFactoryFixture>(Shared = SharedType.PerTestSession)]
[ClassDataSource<CassandraWebFactoryFixture>(Shared = SharedType.PerTestSession)]
public class TagsControllerTests(IWebFactoryFixture fixture) : BaseEndpointTest
{
    [Test]
    public async Task Idempotency_TagCreate_ReplayStable()
    {
        await RunSoakAsync(fixture.Factory, async (client, key) =>
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, "/api/systems/me/tags")
            {
                Content = JsonContent.Create(new { name = "SoakTag" })
            };
            req.Headers.Add("X-Interfold-Idempotency-Key", key);
            return await client.SendAsync(req);
        });
    }
    
    [Test]
    public async Task TagParent_SetAndRemove_Returns204()
    {
        using var client = fixture.Factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var principal = "parity-tag-parent";
        var parentTagId = await CreateTagAsync(client, principal, "ParentTag");
        var childTagId = await CreateTagAsync(client, principal, "ChildTag");

        using var setReq = new HttpRequestMessage(HttpMethod.Post, $"/api/systems/me/tags/{childTagId}/parent")
        {
            Content = JsonContent.Create(new { parentTagId }, options: new JsonSerializerOptions() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower })
        };
        AttachPrincipalAuth(setReq, client, principal);
        var setRes = await client.SendAsync(setReq);

        using var removeReq = new HttpRequestMessage(HttpMethod.Delete, $"/api/systems/me/tags/{childTagId}/parent")
        {
            Content = JsonContent.Create(new { })
        };
        AttachPrincipalAuth(removeReq, client, principal);
        var removeRes = await client.SendAsync(removeReq);

        using (Assert.Multiple())
        {
            await Assert.That(setRes.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
            await Assert.That(removeRes.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        }
    }
}