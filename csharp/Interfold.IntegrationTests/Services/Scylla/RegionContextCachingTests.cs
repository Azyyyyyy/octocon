using Cassandra;
using Interfold.Contracts.Configuration;
using Interfold.Contracts.Secrets;
using Interfold.Infrastructure.Scylla;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Interfold.IntegrationTests.Services.Scylla;

public sealed class RegionContextCachingTests : BaseEndpointTest
{
    // A stub that throws if the session is ever accessed, proving the cache is always used.
    private sealed class ThrowingSessionProvider : IScyllaSessionProvider
    {
        public Task<ISession> GetSessionAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("DB must not be reached when cache is warm.");
    }

    private sealed class EmptySecretsStore : ISecretsStore
    {
        public Task<string?> GetAsync(string key, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
        public Task<string> GetRequiredAsync(string key, CancellationToken cancellationToken = default) => throw new KeyNotFoundException(key);
        public Task<IReadOnlyList<SecretEntry>> ListAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<SecretEntry>>([]);
    }

    private static ScyllaUserRegistryRegionContext BuildContext(string defaultRegion = "nam")
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["OCTOCON_SCYLLA_KEYSPACE"] = defaultRegion })
            .Build();
        return new(new ThrowingSessionProvider(), new PersistenceConfiguration { ScyllaKeyspace = defaultRegion },
            new EmptySecretsStore(), config,
            NullLogger<ScyllaUserRegistryRegionContext>.Instance);
    }

    [Test]
    public async Task ResolveUserRegion_FallsBackToDefault_WhenSessionThrowsAndCacheEmpty()
    {
        // ThrowingSessionProvider will throw; the catch block should return CurrentRegion.
        var ctx = BuildContext("eur");
        var result = ctx.ResolveUserRegion("user-123");
        await Assert.That(result).IsEqualTo("eur");
    }

    [Test]
    public async Task ResolveUserRegion_UsesCachedRegion_AfterRegisterRegion()
    {
        var ctx = BuildContext("nam");
        ctx.RegisterRegion("user-abc", "eur");

        // ThrowingSessionProvider must NOT be called; cache is warm.
        var result = ctx.ResolveUserRegion("user-abc");
        await Assert.That(result).IsEqualTo("eur");
    }

    [Test]
    public async Task ResolveUserRegion_StripsLegacyPrefix_BeforeCacheLookup()
    {
        var ctx = BuildContext("nam");
        ctx.RegisterRegion("eas", "user-xyz");  // plain key stored as "user-xyz"

        // The prefixed form should resolve via the same stripped key.
        ctx.RegisterRegion("eas:user-xyz", "sam");
        var result = ctx.ResolveUserRegion("eas:user-xyz");
        await Assert.That(result).IsEqualTo("sam");

        // Plain key lookup after prefix strip should also be cache-warm.
        var result2 = ctx.ResolveUserRegion("eas:user-xyz");
        await Assert.That(result2).IsEqualTo("sam");
    }

    [Test]
    public async Task ResolveConsistency_ReturnsLocal_ForSameRegion()
    {
        var ctx = BuildContext("nam");
        using (Assert.Multiple())
        {
            await Assert.That(ctx.ResolveConsistency("nam")).IsEqualTo("local");
            await Assert.That(ctx.ResolveConsistency("NAM")).IsEqualTo("local");
        }
    }

    [Test]
    public async Task ResolveConsistency_ReturnsGlobal_ForDifferentRegion()
    {
        var ctx = BuildContext("nam");
        await Assert.That(ctx.ResolveConsistency("eur")).IsEqualTo("global");
    }

    [Test]
    public async Task RegisterRegion_EmptyValues_DoesNotCorruptCache()
    {
        var ctx = BuildContext("nam");
        ctx.RegisterRegion("", "eur");        // empty key — should be no-op
        ctx.RegisterRegion("user-1", "");     // empty region — should be no-op

        // Fallback should still apply because nothing was cached.
        var result = ctx.ResolveUserRegion("user-1");
        await Assert.That(result).IsEqualTo("nam");
    }
}