using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Interfold.IntegrationTests.Attributes;
using Interfold.IntegrationTests.TestServices;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Interfold.IntegrationTests.Controllers;

public class TagsControllerTests : BaseEndpointTest
{
    [Test, ApiIntegration]
    [CombinedDataSources]
    public async Task Idempotency_TagCreate_ReplayStable([InterfoldFactoryGenerator] InterfoldWebApplicationFactory factory)
    {
        await RunSoakAsync(factory, async (client, key) =>
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
    [CombinedDataSources]
    public async Task TagParent_SetAndRemove_Returns204([InterfoldFactoryGenerator] InterfoldWebApplicationFactory factory)
    {
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
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