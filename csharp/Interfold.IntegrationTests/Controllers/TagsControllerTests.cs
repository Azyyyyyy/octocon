using System.Net;
using System.Net.Http.Json;
using Interfold.IntegrationTests.Attributes;
using Interfold.IntegrationTests.TestServices;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Interfold.IntegrationTests.Controllers;

public class TagsControllerTests : BaseEndpointTest
{
    [Test, ApiIntegration]
    public async Task Idempotency_TagCreate_ReplayStable()
    {
        await RunSoakAsync(async (client, key) =>
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, "/api/systems/me/tags")
            {
                Content = JsonContent.Create(new { name = "SoakTag" })
            };
            req.Headers.Add("X-Interfold-Idempotency-Key", key);
            return await client.SendAsync(req);
        });
    }
    
    [Test, ApiIntegration]
    public async Task TagParent_SetAndRemove_Returns204()
    {
        await using var factory = new InterfoldWebApplicationFactory()
            .WithConfiguration("OCTOCON_PERSISTENCE", "inmemory")
            .WithConfiguration("OCTOCON_AUTH_CHALLENGE_ENABLED", "false");

        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var principal = "parity-tag-parent";
        var parentTagId = await CreateTagAsync(client, principal, "ParentTag");
        var childTagId = await CreateTagAsync(client, principal, "ChildTag");

        using var setReq = new HttpRequestMessage(HttpMethod.Post, $"/api/systems/me/tags/{childTagId}/parent")
        {
            Content = JsonContent.Create(new { parentTagId })
        };
        var setRes = await client.SendAsync(setReq);

        await Assert.That(setRes.StatusCode).IsEqualTo(HttpStatusCode.NoContent);

        using var removeReq = new HttpRequestMessage(HttpMethod.Delete, $"/api/systems/me/tags/{childTagId}/parent")
        {
            Content = JsonContent.Create(new { })
        };
        var removeRes = await client.SendAsync(removeReq);

        await Assert.That(removeRes.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
    }
    
}