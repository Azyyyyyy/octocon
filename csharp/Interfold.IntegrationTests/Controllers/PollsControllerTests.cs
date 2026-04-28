using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Interfold.IntegrationTests.Attributes;
using Interfold.IntegrationTests.TestServices;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Interfold.IntegrationTests.Controllers;

public class PollsControllerTests : BaseEndpointTest
{
    public static IEnumerable<string> PollTypes()
    {
        yield return "single_choice";
        yield return "vote";
        yield return "multiple_choice";
        yield return "choice";
        yield return "approval";
    }
    
    [Test, ApiIntegration]
    [CombinedDataSources]
    public async Task ErrorResponse_ConflictFormats_IncludeEntityRefAndCode([InterfoldFactoryGenerator] InterfoldWebApplicationFactory factory)
    {
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
        AttachPrincipalAuth(req, client, principal);
        var res = await client.SendAsync(req);
        var body = await res.Content.ReadAsStringAsync();

        // Verify error response includes expected fields.
        using (Assert.Multiple())
        {
            await Assert.That(res.StatusCode).IsEqualTo(HttpStatusCode.UnprocessableEntity);
            await Assert.That(body).Contains("\"code\"");
            await Assert.That(body).Contains("\"entity_ref\"");
            await Assert.That(body).Contains("poll:title_too_long");
        }
    }
    
    [Test, ApiIntegration]
    [CombinedDataSources]
    public async Task PollValidation_DescriptionTooLong_Returns422([InterfoldFactoryGenerator] InterfoldWebApplicationFactory factory)
    {
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
        AttachPrincipalAuth(req, client, principal);
        var res = await client.SendAsync(req);
        var body = await res.Content.ReadAsStringAsync();

        using (Assert.Multiple())
        {
            await Assert.That(res.StatusCode).IsEqualTo(HttpStatusCode.UnprocessableEntity);
            await Assert.That(body.Contains("poll:description_too_long", StringComparison.OrdinalIgnoreCase)).IsTrue();
        }
    }
    
    [Test, ApiIntegration]
    [CombinedDataSources]
    public async Task PollType_AllSupportedTypes_RoundTripCorrectly([InterfoldFactoryGenerator] InterfoldWebApplicationFactory factory, [MethodDataSource(typeof(PollsControllerTests), nameof(PollTypes))] string type)
    {
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var principal = "parity-poll-types";
        
        using var createReq = new HttpRequestMessage(HttpMethod.Post, "/api/polls")
        {
            Content = JsonContent.Create(new { title = $"Poll_{type}", type })
        };
        AttachPrincipalAuth(createReq, client, principal);
        var createRes = await client.SendAsync(createReq);
        var createBody = await createRes.Content.ReadAsStringAsync();

        // Extract poll ID from response.
        using var createDoc = JsonDocument.Parse(createBody);
        var pollId = createDoc.RootElement
            .GetProperty("data")
            .GetProperty("id")
            .GetString();

        using (Assert.Multiple())
        {
            await Assert.That(createRes.StatusCode).IsEqualTo(HttpStatusCode.Created);
            await Assert.That(pollId).IsNotNullOrWhiteSpace();
        }

        // Verify poll retrieves with correct canonical type.
        using var getReq = new HttpRequestMessage(HttpMethod.Get, $"/api/polls/{pollId}");
        AttachPrincipalAuth(getReq, client, principal);
        var getRes = await client.SendAsync(getReq);
        var getBody = await getRes.Content.ReadAsStringAsync();

        using (Assert.Multiple())
        {
            await Assert.That(getRes.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(getBody).Contains($"\"type\":\"{type}\"");
        }
    }

    [Test, ApiIntegration]
    [CombinedDataSources]
    public async Task PollUpdate_TimeEndNullOnly_ClearsExistingTimeEnd([InterfoldFactoryGenerator] InterfoldWebApplicationFactory factory)
    {
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var initialTimeEnd = DateTime.UtcNow.AddHours(1);

        using var createReq = new HttpRequestMessage(HttpMethod.Post, "/api/polls")
        {
            Content = JsonContent.Create(new { title = "TimeEndEdgeCase", time_end = initialTimeEnd })
        };
        AttachPrincipalAuth(createReq, client, "parity-poll-time-end");
        var createRes = await client.SendAsync(createReq);
        var createBody = await createRes.Content.ReadAsStringAsync();

        using var createDoc = JsonDocument.Parse(createBody);
        var pollId = createDoc.RootElement
            .GetProperty("data")
            .GetProperty("id")
            .GetString();

        using (Assert.Multiple())
        {
            await Assert.That(createRes.StatusCode).IsEqualTo(HttpStatusCode.Created);
            await Assert.That(pollId).IsNotNullOrWhiteSpace();
        }

        using var patchReq = new HttpRequestMessage(HttpMethod.Patch, $"/api/polls/{pollId}")
        {
            Content = JsonContent.Create(new { time_end = (DateTime?)null })
        };
        AttachPrincipalAuth(patchReq, client, "parity-poll-time-end");
        var patchRes = await client.SendAsync(patchReq);
        var patchBody = await patchRes.Content.ReadAsStringAsync();

        await Assert.That(patchRes.StatusCode).IsEqualTo(HttpStatusCode.NoContent);

        using var getReq = new HttpRequestMessage(HttpMethod.Get, $"/api/polls/{pollId}");
        AttachPrincipalAuth(getReq, client, "parity-poll-time-end");
        var getRes = await client.SendAsync(getReq);
        var getBody = await getRes.Content.ReadAsStringAsync();

        using var getDoc = JsonDocument.Parse(getBody);
        var timeEndElement = getDoc.RootElement
            .GetProperty("data")
            .GetProperty("time_end");

        using (Assert.Multiple())
        {
            await Assert.That(getRes.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(timeEndElement.ValueKind).IsEqualTo(JsonValueKind.Null);
        }
    }

    [Test, ApiIntegration]
    [CombinedDataSources]
    public async Task PollValidation_TitleTooLong_Returns422([InterfoldFactoryGenerator] InterfoldWebApplicationFactory factory)
    {
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
        AttachPrincipalAuth(req, client, principal);
        var res = await client.SendAsync(req);
        var body = await res.Content.ReadAsStringAsync();

        using (Assert.Multiple())
        {
            await Assert.That(res.StatusCode).IsEqualTo(HttpStatusCode.UnprocessableEntity);
            await Assert.That(body.Contains("poll:title_too_long", StringComparison.OrdinalIgnoreCase)).IsTrue();
        }
    }
    
    [Test, ApiIntegration]
    [CombinedDataSources]
    public async Task LegacyRoute_SystemsMePolls_Returns404([InterfoldFactoryGenerator] InterfoldWebApplicationFactory factory)
    {
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
            AttachPrincipalAuth(req, client, principal);
            var res = await client.SendAsync(req);

            await Assert.That(res.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        }
    }
    
    [Test, ApiIntegration]
    [CombinedDataSources]
    public async Task Idempotency_PollCreate_ReplayStable([InterfoldFactoryGenerator] InterfoldWebApplicationFactory factory)
    {
        await RunSoakAsync(factory, async (client, key) =>
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