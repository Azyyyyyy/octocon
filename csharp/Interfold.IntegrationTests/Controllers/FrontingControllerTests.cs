using System.Net;
using System.Net.Http.Json;
using Interfold.IntegrationTests.Attributes;
using Interfold.IntegrationTests.TestServices;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Interfold.IntegrationTests.Controllers;

public class FrontingControllerTests : BaseEndpointTest
{
    
    [Test, ApiIntegration]
    public async Task FrontStart_LegacyIdField_Returns201()
    {
        await using var factory = new InterfoldWebApplicationFactory()
            .WithConfiguration("OCTOCON_PERSISTENCE", "inmemory")
            .WithConfiguration("OCTOCON_AUTH_CHALLENGE_ENABLED", "false");

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
        var res = await client.SendAsync(req);
        var body = await res.Content.ReadAsStringAsync();

        await Assert.That(res.StatusCode).IsEqualTo(HttpStatusCode.Created);
    }

    [Test, ApiIntegration]
    public async Task Api_FrontHistoryBetween_IncludesEndedFronts()
    {
        await using var factory = new InterfoldWebApplicationFactory()
            .WithConfiguration("OCTOCON_PERSISTENCE", "inmemory")
            .WithConfiguration("OCTOCON_DEEPLINK_ADDRESS", "octocon://app")
            .WithConfiguration("OCTOCON_REGION", "nam")
            .WithConfiguration("OCTOCON_AUTH_CHALLENGE_ENABLED", "false");

        using var client = factory.CreateClient();

        var startAnchor = DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeSeconds();

        var started = await SendFrontStartAsync(client, alterId: 101, comment: "phase3-history");
        await Assert.That(started.StatusCode).IsEqualTo(HttpStatusCode.Created);

        var startedFrontId = ReadStringField(started.Body, "frontId");
        await Assert.That(startedFrontId).IsNotNullOrWhiteSpace();

        var ended = await SendFrontEndAsync(client, alterId: 101);
        await Assert.That(ended.StatusCode).IsEqualTo(HttpStatusCode.NoContent);

        var endAnchor = DateTimeOffset.UtcNow.AddMinutes(1).ToUnixTimeSeconds();
        using var betweenRequest = new HttpRequestMessage(HttpMethod.Get,
            $"/api/systems/me/front/between?start={startAnchor}&end={endAnchor}");
        var betweenResponse = await client.SendAsync(betweenRequest);
        var betweenBody = await betweenResponse.Content.ReadAsStringAsync();

        await Assert.That(betweenResponse.StatusCode).IsEqualTo(HttpStatusCode.OK);

        using var doc = System.Text.Json.JsonDocument.Parse(betweenBody);
        await Assert.That(doc.RootElement.ValueKind).IsEqualTo(System.Text.Json.JsonValueKind.Array);
        await Assert.That(doc.RootElement.GetArrayLength()).IsGreaterThan(0);

        var row = doc.RootElement.EnumerateArray().First();
        var responseFrontId = ReadStringField(row, "frontId");
        await Assert.That(responseFrontId).IsEqualTo(startedFrontId);

        var responseComment = ReadStringField(row, "comment");
        await Assert.That(responseComment).IsEqualTo("phase3-history");

        var endedAt = ReadNullableStringField(row, "endedAt");
        await Assert.That(endedAt).IsNotNullOrWhiteSpace();
    }
}