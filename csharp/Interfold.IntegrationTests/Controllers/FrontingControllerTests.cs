using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Interfold.IntegrationTests.TestServices;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Interfold.IntegrationTests.Controllers;

[ClassDataSource<InMemoryWebFactoryFixture>(Shared = SharedType.PerTestSession)]
[ClassDataSource<ScyllaWebFactoryFixture>(Shared = SharedType.PerTestSession)]
[ClassDataSource<CassandraWebFactoryFixture>(Shared = SharedType.PerTestSession)]
public class FrontingControllerTests(IWebFactoryFixture fixture) : BaseEndpointTest
{
    
    [Test]
    public async Task FrontStart_LegacyIdField_Returns201()
    {
        using var client = fixture.Factory.CreateClient(new WebApplicationFactoryClientOptions
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

    [Test]
    public async Task Api_FrontHistoryBetween_IncludesEndedFronts()
    {
        using var client = fixture.Factory.CreateClient();

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

    // ===== Set-fronting semantics =====
    //
    // The set endpoint must leave the target alter as the sole fronter, regardless of the
    // starting state. The previous implementation rejected with `fronting:already_fronting`
    // when the target was already in the active set (broke promote-among-many) and only
    // published a single FrontingSetEvent (clients never saw the per-alter end events for
    // the alters that were silently dropped from the active set).
    //
    // The four cases below cover every starting state. They assert the post-set active
    // list because that is the observable contract the client cares about; per-alter
    // FrontingEndedEvent emission is exercised by the same code path (the handler will not
    // reach the post-end event publish without ending the alter first).

    [Test]
    public async Task FrontSet_FromNoActiveFronters_StartsTargetAsSoleFronter()
    {
        using var client = fixture.Factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var principal = $"front-set-empty-{Guid.NewGuid():N}"[..32];
        var target = await CreateAlterAsync(client, principal, "TargetAlter");

        var setResult = await SendFrontSetAsync(client, target, principal, comment: "from-empty");
        await Assert.That(setResult.StatusCode).IsEqualTo(HttpStatusCode.NoContent);

        var activeAlters = await ListActiveFrontingAlterIdsAsync(client, principal);
        await Assert.That(activeAlters).IsEquivalentTo(new[] { target });
    }

    [Test]
    public async Task FrontSet_WhenTargetIsAlreadySoleFronter_PreservesFrontIdIdempotently()
    {
        // Regression: set(X) when X is already the only fronter must not reject with
        // `fronting:already_fronting` and must not create a new front row (front_id is
        // the stable client-facing handle into the active row; rewriting it on every
        // idempotent set call would break clients that pinned that id).
        using var client = fixture.Factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var principal = $"front-set-target-only-{Guid.NewGuid():N}"[..32];
        var target = await CreateAlterAsync(client, principal, "TargetAlter");

        var initialStart = await SendFrontStartAsync(client, alterId: target, comment: "initial", principal);
        await Assert.That(initialStart.StatusCode).IsEqualTo(HttpStatusCode.Created);
        var originalFrontId = ReadNestedStringField(initialStart.Body, "data", "front_id");
        await Assert.That(originalFrontId).IsNotNullOrWhiteSpace();

        var setResult = await SendFrontSetAsync(client, target, principal, comment: "idempotent");
        await Assert.That(setResult.StatusCode).IsEqualTo(HttpStatusCode.NoContent);

        var activeFrontIdsByAlter = await ListActiveFrontIdsByAlterAsync(client, principal);
        using (Assert.Multiple())
        {
            await Assert.That(activeFrontIdsByAlter.Keys).IsEquivalentTo(new[] { target });
            await Assert.That(activeFrontIdsByAlter[target])
                .IsEqualTo(originalFrontId)
                .Because("set against an already-sole fronter must preserve the existing front_id.");
        }
    }

    [Test]
    public async Task FrontSet_WhenOtherAltersAreFronting_EndsThemAndStartsTarget()
    {
        using var client = fixture.Factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var principal = $"front-set-others-only-{Guid.NewGuid():N}"[..32];
        var other = await CreateAlterAsync(client, principal, "OtherAlter");
        var target = await CreateAlterAsync(client, principal, "TargetAlter");

        var otherStart = await SendFrontStartAsync(client, alterId: other, comment: "to-be-ended", principal);
        await Assert.That(otherStart.StatusCode).IsEqualTo(HttpStatusCode.Created);

        var setResult = await SendFrontSetAsync(client, target, principal);
        await Assert.That(setResult.StatusCode).IsEqualTo(HttpStatusCode.NoContent);

        var activeAlters = await ListActiveFrontingAlterIdsAsync(client, principal);
        await Assert.That(activeAlters)
            .IsEquivalentTo(new[] { target })
            .Because("the other alter must be ended so the target is the sole fronter.");
    }

    [Test]
    public async Task FrontSet_WhenTargetAndOthersAreFronting_PreservesTargetAndEndsOthers()
    {
        // The crucial promote-among-many case. Pre-fix this rejected with
        // `fronting:already_fronting`; post-fix it must end the others and keep the
        // target's existing front row intact (preserving front_id and start_time).
        using var client = fixture.Factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var principal = $"front-set-mixed-{Guid.NewGuid():N}"[..32];
        var target = await CreateAlterAsync(client, principal, "TargetAlter");
        var other = await CreateAlterAsync(client, principal, "OtherAlter");

        var targetStart = await SendFrontStartAsync(client, alterId: target, comment: "stays", principal);
        await Assert.That(targetStart.StatusCode).IsEqualTo(HttpStatusCode.Created);
        var targetOriginalFrontId = ReadNestedStringField(targetStart.Body, "data", "front_id");

        var otherStart = await SendFrontStartAsync(client, alterId: other, comment: "ends", principal);
        await Assert.That(otherStart.StatusCode).IsEqualTo(HttpStatusCode.Created);

        var setResult = await SendFrontSetAsync(client, target, principal);
        await Assert.That(setResult.StatusCode)
            .IsEqualTo(HttpStatusCode.NoContent)
            .Because("set must succeed when the target is already in a multi-fronter active set; the old `already_fronting` rejection broke this case.");

        var activeFrontIdsByAlter = await ListActiveFrontIdsByAlterAsync(client, principal);
        using (Assert.Multiple())
        {
            await Assert.That(activeFrontIdsByAlter.Keys)
                .IsEquivalentTo(new[] { target })
                .Because("only the target should remain fronting after set.");
            await Assert.That(activeFrontIdsByAlter[target])
                .IsEqualTo(targetOriginalFrontId)
                .Because("the target's existing front_id must be preserved; set should not end-and-restart the target.");
        }
    }

    private static async Task<IReadOnlyList<int>> ListActiveFrontingAlterIdsAsync(HttpClient client, string principal)
    {
        var byAlter = await ListActiveFrontIdsByAlterAsync(client, principal);
        return byAlter.Keys.OrderBy(id => id).ToArray();
    }

    private static async Task<IReadOnlyDictionary<int, string>> ListActiveFrontIdsByAlterAsync(HttpClient client, string principal)
    {
        // Self-view: when the viewer is the system owner the response includes every active
        // front regardless of visibility - the only filter ListActiveGuardedAsync applies is
        // for cross-system viewers (friends/trusted/public). We authenticate as `principal`
        // and query `/api/systems/{principal}/fronting`.
        using var req = new HttpRequestMessage(HttpMethod.Get, $"/api/systems/{principal}/fronting");
        AttachPrincipalAuth(req, client, principal);
        var res = await client.SendAsync(req);
        var body = await res.Content.ReadAsStringAsync();

        await Assert.That(res.StatusCode)
            .IsEqualTo(HttpStatusCode.OK)
            .Because($"GET /fronting failed with {(int)res.StatusCode}. Body: {body}");

        using var doc = JsonDocument.Parse(body);
        var arr = doc.RootElement.GetProperty("data");
        var result = new Dictionary<int, string>(capacity: arr.GetArrayLength());
        foreach (var entry in arr.EnumerateArray())
        {
            // Active fronts come back shaped { alter: { id, ... }, front: { id, alter_id, ... }, primary }.
            // We pull alter_id from `front` rather than `alter` because the latter is the
            // hydrated alter read model (with its own `id`) and this keeps the test resilient
            // to either snake_case or camelCase JSON policies on the alter sub-object.
            var front = entry.GetProperty("front");
            var alterId = front.GetProperty("alter_id").GetInt32();
            var frontId = front.GetProperty("id").GetString() ?? string.Empty;
            result[alterId] = frontId;
        }

        return result;
    }
}
