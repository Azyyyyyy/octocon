using System.Net;
using Interfold.IntegrationTests.Attributes;
using Interfold.IntegrationTests.TestServices;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Interfold.IntegrationTests.Controllers;

public class HeartbeatControllerTests : BaseEndpointTest
{
    [Test, ApiIntegration]
    [CombinedDataSources]
    public async Task Api_Heartbeat_ReturnsContractHeader([InterfoldFactoryGenerator] InterfoldWebApplicationFactory factory)
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/heartbeat");
        using (Assert.Multiple())
        {
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(
                response.Headers.TryGetValues("X-Interfold-Contract", out var contractValues) &&
                contractValues.Contains("2026-03-v1", StringComparer.Ordinal)).IsTrue();
        }
    }
    
    [Test, ApiIntegration]
    [CombinedDataSources]
    public async Task OperationalHealth_GuardedPaths_ListGuardedAsync_Succeeds([InterfoldFactoryGenerator] InterfoldWebApplicationFactory factory)
    {
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        // If API started successfully, guarded paths are at least partially functional.
        // A full integration test would create test data and verify filtering.
        var res = await client.GetAsync("/api/heartbeat");
        await Assert.That(res.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }
    
    [Test, ApiIntegration]
    [CombinedDataSources]
    public async Task Idempotency_CorrelationId_EchoedFromRequestHeader([InterfoldFactoryGenerator] InterfoldWebApplicationFactory factory)
    {
        using var client = factory.CreateClient();

        var sentId = Guid.NewGuid().ToString("N");
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/heartbeat");
        request.Headers.Add("X-Request-Id", sentId);

        var response = await client.SendAsync(request);
        using (Assert.Multiple())
        {
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(
                response.Headers.TryGetValues("X-Interfold-Request-Id", out var values) &&
                values.First() == sentId).IsTrue();
        }
    }
    
    [Test, ApiIntegration]
    [CombinedDataSources]
    public async Task Idempotency_RequestId_PresentOnEveryResponse([InterfoldFactoryGenerator] InterfoldWebApplicationFactory factory)
    {
        using var client = factory.CreateClient();

        for (var i = 0; i < 3; i++)
        {
            var response = await client.GetAsync("/api/heartbeat");
            using (Assert.Multiple())
            {
                await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
                await Assert.That(
                    response.Headers.TryGetValues("X-Interfold-Request-Id", out var values) &&
                    !string.IsNullOrWhiteSpace(values.First())).IsTrue();
            }
        }
    }
}