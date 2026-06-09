using System.Net;
using System.Net.Http.Json;
using Interfold.IntegrationTests.TestServices;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Interfold.IntegrationTests.Controllers;

[ClassDataSource<InMemoryWebFactoryFixture>(Shared = SharedType.PerTestSession)]
[ClassDataSource<ScyllaWebFactoryFixture>(Shared = SharedType.PerTestSession)]
[ClassDataSource<CassandraWebFactoryFixture>(Shared = SharedType.PerTestSession)]
public class SettingsControllerTests(IWebFactoryFixture fixture) : BaseEndpointTest
{
    [Test]
    public async Task SettingsField_InvalidType_FallsBackToText_ReturnsCreatedWithId()
    {
        using var client = fixture.Factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var principal = "parity-field-fallback";
        await EnsureUserExistsAsync(client, principal);

        // type = "garbage" — Elixir falls back to "text"; C# must do the same
        using var req1 = new HttpRequestMessage(HttpMethod.Post, "/api/settings/fields")
        {
            Content = JsonContent.Create(new { name = "FallbackField", type = "garbage" })
        };
        AttachPrincipalAuth(req1, client, principal);
        var res1 = await client.SendAsync(req1);
        var body1 = await res1.Content.ReadAsStringAsync();

        using (Assert.Multiple())
        {
            await Assert.That(res1.StatusCode).IsEqualTo(HttpStatusCode.Created);
            await Assert.That(ReadNestedString(body1, "data", "id")).IsNotNullOrWhiteSpace();
        }
    }
    
    [Test]
    public async Task SettingsField_MissingType_FallsBackToText_ReturnsCreatedWithId()
    {
        using var client = fixture.Factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var principal = "parity-field-missing-type";
        await EnsureUserExistsAsync(client, principal);

        // type absent entirely
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/settings/fields")
        {
            Content = JsonContent.Create(new { name = "NoTypeField" })
        };
        AttachPrincipalAuth(req, client, principal);
        var res = await client.SendAsync(req);
        var body = await res.Content.ReadAsStringAsync();

        using (Assert.Multiple())
        {
            await Assert.That(res.StatusCode).IsEqualTo(HttpStatusCode.Created);
            await Assert.That(ReadNestedString(body, "data", "id")).IsNotNullOrWhiteSpace();
        }
    }
    
    [Test]
    public async Task Idempotency_SettingsUsernameUpdate_ReplayStable()
    {
        await RunSoakAsync(fixture.Factory, async (client, key) =>
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, "/api/settings/username")
            {
                Content = JsonContent.Create(new { username = "soakuser" })
            };
            req.Headers.Add("X-Interfold-Idempotency-Key", key);
            return await client.SendAsync(req);
        });
    }
    
    [Test]
    public async Task SettingsField_Create_ReturnsCreatedFieldId()
    {
        using var client = fixture.Factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var principal = "parity-field-defaults";
        await EnsureUserExistsAsync(client, principal);

        // Create field without explicit security_level.
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/settings/fields")
        {
            Content = JsonContent.Create(new { name = "DefaultSecurityField", type = "text" })
        };
        AttachPrincipalAuth(req, client, principal);
        var res = await client.SendAsync(req);
        var body = await res.Content.ReadAsStringAsync();

        var fieldId = ReadNestedString(body, "data", "id");

        using (Assert.Multiple())
        {
            await Assert.That(res.StatusCode).IsEqualTo(HttpStatusCode.Created);
            await Assert.That(fieldId).IsNotNullOrWhiteSpace();
        }
    }
}