using System.Net;
using System.Net.Http.Json;
using Interfold.IntegrationTests.Attributes;
using Interfold.IntegrationTests.TestServices;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Interfold.IntegrationTests.Controllers;

[Category("Alter Journals")]
public class AlterJournalsControllerTests : BaseEndpointTest
{
    [Test, ApiIntegration, Category("Index")]
    public async Task AlterJournal_ListWhenEmpty_ReturnsDataAsEmptyArray()
    {
        await using var factory = new InterfoldWebApplicationFactory("inmemory");

        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions());

        var principal = "parity-alter-journal-empty-list";
        var alterId = await CreateAlterAsync(client, principal, "NoJournalAlter");

        using var listReq = new HttpRequestMessage(HttpMethod.Get, $"/api/systems/me/alters/{alterId}/journals");
        AttachPrincipalAuth(listReq, client, principal);
        var listRes = await client.SendAsync(listReq);
        var listBody = await listRes.Content.ReadAsStringAsync();

        using var doc = System.Text.Json.JsonDocument.Parse(listBody);
        using (Assert.Multiple())
        {
            await Assert.That(listRes.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(doc.RootElement.TryGetProperty("data", out var dataProp)).IsTrue();
            await Assert.That(dataProp.ValueKind).IsEqualTo(System.Text.Json.JsonValueKind.Array);
            await Assert.That(dataProp.GetArrayLength()).IsEqualTo(0);
        }
    }

    [Test, ApiIntegration]
    public async Task AlterJournal_NestedCreate_Returns201WithDataAndReplay()
    {
        await using var factory = new InterfoldWebApplicationFactory("inmemory");

        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var principal = "parity-alter-journal";
        var alterId = await CreateAlterAsync(client, principal, "JournalHolder");

        // POST /api/systems/me/alters/:id/journals  →  201 + {data, replay}
        using var createReq = new HttpRequestMessage(HttpMethod.Post, $"/api/systems/me/alters/{alterId}/journals")
        {
            Content = JsonContent.Create(new { title = "NestedParityJournal" })
        };
        AttachPrincipalAuth(createReq, client, principal);
        var createRes = await client.SendAsync(createReq);
        var createBody = await createRes.Content.ReadAsStringAsync();

        var entryId = ReadNestedString(createBody, "data", "id");
        if (string.IsNullOrWhiteSpace(entryId))
            entryId = ReadNestedString(createBody, "data", "entry_id");

        var replay = ReadBool(createBody, "replay");
        using (Assert.Multiple())
        {
            await Assert.That(createRes.StatusCode).IsEqualTo(HttpStatusCode.Created);
            await Assert.That(entryId).IsNotNullOrWhiteSpace();
            await Assert.That(replay).IsFalse();
        }

        // GET /api/systems/me/alters/:id/journals  →  200 + {data:[...]}
        using var listReq = new HttpRequestMessage(HttpMethod.Get, $"/api/systems/me/alters/{alterId}/journals");
        AttachPrincipalAuth(listReq, client, principal);
        var listRes = await client.SendAsync(listReq);
        var listBody = await listRes.Content.ReadAsStringAsync();

        using (Assert.Multiple())
        {
            await Assert.That(listRes.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(listBody.Contains("data", StringComparison.OrdinalIgnoreCase)).IsTrue();
        }

        // GET /api/systems/me/alters/journals/:journalId  →  200 + {data:{...}}
        using var showReq = new HttpRequestMessage(HttpMethod.Get, $"/api/systems/me/alters/journals/{entryId}");
        AttachPrincipalAuth(showReq, client, principal);
        var showRes = await client.SendAsync(showReq);
        var showBody = await showRes.Content.ReadAsStringAsync();

        var shownId = ReadNestedString(showBody, "data", "id");
        if (string.IsNullOrWhiteSpace(shownId))
            shownId = ReadNestedString(showBody, "data", "entry_id");
        using (Assert.Multiple())
        {
            await Assert.That(showRes.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(string.Equals(shownId, entryId, StringComparison.OrdinalIgnoreCase)).IsTrue();
        }

        // PATCH /api/systems/me/alters/journals/:journalId  →  204
        using var patchReq = new HttpRequestMessage(HttpMethod.Patch, $"/api/systems/me/alters/journals/{entryId}")
        {
            Content = JsonContent.Create(new { title = "UpdatedParityJournal" })
        };
        AttachPrincipalAuth(patchReq, client, principal);
        var patchRes = await client.SendAsync(patchReq);

        await Assert.That(patchRes.StatusCode).IsEqualTo(HttpStatusCode.NoContent);

        // DELETE /api/systems/me/alters/journals/:journalId  →  204
        using var deleteReq = new HttpRequestMessage(HttpMethod.Delete, $"/api/systems/me/alters/journals/{entryId}");
        AttachPrincipalAuth(deleteReq, client, principal);
        var deleteRes = await client.SendAsync(deleteReq);

        await Assert.That(deleteRes.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
    }

    [Test, ApiIntegration]
    public async Task AlterJournal_ShowAfterDelete_Returns404()
    {
        await using var factory = new InterfoldWebApplicationFactory("inmemory");

        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var principal = "parity-alter-journal-delete-404";
        var alterId = await CreateAlterAsync(client, principal, "DeleteHolder");

        using var createReq = new HttpRequestMessage(HttpMethod.Post, $"/api/systems/me/alters/{alterId}/journals")
        {
            Content = JsonContent.Create(new { title = "JournalToDelete" })
        };
        AttachPrincipalAuth(createReq, client, principal);
        var createRes = await client.SendAsync(createReq);
        var createBody = await createRes.Content.ReadAsStringAsync();
        var entryId = ReadNestedString(createBody, "data", "id");
        if (string.IsNullOrWhiteSpace(entryId)) entryId = ReadNestedString(createBody, "data", "entry_id");

        using var deleteReq = new HttpRequestMessage(HttpMethod.Delete, $"/api/systems/me/alters/journals/{entryId}");
        AttachPrincipalAuth(deleteReq, client, principal);
        _ = await client.SendAsync(deleteReq);

        using var showReq = new HttpRequestMessage(HttpMethod.Get, $"/api/systems/me/alters/journals/{entryId}");
        AttachPrincipalAuth(showReq, client, principal);
        var showRes = await client.SendAsync(showReq);
        await Assert.That(showRes.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }
}