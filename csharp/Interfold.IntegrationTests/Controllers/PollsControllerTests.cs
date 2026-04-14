using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Interfold.IntegrationTests.Attributes;
using Interfold.IntegrationTests.TestServices;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Interfold.IntegrationTests.Controllers;

public class PollsControllerTests : BaseEndpointTest
{
    [Test, ApiIntegration]
    public async Task ErrorResponse_ConflictFormats_IncludeEntityRefAndCode()
    {
        await using var factory = new InterfoldWebApplicationFactory()
            .WithConfiguration("OCTOCON_PERSISTENCE", "inmemory")
            .WithConfiguration("OCTOCON_AUTH_CHALLENGE_ENABLED", "false");

        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var principal = "parity-error-format";
        var tooLongTitle = new string('a', 101);

        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/polls")
        {
            Content = JsonContent.Create(new { title = tooLongTitle })
        };
        var res = await client.SendAsync(req);
        var body = await res.Content.ReadAsStringAsync();

        await Assert.That(res.StatusCode).IsEqualTo(HttpStatusCode.UnprocessableEntity);

        // Verify error response includes expected fields.
        await Assert.That(body.Contains("\"code\"", StringComparison.OrdinalIgnoreCase)).IsTrue();
        await Assert.That(body.Contains("\"entityRef\"", StringComparison.OrdinalIgnoreCase)).IsTrue();
        await Assert.That(body.Contains("poll:title_too_long", StringComparison.OrdinalIgnoreCase)).IsTrue();
    }
    
    [Test, ApiIntegration]
    public async Task PollValidation_DescriptionTooLong_Returns422()
    {
        await using var factory = new InterfoldWebApplicationFactory()
            .WithConfiguration("OCTOCON_PERSISTENCE", "inmemory")
            .WithConfiguration("OCTOCON_AUTH_CHALLENGE_ENABLED", "false");

        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var principal = "parity-poll-desc-validation";
        var validTitle = "ValidTitle";
        var tooLongDesc = new string('a', 2001); // Max is 2000

        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/polls")
        {
            Content = JsonContent.Create(new { title = validTitle, description = tooLongDesc })
        };
        var res = await client.SendAsync(req);
        var body = await res.Content.ReadAsStringAsync();

        await Assert.That(res.StatusCode).IsEqualTo(HttpStatusCode.UnprocessableEntity);
        await Assert.That(body.Contains("poll:description_too_long", StringComparison.OrdinalIgnoreCase)).IsTrue();
    }
    
    [Test, ApiIntegration]
    public async Task PollType_AllSupportedTypes_RoundTripCorrectly()
    {
        await using var factory = new InterfoldWebApplicationFactory()
            .WithConfiguration("OCTOCON_PERSISTENCE", "inmemory")
            .WithConfiguration("OCTOCON_AUTH_CHALLENGE_ENABLED", "false");

        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var principal = "parity-poll-types";

        // Test all supported poll type variants.
        var typeVariants = new[] { "single_choice", "vote", "multiple_choice", "choice", "approval" };

        foreach (var type in typeVariants)
        {
            using var createReq = new HttpRequestMessage(HttpMethod.Post, "/api/polls")
            {
                Content = JsonContent.Create(new { title = $"Poll_{type}", type })
            };
            var createRes = await client.SendAsync(createReq);
            var createBody = await createRes.Content.ReadAsStringAsync();

            await Assert.That(createRes.StatusCode).IsEqualTo(HttpStatusCode.Created);

            // Extract poll ID from response.
            using var createDoc = JsonDocument.Parse(createBody);
            var pollId = createDoc.RootElement
                .GetProperty("data")
                .GetProperty("id")
                .GetString();
            
            await Assert.That(pollId).IsNotNullOrWhiteSpace();

            // Verify poll retrieves with correct canonical type.
            var getRes = await client.GetAsync($"/api/polls/{pollId}");
            var getBody = await getRes.Content.ReadAsStringAsync();

            await Assert.That(getRes.StatusCode).IsEqualTo(HttpStatusCode.OK);

            // Legacy aliases (single_choice, multiple_choice) should read back as canonical Elixir type names (vote, choice).
            var expectedType = type switch
            {
                "single_choice" => "vote",
                "multiple_choice" => "choice",
                _ => type
            };

            await Assert.That(getBody.Contains($"\"type\":\"{expectedType}\"", StringComparison.Ordinal)).IsTrue();
        }
    }

    [Test, ApiIntegration]
    public async Task PollUpdate_TimeEndNullOnly_ClearsExistingTimeEnd()
    {
        await using var factory = new InterfoldWebApplicationFactory()
            .WithConfiguration("OCTOCON_PERSISTENCE", "inmemory")
            .WithConfiguration("OCTOCON_AUTH_CHALLENGE_ENABLED", "false");

        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var initialTimeEnd = DateTime.UtcNow.AddHours(1);

        using var createReq = new HttpRequestMessage(HttpMethod.Post, "/api/polls")
        {
            Content = JsonContent.Create(new { title = "TimeEndEdgeCase", time_end = initialTimeEnd })
        };
        var createRes = await client.SendAsync(createReq);
        var createBody = await createRes.Content.ReadAsStringAsync();

        await Assert.That(createRes.StatusCode).IsEqualTo(HttpStatusCode.Created);

        using var createDoc = JsonDocument.Parse(createBody);
        var pollId = createDoc.RootElement
            .GetProperty("data")
            .GetProperty("id")
            .GetString();

        await Assert.That(pollId).IsNotNullOrWhiteSpace();

        using var patchReq = new HttpRequestMessage(HttpMethod.Patch, $"/api/polls/{pollId}")
        {
            Content = JsonContent.Create(new { time_end = (DateTime?)null })
        };
        var patchRes = await client.SendAsync(patchReq);
        var patchBody = await patchRes.Content.ReadAsStringAsync();

        await Assert.That(patchRes.StatusCode).IsEqualTo(HttpStatusCode.NoContent);

        var getRes = await client.GetAsync($"/api/polls/{pollId}");
        var getBody = await getRes.Content.ReadAsStringAsync();

        await Assert.That(getRes.StatusCode).IsEqualTo(HttpStatusCode.OK);

        using var getDoc = JsonDocument.Parse(getBody);
        var timeEndElement = getDoc.RootElement
            .GetProperty("data")
            .GetProperty("time_end");

        await Assert.That(timeEndElement.ValueKind).IsEqualTo(JsonValueKind.Null);
    }

    [Test, ApiIntegration]
    public async Task PollValidation_TitleTooLong_Returns422()
    {
        await using var factory = new InterfoldWebApplicationFactory()
            .WithConfiguration("OCTOCON_PERSISTENCE", "inmemory")
            .WithConfiguration("OCTOCON_AUTH_CHALLENGE_ENABLED", "false");

        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var principal = "parity-poll-validation";
        var tooLongTitle = new string('a', 101); // Max is 100

        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/polls")
        {
            Content = JsonContent.Create(new { title = tooLongTitle })
        };
        var res = await client.SendAsync(req);
        var body = await res.Content.ReadAsStringAsync();

        await Assert.That(res.StatusCode).IsEqualTo(HttpStatusCode.UnprocessableEntity);
        await Assert.That(body.Contains("poll:title_too_long", StringComparison.OrdinalIgnoreCase)).IsTrue();
    }
    
    [Test, ApiIntegration]
    public async Task LegacyRoute_SystemsMePolls_Returns404()
    {
        await using var factory = new InterfoldWebApplicationFactory()
            .WithConfiguration("OCTOCON_PERSISTENCE", "inmemory")
            .WithConfiguration("OCTOCON_AUTH_CHALLENGE_ENABLED", "false");

        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var principal = "parity-legacy-routes";

        foreach (var (method, path) in new[]
                 {
                     (HttpMethod.Get,  "/api/systems/me/polls"),
                     (HttpMethod.Post, "/api/systems/me/polls"),
                 })
        {
            using var req = new HttpRequestMessage(method, path)
            {
                Content = method == HttpMethod.Post
                    ? JsonContent.Create(new { title = "LegacyPoll" })
                    : null
            };
            var res = await client.SendAsync(req);

            await Assert.That(res.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        }
    }
    
    [Test, ApiIntegration]
    public async Task Idempotency_PollCreate_ReplayStable()
    {
        await RunSoakAsync(async (client, key) =>
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, "/api/polls")
            {
                Content = JsonContent.Create(new { title = "SoakPoll", options = new[] { "Yes", "No" } })
            };
            req.Headers.Add("X-Interfold-Idempotency-Key", key);
            return await client.SendAsync(req);
        });
    }
}