using System.Net;
using System.Net.Http.Json;
using Interfold.IntegrationTests.TestServices;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Interfold.IntegrationTests.Controllers;

[ClassDataSource<InMemoryWebFactoryFixture>(Shared = SharedType.PerTestSession)]
[ClassDataSource<ScyllaWebFactoryFixture>(Shared = SharedType.PerTestSession)]
[ClassDataSource<CassandraWebFactoryFixture>(Shared = SharedType.PerTestSession)]
public class PublicSystemsControllerTests(IWebFactoryFixture fixture) : BaseEndpointTest
{
    [Test]
    public async Task PublicBatch_SelfLookup_Returns403InvalidEndpoint()
    {
        using var client = fixture.Factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var principal = "parity-public-batch-self";
        _ = await CreateAlterAsync(client, principal, "BatchSelfSeed");
        await EnsurePublicProfileAsync(client, principal, "batch-self");

        using var req = new HttpRequestMessage(HttpMethod.Get, $"/api/systems/{principal}/batch");
        AttachPrincipalAuth(req, client, principal);
        var res = await client.SendAsync(req);
        var body = await res.Content.ReadAsStringAsync();

        using (Assert.Multiple())
        {
            await Assert.That(res.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
            await Assert.That(body.Contains("invalid_endpoint", StringComparison.OrdinalIgnoreCase)).IsTrue();
        }
    }

    [Test, Skip("Need to rework")] //Well all of them really...
    public async Task Visibility_NonFriendFriendTrusted_AppliesToFronting()
    {
        using var client = fixture.Factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var owner = "fronting-visibility-owner";
        var nonFriend = "fronting-visibility-nonfriend";
        var friend = "fronting-visibility-friend";
        var trusted = "fronting-visibility-trusted";

        // Ensure all principals exist.
        _ = await CreateAlterAsync(client, nonFriend, "SeedNonFriend");
        _ = await CreateAlterAsync(client, friend, "SeedFriend");
        _ = await CreateAlterAsync(client, trusted, "SeedTrusted");
        await EnsurePublicProfileAsync(client, owner, "fronting-owner");
        await EnsurePublicProfileAsync(client, nonFriend, "fronting-nonfriend");
        await EnsurePublicProfileAsync(client, friend, "fronting-friend");
        await EnsurePublicProfileAsync(client, trusted, "fronting-trusted");

        // Owner alters with different visibility levels.
        var alterPublic = await CreateAlterAsync(client, owner, "VisPublic");
        var alterFriends = await CreateAlterAsync(client, owner, "VisFriends");
        var alterTrusted = await CreateAlterAsync(client, owner, "VisTrusted");
        var alterPrivate = await CreateAlterAsync(client, owner, "VisPrivate");

        await SetAlterSecurityLevelAsync(client, owner, alterPublic, "public");
        await SetAlterSecurityLevelAsync(client, owner, alterFriends, "friends_only");
        await SetAlterSecurityLevelAsync(client, owner, alterTrusted, "trusted_only");
        await SetAlterSecurityLevelAsync(client, owner, alterPrivate, "private");

        // Start active fronts on all visibility classes.
        await StartFrontAsync(client, owner, alterPublic);
        await StartFrontAsync(client, owner, alterFriends);
        await StartFrontAsync(client, owner, alterTrusted);
        await StartFrontAsync(client, owner, alterPrivate);

        // Baseline (non-friend): only public should be visible.
        await AssertContainsAsync(client, $"/api/systems/{owner}/fronting", nonFriend, alterPublic.ToString(), expectedPresent: true);
        await AssertContainsAsync(client, $"/api/systems/{owner}/fronting", nonFriend, alterFriends.ToString(), expectedPresent: false);
        await AssertContainsAsync(client, $"/api/systems/{owner}/fronting", nonFriend, alterTrusted.ToString(), expectedPresent: false);
        await AssertContainsAsync(client, $"/api/systems/{owner}/fronting", nonFriend, alterPrivate.ToString(), expectedPresent: false);

        // Friend path: public + friends_only.
        await SendFriendRequestAndAcceptAsync(client, friend, owner);

        await AssertContainsAsync(client, $"/api/systems/{owner}/fronting", friend, alterFriends.ToString(), expectedPresent: true);
        await AssertContainsAsync(client, $"/api/systems/{owner}/fronting", friend, alterTrusted.ToString(), expectedPresent: false);

        // Trusted path: public + friends_only + trusted_only.
        await SendFriendRequestAndAcceptAsync(client, trusted, owner);
        await SetFriendTrustAsync(client, owner, trusted);

        await AssertContainsAsync(client, $"/api/systems/{owner}/fronting", trusted, alterTrusted.ToString(), expectedPresent: true);
        await AssertContainsAsync(client, $"/api/systems/{owner}/fronting", trusted, alterPrivate.ToString(), expectedPresent: false);
    }

    [Test]
    public async Task PublicAlter_PrivateSecurity_Returns404ForAnonymous()
    {
        using var client = fixture.Factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var principal = "parity-guarded-alter";
        var alterId = await CreateAlterAsync(client, principal, "GuardedAlter");
        await EnsurePublicProfileAsync(client, principal, "guarded-alter");

        using var updateReq = new HttpRequestMessage(HttpMethod.Patch, $"/api/systems/me/alters/{alterId}")
        {
            Content = JsonContent.Create(new { security_level = "private" })
        };
        AttachPrincipalAuth(updateReq, client, principal);
        var updateRes = await client.SendAsync(updateReq);
        await Assert.That(updateRes.StatusCode).IsEqualTo(HttpStatusCode.NoContent);

        var publicRes = await client.GetAsync($"/api/systems/{principal}/alters/{alterId}");
        var publicBody = await publicRes.Content.ReadAsStringAsync();
        await Assert.That(publicRes.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task Visibility_NonFriendFriendTrusted_AppliesAcrossPublicReads()
    {
        using var client = fixture.Factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var owner = "alters-visibility-owner";
        var nonFriend = "alters-visibility-nonfriend";
        var friend = "alters-visibility-friend";
        var trusted = "alters-visibility-trusted";

        // Ensure all principals exist in the in-memory runtime.
        _ = await CreateAlterAsync(client, nonFriend, "SeedNonFriend");
        _ = await CreateAlterAsync(client, friend, "SeedFriend");
        _ = await CreateAlterAsync(client, trusted, "SeedTrusted");
        await EnsurePublicProfileAsync(client, owner, "alters-owner");
        await EnsurePublicProfileAsync(client, nonFriend, "alters-nonfriend");
        await EnsurePublicProfileAsync(client, friend, "alters-friend");
        await EnsurePublicProfileAsync(client, trusted, "alters-trusted");

        // Owner alters with different visibility levels.
        var alterPublic = await CreateAlterAsync(client, owner, "VisPublic");
        var alterFriends = await CreateAlterAsync(client, owner, "VisFriends");
        var alterTrusted = await CreateAlterAsync(client, owner, "VisTrusted");
        var alterPrivate = await CreateAlterAsync(client, owner, "VisPrivate");

        await SetAlterSecurityLevelAsync(client, owner, alterPublic, "public");
        await SetAlterSecurityLevelAsync(client, owner, alterFriends, "friends_only");
        await SetAlterSecurityLevelAsync(client, owner, alterTrusted, "trusted_only");
        await SetAlterSecurityLevelAsync(client, owner, alterPrivate, "private");

        // Baseline (non-friend): only public should be visible.
        await AssertContainsAsync(client, $"/api/systems/{owner}/alters/{alterPublic}", nonFriend, alterPublic.ToString(), expectedPresent: true);
        await AssertContainsAsync(client, $"/api/systems/{owner}/alters/{alterFriends}", nonFriend, "alter_not_found", expectedPresent: true, expectedStatus: HttpStatusCode.NotFound);
        await AssertContainsAsync(client, $"/api/systems/{owner}/alters/{alterTrusted}", nonFriend, "alter_not_found", expectedPresent: true, expectedStatus: HttpStatusCode.NotFound);
        await AssertContainsAsync(client, $"/api/systems/{owner}/alters/{alterPrivate}", nonFriend, "alter_not_found", expectedPresent: true, expectedStatus: HttpStatusCode.NotFound);

        // Friend path: public + friends_only.
        await SendFriendRequestAndAcceptAsync(client, friend, owner);

        await AssertContainsAsync(client, $"/api/systems/{owner}/alters/{alterFriends}", friend, alterFriends.ToString(), expectedPresent: true);
        await AssertContainsAsync(client, $"/api/systems/{owner}/alters/{alterTrusted}", friend, "alter_not_found", expectedPresent: true, expectedStatus: HttpStatusCode.NotFound);

        // Trusted path: public + friends_only + trusted_only.
        await SendFriendRequestAndAcceptAsync(client, trusted, owner);
        await SetFriendTrustAsync(client, owner, trusted);

        await AssertContainsAsync(client, $"/api/systems/{owner}/alters/{alterTrusted}", trusted, alterTrusted.ToString(), expectedPresent: true);
        await AssertContainsAsync(client, $"/api/systems/{owner}/alters/{alterPrivate}", trusted, "alter_not_found", expectedPresent: true, expectedStatus: HttpStatusCode.NotFound);
    }

    [Test]
    public async Task PublicTag_PrivateSecurity_Returns404ForAnonymous()
    {
        using var client = fixture.Factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var principal = "parity-guarded-tag";
        var tagId = await CreateTagAsync(client, principal, "GuardedTag");
        await EnsurePublicProfileAsync(client, principal, "guarded-tag");

        using var updateReq = new HttpRequestMessage(HttpMethod.Patch, $"/api/systems/me/tags/{tagId}")
        {
            Content = JsonContent.Create(new { security_level = "private" })
        };
        AttachPrincipalAuth(updateReq, client, principal);
        var updateRes = await client.SendAsync(updateReq);
        await Assert.That(updateRes.StatusCode).IsEqualTo(HttpStatusCode.NoContent);

        var publicRes = await client.GetAsync($"/api/systems/{principal}/tags/{tagId}");
        var publicBody = await publicRes.Content.ReadAsStringAsync();
        await Assert.That(publicRes.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task Visibility_NonFriendFriendTrusted_AppliesToTags()
    {
        using var client = fixture.Factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var owner = "tags-visibility-owner";
        var nonFriend = "tags-visibility-nonfriend";
        var friend = "tags-visibility-friend";
        var trusted = "tags-visibility-trusted";

        // Ensure all principals exist.
        _ = await CreateAlterAsync(client, nonFriend, "SeedNonFriend");
        _ = await CreateAlterAsync(client, friend, "SeedFriend");
        _ = await CreateAlterAsync(client, trusted, "SeedTrusted");
        await EnsurePublicProfileAsync(client, owner, "tags-owner");
        await EnsurePublicProfileAsync(client, nonFriend, "tags-nonfriend");
        await EnsurePublicProfileAsync(client, friend, "tags-friend");
        await EnsurePublicProfileAsync(client, trusted, "tags-trusted");

        // Owner tags with different visibility levels.
        var tagPublic = await CreateTagAsync(client, owner, "TagPublic");
        var tagFriends = await CreateTagAsync(client, owner, "TagFriends");
        var tagTrusted = await CreateTagAsync(client, owner, "TagTrusted");
        var tagPrivate = await CreateTagAsync(client, owner, "TagPrivate");

        await SetTagSecurityLevelAsync(client, owner, tagPublic, "public");
        await SetTagSecurityLevelAsync(client, owner, tagFriends, "friends_only");
        await SetTagSecurityLevelAsync(client, owner, tagTrusted, "trusted_only");
        await SetTagSecurityLevelAsync(client, owner, tagPrivate, "private");

        // Baseline (non-friend): only public should be visible.
        await AssertContainsAsync(client, $"/api/systems/{owner}/tags/{tagPublic}", nonFriend, tagPublic, expectedPresent: true);
        await AssertContainsAsync(client, $"/api/systems/{owner}/tags/{tagFriends}", nonFriend, "tag_not_found", expectedPresent: true, expectedStatus: HttpStatusCode.NotFound);
        await AssertContainsAsync(client, $"/api/systems/{owner}/tags/{tagTrusted}", nonFriend, "tag_not_found", expectedPresent: true, expectedStatus: HttpStatusCode.NotFound);
        await AssertContainsAsync(client, $"/api/systems/{owner}/tags/{tagPrivate}", nonFriend, "tag_not_found", expectedPresent: true, expectedStatus: HttpStatusCode.NotFound);

        // Friend path: public + friends_only.
        await SendFriendRequestAndAcceptAsync(client, friend, owner);

        await AssertContainsAsync(client, $"/api/systems/{owner}/tags/{tagFriends}", friend, tagFriends, expectedPresent: true);
        await AssertContainsAsync(client, $"/api/systems/{owner}/tags/{tagTrusted}", friend, "tag_not_found", expectedPresent: true, expectedStatus: HttpStatusCode.NotFound);

        // Trusted path: public + friends_only + trusted_only.
        await SendFriendRequestAndAcceptAsync(client, trusted, owner);
        await SetFriendTrustAsync(client, owner, trusted);

        await AssertContainsAsync(client, $"/api/systems/{owner}/tags/{tagTrusted}", trusted, tagTrusted, expectedPresent: true);
        await AssertContainsAsync(client, $"/api/systems/{owner}/tags/{tagPrivate}", trusted, "tag_not_found", expectedPresent: true, expectedStatus: HttpStatusCode.NotFound);
    }

    private static async Task EnsurePublicProfileAsync(HttpClient client, string principal, string username)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/settings/username")
        {
            Content = JsonContent.Create(new { username })
        };
        AttachPrincipalAuth(request, client, principal);

        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NoContent)
            .Because($"Expected username seed 204 for '{principal}', got {(int)response.StatusCode}. Body: {body}");
    }

}
