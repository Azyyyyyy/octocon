using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Interfold.IntegrationTests.Attributes;
using Interfold.IntegrationTests.Models;
using Interfold.IntegrationTests.TestServices;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Interfold.IntegrationTests.Controllers;

public class AltersControllerTests : BaseEndpointTest
{
    [Test, ApiIntegration]
    [CombinedDataSources]
    public async Task Api_AlterAvatarMultipart_PersistsAndReflectsOnPublicAlter([InterfoldFactoryGenerator] InterfoldWebApplicationFactory factory)
    {
        var runId = Guid.NewGuid().ToString("N");
        var storageRoot = Path.Combine(Path.GetTempPath(), "octocon-itest", "avatars", runId);
        var publicBase = $"/avatars-itest/{runId}";

        try
        {
            Directory.CreateDirectory(storageRoot);

            factory
                .WithConfiguration("OCTOCON_AVATAR_STORAGE_ROOT", storageRoot)
                .WithConfiguration("OCTOCON_AVATAR_PUBLIC_BASE", publicBase);
            
            using var client = factory.CreateClient();

            var principalId = $"sys-alter-avatar-{Guid.NewGuid():N}"[..24];

            using var usernameRequest = new HttpRequestMessage(HttpMethod.Post, "/api/settings/username")
            {
                Content = JsonContent.Create(new { username = "avatar-parity" })
            };
            AttachPrincipalAuth(usernameRequest, client, principalId);
            var usernameResponse = await client.SendAsync(usernameRequest);
            await Assert.That(usernameResponse.StatusCode).IsEqualTo(HttpStatusCode.NoContent);

            using var createRequest = new HttpRequestMessage(HttpMethod.Post, "/api/systems/me/alters")
            {
                Content = JsonContent.Create(new { name = "AvatarTarget" })
            };
            AttachPrincipalAuth(createRequest, client, principalId);

            var createResponse = await client.SendAsync(createRequest);
            var createBody = await createResponse.Content.ReadAsStringAsync();
            await Assert.That(createResponse.StatusCode).IsEqualTo(HttpStatusCode.Created);

            var alterId = ReadTrailingIntFromLocation(createResponse);

            using var uploadRequest = BuildMultipartUploadRequest(client, $"/api/systems/me/alters/{alterId}/avatar", principalId, "avatar-alter.png", "image/png");
            var uploadResponse = await client.SendAsync(uploadRequest);
            await Assert.That(uploadResponse.StatusCode).IsEqualTo(HttpStatusCode.NoContent);

            using var publicAlterRequest = new HttpRequestMessage(HttpMethod.Get, $"/api/systems/{principalId}/alters/{alterId}");
            AttachPrincipalAuth(publicAlterRequest, client, principalId);
            var publicAlterResponse = await client.SendAsync(publicAlterRequest);
            var publicAlterBody = await publicAlterResponse.Content.ReadAsStringAsync();

            var expectedPrefix = $"{publicBase}/{principalId}/{alterId}/";
            var alterAvatarUrl = ReadNestedStringField(publicAlterBody, "data", "avatar_url");
            using (Assert.Multiple())
            {
                await Assert.That(publicAlterResponse.StatusCode).IsEqualTo(HttpStatusCode.OK);
                await Assert.That(alterAvatarUrl).IsNotNullOrWhiteSpace();
                await Assert.That(UrlPathStartsWith(alterAvatarUrl, expectedPrefix)).IsTrue();
            }

            using var deleteReq = new HttpRequestMessage(HttpMethod.Delete, $"/api/systems/me/alters/{alterId}/avatar")
            {
                Content = JsonContent.Create(new { })
            };
            AttachPrincipalAuth(deleteReq, client, principalId);
            var deleteRes = await client.SendAsync(deleteReq);
            await Assert.That(deleteRes.StatusCode).IsEqualTo(HttpStatusCode.NoContent);

            using var afterDeleteRequest = new HttpRequestMessage(HttpMethod.Get, $"/api/systems/{principalId}/alters/{alterId}");
            AttachPrincipalAuth(afterDeleteRequest, client, principalId);
            var afterDeleteResponse = await client.SendAsync(afterDeleteRequest);
            var afterDeleteBody = await afterDeleteResponse.Content.ReadAsStringAsync();

            var staleAvatarUrl = ReadNestedStringField(afterDeleteBody, "data", "avatar_url");
            using (Assert.Multiple())
            {
                await Assert.That(afterDeleteResponse.StatusCode).IsEqualTo(HttpStatusCode.OK);
                await Assert.That(staleAvatarUrl).IsNullOrWhiteSpace();
            }
        }
        finally
        {
            if (Directory.Exists(storageRoot))
                Directory.Delete(storageRoot, true);
        }
    }
    
    [Test, ApiIntegration]
    [CombinedDataSources]
    public async Task FieldSecurityLevelByRelationship_AppliesCorrectly([InterfoldFactoryGenerator] InterfoldWebApplicationFactory factory)
    {
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var owner = "parity-guarded-fields-owner";
        var nonFriend = "parity-guarded-fields-nonfriend";
        var friend = "parity-guarded-fields-friend";
        var trusted = "parity-guarded-fields-trusted";

        // Ensure all principals exist.
        await EnsureUserExistsAsync(client, owner);
        _ = await CreateAlterAsync(client, nonFriend, "SeedNonFriend");
        _ = await CreateAlterAsync(client, friend, "SeedFriend");
        _ = await CreateAlterAsync(client, trusted, "SeedTrusted");

        // Create field definitions with different security levels on owner's profile.
        var fieldPublic = await CreateSettingsFieldAsync(client, owner, "FieldPublic", "text", "public");
        var fieldFriends = await CreateSettingsFieldAsync(client, owner, "FieldFriends", "text", "friends_only");
        var fieldTrusted = await CreateSettingsFieldAsync(client, owner, "FieldTrusted", "text", "trusted_only");
        var fieldPrivate = await CreateSettingsFieldAsync(client, owner, "FieldPrivate", "text", "private");

        // Create an alter and set field values.
        var alterId = await CreateAlterAsync(client, owner, "GuardedFieldsAlter");
        await SetAlterSecurityLevelAsync(client, owner, alterId, "public");
        await UpdateAlterFieldsAsync(client, owner, alterId, new[]
        {
            new { id = fieldPublic, value = "PublicValue" },
            new { id = fieldFriends, value = "FriendsValue" },
            new { id = fieldTrusted, value = "TrustedValue" },
            new { id = fieldPrivate, value = "PrivateValue" }
        });

        // Non-friend: only public field visible.
        var nonFriendReq = new HttpRequestMessage(HttpMethod.Get, $"/api/systems/{owner}/alters/{alterId}");
        AttachPrincipalAuth(nonFriendReq, client, friend);

        var nonFriendRes = await client.SendAsync(nonFriendReq);
        var nonFriendBody = await nonFriendRes.Content.ReadAsStringAsync();
        using (Assert.Multiple())
        {
            await Assert.That(nonFriendRes.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(nonFriendBody).Contains("PublicValue");
            await Assert.That(nonFriendBody).DoesNotContain("FriendsValue");
            await Assert.That(nonFriendBody).DoesNotContain("TrustedValue");
            await Assert.That(nonFriendBody).DoesNotContain("PrivateValue");
        }

        // Establish friend relationship.
        await SendFriendRequestAndAcceptAsync(client, friend, owner);

        // Friend: public + friends_only fields visible.
        var friendReq = new HttpRequestMessage(HttpMethod.Get, $"/api/systems/{owner}/alters/{alterId}");
        AttachPrincipalAuth(friendReq, client, friend);

        var friendRes = await client.SendAsync(friendReq);
        var friendBody = await friendRes.Content.ReadAsStringAsync();
        using (Assert.Multiple())
        {
            await Assert.That(friendRes.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(friendBody).Contains("PublicValue");
            await Assert.That(friendBody).Contains("FriendsValue");
            await Assert.That(friendBody).DoesNotContain("TrustedValue");
            await Assert.That(friendBody).DoesNotContain("PrivateValue");
        }

        // Establish trusted relationship.
        await SendFriendRequestAndAcceptAsync(client, trusted, owner);
        await SetFriendTrustAsync(client, owner, trusted);

        // Trusted: public + friends_only + trusted_only fields visible.
        var trustedReq = new HttpRequestMessage(HttpMethod.Get, $"/api/systems/{owner}/alters/{alterId}");
        AttachPrincipalAuth(trustedReq, client, trusted);

        var trustedRes = await client.SendAsync(trustedReq);
        var trustedBody = await trustedRes.Content.ReadAsStringAsync();
        using (Assert.Multiple())
        {
            await Assert.That(trustedRes.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(trustedBody).Contains("PublicValue");
            await Assert.That(trustedBody).Contains("FriendsValue");
            await Assert.That(trustedBody).Contains("TrustedValue");
            await Assert.That(trustedBody).DoesNotContain("PrivateValue");
        }
    }
    
    [Test, ApiIntegration]
    [CombinedDataSources]
    public async Task CustomFields_FieldSecurityLevelByRelationship_AppliesCorrectly([InterfoldFactoryGenerator] InterfoldWebApplicationFactory factory)
    {
        using var client = factory.CreateClient();

        var owner = "settings-guarded-fields-owner";
        var nonFriend = "settings-guarded-fields-nonfriend";
        var friend = "settings-guarded-fields-friend";
        var trusted = "settings-guarded-fields-trusted";

        // Ensure all principals exist.
        await EnsureUserExistsAsync(client, owner);
        _ = await CreateAlterAsync(client, nonFriend, "SeedNonFriend");
        _ = await CreateAlterAsync(client, friend, "SeedFriend");
        _ = await CreateAlterAsync(client, trusted, "SeedTrusted");

        // Create field definitions with different security levels on owner's profile.
        var fieldPublic = await CreateSettingsFieldAsync(client, owner, "FieldPublic", "text", "public");
        var fieldFriends = await CreateSettingsFieldAsync(client, owner, "FieldFriends", "text", "friends_only");
        var fieldTrusted = await CreateSettingsFieldAsync(client, owner, "FieldTrusted", "text", "trusted_only");
        var fieldPrivate = await CreateSettingsFieldAsync(client, owner, "FieldPrivate", "text", "private");

        // Create an alter and set field values.
        var alterId = await CreateAlterAsync(client, owner, "GuardedFieldsAlter");
        await SetAlterSecurityLevelAsync(client, owner, alterId, "public");
        await UpdateAlterFieldsAsync(client, owner, alterId, new[]
        {
            new { id = fieldPublic, value = "PublicValue" },
            new { id = fieldFriends, value = "FriendsValue" },
            new { id = fieldTrusted, value = "TrustedValue" },
            new { id = fieldPrivate, value = "PrivateValue" }
        });

        // Non-friend: only public field visible.
        var nonFriendReq = new HttpRequestMessage(HttpMethod.Get, $"/api/systems/{owner}/alters/{alterId}");
        AttachPrincipalAuth(nonFriendReq, client, nonFriend);

        var nonFriendRes = await client.SendAsync(nonFriendReq);
        var nonFriendBody = await nonFriendRes.Content.ReadAsStringAsync();
        using (Assert.Multiple())
        {
            await Assert.That(nonFriendRes.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(nonFriendBody).Contains("PublicValue");
            await Assert.That(nonFriendBody).DoesNotContain("FriendsValue");
            await Assert.That(nonFriendBody).DoesNotContain("TrustedValue");
            await Assert.That(nonFriendBody).DoesNotContain("PrivateValue");
        }

        // Establish friend relationship.
        await SendFriendRequestAndAcceptAsync(client, friend, owner);

        // Friend: public + friends_only fields visible.
        var friendReq = new HttpRequestMessage(HttpMethod.Get, $"/api/systems/{owner}/alters/{alterId}");
        AttachPrincipalAuth(friendReq, client, friend);
        var friendRes = await client.SendAsync(friendReq);
        var friendBody = await friendRes.Content.ReadAsStringAsync();
        using (Assert.Multiple())
        {
            await Assert.That(friendRes.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(friendBody).Contains("PublicValue");
            await Assert.That(friendBody).Contains("FriendsValue");
            await Assert.That(friendBody).DoesNotContain("TrustedValue");
            await Assert.That(friendBody).DoesNotContain("PrivateValue");
        }

        // Establish trusted relationship.
        await SendFriendRequestAndAcceptAsync(client, trusted, owner);
        await SetFriendTrustAsync(client, owner, trusted);

        // Trusted: public + friends_only + trusted_only fields visible.
        var trustedReq = new HttpRequestMessage(HttpMethod.Get, $"/api/systems/{owner}/alters/{alterId}");
        AttachPrincipalAuth(trustedReq, client, trusted);
        var trustedRes = await client.SendAsync(trustedReq);
        var trustedBody = await trustedRes.Content.ReadAsStringAsync();
        using (Assert.Multiple())
        {
            await Assert.That(trustedRes.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(trustedBody).Contains("PublicValue");
            await Assert.That(trustedBody).Contains("FriendsValue");
            await Assert.That(trustedBody).Contains("TrustedValue");
            await Assert.That(trustedBody).DoesNotContain("PrivateValue");
        }
    }

    [Test, ApiIntegration]
    [CombinedDataSources]
    public async Task AlterJournal_ListWhenEmpty_ReturnsDataAsEmptyArray([InterfoldFactoryGenerator] InterfoldWebApplicationFactory factory)
    {
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var principal = "parity-alter-journal-empty-list";
        var alterId = await CreateAlterAsync(client, principal, "NoJournalAlter");

        using var listReq = new HttpRequestMessage(HttpMethod.Get, $"/api/systems/me/alters/{alterId}/journals");
        AttachPrincipalAuth(listReq, client, principal);
        var listRes = await client.SendAsync(listReq);
        var listBody = await listRes.Content.ReadAsStringAsync();

        using var doc = JsonDocument.Parse(listBody);
        using (Assert.Multiple())
        {
            await Assert.That(listRes.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(doc.RootElement.TryGetProperty("data", out var dataProp)).IsTrue();
            await Assert.That(dataProp.ValueKind).IsEqualTo(JsonValueKind.Array);
            await Assert.That(dataProp.GetArrayLength()).IsEqualTo(0);
        }
    }

    [Test, ApiIntegration]
    [CombinedDataSources]
    public async Task AlterJournal_NestedCreate_Returns201WithDataAndReplay([InterfoldFactoryGenerator] InterfoldWebApplicationFactory factory)
    {
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
    [CombinedDataSources]
    public async Task AlterJournal_ShowAfterDelete_Returns404([InterfoldFactoryGenerator] InterfoldWebApplicationFactory factory)
    {
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var principal = "parity-alter-journal-404";
        var alterId = await CreateAlterAsync(client, principal, "JournalHolder404");

        using var createReq = new HttpRequestMessage(HttpMethod.Post, $"/api/systems/me/alters/{alterId}/journals")
        {
            Content = JsonContent.Create(new { title = "ToDelete" })
        };
        AttachPrincipalAuth(createReq, client, principal);
        var createRes = await client.SendAsync(createReq);
        var createBody = await createRes.Content.ReadAsStringAsync();
        var entryId = ReadNestedString(createBody, "data", "id");
        if (string.IsNullOrWhiteSpace(entryId))
            entryId = ReadNestedString(createBody, "data", "entry_id");

        using var deleteReq = new HttpRequestMessage(HttpMethod.Delete, $"/api/systems/me/alters/journals/{entryId}");
        AttachPrincipalAuth(deleteReq, client, principal);
        await client.SendAsync(deleteReq);

        using var showReq = new HttpRequestMessage(HttpMethod.Get, $"/api/systems/me/alters/journals/{entryId}");
        AttachPrincipalAuth(showReq, client, principal);
        var showRes = await client.SendAsync(showReq);

        await Assert.That(showRes.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }
    
    [Test, ApiIntegration]
    [CombinedDataSources]
    public async Task AlterCreate_IdempotentReplay_WorksAgainstLiveAdapters([InterfoldFactoryGenerator] InterfoldWebApplicationFactory factory)
    {
        factory
            .WithConfiguration("OCTOCON_REGION", IntegrationTestEnvironment.GetVariable("OCTOCON_TEST_REGION", "nam"));

        using var client = factory.CreateClient();

        var systemId = $"itest-{Guid.NewGuid():N}"[..14];
        var idempotencyKey = Guid.NewGuid().ToString("N");

        var requestContent = new { name = "IntegrationSmoke" };

        // First call
        using var firstReq = new HttpRequestMessage(HttpMethod.Post, "/api/systems/me/alters")
        {
            Content = JsonContent.Create(requestContent)
        };
        AttachPrincipalAuth(firstReq, client, systemId);
        firstReq.Headers.Add("X-Interfold-Idempotency-Key", idempotencyKey);

        var firstRes = await client.SendAsync(firstReq);
        var firstBody = await firstRes.Content.ReadAsStringAsync();
        
        await Assert.That(firstRes.StatusCode).IsEqualTo(HttpStatusCode.Created);
        
        using (var doc = JsonDocument.Parse(firstBody))
        {
            var root = doc.RootElement;
            using (Assert.Multiple())
            {
                await Assert.That(root.TryGetProperty("data", out _)).IsTrue();
                await Assert.That(root.GetProperty("replay").GetBoolean()).IsFalse();
            }
        }

        // Second call (replay)
        using var secondReq = new HttpRequestMessage(HttpMethod.Post, "/api/systems/me/alters")
        {
            Content = JsonContent.Create(requestContent)
        };
        AttachPrincipalAuth(secondReq, client, systemId);
        secondReq.Headers.Add("X-Interfold-Idempotency-Key", idempotencyKey);

        var secondRes = await client.SendAsync(secondReq);
        var secondBody = await secondRes.Content.ReadAsStringAsync();

        await Assert.That(secondRes.StatusCode).IsEqualTo(HttpStatusCode.Created);

        using (var doc = JsonDocument.Parse(secondBody))
        {
            var root = doc.RootElement;
            using (Assert.Multiple())
            {
                await Assert.That(root.TryGetProperty("data", out _)).IsTrue();
                await Assert.That(root.GetProperty("replay").GetBoolean()).IsTrue();
            }
        }
    }
    
    [Test, ApiIntegration]
    [CombinedDataSources]
    public async Task OperationalHealth_GuardedPaths_GetGuardedAsync_Succeeds([InterfoldFactoryGenerator] InterfoldWebApplicationFactory factory)
    {
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        // Query a non-existent alter should return 404, not 500
        var principal = "operational-health-test";
        using var req = new HttpRequestMessage(HttpMethod.Get, "/api/systems/me/alters/9999");
        AttachPrincipalAuth(req, client, principal);
        var res = await client.SendAsync(req);

        // Expect 404 (not found) rather than 500 (error), validating guarded read path worked
        await Assert.That(res.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }
    
    [Test, ApiIntegration]
    [CombinedDataSources]
    public async Task Idempotency_AlterCreate_ReplayStable([InterfoldFactoryGenerator] InterfoldWebApplicationFactory factory)
    {
        await RunSoakAsync(factory, async (client, key) =>
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, "/api/systems/me/alters")
            {
                Content = JsonContent.Create(new { name = "SoakAlter" })
            };
            req.Headers.Add("X-Interfold-Idempotency-Key", key);
            return await client.SendAsync(req);
        });
    }
}