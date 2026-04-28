using System.Net;
using System.Net.Http.Json;
using Interfold.IntegrationTests.Attributes;
using Interfold.IntegrationTests.TestServices;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Interfold.IntegrationTests.Controllers;

public class JournalsControllerTests : BaseEndpointTest
{
    [Test, ApiIntegration]
    [CombinedDataSources]
    public async Task Idempotency_GlobalJournalCreate_ReplayStable([InterfoldFactoryGenerator] InterfoldWebApplicationFactory factory)
    {
        await RunSoakAsync(factory, async (client, key) =>
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, "/api/journals")
            {
                Content = JsonContent.Create(new { title = "SoakJournal", body = "entry body" })
            };
            req.Headers.Add("X-Interfold-Idempotency-Key", key);
            return await client.SendAsync(req);
        });
    }
}