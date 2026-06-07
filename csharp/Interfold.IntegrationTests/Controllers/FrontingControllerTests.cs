using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Interfold.IntegrationTests.Attributes;
using Interfold.IntegrationTests.TestServices;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Interfold.IntegrationTests.Controllers;

public class FrontingControllerTests : BaseEndpointTest
{
    
    [Test, ApiIntegration]
    [CombinedDataSources]
    public async Task FrontStart_LegacyIdField_Returns201([InterfoldFactoryGenerator] InterfoldWebApplicationFactory factory)
    {
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var principal = "parity-front-legacy-id";
        var alterId = await CreateAlterAsync(client, principal, "LegacyFrontAlter");

        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/systems/me/front/start")
        {
            Content = JsonContent.Create(new { id = alterId })
        };
        AttachPrincipalAuth(req, client, principal);
        var res = await client.SendAsync(req);
        var body = await res.Content.ReadAsStringAsync();

        await Assert.That(res.StatusCode).IsEqualTo(HttpStatusCode.Created);
    }

    [Test, ApiIntegration]
    [CombinedDataSources]
    public async Task Api_FrontHistoryBetween_IncludesEndedFronts([InterfoldFactoryGenerator] InterfoldWebApplicationFactory factory)
    {
        factory
            .WithConfiguration("OCTOCON_DEEPLINK_ADDRESS", "octocon://app")
            .WithConfiguration("OCTOCON_SCYLLA_KEYSPACE", "nam");

        using var client = factory.CreateClient();

        var startAnchor = DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeSeconds();

        var principal = "phase3-fronting-history";
        var alter = await CreateAlterAsync(client, principal, "test alter");

        var started = await SendFrontStartAsync(client, alterId: alter, comment: "phase3-history", principal);
        var startedFrontId = ReadNestedStringField(started.Body, "data", "front_id");
        using (Assert.Multiple())
        {
            await Assert.That(started.StatusCode).IsEqualTo(HttpStatusCode.Created);
            await Assert.That(startedFrontId).IsNotNullOrWhiteSpace();
        }

        var ended = await SendFrontEndAsync(client, alterId: alter, principal);
        await Assert.That(ended.StatusCode).IsEqualTo(HttpStatusCode.NoContent);

        var endAnchor = DateTimeOffset.UtcNow.AddMinutes(1).ToUnixTimeSeconds();
        using var betweenRequest = new HttpRequestMessage(HttpMethod.Get,
            $"/api/systems/me/front/between?start={startAnchor}&end={endAnchor}");
        AttachPrincipalAuth(betweenRequest, client, principal);
        var betweenResponse = await client.SendAsync(betweenRequest);
        var betweenBody = await betweenResponse.Content.ReadAsStringAsync();

        using var doc = JsonDocument.Parse(betweenBody);
        var data = betweenResponse.StatusCode == HttpStatusCode.OK
            ? doc.RootElement.GetProperty("data")
            : (JsonElement?)null;
        using (Assert.Multiple())
        {
            await Assert.That(betweenResponse.StatusCode).IsEqualTo(HttpStatusCode.OK);
            
            Assert.NotNull(data);
            await Assert.That(data.Value.ValueKind).IsEqualTo(JsonValueKind.Array);
            await Assert.That(data.Value.GetArrayLength()).IsGreaterThan(0);
        }

        var row = data.Value.EnumerateArray().First();
        var responseFrontId = ReadStringField(row, "id");
        var responseComment = ReadStringField(row, "comment");
        var endedAt = ReadNullableStringField(row, "time_end");
        using (Assert.Multiple())
        {
            await Assert.That(responseFrontId).IsEqualTo(startedFrontId);
            await Assert.That(responseComment).IsEqualTo("phase3-history");
            await Assert.That(endedAt).IsNotNullOrWhiteSpace();
        }
    }
}
